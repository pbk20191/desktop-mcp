//! The `desktop-mcp` command.
use clap::{Args, Parser, Subcommand, ValueEnum};
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

#[derive(Args, Clone)]
struct ServeArgs {
    #[arg(long, value_enum, default_value_t = Transport::Stdio)]
    transport: Transport,
    /// Host to bind the Streamable HTTP server.
    #[arg(long, default_value = "localhost")]
    host: String,
    /// Port to bind the Streamable HTTP server.
    #[arg(long, default_value_t = 8000)]
    port: u16,
    /// Seconds a session may stay idle before it is dropped (0 = never).
    #[arg(long, default_value_t = 1800)]
    session_timeout: u64,
}

#[derive(Clone, Copy, PartialEq, Eq, ValueEnum)]
enum Transport {
    Stdio,
    StreamableHttp,
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    let args = match cli.command {
        Some(Command::Serve(args)) => args,
        None => cli.serve,
    };
    if args.transport == Transport::Stdio {
        eprintln!("stdio is not available in this build yet");
        return ExitCode::from(2);
    }
    serve(args)
}

/// The tools this build serves.
const TOOLS: &[&str] = &["Wait"];

#[cfg(target_os = "macos")]
fn serve(args: ServeArgs) -> ExitCode {
    use desktop_mcp_core::guest::Activity;
    use desktop_mcp_core::http::{self, App};
    use desktop_mcp_core::mcp::{Desktop, Server};
    use desktop_mcp_core::session::{SessionSettings, Sessions};
    use desktop_mcp_macos::{desktop::MacDesktop, main_loop, nw::NwListener, signals};
    use std::rc::Rc;

    // One root future; the process exits by returning from it.
    main_loop::run(|main_loop| async move {
        let listener = match NwListener::bind(&args.host, args.port).await {
            Ok(listener) => listener,
            Err(reason) => {
                eprintln!("desktop-mcp: cannot serve on {}:{}: {reason}", args.host, args.port);
                return ExitCode::FAILURE;
            }
        };
        eprintln!("desktop-mcp (rust) listening on http://{}:{}/mcp", args.host, listener.port());
        let desktop: Rc<dyn Desktop> = Rc::new(MacDesktop::new());
        let idle_timeout = (args.session_timeout > 0).then(|| std::time::Duration::from_secs(args.session_timeout));
        let app = Rc::new(App {
            make_server: Box::new(move || Server::new(desktop.clone(), TOOLS)),
            activity: Activity::new(main_loop),
            sessions: Sessions::new(SessionSettings { idle_timeout, ..SessionSettings::default() }),
        });
        tokio::select! {
            _ = http::serve(listener, app) => {}
            _ = signals::termination() => {}
        }
        ExitCode::SUCCESS
    })
}

#[cfg(not(target_os = "macos"))]
fn serve(_args: ServeArgs) -> ExitCode {
    eprintln!("desktop-mcp: this platform is not supported yet");
    ExitCode::FAILURE
}
