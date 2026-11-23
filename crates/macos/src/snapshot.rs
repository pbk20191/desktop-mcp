//! Snapshot on macOS: the applications, their dialogs, and the accessibility tree walked for interactive and
//! scrollable elements. Everything here blocks on other apps: it runs on libdispatch workers, apps concurrently.
use crate::apps::{OnScreenWindow, NORMAL_LEVEL};
use crate::ax::{Element, Value};
use crate::secure_field;
use crate::tuning::AX_MESSAGING_TIMEOUT;
use desktop_mcp_core::snapshot::{AppStatus, BoundingBox, Meta, State, TreeNode, TreeState, WindowView};
use objc2::runtime::AnyObject;
use objc2_app_kit::{NSApplicationActivationPolicy, NSWorkspace};
use objc2_core_foundation::{CFAttributedString, CFNumber};
use std::collections::BTreeSet;
use std::sync::Mutex;

const FINDER: &str = "com.apple.finder";
const BROWSERS: &[&str] =
    &["com.apple.Safari", "com.google.Chrome", "org.mozilla.firefox", "com.microsoft.edgemac", "com.brave.Browser", "com.operasoftware.Opera", "com.vivaldi.Vivaldi", "company.thebrowser.Browser"];
/// System UI scanned on every snapshot, in a fixed order.
const SYSTEM_UI: &[&str] = &["com.apple.dock", "com.apple.controlcenter", "com.apple.systemuiserver", "com.apple.Spotlight", "com.apple.notificationcenterui"];

// Role tables, ported from the Python tree config.
const INTERACTIVE_ROLES: &[&str] = &[
    "AXButton", "AXCheckBox", "AXRadioButton", "AXTextField", "AXTextArea", "AXComboBox", "AXPopUpButton", "AXSlider", "AXIncrementor", "AXLink",
    "AXMenuItem", "AXMenuButton", "AXMenuBarItem", "AXTab", "AXDockItem", "AXCell", "AXToggle", "AXSwitch", "AXDisclosureTriangle", "AXColorWell",
    "AXLevelIndicator", "AXValueIndicator",
];
const SCROLLABLE_ROLES: &[&str] = &["AXScrollArea", "AXScrollView", "AXWebArea", "AXBrowser"];
/// Subroles that make an element interactive whatever its role (notification banners are AXGroups; some search
/// fields report AXStaticText).
const INTERACTIVE_SUBROLES: &[&str] = &["AXNotificationCenterBanner", "AXNotificationCenterBannerStack", "AXSearchField"];
/// Roles whose AXValue is a state (1 = checked), not a name.
const STATE_VALUED_ROLES: &[&str] = &["AXCheckBox", "AXRadioButton", "AXDisclosureTriangle"];
const TEXT_INPUT_ROLES: &[&str] = &["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField"];
/// Decorative or structural subtrees that never yield actionable nodes.
const PRUNABLE_ROLES: &[&str] = &[
    "AXScrollBar", "AXGrowArea", "AXUnknown", "AXValueIndicator", "AXLevelIndicator", "AXProgressIndicator", "AXSeparator", "AXSplitter", "AXHandle",
    "AXRuler", "AXRulerMarker", "AXBusyIndicator", "AXRelevanceIndicator", "AXSizeHandle", "AXResizeIndicator",
];
/// Words reported per text area; each costs an AXBoundsForRange round trip.
const MAX_WORDS_PER_ELEMENT: usize = 200;

fn window_control_name(subrole: &str) -> Option<&'static str> {
    Some(match subrole {
        "AXCloseButton" => "Close Button",
        "AXMinimizeButton" => "Minimize Button",
        "AXZoomButton" => "Zoom Button",
        "AXFullScreenButton" => "Full Screen Button",
        _ => return None,
    })
}

/// Attributes read in one round trip.
struct Attrs(Vec<(String, Value)>);

impl Attrs {
    fn read(element: &Element, names: &[&str]) -> Attrs {
        Attrs(element.attributes(names).unwrap_or_default())
    }
    fn get(&self, name: &str) -> Option<&Value> {
        self.0.iter().find(|(key, _)| key == name).map(|(_, value)| value)
    }
    fn text(&self, name: &str) -> String {
        self.get(name).map(|v| v.text().to_string()).unwrap_or_default()
    }
    fn is_true(&self, name: &str) -> bool {
        self.get(name).is_some_and(Value::is_true)
    }
    fn int(&self, name: &str) -> Option<i64> {
        match self.get(name)? {
            Value::Int(n) => Some(*n),
            Value::Float(n) => Some(*n as i64),
            Value::Bool(b) => Some(i64::from(*b)),
            _ => None,
        }
    }
}

type Rect = (f64, f64, f64, f64); // left, top, right, bottom

fn rect_of(attrs: &Attrs) -> Option<Rect> {
    match (attrs.get("AXPosition"), attrs.get("AXSize")) {
        (Some(Value::Point(x, y)), Some(Value::Size(w, h))) => Some((*x, *y, x + w, y + h)),
        _ => None,
    }
}

fn rect(element: &Element) -> Option<Rect> {
    rect_of(&Attrs::read(element, &["AXPosition", "AXSize"]))
}

fn bbox((l, t, r, b): Rect) -> BoundingBox {
    BoundingBox::from_edges(l, t, r, b)
}

/// A running application, read once.
#[derive(Clone)]
struct RunningApp {
    pid: i32,
    bundle_id: Option<String>,
    localized: Option<String>,
    english: Option<String>,
    regular: bool,
    accessory: bool,
    hidden: bool,
    active: bool,
}

fn running() -> Vec<RunningApp> {
    NSWorkspace::sharedWorkspace()
        .runningApplications()
        .iter()
        .filter(|app| !app.isTerminated())
        .map(|app| RunningApp {
            pid: app.processIdentifier(),
            bundle_id: app.bundleIdentifier().map(|s| s.to_string()),
            localized: app.localizedName().map(|s| s.to_string()),
            english: crate::open::english_name(&app),
            regular: app.activationPolicy() == NSApplicationActivationPolicy::Regular,
            accessory: app.activationPolicy() == NSApplicationActivationPolicy::Accessory,
            hidden: app.isHidden(),
            active: app.isActive(),
        })
        .collect()
}

fn element(pid: i32) -> Element {
    let element = Element::application(pid);
    element.set_messaging_timeout(AX_MESSAGING_TIMEOUT);
    element
}

/// The AX title (what the menu bar shows), else the localized name.
fn app_name(app: &RunningApp, element: &Element) -> String {
    let title = element.attribute("AXTitle").map(|v| v.text().to_string()).unwrap_or_default();
    if title.is_empty() { app.localized.clone().unwrap_or_default() } else { title }
}

fn child(element: &Element, name: &str) -> Option<Element> {
    element.attribute(name)?.element().cloned()
}

fn windows(element: &Element) -> Vec<Element> {
    element.attribute("AXWindows").map(|v| v.elements()).unwrap_or_default()
}

fn status(app: &RunningApp, element: &Element) -> AppStatus {
    if app.hidden {
        return AppStatus::Hidden;
    }
    let windows = windows(element);
    if windows.is_empty() {
        return AppStatus::Windowless;
    }
    if windows.iter().all(|w| w.attribute("AXMinimized").is_some_and(|v| v.is_true())) {
        return AppStatus::Minimized;
    }
    if app.active {
        if windows.iter().any(|w| w.attribute("AXFullScreen").is_some_and(|v| v.is_true())) {
            return AppStatus::Fullscreen;
        }
        return AppStatus::Active;
    }
    AppStatus::Visible
}

/// The sheet or modal window blocking an app.
struct Dialog {
    pid: i32,
    window: Element,
    /// Stacked above every normal window, whichever app is active.
    floating: bool,
    /// Its app is the active one.
    frontmost: bool,
}

impl Dialog {
    /// Whether the user can see and click it right now.
    fn reachable(&self) -> bool {
        self.floating || self.frontmost
    }

    /// The title, or the first line of text for alert panels, which have none.
    fn title(&self) -> String {
        let title = self.window.attribute("AXTitle").map(|v| v.text().to_string()).unwrap_or_default();
        if !title.is_empty() {
            return title;
        }
        for kid in self.window.children() {
            // The value only after the role: a password field's is never fetched.
            if kid.attribute("AXRole").is_some_and(|r| r.text() == "AXStaticText") {
                let text = kid.attribute("AXValue").map(|v| v.describe()).unwrap_or_default();
                if !text.trim().is_empty() {
                    return text.trim().to_string();
                }
            }
        }
        "Dialog".to_string()
    }
}

fn same_frame((l, t, r, b): Rect, bounds: &crate::apps::Rect) -> bool {
    (l - bounds.left).abs() <= 1.0 && (t - bounds.top).abs() <= 1.0 && ((r - l) - (bounds.right - bounds.left)).abs() <= 1.0 && ((b - t) - (bounds.bottom - bounds.top)).abs() <= 1.0
}

/// The app's dialog, found by walking its on-screen windows front to back and matching each to its AX window by
/// frame. AXModal covers an app's own alerts; system prompts say so by subrole. (Subrole AXDialog alone is not
/// trusted: Chrome reports it on ordinary windows.)
fn dialog(pid: i32, element: &Element, screen: &[OnScreenWindow]) -> Option<Dialog> {
    let own: Vec<&OnScreenWindow> = screen.iter().filter(|w| w.pid == pid && w.is_application()).collect();
    if own.is_empty() {
        return None;
    }
    let frontmost = OnScreenWindow::frontmost_pid(screen) == Some(pid);
    let ax_windows: Vec<(Element, Option<Rect>)> = windows(element).into_iter().map(|w| {
        let r = rect(&w);
        (w, r)
    }).collect();
    for entry in own {
        let Some((window, _)) = ax_windows.iter().find(|(_, r)| r.is_some_and(|r| same_frame(r, &entry.bounds))) else { continue };
        let floating = entry.layer > NORMAL_LEVEL;
        let attrs = Attrs::read(window, &["AXModal", "AXSubrole"]);
        if attrs.is_true("AXModal") || attrs.text("AXSubrole") == "AXSystemDialog" {
            return Some(Dialog { pid, window: window.clone(), floating, frontmost });
        }
        if let Some(sheet) = window.children().into_iter().find(|kid| kid.attribute("AXRole").is_some_and(|r| r.text() == "AXSheet")) {
            return Some(Dialog { pid, window: sheet, floating, frontmost });
        }
    }
    None
}

/// Every dialog on screen, front to back, one per app.
fn all_dialogs(screen: &[OnScreenWindow]) -> Vec<Dialog> {
    let mut seen = BTreeSet::new();
    screen.iter().filter(|w| w.is_application() && seen.insert(w.pid)).filter_map(|w| dialog(w.pid, &element(w.pid), screen)).collect()
}

fn view(app: &RunningApp, element: &Element, name: String, bounding_box: BoundingBox, dialog: Option<&Dialog>) -> WindowView {
    WindowView {
        name,
        status: status(app, element),
        bounding_box,
        pid: app.pid,
        bundle_id: app.bundle_id.clone().unwrap_or_default(),
        dialog: dialog.map(|d| (d.title(), d.reachable())),
        english_name: None,
    }
}

/// The window the user is working in: the front window's app, else the menu bar owner, else Finder.
fn foreground(owner: Option<i32>, running: &[RunningApp], screen: &[OnScreenWindow]) -> Option<WindowView> {
    let pid = OnScreenWindow::frontmost_pid(screen).or(owner);
    let app = match pid {
        Some(pid) => running.iter().find(|a| a.pid == pid).cloned().unwrap_or(RunningApp { pid, bundle_id: None, localized: None, english: None, regular: false, accessory: false, hidden: false, active: false }),
        None => running.iter().find(|a| a.bundle_id.as_deref() == Some(FINDER))?.clone(),
    };
    let element = element(app.pid);
    let bundle_id = app.bundle_id.clone().unwrap_or_else(|| FINDER.to_string());
    match child(&element, "AXMainWindow") {
        None => {
            // Windowless (minimised or no windows): kept so the tree still scans its menu bar; for Finder this is
            // the desktop-only state.
            let name = if bundle_id == FINDER { "Finder".to_string() } else {
                let name = app_name(&app, &element);
                if name.is_empty() { bundle_id.clone() } else { name }
            };
            let mut view = view(&app, &element, name, BoundingBox::default(), None);
            view.bundle_id = bundle_id;
            Some(view)
        }
        Some(window) => {
            let name = window.attribute("AXTitle").map(|v| v.text().to_string()).unwrap_or_default();
            let found = dialog(app.pid, &element, screen);
            let mut view = view(&app, &element, name, rect(&window).map(bbox).unwrap_or_default(), found.as_ref());
            view.bundle_id = bundle_id;
            Some(view)
        }
    }
}

/// User-facing apps (Dock-visible ones, plus any background app showing a dialog). Finder is left out unless it
/// shows a dialog: it is always running and usually just the desktop.
fn list(running: &[RunningApp], dialogs: &[Dialog]) -> Vec<WindowView> {
    let mut apps: Vec<RunningApp> = running.iter().filter(|a| a.regular).cloned().collect();
    for dialog in dialogs {
        if !apps.iter().any(|a| a.pid == dialog.pid) {
            if let Some(app) = running.iter().find(|a| a.pid == dialog.pid) {
                apps.push(app.clone());
            }
        }
    }
    let titles: Vec<(i32, String, bool)> = dialogs.iter().map(|d| (d.pid, d.title(), d.reachable())).collect();
    crate::main_loop::concurrent_map(apps, move |app| {
        let dialog = titles.iter().find(|(pid, _, _)| *pid == app.pid).map(|(_, title, reachable)| (title.clone(), *reachable));
        if app.bundle_id.as_deref() == Some(FINDER) && dialog.is_none() {
            return None;
        }
        let element = element(app.pid);
        let status = status(&app, &element);
        let mut bounding_box = BoundingBox::default();
        if !matches!(status, AppStatus::Hidden | AppStatus::Minimized | AppStatus::Windowless) {
            if let Some(r) = child(&element, "AXMainWindow").and_then(|w| rect(&w)) {
                bounding_box = bbox(r);
            }
        }
        Some(WindowView { name: app_name(&app, &element), status, bounding_box, pid: app.pid, bundle_id: app.bundle_id.clone().unwrap_or_default(), dialog, english_name: app.english.clone() })
    })
    .into_iter()
    .flatten()
    .collect()
}

// MARK: The tree

/// How much of one application to walk; with no flag set the whole app is scanned.
#[derive(Clone)]
struct Scan {
    bundle_id: String,
    is_browser: bool,
    desktop_only: bool,
    extras_only: bool,
    dialog_only: bool,
}

const EARLY: [&str; 11] = ["AXRole", "AXHidden", "AXPosition", "AXSize", "AXEnabled", "AXHelp", "AXHasPopup", "AXTitleUIElement", "AXSubrole", "AXIndex", "AXChildren"];

/// Phase-1 attributes, read for every element.
struct Early {
    role: String,
    hidden: bool,
    enabled: bool,
    help: String,
    has_popup: bool,
    title_element: Option<Element>,
    subrole: String,
    has_index: bool,
    rect: Option<Rect>,
    children: Vec<Element>,
}

impl Early {
    fn read(element: &Element) -> Early {
        let a = Attrs::read(element, &EARLY);
        Early {
            role: a.text("AXRole"),
            hidden: a.is_true("AXHidden"),
            enabled: !matches!(a.get("AXEnabled"), Some(Value::Bool(false)) | Some(Value::Int(0))),
            help: a.text("AXHelp"),
            has_popup: a.is_true("AXHasPopup"),
            title_element: a.get("AXTitleUIElement").and_then(|v| v.element().cloned()),
            subrole: a.text("AXSubrole"),
            has_index: a.get("AXIndex").is_some_and(Value::truthy),
            rect: rect_of(&a),
            children: a.get("AXChildren").map(|v| v.elements()).unwrap_or_default(),
        }
    }

    /// A password field, or an element whose role could not be read (fail closed: its value is never fetched).
    fn is_secure(&self) -> bool {
        self.role.is_empty() || secure_field::is_secure(Some(&self.role), Some(&self.subrole))
    }
}

/// Phase-2 attributes, read only for elements already judged interactive, and only those their role can carry.
#[derive(Default)]
struct Late {
    title: String,
    description: String,
    identifier: String,
    value: Option<Value>,
    placeholder: Option<String>,
    url: Option<String>,
    filename: Option<String>,
    expanded: Option<bool>,
    shortcut: String,
    selected_text: String,
    selected_range: Option<(i64, i64)>,
    secure: bool,
}

const SECRET: [&str; 3] = ["AXValue", "AXSelectedText", "AXSelectedTextRange"];

fn late_names(role: Option<&str>, secure: bool) -> Vec<&'static str> {
    let always = ["AXTitle", "AXDescription", "AXIdentifier", "AXValue"];
    let textual = ["AXPlaceholderValue", "AXSelectedText", "AXSelectedTextRange", "AXExpanded"];
    let link_like = ["AXURL", "AXFilename"];
    let menu = ["AXMenuItemCmdChar", "AXMenuItemCmdModifiers", "AXMenuItemCmdVirtualKey"];
    let mut names: Vec<&'static str> = always.to_vec();
    match role {
        Some(role) => {
            if ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField", "AXRow"].contains(&role) {
                names.extend(textual);
            }
            if ["AXLink", "AXImage"].contains(&role) {
                names.extend(link_like);
            }
            if ["AXMenuItem", "AXMenuBarItem"].contains(&role) {
                names.extend(menu);
            }
        }
        None => {
            names.extend(textual);
            names.extend(link_like);
        }
    }
    if secure {
        names.retain(|name| !SECRET.contains(name));
    }
    names
}

impl Late {
    fn read(element: &Element, role: Option<&str>, secure: bool) -> Late {
        let a = Attrs::read(element, &late_names(role, secure));
        let described = |name: &str| a.get(name).map(Value::describe);
        Late {
            title: a.text("AXTitle"),
            description: a.text("AXDescription"),
            identifier: a.text("AXIdentifier"),
            value: a.get("AXValue").cloned(),
            placeholder: described("AXPlaceholderValue"),
            url: described("AXURL"),
            filename: described("AXFilename"),
            expanded: match a.get("AXExpanded") {
                Some(Value::Bool(on)) => Some(*on),
                Some(Value::Int(n)) => Some(*n != 0),
                _ => None,
            },
            shortcut: desktop_mcp_core::menu::shortcut_label(described("AXMenuItemCmdChar").as_deref(), a.int("AXMenuItemCmdModifiers"), a.int("AXMenuItemCmdVirtualKey")),
            selected_text: described("AXSelectedText").unwrap_or_default(),
            selected_range: match a.get("AXSelectedTextRange") {
                Some(Value::Range { location, length }) => Some((*location, *length)),
                _ => None,
            },
            secure,
        }
    }

    /// The first of title, description, value (never a secure field's), placeholder, identifier.
    fn label(&self) -> String {
        let value = if self.secure { String::new() } else { self.value.as_ref().map(Value::describe).unwrap_or_default() };
        let placeholder = self.placeholder.clone().unwrap_or_default();
        [self.title.clone(), self.description.clone(), value, placeholder, self.identifier.clone()].into_iter().find(|s| !s.is_empty()).unwrap_or_default()
    }
}

fn json(value: &Value) -> Meta {
    match value {
        Value::Text(text) => Meta::Text(text.clone()),
        Value::Bool(on) => Meta::Bool(*on),
        Value::Int(n) => Meta::Int(*n),
        Value::Float(n) => Meta::Float(*n),
        other => Meta::Text(other.describe()),
    }
}

fn is_reportable_url(url: &str) -> bool {
    url.starts_with("file://") || url.starts_with("http://") || url.starts_with("https://")
}

/// The text of an element's AXTitleUIElement: its title, else its value (not a secure or unreadable field's),
/// else its description.
fn linked_title(element: &Element) -> Option<String> {
    let a = Attrs::read(element, &["AXRole", "AXSubrole", "AXTitle", "AXDescription"]);
    let title = a.get("AXTitle").map(Value::describe).unwrap_or_default();
    if !title.is_empty() {
        return Some(title);
    }
    let role = a.text("AXRole");
    if !role.is_empty() && !secure_field::is_secure(Some(&role), a.get("AXSubrole").map(Value::text)) {
        let value = element.attribute("AXValue").map(|v| v.describe()).unwrap_or_default();
        if !value.is_empty() {
            return Some(value);
        }
    }
    let description = a.get("AXDescription").map(Value::describe).unwrap_or_default();
    (!description.is_empty()).then_some(description)
}

/// Name and metadata of an interactive element. A secure field shows `value: "(hidden)"` and nothing derived from
/// its value or selection.
fn describe(early: &Early, mut late: Late, linked: Option<String>, bounding_box: BoundingBox, window_name: &str) -> TreeNode {
    let role = early.role.as_str();
    late.secure = late.secure || early.is_secure();
    let secure = late.secure;
    let linked_text = linked.clone().unwrap_or_default();
    let mut label = if STATE_VALUED_ROLES.contains(&role) {
        // AXValue is the state here; a switch reporting 1 would otherwise be named "1".
        [&late.title, &late.description, &linked_text, &late.identifier].into_iter().find(|s| !s.is_empty()).cloned().unwrap_or_default()
    } else {
        let label = late.label();
        if label.is_empty() { linked_text.clone() } else { label }
    };
    let mut metadata: Vec<(String, Meta)> = Vec::new();
    let mut put = |key: &str, value: Meta| metadata.push((key.to_string(), value));
    let value_set = late.value.as_ref().is_some_and(Value::truthy);
    let placeholder = late.placeholder.clone().filter(|p| !p.is_empty());
    match if secure { secure_field::ROLE } else { role } {
        secure_field::ROLE => {
            if let Some(p) = placeholder {
                put("placeholder", Meta::Text(p));
            }
            put("value", Meta::Text("(hidden)".into()));
        }
        "AXTextField" => {
            if let Some(p) = placeholder {
                put("placeholder", Meta::Text(p));
            }
            if value_set {
                put("value", json(late.value.as_ref().unwrap()));
            }
        }
        "AXComboBox" | "AXTextArea" | "AXRow" => {
            if let Some(p) = placeholder {
                put("placeholder", Meta::Text(p));
            }
            if value_set {
                put("value", json(late.value.as_ref().unwrap()));
            }
            if let Some(expanded) = late.expanded {
                put("expanded", Meta::Bool(expanded));
            }
            if early.has_popup {
                put("has_popup", Meta::Bool(true));
            }
        }
        "AXRadioButton" if value_set => put("selected", json(late.value.as_ref().unwrap())),
        "AXMenuItem" | "AXMenuBarItem" if !late.shortcut.is_empty() => put("shortcut", Meta::Text(late.shortcut.clone())),
        "AXCheckBox" => {
            // 0 off, 1 on, 2 mixed.
            let state = match late.value.as_ref() {
                Some(Value::Int(n)) => Some(*n),
                Some(Value::Float(n)) => Some(*n as i64),
                Some(Value::Bool(b)) => Some(i64::from(*b)),
                Some(other) => other.describe().parse().ok(),
                None => None,
            };
            if let Some(state) = state {
                put("checked", if state == 2 { Meta::Text("mixed".into()) } else { Meta::Bool(state != 0) });
            }
        }
        "AXPopUpButton" => {
            if let Some(title) = &linked {
                put("title", Meta::Text(title.clone()));
            }
            if value_set {
                put("selected", json(late.value.as_ref().unwrap()));
            }
        }
        "AXLink" => {
            if let Some(url) = late.url.clone().filter(|u| is_reportable_url(u)) {
                put("url", Meta::Text(url));
            }
        }
        "AXImage" => {
            if let Some(f) = late.filename.clone().filter(|f| !f.is_empty()) {
                put("filename", Meta::Text(f));
            }
            if let Some(url) = late.url.clone().filter(|u| is_reportable_url(u)) {
                put("url", Meta::Text(url));
            }
        }
        _ => {}
    }
    // Only a real selection is reported: it tells the caller typing would replace content.
    if !secure && (TEXT_INPUT_ROLES.contains(&role) || early.subrole == "AXSearchField") {
        if let Some((location, length)) = late.selected_range.filter(|(_, length)| *length > 0) {
            put("selected_text", Meta::Text(late.selected_text.clone()));
            let total = late.value.as_ref().map(|v| v.describe().encode_utf16().count() as i64).unwrap_or(0);
            if location == 0 && total > 0 && length == total {
                put("all_selected", Meta::Bool(true));
            }
        }
    }
    if !late.identifier.is_empty() {
        put("axidentifier", Meta::Text(late.identifier.clone()));
    }
    if label.is_empty() {
        if let Some(name) = window_control_name(&early.subrole) {
            label = name.to_string();
        }
    }
    // Third-party menu bar extras are bare icons; the owning app's name is all there is.
    if label.is_empty() && role == "AXMenuBarItem" {
        label = window_name.to_string();
    }
    // An unlabelled password field would otherwise be dropped as nameless.
    if label.is_empty() && secure {
        label = "(hidden)".to_string();
    }
    TreeNode { bounding_box, name: label, control_type: role.to_string(), window_name: window_name.to_string(), metadata }
}

fn clipped(r: Rect, clip: Option<&BoundingBox>) -> BoundingBox {
    let b = bbox(r);
    clip.map_or(b, |clip| clip.intersection(&b))
}

/// A link wrapping a heading is reported as the heading.
fn dom_correction(node: TreeNode, early: &Early, window_name: &str, clip: Option<&BoundingBox>) -> Option<TreeNode> {
    let (true, Some(first)) = (early.role == "AXLink", early.children.first()) else { return Some(node) };
    let child = Early::read(first);
    if child.role != "AXHeading" {
        return Some(node);
    }
    let r = child.rect?;
    let late = Late::read(first, None, child.is_secure());
    let metadata = if late.identifier.is_empty() { vec![] } else { vec![("axidentifier".to_string(), Meta::Text(late.identifier.clone()))] };
    Some(TreeNode { bounding_box: clipped(r, clip), name: late.label(), control_type: child.role, window_name: window_name.to_string(), metadata })
}

/// Cells and groups take the text of their first static-text descendant; window frame buttons get their names.
fn desktop_correction(node: TreeNode, early: &Early, window_name: &str, clip: Option<&BoundingBox>) -> Option<TreeNode> {
    let Some(r) = early.rect else { return Some(node) };
    let relabelled = |name: String| TreeNode { bounding_box: clipped(r, clip), name, control_type: early.role.clone(), window_name: window_name.to_string(), metadata: node.metadata.clone() };
    if early.role == "AXCell" || early.role == "AXGroup" {
        let mut current = early.children.first().cloned();
        while let Some(element) = current {
            let a = Attrs::read(&element, &["AXRole", "AXChildren"]);
            if a.text("AXRole") == "AXStaticText" {
                // An empty label still replaces the node, as in the original. The value is fetched only for static
                // text, so a password field's never is.
                return Some(relabelled(element.attribute("AXValue").map(|v| v.describe()).unwrap_or_default()));
            }
            current = a.get("AXChildren").and_then(|v| v.elements().into_iter().next());
        }
    } else if early.role == "AXButton" {
        if let Some(name) = window_control_name(&early.subrole) {
            return Some(relabelled(name.to_string()));
        }
    }
    Some(node)
}

/// Iterative depth-first walk collecting interactive and scrollable nodes.
fn traverse(root: &Element, window_name: &str, state: &mut TreeState, clip: Option<BoundingBox>, is_browser: bool) {
    // With each element: whether its parent had a usable box. A collapsed wrapper directly inside a real container
    // is an overlay worth descending into; one inside another collapsed wrapper is far more likely hidden.
    let mut stack: Vec<(Element, bool)> = vec![(root.clone(), true)];
    while let Some((element, parent_box_ok)) = stack.pop() {
        let early = Early::read(&element);
        let role = early.role.as_str();
        if early.hidden || PRUNABLE_ROLES.contains(&role) {
            continue;
        }
        let Some(r) = early.rect else {
            stack.extend(early.children.iter().rev().map(|c| (c.clone(), parent_box_ok)));
            continue;
        };
        let visible = r.2 - r.0 > 1.0 && r.3 - r.1 > 1.0;
        // A closed submenu reports a 0-size rect for its whole subtree.
        if role == "AXMenu" && !visible {
            continue;
        }
        let has_role = INTERACTIVE_ROLES.contains(&role) || role == "AXImage";
        let has_list_title = role == "AXList" && element.attribute("AXTitle").is_some_and(|t| !t.text().is_empty());
        let interactive = visible
            && ((has_role && early.enabled) || !early.help.is_empty() || early.has_popup || early.has_index || early.title_element.is_some() || INTERACTIVE_SUBROLES.contains(&early.subrole.as_str()) || has_list_title);
        let mut b = bbox(r);
        if let Some(clip) = &clip {
            b = clip.intersection(&b);
            if b.width == 0 || b.height == 0 {
                // No clickable area of its own, but its descendants might have one (a floating button inside a
                // zero-height wrapper).
                if parent_box_ok {
                    stack.extend(early.children.iter().rev().map(|c| (c.clone(), false)));
                }
                continue;
            }
        }
        if interactive {
            let late = Late::read(&element, Some(role), early.is_secure());
            let linked = early.title_element.as_ref().and_then(linked_title);
            let node = describe(&early, late, linked, b, window_name);
            let node = if is_browser { dom_correction(node, &early, window_name, clip.as_ref()) } else { desktop_correction(node, &early, window_name, clip.as_ref()) };
            if let Some(node) = node.filter(|n| !n.name.trim().is_empty()) {
                state.interactive.push(node);
            }
            if role == "AXTextArea" && !early.is_secure() {
                word_nodes(&element, window_name, clip.as_ref(), state);
            }
        }
        if SCROLLABLE_ROLES.contains(&role) && visible {
            let label = early.children.first().map(|first| {
                let role = first.attribute("AXRole").map(|v| v.text().to_string()).unwrap_or_default();
                let subrole = first.attribute("AXSubrole").map(|v| v.text().to_string());
                let withholds = role.is_empty() || secure_field::is_secure(Some(&role), subrole.as_deref());
                Late::read(first, None, withholds).label()
            });
            state.scrollable.push(TreeNode { bounding_box: b, name: label.unwrap_or_default(), control_type: role.to_string(), window_name: window_name.to_string(), metadata: vec![] });
        }
        stack.extend(early.children.iter().rev().map(|c| (c.clone(), true)));
    }
}

fn traverse_windows(windows: &[Element], window_name: &str, state: &mut TreeState, is_browser: bool) {
    for window in windows {
        traverse(window, window_name, state, rect(window).map(bbox), is_browser);
    }
}

// MARK: Word nodes

/// One node per whitespace-delimited word of a text area, each clickable; a soft-wrapped word gives one box per
/// line.
fn word_nodes(element: &Element, window_name: &str, clip: Option<&BoundingBox>, state: &mut TreeState) {
    for (word, rects) in word_boxes(element, MAX_WORDS_PER_ELEMENT) {
        for r in rects {
            let b = clipped(r, clip);
            if b.width > 0 && b.height > 0 {
                state.interactive.push(TreeNode { bounding_box: b, name: word.clone(), control_type: "Word".into(), window_name: window_name.to_string(), metadata: vec![] });
            }
        }
    }
}

fn word_boxes(element: &Element, limit: usize) -> Vec<(String, Vec<Rect>)> {
    // Without AXBoundsForRange there is no geometry; some apps answer with a degenerate rect anyway.
    if !element.parameterized_names().iter().any(|n| n == "AXBoundsForRange") {
        return vec![];
    }
    let Some(Value::Int(count)) = element.attribute("AXNumberOfCharacters") else { return vec![] };
    if count <= 0 {
        return vec![];
    }
    let Some(full) = crate::ax::range(0, count) else { return vec![] };
    let text = element.parameterized("AXStringForRange", &full).map(|v| v.text().to_string()).unwrap_or_default();
    if text.is_empty() {
        return vec![];
    }
    let units: Vec<u16> = text.encode_utf16().collect();
    let lines = line_table(element, units.len() as i64);
    let font_size = document_font_size(element);
    let mut words = Vec::new();
    for (start, end) in words_in(&text) {
        if words.len() >= limit {
            break;
        }
        let mut rects = Vec::new();
        for (location, length) in split(start, end - start, &lines) {
            let Some(range) = crate::ax::range(location, length) else { continue };
            let Some(Value::Rect { x, y, width, height }) = element.parameterized("AXBoundsForRange", &range) else { continue };
            let (mut top, bottom) = (y, y + height);
            if width <= 0.0 || height <= 0.0 {
                continue;
            }
            if let Some(size) = font_size.filter(|s| *s > 0.0 && *s < height) {
                // AXBoundsForRange reports the whole line box; trim the leading above the glyphs.
                top = bottom - size;
            }
            rects.push((x, top, x + width, bottom));
        }
        if !rects.is_empty() {
            words.push((String::from_utf16_lossy(&units[start as usize..end as usize]), rects));
        }
    }
    words
}

/// The UTF-16 ranges of the maximal runs of non-whitespace.
fn words_in(text: &str) -> Vec<(i64, i64)> {
    let mut words = Vec::new();
    let (mut offset, mut start) = (0i64, None);
    for c in text.chars() {
        if c.is_whitespace() {
            if let Some(s) = start.take() {
                words.push((s, offset));
            }
        } else if start.is_none() {
            start = Some(offset);
        }
        offset += c.len_utf16() as i64;
    }
    if let Some(s) = start {
        words.push((s, offset));
    }
    words
}

fn line_table(element: &Element, total: i64) -> Vec<(i64, i64)> {
    let mut lines: Vec<(i64, i64)> = Vec::new();
    let mut offset = 0;
    while offset < total {
        let index = CFNumber::new_i64(lines.len() as i64);
        let Some(Value::Range { location, length }) = element.parameterized("AXRangeForLine", &index) else { return vec![] };
        if length <= 0 || location + length <= offset {
            break;
        }
        lines.push((location, length));
        offset = location + length;
    }
    lines
}

/// AXBoundsForRange merges a multi-line range into one useless union rect, so split it by line first.
fn split(location: i64, length: i64, lines: &[(i64, i64)]) -> Vec<(i64, i64)> {
    if lines.is_empty() {
        return vec![(location, length)];
    }
    let end = location + length;
    let segments: Vec<(i64, i64)> = lines
        .iter()
        .filter(|(l, n)| *l < end && l + n > location)
        .map(|(l, n)| (location.max(*l), end.min(l + n)))
        .filter(|(s, e)| e > s)
        .map(|(s, e)| (s, e - s))
        .collect();
    if segments.is_empty() { vec![(location, length)] } else { segments }
}

fn document_font_size(element: &Element) -> Option<f64> {
    let range = crate::ax::range(0, 1)?;
    let raw = element.parameterized_raw("AXAttributedStringForRange", &range)?;
    let attributed = raw.downcast_ref::<CFAttributedString>()?;
    let attributes = unsafe { attributed.attributes(0, std::ptr::null_mut()) }?;
    let attributes: &AnyObject = unsafe { &*(&*attributes as *const _ as *const AnyObject) };
    let key = objc2_foundation::NSString::from_str("AXFont");
    let font: Option<objc2::rc::Retained<AnyObject>> = unsafe { objc2::msg_send![attributes, objectForKey: &*key] };
    let size_key = objc2_foundation::NSString::from_str("AXFontSize");
    let size: Option<objc2::rc::Retained<AnyObject>> = unsafe { objc2::msg_send![&*font?, objectForKey: &*size_key] };
    let size = size?;
    Some(unsafe { objc2::msg_send![&*size, doubleValue] })
}

// MARK: Scans

/// Bundle ids of running apps with an icon in the menu bar: only that icon of theirs is on screen, so only it is
/// scanned. Cached against the set of running processes.
fn bundles_with_menu_bar_extras(running: &[RunningApp]) -> Vec<String> {
    static CACHE: Mutex<Option<(BTreeSet<i32>, Vec<String>)>> = Mutex::new(None);
    let candidates: Vec<RunningApp> = running.iter().filter(|a| (a.regular || a.accessory) && a.bundle_id.is_some()).cloned().collect();
    let key: BTreeSet<i32> = candidates.iter().map(|a| a.pid).collect();
    if let Some((pids, bundles)) = CACHE.lock().unwrap().as_ref() {
        if *pids == key {
            return bundles.clone();
        }
    }
    let candidates: Vec<RunningApp> = candidates.into_iter().filter(|a| !SYSTEM_UI.contains(&a.bundle_id.as_deref().unwrap_or_default())).collect();
    let found = crate::main_loop::concurrent_map(candidates, |app| {
        let extras = child(&element(app.pid), "AXExtrasMenuBar")?;
        (!extras.children().is_empty()).then(|| app.bundle_id.clone().unwrap_or_default())
    });
    let mut bundles: Vec<String> = Vec::new();
    for bundle in found.into_iter().flatten() {
        if !bundles.contains(&bundle) {
            bundles.push(bundle);
        }
    }
    *CACHE.lock().unwrap() = Some((key, bundles.clone()));
    bundles
}

/// The nodes of one app; empty when it is not running.
fn nodes(scan: &Scan, screen: &[OnScreenWindow], running: &[RunningApp]) -> TreeState {
    let mut state = TreeState::default();
    let Some(pid) = crate::apps::by_bundle_id(&scan.bundle_id) else { return state };
    let app = running.iter().find(|a| a.pid == pid).cloned().unwrap_or(RunningApp { pid, bundle_id: Some(scan.bundle_id.clone()), localized: None, english: None, regular: false, accessory: false, hidden: false, active: false });
    // Keep an unresponsive app from stalling the snapshot.
    let element = element(pid);
    let name = app_name(&app, &element);
    let name = if name.is_empty() { scan.bundle_id.clone() } else { name };
    let menu_bar = child(&element, "AXMenuBar");
    let extras = child(&element, "AXExtrasMenuBar");
    if scan.extras_only || scan.dialog_only {
        if scan.extras_only {
            if let Some(extras) = &extras {
                traverse(extras, &name, &mut state, None, scan.is_browser);
            }
        }
        if scan.dialog_only {
            if let Some(d) = dialog(pid, &element, screen) {
                traverse_windows(&[d.window], &name, &mut state, scan.is_browser);
            }
        }
        return state;
    }
    // Menus are scanned even while a dialog is up: AppKit keeps modal-safe items live.
    if !scan.desktop_only {
        for bar in [&menu_bar, &extras].into_iter().flatten() {
            traverse(bar, &name, &mut state, None, scan.is_browser);
        }
    }
    if let Some(d) = dialog(pid, &element, screen) {
        // The window behind a sheet or modal still reports itself enabled, but clicks on it do nothing: only the
        // dialog is walked.
        traverse_windows(&[d.window], &name, &mut state, scan.is_browser);
    } else if let Some(main) = child(&element, "AXMainWindow") {
        if let Some(r) = rect(&main) {
            traverse(&main, &name, &mut state, Some(bbox(r)), scan.is_browser);
        }
    } else {
        let all = windows(&element);
        let visible: Vec<Element> = all.iter().filter(|w| !w.attribute("AXMinimized").is_some_and(|v| v.is_true())).cloned().collect();
        if !visible.is_empty() {
            // Finder's desktop, for instance.
            traverse_windows(&visible, &name, &mut state, scan.is_browser);
        } else if all.is_empty() {
            // Windowless apps such as the Dock show their UI as top-level children.
            let scanned: Vec<Element> = [menu_bar, extras].into_iter().flatten().collect();
            for kid in element.children().into_iter().filter(|kid| !scanned.contains(kid)) {
                traverse(&kid, &name, &mut state, None, scan.is_browser);
            }
        }
    }
    state
}

fn tree(active: Option<&WindowView>, running: &[RunningApp], screen: &[OnScreenWindow], dialogs: &[Dialog]) -> TreeState {
    // Ordered: the active app first, so its elements get the lowest ids.
    let mut scans: Vec<Scan> = Vec::new();
    let scan = |bundle_id: &str| Scan { bundle_id: bundle_id.to_string(), is_browser: BROWSERS.contains(&bundle_id), desktop_only: false, extras_only: false, dialog_only: false };
    if let Some(active) = active {
        if let Some(pid) = crate::apps::by_bundle_id(&active.bundle_id) {
            Element::application(pid).set_bool("AXEnhancedUserInterface", true);
        }
        scans.push(scan(&active.bundle_id));
    }
    for bundle_id in SYSTEM_UI {
        if !scans.iter().any(|s| s.bundle_id == *bundle_id) && running.iter().any(|a| a.bundle_id.as_deref() == Some(*bundle_id)) {
            scans.push(scan(bundle_id));
        }
    }
    let fully: Vec<String> = scans.iter().map(|s| s.bundle_id.clone()).collect();
    let narrow = |scans: &mut Vec<Scan>, bundle_id: &str, apply: &dyn Fn(&mut Scan)| {
        if fully.iter().any(|b| b == bundle_id) {
            return;
        }
        if !scans.iter().any(|s| s.bundle_id == bundle_id) {
            scans.push(scan(bundle_id));
        }
        apply(scans.iter_mut().find(|s| s.bundle_id == bundle_id).expect("just added"));
    };
    // A non-Finder app with no visible window: also scan the desktop icons, but not Finder's menus.
    if let Some(active) = active {
        if active.bundle_id != FINDER && active.bounding_box.width == 0 && active.bounding_box.height == 0 {
            narrow(&mut scans, FINDER, &|s| s.desktop_only = true);
        }
    }
    for bundle_id in bundles_with_menu_bar_extras(running) {
        narrow(&mut scans, &bundle_id, &|s| s.extras_only = true);
    }
    // A background app's dialog is scanned only while the user can reach it.
    for d in dialogs.iter().filter(|d| d.reachable() && !d.frontmost) {
        if let Some(bundle_id) = running.iter().find(|a| a.pid == d.pid).and_then(|a| a.bundle_id.clone()) {
            narrow(&mut scans, &bundle_id, &|s| s.dialog_only = true);
        }
    }
    let results = crate::main_loop::concurrent_map(scans, |scan| nodes(&scan, screen, running));
    let mut state = TreeState::default();
    for result in results {
        state.interactive.extend(result.interactive);
        state.scrollable.extend(result.scrollable);
    }
    state
}

fn format_scale(value: f64) -> String {
    if value == value.round() { (value as i64).to_string() } else { value.to_string() }
}

/// How Click and Move coordinates relate to the display the active window is on.
fn coordinate_space(active: Option<&WindowView>) -> String {
    let display = active.filter(|a| a.bounding_box != BoundingBox::default()).and_then(|a| {
        let b = a.bounding_box;
        crate::displays::containing((b.left + b.right) as f64 / 2.0, (b.top + b.bottom) as f64 / 2.0)
    });
    match display {
        Some(d) => format!(
            "macOS logical points; active display origin ({},{}), size {}x{}, backing scale {}x",
            d.logical.x as i64, d.logical.y as i64, d.logical.width as i64, d.logical.height as i64, format_scale(d.scale)
        ),
        None => "macOS logical points (display could not be identified)".to_string(),
    }
}

/// The desktop's state for Snapshot, without the screenshot. Blocks on other apps: call it off the main thread,
/// with the menu bar owner read on the main thread.
pub fn state(owner: Option<i32>) -> State {
    let trusted = crate::ax::trusted();
    let running = running();
    let screen = OnScreenWindow::all();
    let active = foreground(owner, &running, &screen);
    let dialogs = all_dialogs(&screen);
    let windows = list(&running, &dialogs);
    let tree = tree(active.as_ref(), &running, &screen, &dialogs);
    State { coordinate_space: coordinate_space(active.as_ref()), active, windows, tree, trusted, screenshot: None, notes: Vec::new(), screenshot_hint: None }
}
