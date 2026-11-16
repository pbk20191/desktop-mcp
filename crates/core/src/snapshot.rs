//! The Snapshot tool: the desktop as text (the focused window, the open applications, the interactive and
//! scrollable elements with ids and click coordinates) and, with `use_vision`, an annotated screenshot. The
//! platform reads the desktop; this module holds the shapes and the wording.
use crate::mcp::Desktop;

/// Whole points, as a snapshot reports them (Python's `BoundingBox`).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct BoundingBox {
    pub left: i64,
    pub top: i64,
    pub right: i64,
    pub bottom: i64,
    pub width: i64,
    pub height: i64,
}

impl BoundingBox {
    /// From a rectangle in points, each edge truncated toward zero.
    pub fn from_edges(left: f64, top: f64, right: f64, bottom: f64) -> BoundingBox {
        let whole = |v: f64| if v.is_finite() { v as i64 } else { 0 };
        BoundingBox { left: whole(left), top: whole(top), right: whole(right), bottom: whole(bottom), width: whole(right - left), height: whole(bottom - top) }
    }

    /// The centre, with Python's floor division.
    pub fn center(&self) -> (i64, i64) {
        (self.left + self.width.div_euclid(2), self.top + self.height.div_euclid(2))
    }

    /// The part of `other` inside this box, or the empty box when they do not overlap.
    pub fn intersection(&self, other: &BoundingBox) -> BoundingBox {
        let (l, t, r, b) = (self.left.max(other.left), self.top.max(other.top), self.right.min(other.right), self.bottom.min(other.bottom));
        if r > l && b > t { BoundingBox { left: l, top: t, right: r, bottom: b, width: r - l, height: b - t } } else { BoundingBox::default() }
    }
}

/// A metadata value.
#[derive(Clone, Debug, PartialEq)]
pub enum Meta {
    Text(String),
    Bool(bool),
    Int(i64),
    Float(f64),
}

/// One element of a snapshot.
#[derive(Clone, Debug, PartialEq)]
pub struct TreeNode {
    pub bounding_box: BoundingBox,
    pub name: String,
    pub control_type: String,
    pub window_name: String,
    /// In insertion order.
    pub metadata: Vec<(String, Meta)>,
}

/// Metadata rendered like Python's `json.dumps`: `{"key": value, "other": value}`, insertion order.
pub fn metadata_text(metadata: &[(String, Meta)]) -> String {
    let quote = |text: &str| serde_json::to_string(text).expect("a string serializes");
    let value = |meta: &Meta| match meta {
        Meta::Text(text) => quote(text),
        Meta::Bool(on) => on.to_string(),
        Meta::Int(n) => n.to_string(),
        Meta::Float(n) if !n.is_finite() => "null".to_string(),
        Meta::Float(n) if *n == n.round() && n.abs() < 9_007_199_254_740_992.0 => (*n as i64).to_string(),
        Meta::Float(n) => n.to_string(),
    };
    let fields: Vec<String> = metadata.iter().map(|(key, meta)| format!("{}: {}", quote(key), value(meta))).collect();
    format!("{{{}}}", fields.join(", "))
}

impl TreeNode {
    /// `id|control_type|name|(x,y)`, then `|{metadata}` when there is any. The window is the `## ` line above. A `|`
    /// in the name is written `\|` and a line break as a space, so a row splits into its columns unambiguously.
    fn row(&self, index: usize) -> String {
        let (x, y) = self.bounding_box.center();
        let name = self.name.replace('|', r"\|").replace(['\r', '\n'], " ");
        let row = format!("{index}|{}|{name}|({x},{y})", self.control_type);
        if self.metadata.is_empty() { row } else { format!("{row}|{}", metadata_text(&self.metadata)) }
    }
}

/// The elements a snapshot found.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct TreeState {
    pub interactive: Vec<TreeNode>,
    pub scrollable: Vec<TreeNode>,
}

const HEADER: &str = "# id|control_type|name|coords|metadata (when any), under the \"## window\" they are in; a | in a name is \\|";

/// The header, then the rows numbered from `base`, with a `## window` line wherever the window changes: a window's
/// title is printed once, not on every row.
fn rows(nodes: &[TreeNode], base: usize) -> String {
    if nodes.is_empty() {
        return "No elements found".to_string();
    }
    let mut lines = vec![HEADER.to_string()];
    let mut window = None;
    for (i, node) in nodes.iter().enumerate() {
        if window != Some(&node.window_name) {
            window = Some(&node.window_name);
            lines.push(format!("## {}", node.window_name));
        }
        lines.push(node.row(base + i));
    }
    lines.join("
")
}

impl TreeState {
    pub fn interactive_text(&self) -> String {
        rows(&self.interactive, 0)
    }

    /// Scrollable ids continue after the interactive ones, so every id in a snapshot is unique.
    pub fn scrollable_text(&self) -> String {
        rows(&self.scrollable, self.interactive.len())
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AppStatus {
    Active,
    Fullscreen,
    Visible,
    Hidden,
    Minimized,
    Windowless,
}

impl AppStatus {
    pub fn name(self) -> &'static str {
        match self {
            AppStatus::Active => "Active",
            AppStatus::Fullscreen => "Fullscreen",
            AppStatus::Visible => "Visible",
            AppStatus::Hidden => "Hidden",
            AppStatus::Minimized => "Minimized",
            AppStatus::Windowless => "Windowless",
        }
    }
}

/// An application as a snapshot lists it.
#[derive(Clone, Debug, PartialEq)]
pub struct WindowView {
    pub name: String,
    pub status: AppStatus,
    pub bounding_box: BoundingBox,
    pub pid: i32,
    pub bundle_id: String,
    /// The dialog blocking it: its title, and whether the user can reach it now.
    pub dialog: Option<(String, bool)>,
}

impl WindowView {
    pub fn description(&self) -> String {
        let mut line = format!("{} ({}) - {}", self.name, self.bundle_id, self.status.name());
        if let Some((title, reachable)) = &self.dialog {
            line += &format!(" - dialog: \"{title}\"");
            if !reachable {
                line += " (behind other windows; switch to the app to reach it)";
            }
        }
        line
    }
}

/// What the platform read.
#[derive(Clone, Debug, Default)]
pub struct State {
    pub active: Option<WindowView>,
    pub windows: Vec<WindowView>,
    pub tree: TreeState,
    /// How Click and Move coordinates relate to the displays.
    pub coordinate_space: String,
    /// Whether Accessibility is granted (without it there are apps but no elements).
    pub trusted: bool,
    /// The annotated screenshot as PNG, when asked for and captured.
    pub screenshot: Option<Vec<u8>>,
    /// Platform lines printed after the coordinate space (Windows: the current virtual desktop). Empty on macOS.
    pub notes: Vec<String>,
    /// Replaces the macOS hint printed when a screenshot was asked for and none came back.
    pub screenshot_hint: Option<String>,
}

/// The snapshot's text.
pub fn text(state: &State, use_vision: bool) -> String {
    let note = if state.trusted { String::new() } else { format!("{}\n", crate::permissions::ACCESSIBILITY_MESSAGE) };
    let active = state.active.as_ref().map(WindowView::description).unwrap_or_else(|| "No focused window.".to_string());
    let windows = if state.windows.is_empty() { "No open applications.".to_string() } else { state.windows.iter().map(WindowView::description).collect::<Vec<_>>().join("\n") };
    let mut text = format!(
        "{note}Focused Window:\n{active}\n\nInput Coordinate Space:\n{}{}\n\nOpen Applications:\n{windows}\n\nList of Interactive Elements:\n{}\n\nList of Scrollable Elements:\n{}\n",
        state.coordinate_space,
        state.notes.iter().map(|line| format!("\n{line}")).collect::<String>(),
        state.tree.interactive_text(),
        state.tree.scrollable_text()
    );
    if use_vision && state.screenshot.is_none() {
        if let Some(hint) = &state.screenshot_hint {
            return text + "\n" + hint + "\n";
        }
        // Capture fails transiently (display asleep or locked, hot-plug) or without permission.
        text += "\nNote: Screenshot capture failed, so no annotated image is attached. The elements\nlisted above are still valid; call Snapshot again if you need the screenshot.\nIf this persists, grant Screen Recording in System Settings > Privacy & Security\n(or call Permissions with request=true).\n";
    }
    text
}

/// The tool's answer: the text, then the screenshot when there is one. The text is formatted off the event loop.
pub async fn run(desktop: &dyn Desktop, use_vision: bool) -> Vec<rmcp::model::ContentBlock> {
    let state = desktop.snapshot(use_vision).await;
    let (text, image) = crate::mcp::off_loop(desktop, move || {
        use base64::Engine;
        (text(&state, use_vision), state.screenshot.as_ref().map(|png| base64::engine::general_purpose::STANDARD.encode(png)))
    })
    .await;
    let mut content = vec![rmcp::model::ContentBlock::text(text)];
    if let (true, Some(image)) = (use_vision, image) {
        content.push(rmcp::model::ContentBlock::image(image, "image/png"));
    }
    content
}
