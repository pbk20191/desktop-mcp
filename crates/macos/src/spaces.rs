//! Mission Control Spaces. There is no public API to create a Space, so this is GUI automation: open Mission
//! Control, press the Dock's "Add Desktop" control, and check that the number of Spaces rose.
use crate::ax::Element;
use crate::tuning::{SPACES_ADDED_SETTLE, SPACES_CLOSE_SETTLE, SPACES_POLL, SPACES_SEARCH_DEPTH};
use desktop_mcp_core::input::{self, Event};
use desktop_mcp_core::mcp::Desktop;
use std::time::{Duration, Instant};

const ADD_DESKTOP_LABELS: [&str; 5] = ["add desktop", "new desktop", "add space", "new space", "add space desktop"];

fn text_blob(element: &Element) -> String {
    let found = element.attributes(&["AXDescription", "AXTitle", "AXHelp", "AXIdentifier", "AXRoleDescription"]).unwrap_or_default();
    found.iter().map(|(_, value)| value.text()).collect::<Vec<_>>().join(" ").to_lowercase()
}

fn is_add_desktop(element: &Element) -> bool {
    let text = text_blob(element);
    !text.trim().is_empty() && ADD_DESKTOP_LABELS.iter().any(|label| text.contains(label))
}

/// The Dock's add control (Mission Control's Spaces bar lives in the Dock's tree), a button preferred. Blocks:
/// call it off the main thread.
fn find_add_desktop() -> Option<Element> {
    let pid = crate::apps::by_bundle_id("com.apple.dock").or_else(|| crate::apps::by_name("Dock"))?;
    let mut matches = Vec::new();
    let mut queue = std::collections::VecDeque::from([(Element::application(pid), 0)]);
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

/// Spaces: the add control's siblings that are not the add control.
fn count_spaces(button: &Element) -> Option<usize> {
    let parent = button.attribute("AXParent")?.element()?.clone();
    Some(parent.children().iter().filter(|child| !is_add_desktop(child)).count())
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
    input::shortcut(desktop, "control+up").await?;
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
