//! The Permissions tool: the system grants desktop-mcp depends on, as the platform sees them right now.

pub const ACCESSIBILITY_MESSAGE: &str =
    "Accessibility is not granted to desktop-mcp. Grant it in System Settings > Privacy & Security > Accessibility, then restart desktop-mcp.";

/// Apple Events to System Events. Reported, never requested.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Automation {
    Granted,
    Denied,
    NotDetermined,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PermissionState {
    pub accessibility: bool,
    pub screen_recording: bool,
    pub automation: Automation,
}

pub fn format(state: PermissionState) -> String {
    let granted = |on: bool| if on { "granted" } else { "not granted" };
    let automation = match state.automation {
        Automation::Granted => "granted",
        Automation::Denied => "not granted",
        Automation::NotDetermined => "not determined",
    };
    [
        format!("Accessibility: {}", granted(state.accessibility)),
        format!("Screen Recording: {}", granted(state.screen_recording)),
        format!("Automation (System Events): {automation}"),
        "Needs Accessibility: App (resize, move), Click, Type, Scroll, Move, Shortcut, Snapshot, Desktop, Window, Menu, Wait (until)".to_string(),
        "Needs Screen Recording: Snapshot (use_vision), OCR".to_string(),
    ]
    .join("\n")
}
