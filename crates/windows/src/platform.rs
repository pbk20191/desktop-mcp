//! What Windows changes in the shared tools' text, and which tools this build offers.
use desktop_mcp_core::platform::{Platform, ToolText};

/// The tools offered on Windows in this build (W1: the ones that need no desktop access).
pub const TOOLS: &[&str] = &["Clipboard", "Notification", "Scrape", "Shell", "Wait"];

pub struct WindowsPlatform;

impl Platform for WindowsPlatform {
    fn text(&self, tool: &str) -> Option<ToolText> {
        match tool {
            "Shell" => Some(crate::shell::text()),
            "Notification" => Some(crate::notification::text()),
            _ => None,
        }
    }
}
