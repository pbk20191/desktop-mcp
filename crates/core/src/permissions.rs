//! The Permissions tool: the system grants desktop-mcp depends on, as the platform sees them right now.

pub const ACCESSIBILITY_MESSAGE: &str =
    "Accessibility is not granted to desktop-mcp. Grant it in System Settings > Privacy & Security > Accessibility (or call Permissions with request=true), then restart desktop-mcp.";

/// A grant desktop-mcp can ask for. Automation is only reported.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Permission {
    Accessibility,
    ScreenRecording,
}

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

/// The grants `state` lacks, in the order they are asked for.
pub fn missing(state: PermissionState) -> Vec<Permission> {
    [(Permission::Accessibility, state.accessibility), (Permission::ScreenRecording, state.screen_recording)]
        .into_iter()
        .filter(|(_, granted)| !granted)
        .map(|(permission, _)| permission)
        .collect()
}

/// The report, then what was done about each grant that can be asked for.
pub fn format_request(state: PermissionState, asked: &[Permission]) -> String {
    let line = |permission: Permission, name: &str| {
        if asked.contains(&permission) {
            format!("{name}: asked macOS (it prompts the first time) and opened its page in System Settings > Privacy & Security; turn it on there.")
        } else {
            format!("{name}: already granted, nothing asked.")
        }
    };
    let mut lines = vec![format(state), line(Permission::Accessibility, "Accessibility"), line(Permission::ScreenRecording, "Screen Recording")];
    if !asked.is_empty() {
        lines.push("macOS asks for the app that started desktop-mcp, or for desktop-mcp itself under Claude Desktop. After granting, restart desktop-mcp (restart the MCP server, or the host app).".to_string());
    }
    lines.join("\n")
}
