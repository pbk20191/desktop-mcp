//! The snapshot's walk of each window's UI Automation tree: which elements are interactive or scrollable (the C#
//! port's rules, from upstream Windows-MCP), bounded per parent, in total and in time. The tree comes from a
//! [`TreeSource`], UI Automation in use (`crate::uia`) or a synthetic one in tests.
use crate::windows_list::TopWindow;
use desktop_mcp_core::snapshot::{AppStatus, BoundingBox, Meta, TreeNode, TreeState, WindowView};
use std::collections::HashSet;
use std::time::{Duration, Instant};
use windows::Win32::Foundation::{HWND, LPARAM, POINT, WPARAM};
use windows::Win32::UI::WindowsAndMessaging::{GetAncestor, SendMessageTimeoutW, WindowFromPoint, GA_ROOT, SMTO_ABORTIFHUNG, SMTO_BLOCK, WM_NULL};

/// What the walk reads of one element (cached in one round trip by the UIA source).
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ElementProps {
    /// The UIA control type's programmatic name without "Control": `Button`, `Edit`, …
    pub control_type: String,
    pub name: String,
    pub automation_id: String,
    pub enabled: bool,
    pub offscreen: bool,
    pub control_element: bool,
    pub keyboard_focusable: bool,
    /// left, top, right, bottom in screen pixels.
    pub rect: (f64, f64, f64, f64),
    pub legacy_role: Option<u32>,
    pub legacy_default_action: String,
    /// ExpandCollapseState (0–3) when the element supports it.
    pub expand_collapse: Option<i32>,
    pub vertically_scrollable: bool,
    pub horizontally_scrollable: bool,
    pub vertical_percent: f64,
    pub horizontal_percent: f64,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Classification {
    pub interactive: bool,
    pub scrollable: bool,
}

const INTERACTIVE_TYPES: [&str; 17] =
    ["Button", "ListItem", "MenuItem", "Edit", "CheckBox", "RadioButton", "ComboBox", "Hyperlink", "SplitButton", "TabItem", "TreeItem", "DataItem", "HeaderItem", "TextBox", "Spinner", "Slider", "ScrollBar"];
/// Types never listed as scrollable (the C# port's informative types): an interactive element is listed once, as
/// interactive.
const INFORMATIVE_TYPES: [&str; 3] = ["Text", "Image", "StatusBar"];
const FOCUSABLE_TYPES: [&str; 6] = ["Edit", "Button", "CheckBox", "RadioButton", "TabItem", "ListItem"];
/// MSAA roles that mean "something to act on": buttons, links, text fields, lists, menus, tabs, outlines, sliders,
/// headers, cells.
const INTERACTIVE_ROLES: [u32; 26] = [0x2B, 0x3E, 0x38, 0x39, 0x3A, 0x40, 0x1E, 0x2A, 0x3F, 0x32, 0x2E, 0x2F, 0x2C, 0x2D, 0x0C, 0x22, 0x25, 0x24, 0x33, 0x34, 0x31, 0x03, 0x04, 0x19, 0x1A, 0x1D];
const DEFAULT_ACTIONS: [&str; 6] = ["Click", "Press", "Jump", "Check", "Uncheck", "Double Click"];

fn area(rect: (f64, f64, f64, f64)) -> f64 {
    (rect.2 - rect.0).max(0.0) * (rect.3 - rect.1).max(0.0)
}

/// Whether an element is interactive and whether it scrolls.
pub fn classify(element: &ElementProps, browser: bool) -> Classification {
    let kind = element.control_type.as_str();
    let scrollable = !element.offscreen && element.vertically_scrollable && !INTERACTIVE_TYPES.contains(&kind) && !INFORMATIVE_TYPES.contains(&kind);
    let visible = area(element.rect) > 0.0 && (!element.offscreen || kind == "Edit" || (kind == "ListItem" && browser)) && element.control_element;
    if !visible || !element.enabled {
        return Classification { interactive: false, scrollable };
    }
    let focusable = FOCUSABLE_TYPES.contains(&kind) || element.keyboard_focusable;
    let role = element.legacy_role.is_some_and(|role| INTERACTIVE_ROLES.contains(&role));
    let interactive = if browser && kind == "DataItem" && !focusable {
        false
    } else if !browser && kind == "Image" && focusable {
        true
    } else if INTERACTIVE_TYPES.contains(&kind) || kind == "Document" {
        role
    } else if kind == "Group" && browser {
        let action = DEFAULT_ACTIONS.iter().any(|known| known.eq_ignore_ascii_case(element.legacy_default_action.trim()));
        element.expand_collapse.is_some_and(|state| (0..=3).contains(&state)) || (role && (action || focusable))
    } else {
        false
    };
    Classification { interactive: interactive || kind == "Document", scrollable }
}

/// A tree to walk: the UI Automation tree of one window, or a synthetic one.
pub trait TreeSource {
    type Node;
    /// What tells two nodes apart (UIA's runtime id), so a provider whose tree loops back is not walked twice.
    type Identity: Eq + std::hash::Hash;
    fn root(&self) -> Option<Self::Node>;
    /// `None` when the node has no identity; such a node is always walked.
    fn identity(&self, node: &Self::Node) -> Option<Self::Identity>;
    /// At most `limit` children.
    fn children(&self, node: &Self::Node, limit: usize) -> Vec<Self::Node>;
    fn props(&self, node: &Self::Node) -> ElementProps;
}

#[derive(Clone, Copy, Debug)]
pub struct Limits {
    pub children_per_parent: usize,
    /// Elements read across the whole snapshot.
    pub elements: usize,
    pub deadline: Duration,
}

impl Default for Limits {
    fn default() -> Limits {
        Limits { children_per_parent: 300, elements: 5000, deadline: Duration::from_secs(4) }
    }
}

#[derive(Debug, Default)]
pub struct WindowWalk {
    pub interactive: Vec<TreeNode>,
    pub scrollable: Vec<TreeNode>,
    /// The budget or the deadline ran out before the whole tree was read.
    pub stopped_early: bool,
    /// Elements read.
    pub read: usize,
}

/// The part of `rect` inside `window`; `None` when nothing of it is (an element scrolled out of view).
fn clipped(rect: (f64, f64, f64, f64), window: (f64, f64, f64, f64)) -> Option<BoundingBox> {
    let (left, top, right, bottom) = (rect.0.max(window.0), rect.1.max(window.1), rect.2.min(window.2), rect.3.min(window.3));
    (right > left && bottom > top).then(|| BoundingBox::from_edges(left, top, right, bottom))
}

/// Text-entry types: listed even unnamed.
const ENTRY_TYPES: [&str; 5] = ["Edit", "ComboBox", "Document", "Spinner", "Slider"];

/// The element's name; an unnamed one is named by its automation id, else its control type (the C# port's fallback
/// for scrollables).
fn fallback_name(element: &ElementProps) -> String {
    [element.name.trim(), element.automation_id.as_str(), element.control_type.as_str()].into_iter().find(|name| !name.is_empty()).unwrap_or_default().to_string()
}

fn node(element: &ElementProps, window_name: &str, window: (f64, f64, f64, f64), metadata: Vec<(String, Meta)>) -> Option<TreeNode> {
    Some(TreeNode { bounding_box: clipped(element.rect, window)?, name: element.name.trim().to_string(), control_type: element.control_type.clone(), window_name: window_name.to_string(), metadata })
}

/// Walks one window's tree depth first within `limits` (`window` is its frame as edges, for clipping).
pub fn walk_window<S: TreeSource>(source: &S, window_name: &str, browser: bool, window: (f64, f64, f64, f64), limits: &Limits) -> WindowWalk {
    let started = Instant::now();
    let mut walk = WindowWalk::default();
    let Some(root) = source.root() else { return walk };
    let mut stack = vec![root];
    let mut seen = HashSet::new();
    while let Some(current) = stack.pop() {
        if walk.read >= limits.elements || started.elapsed() > limits.deadline {
            walk.stopped_early = true;
            break;
        }
        if source.identity(&current).is_some_and(|identity| !seen.insert(identity)) {
            continue;
        }
        walk.read += 1;
        let element = source.props(&current);
        let class = classify(&element, browser);
        // An unnamed one is listed only when text can be entered or focus can go there (an empty text box).
        if class.interactive && (!element.name.trim().is_empty() || ENTRY_TYPES.contains(&element.control_type.as_str()) || element.keyboard_focusable) {
            walk.interactive.extend(node(&ElementProps { name: fallback_name(&element), ..element.clone() }, window_name, window, Vec::new()));
        }
        if class.scrollable {
            let metadata = vec![
                ("horizontal_scrollable".to_string(), Meta::Bool(element.horizontally_scrollable)),
                ("horizontal_scroll_percent".to_string(), Meta::Float(if element.horizontally_scrollable { (element.horizontal_percent * 100.0).round() / 100.0 } else { 0.0 })),
                ("vertical_scrollable".to_string(), Meta::Bool(true)),
                ("vertical_scroll_percent".to_string(), Meta::Float((element.vertical_percent * 100.0).round() / 100.0)),
            ];
            walk.scrollable.extend(node(&ElementProps { name: fallback_name(&element), ..element.clone() }, window_name, window, metadata));
        }
        let mut children = source.children(&current, limits.children_per_parent);
        children.reverse(); // depth first, in document order
        stack.extend(children);
    }
    walk
}

/// Whether the window's thread answers a message within 200 ms (a hung one is listed but not walked).
pub fn answers(hwnd: isize) -> bool {
    let mut result = 0usize;
    unsafe { SendMessageTimeoutW(HWND(hwnd as *mut _), WM_NULL, WPARAM(0), LPARAM(0), SMTO_ABORTIFHUNG | SMTO_BLOCK, 200, Some(&mut result)) }.0 != 0
}

/// Whether the point shows the top-level window `hwnd`, not another window in front of it.
pub fn shows(hwnd: isize, (x, y): (i64, i64)) -> bool {
    let at = unsafe { WindowFromPoint(POINT { x: x as i32, y: y as i32 }) };
    !at.is_invalid() && unsafe { GetAncestor(at, GA_ROOT) }.0 as isize == hwnd
}

pub fn is_browser(exe: &str) -> bool {
    matches!(exe.to_ascii_lowercase().as_str(), "chrome.exe" | "msedge.exe" | "firefox.exe")
}

fn view(window: &TopWindow) -> WindowView {
    let f = window.frame;
    let status = if window.focused {
        AppStatus::Active
    } else if window.minimized {
        AppStatus::Minimized
    } else if window.fullscreen {
        AppStatus::Fullscreen
    } else {
        AppStatus::Visible
    };
    WindowView { name: window.title.clone(), status, bounding_box: BoundingBox::from_edges(f.x, f.y, f.x + f.width, f.y + f.height), pid: window.pid as i32, bundle_id: window.exe.clone(), dialog: None }
}

/// The snapshot's windows and elements.
#[derive(Debug, Default)]
pub struct Walk {
    pub tree: TreeState,
    pub windows: Vec<WindowView>,
    pub active: Option<WindowView>,
    pub stopped_early: bool,
}

/// Lists `windows` (the applications') and walks every shown top-level window (owned dialogs, menus and popups
/// too) that is not minimized and answers, front to back, sharing one budget; an element another window covers is
/// left out (its window stays listed). The focused window is the foreground window's root, application window or
/// not. Blocking: on the pool.
pub fn walk(windows: &[TopWindow], limits: &Limits) -> Walk {
    let started = Instant::now();
    let shown = crate::windows_list::shown_windows();
    let foreground = crate::windows_list::foreground_root();
    let active = shown.iter().chain(windows).find(|window| Some(window.hwnd) == foreground).map(|window| WindowView { status: AppStatus::Active, ..view(window) });
    let mut result = Walk { windows: windows.iter().map(view).collect(), active, ..Walk::default() };
    let mut remaining = *limits;
    for window in shown.iter().filter(|window| !window.minimized) {
        if remaining.elements == 0 || started.elapsed() > limits.deadline {
            result.stopped_early = true;
            break;
        }
        if !answers(window.hwnd) {
            continue;
        }
        let Some(source) = crate::uia::WindowTree::new(window.hwnd) else { continue };
        let f = window.frame;
        remaining.deadline = limits.deadline.saturating_sub(started.elapsed());
        let walked = walk_window(&source, &window.title, is_browser(&window.exe), (f.x, f.y, f.x + f.width, f.y + f.height), &remaining);
        remaining.elements = remaining.elements.saturating_sub(walked.read);
        // An element whose centre (where Click lands) another window covers is not listed.
        let visible = |element: &TreeNode| shows(window.hwnd, element.bounding_box.center());
        result.tree.interactive.extend(walked.interactive.into_iter().filter(visible));
        result.tree.scrollable.extend(walked.scrollable.into_iter().filter(visible));
        result.stopped_early |= walked.stopped_early;
    }
    result
}

/// The first element of `source` whose control type is `role` and whose name contains `title` (ignoring case),
/// an exact name preferred; `None` past `deadline`.
pub fn find_in<S: TreeSource>(source: &S, title: Option<&str>, role: Option<&str>, deadline: Instant) -> Option<(ElementProps, bool)> {
    let title = title.map(str::to_lowercase);
    let mut best: Option<ElementProps> = None;
    let mut stack: Vec<S::Node> = source.root().into_iter().collect();
    let mut seen = HashSet::new();
    while let Some(current) = stack.pop() {
        if Instant::now() > deadline {
            break;
        }
        if source.identity(&current).is_some_and(|identity| !seen.insert(identity)) {
            continue;
        }
        let element = source.props(&current);
        let role_matches = role.is_none_or(|role| element.control_type.eq_ignore_ascii_case(role));
        let name = element.name.trim().to_lowercase();
        if role_matches && area(element.rect) > 0.0 && !element.offscreen {
            match &title {
                Some(title) if name == *title => return Some((element, true)),
                Some(title) if name.contains(title.as_str()) && best.is_none() => best = Some(element.clone()),
                None => return Some((element, true)),
                _ => {}
            }
        }
        let mut children = source.children(&current, Limits::default().children_per_parent);
        children.reverse();
        stack.extend(children);
    }
    best.map(|element| (element, false))
}
