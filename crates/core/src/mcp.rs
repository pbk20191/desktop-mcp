//! The MCP server: the tools, declared with rmcp's macros, over a platform's [`Desktop`].
use rmcp::handler::server::router::tool::ToolRouter;
use rmcp::handler::server::wrapper::Parameters;
use rmcp::model::{CallToolResult, ClientJsonRpcMessage, ContentBlock, Implementation, JsonRpcMessage, ServerCapabilities, ServerConfig, ServerJsonRpcMessage};
use rmcp::transport::OneshotTransport;
use crate::clipboard::{Clipboard, ClipboardWrite};
use crate::html::Element;
use crate::open::{Application, Target as OpenTarget};
use crate::shortcuts::ShortcutRun;
use crate::spotlight::{Found, Kind as SearchKind, Search};
use std::path::Path;
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
    /// The clipboard's text, file paths and image (as PNG scaled to at most `image_max_side`).
    fn read_clipboard(&self, image_max_side: u32) -> Pin<Box<dyn Future<Output = Clipboard>>>;
    /// Replaces the clipboard's contents.
    fn write_clipboard(&self, contents: ClipboardWrite) -> Pin<Box<dyn Future<Output = Result<(), String>>>>;
    /// Opens a URL or file with its default application, or with the application at `app`, bringing it forward.
    fn open(&self, target: &OpenTarget, app: Option<&Path>) -> Pin<Box<dyn Future<Output = Result<(), String>>>>;
    /// Shows `path` selected in the file manager.
    fn reveal(&self, path: &Path) -> Pin<Box<dyn Future<Output = ()>>>;
    /// The application with this bundle id or name.
    fn find_application(&self, name_or_bundle_id: &str) -> Pin<Box<dyn Future<Output = Option<Application>>>>;
    /// Posts a notification banner. `Err` carries the system's reason.
    fn notify(&self, message: &str, title: &str, subtitle: Option<&str>, sound: Option<&str>) -> Pin<Box<dyn Future<Output = Result<(), String>>>>;
    /// The `shortcuts list` run: one name per line on stdout.
    fn list_shortcuts(&self, folder: Option<&str>, timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>>;
    /// Runs the shortcut named exactly `name`, with `input` as its input text. `Err` when it cannot start.
    fn run_shortcut(&self, name: &str, input: Option<&str>, timeout: Duration) -> Pin<Box<dyn Future<Output = Result<ShortcutRun, String>>>>;
    /// Searches the file index. `Err` when the platform rejects the query.
    fn search_files(&self, search: Search) -> Pin<Box<dyn Future<Output = Result<Found, String>>>>;
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

/// What a tool answers: one text, or several blocks.
struct Content(Vec<ContentBlock>);

impl From<String> for Content {
    fn from(text: String) -> Content {
        Content(vec![ContentBlock::text(text)])
    }
}

impl From<Vec<ContentBlock>> for Content {
    fn from(blocks: Vec<ContentBlock>) -> Content {
        Content(blocks)
    }
}

/// Runs a tool's work until it finishes or its request is cancelled (`notifications/cancelled`, or the client
/// went away), and turns the outcome into the result the model sees. Cancellation drops the work on the spot,
/// so whatever it holds (a child process, a timer) is released then.
async fn run_tool<T: Into<Content>>(tool: &str, cancellation: CancellationToken, work: impl Future<Output = Result<T, ToolError>>) -> CallToolResult {
    tokio::select! {
        outcome = work => match outcome {
            Ok(content) => CallToolResult::success(content.into().0),
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

#[derive(Clone, Copy, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
enum ClipboardMode {
    #[default]
    Read,
    Write,
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct ClipboardArgs {
    #[serde(default)]
    mode: ClipboardMode,
    text: Option<String>,
    paths: Option<Vec<String>>,
}

#[derive(Clone, Copy, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
enum OpenMode {
    #[default]
    Open,
    Reveal,
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct OpenArgs {
    /// Absolute path, ~-path, or URL
    target: String,
    #[serde(default)]
    mode: OpenMode,
    app: Option<String>,
}

#[derive(Clone, Copy, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
enum NotificationSound {
    Glass,
    Ping,
    Basso,
    Tink,
    Funk,
    Bottle,
    Frog,
    Hero,
    Morse,
    Pop,
    Purr,
    Sosumi,
    Submarine,
}

fn notification() -> String {
    "Notification".to_string()
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct NotificationArgs {
    message: String,
    #[serde(default = "notification")]
    title: String,
    subtitle: Option<String>,
    sound: Option<NotificationSound>,
}

#[derive(Clone, Copy, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
enum ShortcutMode {
    #[default]
    Run,
    List,
}

fn sixty() -> i64 {
    60
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct RunShortcutArgs {
    #[serde(default)]
    mode: ShortcutMode,
    name: Option<String>,
    input: Option<String>,
    folder: Option<String>,
    #[serde(default = "sixty")]
    timeout: i64,
}

fn twenty() -> i64 {
    20
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct SpotlightArgs {
    query: String,
    #[serde(default)]
    raw: bool,
    scope: Option<Vec<String>>,
    #[serde(default)]
    kind: SearchKind,
    #[serde(default = "twenty")]
    limit: i64,
    #[serde(default)]
    content: bool,
}

#[tool_router]
impl Server {
    #[tool(
        name = "Spotlight",
        description = "Searches the Spotlight index for files. By default 'query' is words that must each appear in the file name or text, name matches first, then content-only matches; raw=true passes 'query' as a Spotlight query (e.g. kMDItemContentModificationDate > $time.today(-7)). 'scope' is a list of folders (default: home; kind=application without scope searches the app folders /Applications, /System/Applications, ~/Applications). 'kind' narrows the type. Returns up to 'limit' (max 200) lines '<path>\\t<kind>\\t<modified>\\t<size>', newest first within each group; content=true adds the first 300 characters of each plain-text file.",
        annotations(title = "Spotlight", read_only_hint = true, destructive_hint = false, idempotent_hint = true, open_world_hint = false)
    )]
    async fn spotlight(&self, Parameters(args): Parameters<SpotlightArgs>, cancellation: CancellationToken) -> CallToolResult {
        let request = crate::spotlight::Request { query: args.query, raw: args.raw, kind: args.kind, scope: args.scope.unwrap_or_default(), limit: args.limit, content: args.content };
        run_tool("Spotlight", cancellation, async move { crate::spotlight::run(self.desktop.as_ref(), request).await.map_err(ToolError) }).await
    }

    #[tool(
        name = "RunShortcut",
        description = "Lists or runs the user's Shortcuts (the Shortcuts app, not keyboard shortcuts). mode='list' returns the shortcut names, optionally only those in 'folder'. mode='run' runs the shortcut 'name' (exact name), passing 'input' text to it, and returns its output.",
        annotations(title = "RunShortcut", read_only_hint = false, destructive_hint = true, idempotent_hint = false, open_world_hint = true)
    )]
    async fn run_shortcut(&self, Parameters(args): Parameters<RunShortcutArgs>, cancellation: CancellationToken) -> CallToolResult {
        let desktop = self.desktop.as_ref();
        run_tool("RunShortcut", cancellation, async move {
            let timeout = crate::shortcuts::timeout(args.timeout);
            match args.mode {
                ShortcutMode::List => crate::shortcuts::list(desktop, args.folder.as_deref(), timeout).await.map(|names| names.join("\n")),
                ShortcutMode::Run => match args.name.filter(|name| !name.is_empty()) {
                    Some(name) => crate::shortcuts::run(desktop, &name, args.input.as_deref(), timeout).await,
                    None => Err("name is required for run.".to_string()),
                },
            }
            .map_err(ToolError)
        })
        .await
    }

    #[tool(
        name = "Notification",
        description = "Sends a macOS notification banner with a message, title, optional subtitle, and optional sound.",
        annotations(title = "Notification", read_only_hint = false, destructive_hint = false, idempotent_hint = false, open_world_hint = false)
    )]
    async fn notification(&self, Parameters(args): Parameters<NotificationArgs>, cancellation: CancellationToken) -> CallToolResult {
        run_tool("Notification", cancellation, async move {
            let sound = args.sound.map(|sound| serde_json::to_value(sound).ok().and_then(|v| v.as_str().map(str::to_string)).unwrap_or_default());
            let subtitle = args.subtitle.filter(|subtitle| !subtitle.is_empty());
            Ok(match self.desktop.notify(&args.message, &args.title, subtitle.as_deref(), sound.as_deref()).await {
                Ok(()) => format!("Notification sent: [{}] {}", args.title, args.message),
                Err(reason) => format!("Failed to send notification: {reason}"),
            })
        })
        .await
    }

    #[tool(
        name = "Open",
        description = "Opens a file, folder or URL with the default application, or with the application given by 'app' (name or bundle id). mode='reveal' shows an existing file or folder in Finder instead.",
        annotations(title = "Open", read_only_hint = false, destructive_hint = false, idempotent_hint = false, open_world_hint = true)
    )]
    async fn open(&self, Parameters(args): Parameters<OpenArgs>, cancellation: CancellationToken) -> CallToolResult {
        let reveal = args.mode == OpenMode::Reveal;
        run_tool("Open", cancellation, async move { crate::open::run(self.desktop.as_ref(), &args.target, reveal, args.app.as_deref()).await.map_err(ToolError) }).await
    }

    #[tool(
        name = "Clipboard",
        description = "Reads or writes the clipboard. mode='read' (default) returns the copied text, file paths and image; mode='write' replaces the clipboard with 'text' or with the files in 'paths' (which must exist), not both.",
        annotations(title = "Clipboard", read_only_hint = false, destructive_hint = true, idempotent_hint = false, open_world_hint = false)
    )]
    async fn clipboard(&self, Parameters(args): Parameters<ClipboardArgs>, cancellation: CancellationToken) -> CallToolResult {
        let desktop = self.desktop.as_ref();
        run_tool("Clipboard", cancellation, async move {
            match args.mode {
                ClipboardMode::Read => Ok(crate::clipboard::read(desktop).await),
                ClipboardMode::Write => crate::clipboard::write(desktop, args.text, args.paths).await.map(|text| vec![ContentBlock::text(text)]).map_err(ToolError),
            }
        })
        .await
    }

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
