//! Mission Control Spaces. There is no public API to create a Space, so this is GUI automation: open Mission
//! Control, press its "Add Desktop" control, and check that the number of Spaces rose.
//!
//! Mission Control's tree belongs to WindowManager on macOS 27 (the Dock before): per display an `mc.display` group
//! holding `mc.spaces`, which holds the Spaces (`mc.spaces.list`) and the add button (`mc.spaces.add`). The
//! identifiers do not depend on the language; the English labels are a fallback.
use crate::ax::Element;
use crate::tuning::{SPACES_ADDED_SETTLE, SPACES_CLOSE_SETTLE, SPACES_POLL, SPACES_SEARCH_DEPTH};
use desktop_mcp_core::input::{self, Event};
use desktop_mcp_core::mcp::Desktop;
use std::time::{Duration, Instant};

const OWNERS: [&str; 2] = ["com.apple.WindowManager", "com.apple.dock"];
const ADD_IDENTIFIER: &str = "mc.spaces.add";
const LIST_IDENTIFIER: &str = "mc.spaces.list";
const MISSION_CONTROL: &str = "/System/Applications/Mission Control.app";
const ADD_DESKTOP_LABELS: [&str; 5] = ["add desktop", "new desktop", "add space", "new space", "add space desktop"];

fn text_blob(element: &Element) -> String {
    let found = element.attributes(&["AXDescription", "AXTitle", "AXHelp", "AXIdentifier", "AXRoleDescription"]).unwrap_or_default();
    found.iter().map(|(_, value)| value.text()).collect::<Vec<_>>().join(" ").to_lowercase()
}

fn identifier(element: &Element) -> Option<String> {
    element.attribute("AXIdentifier").map(|value| value.text().to_string())
}

fn is_add_desktop(element: &Element) -> bool {
    if identifier(element).as_deref() == Some(ADD_IDENTIFIER) {
        return true;
    }
    let text = text_blob(element);
    !text.trim().is_empty() && ADD_DESKTOP_LABELS.iter().any(|label| text.contains(label))
}

/// The add control of the first display Mission Control shows, a button preferred. Blocks: call it off the main
/// thread.
fn find_add_desktop() -> Option<Element> {
    let mut matches = Vec::new();
    let mut queue: std::collections::VecDeque<_> =
        OWNERS.iter().filter_map(|owner| crate::apps::by_bundle_id(owner)).map(|pid| (Element::application(pid), 0)).collect();
    while let Some((element, depth)) = queue.pop_front() {
        if depth > 0 && is_add_desktop(&element) {
            matches.push(element.clone());
        }
        if depth < SPACES_SEARCH_DEPTH {
            queue.extend(element.children().into_iter().map(|child| (child, depth + 1)));
        }
    }
    let button = matches.iter().position(|m| m.attribute("AXRole").is_some_and(|r| r.text() == "AXButton"));
    button.map(|i| matches.swap_remove(i)).or_else(|| matches.into_iter().next())
}

async fn wait_for_add_desktop(desktop: &dyn Desktop, wait: Duration) -> Option<Element> {
    let deadline = Instant::now() + wait;
    loop {
        if let Some(found) = crate::main_loop::offload(find_add_desktop).await {
            return Some(found);
        }
        if Instant::now() >= deadline {
            return None;
        }
        desktop.sleep(SPACES_POLL).await;
    }
}

/// Spaces: the entries of the add control's sibling list, else (older layouts) its siblings that are not it.
fn count_spaces(button: &Element) -> Option<usize> {
    let parent = button.attribute("AXParent")?.element()?.clone();
    let siblings = parent.children();
    match siblings.iter().find(|sibling| identifier(sibling).as_deref() == Some(LIST_IDENTIFIER)) {
        Some(list) => Some(list.children().len()),
        None => Some(siblings.iter().filter(|sibling| !is_add_desktop(sibling)).count()),
    }
}

async fn close(desktop: &dyn Desktop, wait: Duration) {
    desktop.post(Event::KeyDown { key: "escape".into(), modifiers: vec![] });
    desktop.post(Event::KeyUp { key: "escape".into(), modifiers: vec![] });
    desktop.sleep(wait).await;
}

/// Creates a Space and says how it went.
pub async fn create(desktop: &dyn Desktop, open_delay: f64, close_after: bool) -> String {
    match try_create(desktop, open_delay, close_after).await {
        Ok(message) => message,
        Err(message) => format!("Failed: {message}"),
    }
}

async fn try_create(desktop: &dyn Desktop, open_delay: f64, close_after: bool) -> Result<String, String> {
    let open_delay = Duration::from_secs_f64(open_delay.max(0.0));
    // Launching Mission Control works whatever its keyboard shortcut is; control+up is the fallback.
    let target = desktop_mcp_core::open::Target::File(MISSION_CONTROL.into());
    if crate::open::open(target, None).await.is_err() {
        input::shortcut(desktop, "control+up").await?;
    }
    desktop.sleep(open_delay).await;
    if let Some(button) = wait_for_add_desktop(desktop, open_delay.max(Duration::from_secs(1))).await {
        let before = crate::main_loop::offload({
            let button = button.clone();
            move || count_spaces(&button)
        })
        .await;
        // AXPress first, then a click at the control's centre.
        let pressed = crate::main_loop::offload({
            let button = button.clone();
            move || button.perform("AXPress")
        })
        .await;
        let activated = pressed || {
            let centre = crate::main_loop::offload(move || {
                let found = button.attributes(&["AXPosition", "AXSize"]).unwrap_or_default();
                let get = |name: &str| found.iter().find(|(key, _)| key == name).map(|(_, v)| v.clone());
                match (get("AXPosition"), get("AXSize")) {
                    (Some(crate::ax::Value::Point(x, y)), Some(crate::ax::Value::Size(w, h))) => Some(((x + w / 2.0) as i64, (y + h / 2.0) as i64)),
                    _ => None,
                }
            })
            .await;
            if let Some((x, y)) = centre {
                input::click(desktop, x, y, input::Button::Left, 1).await;
            }
            centre.is_some()
        };
        if activated {
            desktop.sleep(SPACES_ADDED_SETTLE).await;
            let after = match wait_for_add_desktop(desktop, Duration::from_millis(500)).await {
                Some(button) => crate::main_loop::offload(move || count_spaces(&button)).await,
                None => None,
            };
            if close_after {
                close(desktop, SPACES_CLOSE_SETTLE).await;
            }
            return match (before, after) {
                (Some(before), Some(after)) if after > before => Ok(format!("Created new desktop space ({after} total).")),
                (Some(before), Some(after)) => Err(format!("Add Desktop control was activated but the space count did not increase ({before} -> {after}).")),
                // The count could not be read through Accessibility; trust the activation.
                _ => Ok("Created new desktop space.".to_string()),
            };
        }
    }
    if close_after {
        close(desktop, SPACES_CLOSE_SETTLE).await;
    }
    Err("Could not find Mission Control 'Add Desktop' button via Accessibility. Ensure Accessibility is granted and Mission Control is available.".to_string())
}
