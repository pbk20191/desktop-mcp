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
use crate::permissions::PermissionState;
use crate::wait::RunningApp;
use crate::input::{Button, Caret, Event as InputEvent};
use crate::app::{FrameChange, Mode as AppMode};
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
    /// The grants as they are now. Never prompts.
    fn permissions(&self) -> Pin<Box<dyn Future<Output = PermissionState>>>;
    /// The applications running now.
    fn running_apps(&self) -> Pin<Box<dyn Future<Output = Vec<RunningApp>>>>;
    /// Whether Accessibility is granted. Never prompts.
    fn accessibility_trusted(&self) -> bool;
    /// Posts one input event.
    fn post(&self, event: InputEvent);
    /// Where the pointer is, in input points.
    fn cursor_position(&self) -> (f64, f64);
    /// Whether keys typed now go to a password field: the focused element, or the one at the point.
    fn secure_input_at(&self, x: i64, y: i64) -> Pin<Box<dyn Future<Output = bool>>>;
    /// Selects a keyboard layout that passes Unicode events through when the current input source would compose
    /// them; returns what puts the previous one back, or `None` when nothing was switched.
    fn select_typing_layout(&self) -> Option<Box<dyn FnOnce()>>;
    /// Opens an application by name, path or bundle id; whether it worked.
    fn launch_app(&self, name: &str) -> Pin<Box<dyn Future<Output = bool>>>;
    /// Brings the application with this name or bundle id forward; `false` when none is running.
    fn activate_app(&self, name: &str) -> Pin<Box<dyn Future<Output = bool>>>;
    /// Applies `change` to the frontmost app's main window (or with `None` only looks); whether there is one.
    fn change_front_window(&self, change: Option<FrameChange>) -> Pin<Box<dyn Future<Output = bool>>>;
    /// Asks these processes to quit, or with `force` terminates them; whether any was signalled.
    fn terminate(&self, pids: &[i32], force: bool) -> Pin<Box<dyn Future<Output = bool>>>;
    /// The application windows and the actions on them.
    fn window_source(&self) -> Rc<dyn crate::window::WindowSource>;
    /// Lists or selects in the menu bar of `app` (name or bundle id), or of the app that owns the menu bar.
    fn menu(&self, app: Option<String>, request: crate::menu::Request) -> Pin<Box<dyn Future<Output = Result<String, String>>>>;
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

#[allow(dead_code)] // `title` and `role` belong to the conditions that are not ported yet
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

const LOCATION: &str = "Location must be a list of exactly 2 integers [x, y]";

/// `loc` as a point, or `message` when it is not two numbers.
fn point(loc: &[i64], message: &str) -> Result<(i64, i64), ToolError> {
    match loc {
        [x, y] => Ok((*x, *y)),
        _ => Err(ToolError(message.to_string())),
    }
}

fn one() -> i64 {
    1
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct ClickArgs {
    loc: Vec<i64>,
    #[serde(default)]
    button: Button,
    #[serde(default = "one")]
    clicks: i64,
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct TypeArgs {
    loc: Vec<i64>,
    text: String,
    #[serde(default)]
    clear: bool,
    #[serde(default)]
    caret_position: Caret,
    #[serde(default)]
    press_enter: bool,
}

#[derive(Clone, Copy, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
enum ScrollType {
    Horizontal,
    #[default]
    Vertical,
}

#[derive(Clone, Copy, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
enum ScrollDirection {
    Up,
    #[default]
    Down,
    Left,
    Right,
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct ScrollArgs {
    loc: Option<Vec<i64>>,
    #[serde(default, rename = "type")]
    kind: ScrollType,
    #[serde(default)]
    direction: ScrollDirection,
    #[serde(default = "one")]
    wheel_times: i64,
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct MoveArgs {
    loc: Vec<i64>,
    #[serde(default)]
    drag: bool,
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct ShortcutArgs {
    shortcut: String,
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct AppArgs {
    #[serde(default)]
    mode: AppMode,
    name: Option<String>,
    window_loc: Option<Vec<i64>>,
    window_size: Option<Vec<i64>>,
}

fn two() -> i64 {
    2
}

#[derive(serde::Deserialize, schemars::JsonSchema)]
struct MenuArgs {
    #[serde(default)]
    mode: crate::menu::Mode,
    app: Option<String>,
    path: Option<Vec<String>>,
    #[serde(default = "two")]
    depth: i64,
}

#[tool_router]
impl Server {
    #[tool(
        name = "Menu",
        description = "Reads and chooses commands from an app's menu bar ('app': name or bundle id; default the frontmost app). 'list' (default) returns an indented tree of item titles with keyboard shortcuts and [disabled], 'depth' levels deep (default 2, max 4), below the optional 'path' prefix. 'select' presses the item 'path' names from the bar item down, e.g. [\"File\", \"Export…\"]: titles match exactly, then ignoring case, then also treating '...' as '…'; the Apple menu is \"Apple\". A disabled item is not pressed.",
        annotations(title = "Menu", read_only_hint = false, destructive_hint = true, idempotent_hint = false, open_world_hint = false)
    )]
    async fn menu(&self, Parameters(args): Parameters<MenuArgs>, cancellation: CancellationToken) -> CallToolResult {
        run_tool("Menu", cancellation, async move {
            crate::menu::run(self.desktop.as_ref(), args.mode, args.app, args.path.unwrap_or_default(), args.depth).await.map_err(ToolError)
        })
        .await
    }

    #[tool(
        name = "Window",
        description = "Lists and arranges application windows. 'list' (default) returns one line per window: '<id>\\t<app>\\t\"<title>\"\\t[x,y,w,h]\\t<flags>' (flags: focused, minimized, fullscreen), optionally only for 'app' (name or bundle id). The other modes act on the window 'id' from list: 'focus', 'move' (to 'loc' [x, y]), 'resize' (to 'size' [width, height]), 'minimize', 'restore', 'close' (presses its close button; the app may ask to save), 'maximize' (fills its display minus the menu bar and Dock), 'fullscreen', 'exit_fullscreen', 'display' (moves it to display index 'display', same relative position, kept inside that display). 'displays' (no id) returns one line per display: '<index>\\t<name>\\t[x,y,w,h]\\tvisible [x,y,w,h]\\tscale <s>', then 'main' for the main one, in the same top-left coordinates as window frames. 'maximize' and 'display' refuse a window in full screen or minimized. A window that closed since it was listed is refused; list again. An index id (no 'w') must also be listed again after 'fullscreen' or 'exit_fullscreen'.",
        annotations(title = "Window", read_only_hint = false, destructive_hint = true, idempotent_hint = false, open_world_hint = false)
    )]
    async fn window(&self, Parameters(args): Parameters<crate::window::Arguments>, cancellation: CancellationToken) -> CallToolResult {
        let source = self.desktop.window_source();
        let trusted = self.desktop.accessibility_trusted();
        let cache = crate::window::CACHE.with(Rc::clone);
        run_tool("Window", cancellation, async move { crate::window::run(source.as_ref(), &cache, trusted, args).await.map_err(ToolError) }).await
    }

    #[tool(
        name = "App",
        description = "Manages macOS applications with six modes: 'launch' (opens the application), 'resize' (adjusts active window size), 'move' (repositions active window), 'switch' (brings specific app into focus), 'quit' (asks every process with that name or bundle id to quit; the app may ask to save), 'force_quit' (terminates them without asking). Quit never targets desktop-mcp itself or loginwindow.",
        annotations(title = "App", read_only_hint = false, destructive_hint = true, idempotent_hint = false, open_world_hint = false)
    )]
    async fn app(&self, Parameters(args): Parameters<AppArgs>, cancellation: CancellationToken) -> CallToolResult {
        run_tool("App", cancellation, async move {
            crate::app::run(self.desktop.as_ref(), args.mode, args.name.as_deref(), args.window_loc.as_deref(), args.window_size.as_deref()).await.map_err(ToolError)
        })
        .await
    }

    #[tool(
        name = "Click",
        description = "Performs mouse clicks at specified coordinates [x, y]. Supports button types: 'left' for selection/activation, 'right' for context menus, 'middle'. Supports clicks: 0=hover only, 1=single click, 2=double click.",
        annotations(title = "Click", read_only_hint = false, destructive_hint = true, idempotent_hint = false, open_world_hint = false)
    )]
    async fn click(&self, Parameters(args): Parameters<ClickArgs>, cancellation: CancellationToken) -> CallToolResult {
        let desktop = self.desktop.as_ref();
        run_tool("Click", cancellation, async move {
            let (x, y) = point(&args.loc, LOCATION)?;
            crate::input::require_accessibility(desktop).map_err(ToolError)?;
            if args.clicks == 0 {
                crate::input::move_to(desktop, x, y);
            } else {
                crate::input::click(desktop, x, y, args.button, args.clicks).await;
            }
            let label = match args.clicks {
                0 => "Hover",
                1 => "Single",
                2 => "Double",
                _ => "None",
            };
            Ok(format!("{label} {} clicked at ({x},{y}).", args.button.name()))
        })
        .await
    }

    #[tool(
        name = "Type",
        description = "Types text at specified coordinates [x, y]. Set clear=True to clear existing text first. Set press_enter=True to submit after typing. Set caret_position to 'start', 'end', or 'idle' (default).",
        annotations(title = "Type", read_only_hint = false, destructive_hint = true, idempotent_hint = false, open_world_hint = false)
    )]
    async fn type_text(&self, Parameters(args): Parameters<TypeArgs>, cancellation: CancellationToken) -> CallToolResult {
        let desktop = self.desktop.as_ref();
        run_tool("Type", cancellation, async move {
            let (x, y) = point(&args.loc, LOCATION)?;
            crate::input::require_accessibility(desktop).map_err(ToolError)?;
            // Checked after the click and before typing: the text of a password field never comes back.
            if crate::input::type_at(desktop, x, y, &args.text, args.caret_position, args.clear, args.press_enter).await {
                return Ok(format!("Typed {} characters into a secure field at ({x},{y}).", args.text.chars().count()));
            }
            Ok(format!("Typed {} at ({x},{y}).", args.text))
        })
        .await
    }

    #[tool(
        name = "Scroll",
        description = "Scrolls at coordinates [x, y] or current mouse position if loc=None. Type: vertical (default) or horizontal. Direction: up/down for vertical, left/right for horizontal. wheel_times controls scroll amount.",
        annotations(title = "Scroll", read_only_hint = false, destructive_hint = false, idempotent_hint = true, open_world_hint = false)
    )]
    async fn scroll(&self, Parameters(args): Parameters<ScrollArgs>, cancellation: CancellationToken) -> CallToolResult {
        let desktop = self.desktop.as_ref();
        run_tool("Scroll", cancellation, async move {
            let at = args.loc.as_deref().map(|loc| point(loc, LOCATION)).transpose()?;
            crate::input::require_accessibility(desktop).map_err(ToolError)?;
            let kind = if args.kind == ScrollType::Horizontal { "horizontal" } else { "vertical" };
            let direction = serde_json::to_value(args.direction).ok().and_then(|v| v.as_str().map(str::to_string)).unwrap_or_default();
            if let Some(message) = crate::input::scroll(desktop, at, args.kind == ScrollType::Horizontal, &direction, args.wheel_times).await {
                return Ok(message);
            }
            let at = at.map(|(x, y)| format!(" at ({x},{y}).")).unwrap_or_else(|| ".".to_string());
            Ok(format!("Scrolled {kind} {direction} by {} wheel times{at}", args.wheel_times))
        })
        .await
    }

    #[tool(
        name = "Move",
        description = "Moves mouse cursor to coordinates [x, y]. Set drag=True to perform a drag-and-drop operation from current position to target coordinates.",
        annotations(title = "Move", read_only_hint = false, destructive_hint = false, idempotent_hint = true, open_world_hint = false)
    )]
    async fn move_pointer(&self, Parameters(args): Parameters<MoveArgs>, cancellation: CancellationToken) -> CallToolResult {
        let desktop = self.desktop.as_ref();
        run_tool("Move", cancellation, async move {
            let (x, y) = point(&args.loc, "loc must be a list of exactly 2 integers [x, y]")?;
            crate::input::require_accessibility(desktop).map_err(ToolError)?;
            if args.drag {
                crate::input::drag(desktop, x, y).await;
                return Ok(format!("Dragged to ({x},{y})."));
            }
            crate::input::move_to(desktop, x, y);
            Ok(format!("Moved the mouse pointer to ({x},{y})."))
        })
        .await
    }

    #[tool(
        name = "Shortcut",
        description = "Executes keyboard shortcuts using key combinations separated by +. Examples: \"command+c\" (copy), \"command+v\" (paste), \"command+tab\" (switch apps), \"command+space\" (Spotlight). Note: Use \"command\" instead of \"ctrl\" on macOS.",
        annotations(title = "Shortcut", read_only_hint = false, destructive_hint = true, idempotent_hint = false, open_world_hint = false)
    )]
    async fn shortcut(&self, Parameters(args): Parameters<ShortcutArgs>, cancellation: CancellationToken) -> CallToolResult {
        let desktop = self.desktop.as_ref();
        run_tool("Shortcut", cancellation, async move {
            crate::input::require_accessibility(desktop).map_err(ToolError)?;
            crate::input::shortcut(desktop, &args.shortcut).await.map_err(ToolError)?;
            Ok(format!("Pressed {}.", args.shortcut))
        })
        .await
    }

    #[tool(
        name = "Permissions",
        description = "Reports which macOS permissions desktop-mcp holds: Accessibility, Screen Recording, and Automation of System Events (granted, not granted or not determined), and which tools need each. Never prompts. A tool that needs a missing permission fails with a message saying how to grant it.",
        annotations(title = "Permissions", read_only_hint = true, destructive_hint = false, idempotent_hint = true, open_world_hint = false)
    )]
    async fn permissions(&self, cancellation: CancellationToken) -> CallToolResult {
        run_tool("Permissions", cancellation, async { Ok(crate::permissions::format(self.desktop.permissions().await)) }).await
    }

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
        match args.until {
            Some(WaitUntil::AppRunning) => return crate::wait::until_app(self.desktop.as_ref(), args.app.as_deref(), true, args.timeout).await.map_err(ToolError),
            Some(WaitUntil::AppQuit) => return crate::wait::until_app(self.desktop.as_ref(), args.app.as_deref(), false, args.timeout).await.map_err(ToolError),
            Some(until) => return Err(ToolError(format!("Wait until '{}' is not available in this build.", until.name()))),
            None => {}
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
