//! The `desktop-mcp` command.
use clap::{Args, Parser, Subcommand};
use desktop_mcp_core::config::Config;
use desktop_mcp_core::plan::{CommandLine, ServePlan};
use std::path::PathBuf;
use std::process::ExitCode;

#[derive(Parser)]
#[command(name = "desktop-mcp", version, about = "An MCP server for computer use on the desktop")]
struct Cli {
    #[command(subcommand)]
    command: Option<Command>,
    #[command(flatten)]
    serve: ServeArgs,
}

#[derive(Subcommand)]
enum Command {
    /// Run the server (the default command).
    Serve(ServeArgs),
    /// Bridge stdio to the running server, for clients that only spawn stdio servers.
    ///
    /// Without --url or --socket the installed Launch Agent is used (desktop-mcp install): its Unix socket
    /// when it has one, with HTTP as the fallback if the socket refuses the connection, else its HTTP address.
    /// Over the socket, stdin and stdout are piped to the server unchanged.
    Connect(ConnectArgs),
}

#[derive(Args)]
struct ConnectArgs {
    /// The streamable-http endpoint. (default: the installed Launch Agent's socket, else its /mcp URL)
    #[arg(long)]
    url: Option<String>,
    /// Connect over this Unix domain socket instead of HTTP (no fallback).
    #[arg(long)]
    socket: Option<String>,
    /// Bearer token for the server (HTTP only). Or $DESKTOP_MCP_AUTH_KEY, else auth_key from config.toml.
    #[arg(long)]
    auth_key: Option<String>,
}

#[derive(Args, Clone, Default)]
struct ServeArgs {
    /// The transport layer used by the MCP server: stdio|streamable-http. (default: stdio)
    #[arg(long)]
    transport: Option<String>,
    /// Host to bind the Streamable HTTP server. (default: localhost)
    #[arg(long)]
    host: Option<String>,
    /// Port to bind the Streamable HTTP server. (default: 8000)
    #[arg(long)]
    port: Option<u16>,
    /// Path to desktop-mcp config file (TOML).
    #[arg(long)]
    config: Option<String>,
    /// Bearer token required on all HTTP requests. Or $DESKTOP_MCP_AUTH_KEY.
    #[arg(long)]
    auth_key: Option<String>,
    /// Allow binding to non-loopback addresses without authentication (not recommended).
    #[arg(long)]
    allow_insecure_remote: bool,
    /// Comma-separated client IPs or CIDR ranges (IPv4/IPv6). Or $DESKTOP_MCP_IP_ALLOWLIST.
    #[arg(long)]
    ip_allowlist: Option<String>,
    /// TLS certificate (.pem) for HTTPS. Requires --ssl-keyfile. Or $DESKTOP_MCP_SSL_CERTFILE.
    #[arg(long)]
    ssl_certfile: Option<String>,
    /// TLS private key (.pem) for HTTPS. Requires --ssl-certfile. Or $DESKTOP_MCP_SSL_KEYFILE.
    #[arg(long)]
    ssl_keyfile: Option<String>,
    /// OAuth client ID (pre-provisioned confidential client). Or $DESKTOP_MCP_OAUTH_CLIENT_ID.
    #[arg(long)]
    oauth_client_id: Option<String>,
    /// OAuth client secret. Or $DESKTOP_MCP_OAUTH_CLIENT_SECRET.
    #[arg(long)]
    oauth_client_secret: Option<String>,
    /// Comma-separated tool names to disable, e.g. "Shell,Scrape". Or $DESKTOP_MCP_EXCLUDE_TOOLS.
    #[arg(long)]
    exclude_tools: Option<String>,
    /// Comma-separated opt-in tools to enable, e.g. "OCR". Or $DESKTOP_MCP_INCLUDE_TOOLS.
    #[arg(long)]
    include_tools: Option<String>,
    /// Seconds a session may stay idle before it is dropped; 0 keeps sessions until the client ends them. (default: 1800)
    #[arg(long)]
    session_timeout: Option<u64>,
    /// Also serve the stdio binding on this Unix domain socket (what `install` sets up).
    #[arg(long)]
    socket: Option<String>,
}

impl ServeArgs {
    fn command_line(&self) -> CommandLine {
        CommandLine {
            transport: self.transport.clone(),
            host: self.host.clone(),
            port: self.port,
            auth_key: self.auth_key.clone(),
            allow_insecure_remote: self.allow_insecure_remote,
            ip_allowlist: self.ip_allowlist.clone(),
            ssl_certfile: self.ssl_certfile.clone(),
            ssl_keyfile: self.ssl_keyfile.clone(),
            oauth_client_id: self.oauth_client_id.clone(),
            oauth_client_secret: self.oauth_client_secret.clone(),
            exclude_tools: self.exclude_tools.clone(),
            include_tools: self.include_tools.clone(),
            session_timeout: self.session_timeout,
            socket_path: self.socket.clone(),
        }
    }
}

pub fn log(message: &str) {
    eprintln!("[desktop-mcp] {message}");
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    let args = match cli.command {
        Some(Command::Serve(args)) => args,
        Some(Command::Connect(args)) => return connect(args),
        None => cli.serve,
    };
    let plan = match plan(&args) {
        Ok(plan) => plan,
        Err(message) => {
            eprintln!("Error: {message}");
            return ExitCode::FAILURE;
        }
    };
    for warning in &plan.warnings {
        log(warning);
    }
    serve(plan)
}

fn home() -> PathBuf {
    std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default()
}

/// Where `connect` goes.
enum Route {
    Socket { path: String, fallback: Option<desktop_mcp_core::launch_agent::HttpTarget> },
    Http(desktop_mcp_core::launch_agent::HttpTarget),
}

fn connect(args: ConnectArgs) -> ExitCode {
    use desktop_mcp_core::launch_agent::{plist_path, ConnectPlan, HttpTarget};
    if args.url.is_some() && args.socket.is_some() {
        eprintln!("Error: --url and --socket are exclusive.");
        return ExitCode::from(64);
    }
    let config = Config::discover(None, &home()).and_then(|path| Config::load(Some(&path)).ok()).unwrap_or_default();
    let key = [args.auth_key.clone(), std::env::var(desktop_mcp_core::plan::ENV_AUTH_KEY).ok(), config.server.auth_key.clone()].into_iter().flatten().find(|key| !key.is_empty());
    let route = if let Some(url) = args.url {
        if !(url.starts_with("http://") || url.starts_with("https://")) {
            eprintln!("Error: --url must be an absolute http(s) URL.");
            return ExitCode::from(64);
        }
        Route::Http(HttpTarget { url, auth_key: key })
    } else if let Some(socket) = args.socket {
        if socket.len() > 103 {
            eprintln!("Error: Socket path too long (max 103 bytes): {socket}");
            return ExitCode::FAILURE;
        }
        Route::Socket { path: socket, fallback: None }
    } else {
        let plist = plist_path(&home());
        if !plist.exists() {
            eprintln!("Error: No Launch Agent is installed ({}). Run `desktop-mcp install` first, or pass --url.", plist.display());
            return ExitCode::FAILURE;
        }
        let installed = installed_arguments(&plist).and_then(|arguments| ConnectPlan::installed(&arguments, &config));
        match installed {
            Ok(ConnectPlan::Socket { path, mut fallback }) => {
                fallback.auth_key = key;
                Route::Socket { path, fallback: Some(fallback) }
            }
            Ok(ConnectPlan::Http(mut target)) => {
                target.auth_key = key;
                Route::Http(target)
            }
            Err(reason) => {
                eprintln!("Error: {reason}");
                return ExitCode::FAILURE;
            }
        }
    };
    run_connect(route)
}

#[cfg(target_os = "macos")]
fn installed_arguments(plist: &std::path::Path) -> Result<Vec<String>, String> {
    desktop_mcp_macos::plist::read(plist).map(|plist| desktop_mcp_macos::plist::program_arguments(&plist))
}

#[cfg(target_os = "macos")]
fn run_connect(route: Route) -> ExitCode {
    use desktop_mcp_core::bridge::{relay, Relayed};
    use desktop_mcp_macos::{fd::FdIo, main_loop};
    use std::os::fd::IntoRawFd;

    // A write to a closed stdout or socket must fail with EPIPE, not end the process.
    unsafe { libc::signal(libc::SIGPIPE, libc::SIG_IGN) };
    let target = match route {
        Route::Http(target) => target,
        Route::Socket { path, fallback } => match std::os::unix::net::UnixStream::connect(&path) {
            Ok(stream) => {
                let socket = FdIo::socket(stream.into_raw_fd());
                return match main_loop::run(|_| relay(FdIo::stdio(), socket)) {
                    Relayed::Finished | Relayed::OutputClosed => ExitCode::SUCCESS,
                    Relayed::ServerClosed => {
                        log("desktop-mcp connect: the server closed the connection");
                        ExitCode::FAILURE
                    }
                };
            }
            Err(error) => match fallback {
                Some(fallback) => {
                    log(&format!("desktop-mcp connect: {path}: {error}; using HTTP at {}", fallback.url));
                    fallback
                }
                None => {
                    log(&format!("desktop-mcp connect: {path}: {error}"));
                    return ExitCode::FAILURE;
                }
            },
        },
    };
    let Ok(uri) = target.url.parse::<http::Uri>() else {
        eprintln!("Error: --url must be an absolute http(s) URL.");
        return ExitCode::from(64);
    };
    let tls = uri.scheme_str() == Some("https");
    let host = uri.host().unwrap_or("127.0.0.1").trim_start_matches('[').trim_end_matches(']').to_string();
    let port = uri.port_u16().unwrap_or(if tls { 443 } else { 80 });
    let bridge = std::rc::Rc::new(desktop_mcp_core::http_bridge::HttpBridge::new(target, desktop_mcp_macos::nw::NwConnector { host, port, tls }));
    main_loop::run(|main_loop| async move {
        // The bridge's GET listener waits a second between attempts on a tokio timer.
        let activity = desktop_mcp_core::guest::Activity::new(main_loop);
        let _alive = activity.connection();
        desktop_mcp_core::http_bridge::run_http_bridge(FdIo::stdio(), bridge).await;
    });
    ExitCode::SUCCESS
}

#[cfg(not(target_os = "macos"))]
fn installed_arguments(_plist: &std::path::Path) -> Result<Vec<String>, String> {
    Err("this platform is not supported yet".into())
}

#[cfg(not(target_os = "macos"))]
fn run_connect(_route: Route) -> ExitCode {
    eprintln!("Error: this platform is not supported yet");
    ExitCode::FAILURE
}

/// The command line, the environment and `config.toml`, merged.
fn plan(args: &ServeArgs) -> Result<ServePlan, String> {
    let config = Config::load(Config::discover(args.config.as_deref(), &home()).as_deref())?;
    ServePlan::resolve(&args.command_line(), &std::env::vars().collect(), &config)
}

/// The tools this build has.
const TOOLS: &[&str] = &["Clipboard", "Open", "Scrape", "Shell", "Wait"];

/// The name of the Launch Agent plist's `Sockets` entry that `install` writes.
const LAUNCHD_SOCKET: &str = "Listener";

#[cfg(target_os = "macos")]
fn serve(plan: ServePlan) -> ExitCode {
    use desktop_mcp_core::guest::Activity;
    use desktop_mcp_core::http::{self, App, Security};
    use desktop_mcp_core::io::Listener;
    use desktop_mcp_core::mcp::{Desktop, Server};
    use desktop_mcp_core::oauth::OAuth;
    use desktop_mcp_core::session::{SessionSettings, Sessions};
    use desktop_mcp_core::stream::serve_stream;
    use desktop_mcp_macos::fd::{FdIo, UnixListener};
    use desktop_mcp_macos::{desktop::MacDesktop, main_loop, nw::NwListener, signals, tls::TlsIdentity};
    use std::rc::Rc;

    // A write to a closed stdout or socket must fail with EPIPE, not end the process.
    unsafe { libc::signal(libc::SIGPIPE, libc::SIG_IGN) };
    let tls = match &plan.tls {
        Some(tls) => match TlsIdentity::load(tls.certificate.as_ref(), tls.private_key.as_ref()) {
            Ok(identity) => Some(Rc::new(identity)),
            Err(reason) => {
                eprintln!("Error: {reason}");
                return ExitCode::FAILURE;
            }
        },
        None => None,
    };
    let tools: Vec<&'static str> = TOOLS.iter().copied().filter(|tool| !plan.excluded_tools.iter().any(|excluded| excluded == tool)).collect();
    let desktop: Rc<dyn Desktop> = Rc::new(MacDesktop::new());
    let make_server: Rc<dyn Fn() -> Server> = Rc::new(move || Server::new(desktop.clone(), &tools));

    // One root future; the process exits by returning from it.
    main_loop::run(|main_loop| async move {
        let activity = Activity::new(main_loop);
        if plan.transport == "stdio" {
            let _session = activity.connection();
            tokio::select! {
                _ = serve_stream(FdIo::stdio(), make_server()) => {}
                _ = signals::termination() => {}
            }
            return ExitCode::SUCCESS;
        }

        let listener = match NwListener::bind_with(&plan.host, plan.port, tls).await {
            Ok(listener) => listener,
            Err(reason) => {
                eprintln!("Error: cannot serve on {}:{}: {reason}", plan.host, plan.port);
                return ExitCode::FAILURE;
            }
        };
        log(&format!("Serving streamable-http on {}://{}:{}/mcp (rust)", plan.scheme(), plan.host, listener.port()));
        // The socket is beside HTTP, not in its way: when it cannot be had, HTTP is served alone.
        if let Some(path) = &plan.socket_path {
            match UnixListener::listen(path.as_ref(), Some(LAUNCHD_SOCKET)) {
                Ok(mut socket) => {
                    log(&format!("Serving stdio over {path}{}", if socket.activated_by_launchd() { " (socket from launchd)" } else { "" }));
                    let (make_server, activity) = (make_server.clone(), activity.clone());
                    tokio::task::spawn_local(async move {
                        while let Some(io) = socket.accept().await {
                            let (server, connection) = (make_server(), activity.connection());
                            tokio::task::spawn_local(async move {
                                serve_stream(io, server).await;
                                drop(connection);
                            });
                        }
                    });
                }
                Err(reason) => log(&format!("Socket {path}: {reason}; serving HTTP only")),
            }
        }
        let security = Security {
            auth_key: plan.auth_key.clone(),
            ip_allowlist: plan.ip_allowlist.clone(),
            oauth: plan.oauth.as_ref().map(|client| Rc::new(OAuth::new(&format!("{}://{}:{}", plan.scheme(), plan.host, plan.port), &client.client_id, &client.client_secret))),
        };
        let app = Rc::new(App {
            make_server: Box::new(move || make_server()),
            activity,
            sessions: Sessions::new(SessionSettings { idle_timeout: plan.session_timeout, ..SessionSettings::default() }),
            security,
        });
        tokio::select! {
            _ = http::serve(listener, app) => {}
            _ = signals::termination() => {}
        }
        ExitCode::SUCCESS
    })
}

#[cfg(not(target_os = "macos"))]
fn serve(_plan: ServePlan) -> ExitCode {
    eprintln!("Error: this platform is not supported yet");
    ExitCode::FAILURE
}
