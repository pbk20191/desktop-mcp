//! `Wait until element` on macOS: the element walk over Accessibility nodes, off the main thread.
use crate::ax::{Element, Value};
use desktop_mcp_core::element::{walk, ElementAttributes, ElementNode, FoundElement, NotAnswering, MAX_ELEMENTS};
use std::time::Instant;

/// An element of another app, with a 0.5 s messaging timeout set before its first read.
pub struct AxNode(Element);

impl AxNode {
    pub fn new(element: Element) -> AxNode {
        element.set_messaging_timeout(0.5);
        AxNode(element)
    }
}

impl ElementNode for AxNode {
    fn attributes(&self) -> Result<ElementAttributes, NotAnswering> {
        let found = self.0.attributes(&["AXRole", "AXSubrole", "AXTitle", "AXDescription", "AXPosition", "AXSize"]).ok_or(NotAnswering)?;
        let get = |name: &str| found.iter().find(|(key, _)| key == name).map(|(_, value)| value);
        let text = |name: &str| get(name).map(|v| v.text().to_string()).unwrap_or_default();
        let frame = match (get("AXPosition"), get("AXSize")) {
            (Some(Value::Point(x, y)), Some(Value::Size(w, h))) => Some((*x, *y, *w, *h)),
            _ => None,
        };
        Ok(ElementAttributes { role: text("AXRole"), subrole: get("AXSubrole").map(|v| v.text().to_string()), title: text("AXTitle"), description: text("AXDescription"), frame })
    }

    fn value(&self) -> String {
        self.0.attribute("AXValue").map(|v| v.text().to_string()).unwrap_or_default()
    }

    fn children(&self) -> Result<Vec<AxNode>, NotAnswering> {
        Ok(self.0.elements("AXChildren").ok_or(NotAnswering)?.into_iter().map(AxNode::new).collect())
    }
}

/// The processes to search: the regular apps matching `app`, else the menu bar owner.
pub fn pids(app: Option<&str>, owner: Option<i32>) -> Vec<i32> {
    match app {
        Some(app) => crate::open::running_apps().into_iter().filter(|running| desktop_mcp_core::wait::matches(running.name.as_deref(), running.bundle_id.as_deref(), app)).map(|running| running.pid).collect(),
        None => owner.into_iter().collect(),
    }
}

/// Walks each app's windows until an exact match, sharing one budget; else the first partial match. An app that
/// does not answer is skipped. Blocks: call it off the main thread.
pub fn find(pids: &[i32], title: Option<&str>, role: Option<&str>, deadline: Instant) -> Option<FoundElement> {
    let mut budget = MAX_ELEMENTS;
    let mut fallback = None;
    for &pid in pids {
        let app = Element::application(pid);
        app.set_messaging_timeout(0.5);
        let Some(windows) = app.elements("AXWindows") else { continue };
        let result = walk(windows.into_iter().map(AxNode::new).collect(), title, role, &mut budget, deadline, &|| false);
        if result.exact.is_some() {
            return result.exact;
        }
        fallback = fallback.or(result.partial);
        if result.stopped {
            break;
        }
    }
    fallback
}
