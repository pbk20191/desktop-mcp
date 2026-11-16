//! What Windows changes in the shared tools' text, the tools only Windows has, and which tools this build offers.
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_core::platform::{Platform, PlatformTool, ToolText};
use std::rc::Rc;

/// The tools offered on Windows. `OCR` comes when included.
pub const TOOLS: &[&str] = &[
    "App", "Batch", "Click", "Clipboard", "Move", "MultiEdit", "MultiSelect", "Notification", "Scrape", "Screenshot", "Scroll", "Shell", "Shortcut", "Snapshot", "Type", "Wait", "Window",
];

const SHORTCUT: &str = "Executes keyboard shortcuts using key combinations separated by +. Examples: \"ctrl+c\" (copy), \"ctrl+v\" (paste), \"alt+tab\" (switch windows), \"win+r\" (Run), \"ctrl+shift+esc\" (Task Manager). Modifiers: ctrl, shift, alt, win.";

const APP: &str = "Manages Windows applications with six modes: 'launch' (opens an app by its Start menu name, or an executable by its full path), 'resize' (adjusts the focused window's size), 'move' (repositions the focused window), 'switch' (brings the named app's window to the front), 'quit' (asks every window of that app to close; the app may ask to save), 'force_quit' (ends its processes without asking). 'name' is the app's name, executable or Start menu name. Quit never targets desktop-mcp itself or the Windows shell.";

const WINDOW: &str = "Lists and arranges application windows. 'list' (default) returns one line per window: '<id>\\t<app>\\t\"<title>\"\\t[x,y,w,h]\\t<flags>' (flags: focused, minimized, fullscreen), optionally only for 'app' (application name or executable). The other modes act on the window 'id' from list: 'focus', 'move' (to 'loc' [x, y]), 'resize' (to 'size' [width, height]), 'minimize', 'restore', 'close' (asks the window to close; the app may ask to save), 'maximize' (fills its display minus the taskbar), 'fullscreen', 'exit_fullscreen', 'display' (moves it to display index 'display', same relative position, kept inside that display). 'displays' (no id) returns one line per display: '<index>\\t<name>\\t[x,y,w,h]\\tvisible [x,y,w,h]\\tscale <s>', then 'main' for the primary one, in the same physical screen pixels as window frames. 'maximize' and 'display' refuse a window in full screen or minimized. A window that closed since it was listed is refused; list again.";

const WAIT: &str = "Waits a fixed 'duration' in seconds, or with 'until' waits for a condition, checking with backoff until it holds or 'timeout' seconds (default 10) pass, then fails. 'duration' is ignored with 'until'. Conditions: 'app_running' / 'app_quit' ('app': application name or executable); 'window' / 'window_gone' ('app' and/or 'title', a case-insensitive substring; 'window' returns the matching Window list lines); 'element' ('title' and/or 'role', a UI Automation control type such as Button, Edit or MenuItem, in 'app' or else the focused window's app; title matches the element's name; returns '<role> \"<title>\" at [x,y]', the centre in Click coordinates).";

const OCR: &str = "Reads the text shown on screen with Windows OCR and returns one line per text line, top to bottom: '[cx,cy] \"<text>\" (w×h, confidence 1.00)', where [cx,cy] is the line's centre in the screen coordinates Click takes. 'region' [x, y, width, height] in screen pixels (default the focused window, else the primary display); 'languages' (default [\"ko-KR\", \"en-US\"]; the first one with Windows OCR installed is used, else the user's languages). 'level' and 'min_confidence' are accepted but change nothing on Windows, which reports no confidence.";

pub struct WindowsPlatform {
    desktop: Rc<dyn Desktop>,
}

impl WindowsPlatform {
    /// The platform for a server over `desktop` (its own tools act through it).
    pub fn new(desktop: Rc<dyn Desktop>) -> WindowsPlatform {
        WindowsPlatform { desktop }
    }
}

impl Platform for WindowsPlatform {
    fn text(&self, tool: &str) -> Option<ToolText> {
        let described = |description| Some(ToolText { description: Some(description), schema_patches: Vec::new() });
        match tool {
            "Shell" => Some(crate::shell::text()),
            "Notification" => Some(crate::notification::text()),
            "Window" => described(WINDOW),
            "Wait" => described(WAIT),
            "OCR" => described(OCR),
            "Shortcut" => described(SHORTCUT),
            "App" => described(APP),
            _ => None,
        }
    }

    fn tools(&self) -> Vec<PlatformTool> {
        let mut tools = vec![crate::screenshot_tool::tool()];
        tools.extend(crate::multi_tools::tools(self.desktop.clone()));
        tools
    }
}
