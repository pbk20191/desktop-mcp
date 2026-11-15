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
    if plan.transport == "stdio" {
        eprintln!("stdio is not available in this build yet");
        return ExitCode::from(2);
    }
    serve(plan)
}

/// The command line, the environment and `config.toml`, merged.
fn plan(args: &ServeArgs) -> Result<ServePlan, String> {
    let home = std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default();
    let config = Config::load(Config::discover(args.config.as_deref(), &home).as_deref())?;
    ServePlan::resolve(&args.command_line(), &std::env::vars().collect(), &config)
}

/// The tools this build has.
const TOOLS: &[&str] = &["Wait"];

#[cfg(target_os = "macos")]
fn serve(plan: ServePlan) -> ExitCode {
    use desktop_mcp_core::guest::Activity;
    use desktop_mcp_core::http::{self, App, Security};
    use desktop_mcp_core::mcp::{Desktop, Server};
    use desktop_mcp_core::oauth::OAuth;
    use desktop_mcp_core::session::{SessionSettings, Sessions};
    use desktop_mcp_macos::{desktop::MacDesktop, main_loop, nw::NwListener, signals, tls::TlsIdentity};
    use std::rc::Rc;

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
    let security = Security {
        auth_key: plan.auth_key.clone(),
        ip_allowlist: plan.ip_allowlist.clone(),
        oauth: plan.oauth.as_ref().map(|client| Rc::new(OAuth::new(&format!("{}://{}:{}", plan.scheme(), plan.host, plan.port), &client.client_id, &client.client_secret))),
    };
    // One root future; the process exits by returning from it.
    main_loop::run(|main_loop| async move {
        let listener = match NwListener::bind_with(&plan.host, plan.port, tls).await {
            Ok(listener) => listener,
            Err(reason) => {
                eprintln!("Error: cannot serve on {}:{}: {reason}", plan.host, plan.port);
                return ExitCode::FAILURE;
            }
        };
        log(&format!("Serving streamable-http on {}://{}:{}/mcp (rust)", plan.scheme(), plan.host, listener.port()));
        let desktop: Rc<dyn Desktop> = Rc::new(MacDesktop::new());
        let app = Rc::new(App {
            make_server: Box::new(move || Server::new(desktop.clone(), &tools)),
            activity: Activity::new(main_loop),
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
