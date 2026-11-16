//! Menu bars through the Accessibility API: the items as `desktop_mcp_core::menu::MenuNode`s.
use crate::ax::{Element, Value};
use desktop_mcp_core::menu::{list, resolve, select, shortcut_label, MenuNode, Request};
use objc2_app_kit::{NSApplicationActivationPolicy, NSWorkspace};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

/// A menu bar item or menu item, read once when made.
#[derive(Clone)]
pub struct AxMenuNode {
    element: Element,
    title: String,
    enabled: bool,
    shortcut: Option<String>,
    separator: bool,
    /// Set for the bar items of a select: a bar item that reports no children is pressed once to populate its
    /// menu.
    populates: bool,
    /// Whether that press opened a menu with items; shared by copies.
    opened: Arc<AtomicBool>,
}

fn get<'a>(found: &'a [(String, Value)], name: &str) -> Option<&'a Value> {
    found.iter().find(|(key, _)| key == name).map(|(_, value)| value)
}

fn number(value: Option<&Value>) -> Option<i64> {
    match value {
        Some(Value::Int(n)) => Some(*n),
        Some(Value::Float(n)) => Some(*n as i64),
        Some(Value::Bool(b)) => Some(i64::from(*b)),
        _ => None,
    }
}

impl AxMenuNode {
    pub fn new(element: Element, populates: bool) -> AxMenuNode {
        let found = element
            .attributes(&["AXTitle", "AXEnabled", "AXRole", "AXRoleDescription", "AXMenuItemCmdChar", "AXMenuItemCmdModifiers", "AXMenuItemCmdVirtualKey"])
            .unwrap_or_default();
        let title = get(&found, "AXTitle").map(|v| v.text().to_string()).unwrap_or_default();
        // An item that does not report AXEnabled is treated as enabled.
        let enabled = match get(&found, "AXEnabled") {
            Some(Value::Bool(on)) => *on,
            Some(Value::Int(n)) => *n != 0,
            _ => true,
        };
        let character = get(&found, "AXMenuItemCmdChar").map(|v| match v {
            Value::Text(text) => text.clone(),
            other => format!("{other:?}"),
        });
        let label = shortcut_label(character.as_deref(), number(get(&found, "AXMenuItemCmdModifiers")), number(get(&found, "AXMenuItemCmdVirtualKey")));
        let role = get(&found, "AXRole").map(|v| v.text().to_string()).unwrap_or_default();
        let separator = (title.is_empty() && role == "AXMenuItem" && !enabled) || get(&found, "AXRoleDescription").is_some_and(|v| v.text().to_lowercase() == "separator");
        AxMenuNode { element, title, enabled, shortcut: (!label.is_empty()).then_some(label), separator, populates, opened: Arc::default() }
    }

    /// The items of the single `AXMenu` child, read without opening the menu.
    fn items(&self) -> Vec<Element> {
        let kids = self.element.children();
        if kids.len() == 1 && kids[0].attribute("AXRole").is_some_and(|role| role.text() == "AXMenu") {
            return kids[0].children();
        }
        kids
    }
}

impl MenuNode for AxMenuNode {
    fn title(&self) -> String {
        self.title.clone()
    }
    fn enabled(&self) -> bool {
        self.enabled
    }
    fn shortcut(&self) -> Option<String> {
        self.shortcut.clone()
    }
    fn is_separator(&self) -> bool {
        self.separator
    }

    fn children(&self) -> Vec<AxMenuNode> {
        let mut found = self.items();
        // Some apps build a bar item's menu only when it opens.
        if found.is_empty() && self.populates && self.press() {
            for _ in 0..10 {
                if !found.is_empty() {
                    break;
                }
                std::thread::sleep(Duration::from_millis(50));
                found = self.items();
            }
            if !found.is_empty() {
                self.opened.store(true, Ordering::SeqCst);
            }
        }
        found.into_iter().map(|element| AxMenuNode::new(element, false)).collect()
    }

    fn press(&self) -> bool {
        // An item that opens a modal panel can hold the reply past the messaging timeout; the press was delivered
        // all the same.
        self.element.perform_answering("AXPress")
    }

    fn opened_menu(&self) -> bool {
        self.opened.load(Ordering::SeqCst)
    }

    /// Cancels the open menu through this app's tree (its `AXMenu` child, else the bar item), so no key event
    /// reaches whatever app is frontmost.
    fn cancel(&self) {
        let menu = self.element.children().into_iter().find(|kid| kid.attribute("AXRole").is_some_and(|role| role.text() == "AXMenu"));
        let cancelled = menu.as_ref().unwrap_or(&self.element).perform("AXCancel");
        if !cancelled && menu.is_some() {
            self.element.perform("AXCancel");
        }
    }
}

/// The pid of the app that owns the menu bar (else the frontmost app). NSWorkspace: main thread.
pub fn menu_bar_owner() -> Option<i32> {
    let workspace = NSWorkspace::sharedWorkspace();
    workspace.menuBarOwningApplication().or_else(|| workspace.frontmostApplication()).map(|app| app.processIdentifier())
}

/// The regular app named `app` (localized name ignoring case, or exact bundle id).
fn target(app: &str) -> Option<i32> {
    let wanted = app.trim().to_lowercase();
    NSWorkspace::sharedWorkspace()
        .runningApplications()
        .iter()
        .find(|running| {
            !running.isTerminated()
                && running.activationPolicy() == NSApplicationActivationPolicy::Regular
                && (running.bundleIdentifier().is_some_and(|id| id.to_string() == app) || running.localizedName().is_some_and(|name| name.to_string().to_lowercase() == wanted))
        })
        .map(|running| running.processIdentifier())
}

/// The bar items of the app's menu bar, the messaging timeout set first.
fn roots(app: Option<&str>, owner: Option<i32>, populate: bool) -> Result<Vec<AxMenuNode>, String> {
    let pid = match app {
        Some(app) => target(app).ok_or_else(|| format!("Application '{app}' not found."))?,
        None => owner.ok_or("No menu bar for the frontmost app.")?,
    };
    let element = Element::application(pid);
    element.set_messaging_timeout(0.5);
    let bar = element.attribute("AXMenuBar").and_then(|v| v.element().cloned()).ok_or_else(|| format!("No menu bar for {}.", app.unwrap_or("the frontmost app")))?;
    bar.set_messaging_timeout(0.5);
    Ok(bar.children().into_iter().map(|item| AxMenuNode::new(item, populate)).collect())
}

/// Answers a menu request. Blocks on the app: call it off the main thread, with the owner read on it.
pub fn answer(app: Option<&str>, owner: Option<i32>, request: Request) -> Result<String, String> {
    match request {
        Request::List { path, depth } => {
            let bar = roots(app, owner, false)?;
            match resolve(&path, bar.clone())?.pop() {
                Some(parent) => Ok(list(parent.children(), depth)),
                None => Ok(list(bar, depth)),
            }
        }
        Request::Select { path } => select(&path, roots(app, owner, true)?),
    }
}
