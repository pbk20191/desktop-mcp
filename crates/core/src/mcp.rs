//! The MCP server: the tools, declared with rmcp's macros, over a platform's [`Desktop`].
use rmcp::handler::server::router::tool::ToolRouter;
use rmcp::handler::server::wrapper::Parameters;
use rmcp::model::{CallToolResult, ClientJsonRpcMessage, ContentBlock, Implementation, JsonRpcMessage, ServerCapabilities, ServerConfig, ServerJsonRpcMessage};
use rmcp::transport::OneshotTransport;
use crate::html::Element;
use crate::io::Stream;
use crate::shell::{self, Run, ShellMode};
use std::net::IpAddr;
use rmcp::{schemars, tool, tool_handler, tool_router, RoleServer, ServerHandler};
use std::future::Future;
use std::pin::Pin;
use std::rc::Rc;
use std::time::Duration;
use tokio_util::sync::CancellationToken;

/// The tools' behaviour, implemented per platform. Lives on the main thread and is not `Send`.
pub trait Desktop: 'static {
    /// Completes after `duration`, measured exactly (not by the guest runtime's coarse timers).
    fn sleep(&self, duration: Duration) -> Pin<Box<dyn Future<Output = ()>>>;
    /// Runs `command` in `mode`'s interpreter with no stdin, killing it (and whatever it started) after
    /// `timeout` or when the future is dropped. `Err` when it cannot start.
    fn shell(&self, command: &str, mode: ShellMode, timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>>;
    /// The addresses `host` (a name or a numeric address) resolves to, or the resolver's reason for none.
    fn resolve(&self, host: &str) -> Pin<Box<dyn Future<Output = Result<Vec<IpAddr>, String>>>>;
    /// A connection to `host`:`port`; with `tls`, over TLS that trusts what the system trusts.
    fn connect(&self, host: &str, port: u16, tls: bool) -> Pin<Box<dyn Future<Output = Result<Box<dyn Stream>, String>>>>;
    /// An HTML page's `html` element, parsed the way a browser would after decoding `body` from `charset` (UTF-8
    /// when absent or unknown). `None` when nothing could be parsed.
    fn parse_html(&self, body: &[u8], charset: Option<&str>) -> Option<Element>;
}

/// One MCP server. Stateless requests get a fresh one each; a session keeps one.
pub struct Server {
    tool_router: ToolRouter<Self>,
    desktop: Rc<dyn Desktop>,
}

impl Server {
    /// A server offering the tools named in `tools`.
    pub fn new(desktop: Rc<dyn Desktop>, tools: &[&str]) -> Server {
        let mut tool_router = Self::tool_router();
        for tool in tool_router.list_all() {
            if !tools.contains(&tool.name.as_ref()) {
                tool_router.remove_route(&tool.name);
            }
        }
        Server { tool_router, desktop }
    }
}

/// A tool's failure, shown to the model as an error result (not a protocol error).
struct ToolError(String);

/// Runs a tool's work until it finishes or its request is cancelled (`notifications/cancelled`, or the client
/// went away), and turns the outcome into the result the model sees. Cancellation drops the work on the spot,
/// so whatever it holds (a child process, a timer) is released then.
async fn run_tool(tool: &str, cancellation: CancellationToken, work: impl Future<Output = Result<String, ToolError>>) -> CallToolResult {
    tokio::select! {
        outcome = work => match outcome {
            Ok(text) => CallToolResult::success(vec![ContentBlock::text(text)]),
            Err(ToolError(message)) => CallToolResult::error(vec![ContentBlock::text(format!("Error executing tool {tool}: {message}"))]),
        },
        _ = cancellation.cancelled() => CallToolResult::error(vec![ContentBlock::text("Cancelled.")]),
    }
}

#[derive(Clone, Copy, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
enum WaitUntil {
    AppRunning,
    AppQuit,
    Window,
    WindowGone,
    Element,
}

impl WaitUntil {
    fn name(self) -> &'static str {
        match self {
            WaitUntil::AppRunning => "app_running",
            WaitUntil::AppQuit => "app_quit",
            WaitUntil::Window => "window",
            WaitUntil::WindowGone => "window_gone",
            WaitUntil::Element => "element",
        }
    }
}

fn ten() -> i64 {
    10
}

#[allow(dead_code)] // `timeout`, `app`, `title` and `role` belong to the conditions that are not ported yet
#[derive(serde::Deserialize, schemars::JsonSchema)]
struct WaitArgs {
    duration: Option<i64>,
    until: Option<WaitUntil>,
    #[serde(default = "ten")]
    timeout: i64,
    app: Option<String>,
    title: Option<String>,
    role: Option<String>,
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct ShellArgs {
    command: String,
    #[serde(default)]
    mode: ShellMode,
    #[serde(default = "ten")]
    timeout: i64,
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct ScrapeArgs {
    url: String,
}

#[tool_router]
impl Server {
    #[tool(
        name = "Scrape",
        description = "Fetch content from a URL. Performs a lightweight HTTP request and returns the page content as text.",
        annotations(title = "Scrape", read_only_hint = true, destructive_hint = false, idempotent_hint = true, open_world_hint = true)
    )]
    async fn scrape(&self, Parameters(args): Parameters<ScrapeArgs>, cancellation: CancellationToken) -> CallToolResult {
        run_tool("Scrape", cancellation, async { crate::scrape::run(self.desktop.as_ref(), &args.url).await.map_err(ToolError) }).await
    }

    #[tool(
        name = "Shell",
        description = "Execute commands on macOS. Modes: 'shell' (default) for bash/zsh commands, 'osascript' for AppleScript. Use for file system, process management, system operations, and automation scripts. SECURITY WARNING: Commands are executed with the same permissions as the terminal/application running this server. Review and understand all actions before execution, especially those involving file operations, system modifications, or external processes.",
        annotations(title = "Shell", read_only_hint = false, destructive_hint = true, idempotent_hint = false, open_world_hint = true)
    )]
    async fn shell(&self, Parameters(args): Parameters<ShellArgs>, cancellation: CancellationToken) -> CallToolResult {
        run_tool("Shell", cancellation, async {
            let timeout = shell::clamp_timeout(args.timeout, std::env::var(shell::MAX_TIMEOUT_ENV).ok().as_deref());
            let run = self.desktop.shell(&args.command, args.mode, Duration::from_secs(timeout.max(0) as u64)).await;
            let (output, status) = shell::report(run, args.timeout, timeout);
            let label = if args.mode == ShellMode::Osascript { "AppleScript" } else { "Shell" };
            Ok(format!("{label} Response: {output}\nStatus Code: {status}"))
        })
        .await
    }

    #[tool(
        name = "Wait",
        description = "Waits a fixed 'duration' in seconds, or with 'until' waits for a condition, checking with backoff until it holds or 'timeout' seconds (default 10) pass, then fails. 'duration' is ignored with 'until'. Conditions: 'app_running' / 'app_quit' ('app': name or bundle id); 'window' / 'window_gone' ('app' and/or 'title', a case-insensitive substring; 'window' returns the matching Window list lines); 'element' ('title' and/or 'role', an exact AX role such as AXButton, in 'app' or else the app owning the menu bar; title matches title, description or value; returns '<role> \"<title>\" at [x,y]', the centre in Click coordinates). Window and element conditions need Accessibility.",
        annotations(title = "Wait", read_only_hint = true, destructive_hint = false, idempotent_hint = true, open_world_hint = false)
    )]
    async fn wait(&self, Parameters(args): Parameters<WaitArgs>, cancellation: CancellationToken) -> CallToolResult {
        run_tool("Wait", cancellation, self.wait_for(args)).await
    }
}

impl Server {
    async fn wait_for(&self, args: WaitArgs) -> Result<String, ToolError> {
        if let Some(until) = args.until {
            return Err(ToolError(format!("Wait until '{}' is not available in this build.", until.name())));
        }
        let duration = args.duration.ok_or_else(|| ToolError("Missing required argument 'duration'".to_string()))?;
        self.desktop.sleep(Duration::from_secs(duration.max(0) as u64)).await;
        Ok(format!("Waited for {duration} seconds."))
    }
}

#[tool_handler(router = self.tool_router)]
impl ServerHandler for Server {
    fn get_info(&self) -> ServerConfig {
        ServerConfig::new(ServerCapabilities::builder().enable_tools().build()).with_server_info(Implementation::new("desktop-mcp", env!("CARGO_PKG_VERSION")))
    }
}

/// Answers one JSON-RPC message with a fresh service, the way rmcp's own Streamable HTTP server implements
/// stateless mode: a one-message transport and no initialize handshake. `None` for notifications and responses,
/// which have nothing to act on without a session. Dropping the future ends the service.
pub async fn answer_stateless(server: Server, message: ClientJsonRpcMessage) -> Option<ServerJsonRpcMessage> {
    if !matches!(message, JsonRpcMessage::Request(_)) {
        return None;
    }
    let (transport, mut answers) = OneshotTransport::<RoleServer>::new(message);
    // Dropping this future (the client went away) drops the service, which cancels the request's token;
    // the tool watches that token (`run_tool`) and stops.
    let _service = rmcp::service::serve_directly(server, transport, None);
    answers.recv().await
}
