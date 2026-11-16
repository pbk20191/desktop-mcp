//! Applications and their windows as the window server and NSWorkspace see them.
use crate::ax::Element;
use desktop_mcp_core::app::FrameChange;
use objc2_app_kit::{NSApplicationActivationOptions, NSApplicationActivationPolicy, NSRunningApplication, NSWorkspace};
use objc2_core_graphics::{CGWindowListCopyWindowInfo, CGWindowListOption};
use serde_json::Value;
use std::time::Duration;

/// Window server stacking levels (kCGWindowLayer).
pub const NORMAL_LEVEL: i64 = 0;
pub const DOCK_LEVEL: i64 = 20;

/// A rectangle in points, top-left origin.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Rect {
    pub left: f64,
    pub top: f64,
    pub right: f64,
    pub bottom: f64,
}

impl Rect {
    pub fn new(x: f64, y: f64, width: f64, height: f64) -> Rect {
        Rect { left: x, top: y, right: x + width, bottom: y + height }
    }
}

/// One entry of the window server's on-screen list, front to back.
#[derive(Clone, Debug, PartialEq)]
pub struct OnScreenWindow {
    pub window_id: i64,
    pub pid: i32,
    pub owner_name: String,
    pub title: String,
    pub layer: i64,
    pub z_index: usize,
    pub bounds: Rect,
}

impl OnScreenWindow {
    /// An application's window, as opposed to the desktop or the Dock and menu bar above.
    pub fn is_application(&self) -> bool {
        (NORMAL_LEVEL..DOCK_LEVEL).contains(&self.layer)
    }

    /// The on-screen windows, front to back, without the desktop and fully transparent ones.
    pub fn all() -> Vec<OnScreenWindow> {
        let list = CGWindowListCopyWindowInfo(CGWindowListOption::OptionOnScreenOnly | CGWindowListOption::ExcludeDesktopElements, 0);
        let Some(Value::Array(entries)) = list.and_then(|list| crate::plist::to_json(&list)) else { return vec![] };
        Self::parse(&entries)
    }

    /// Entries of `CGWindowListCopyWindowInfo`, as JSON.
    pub fn parse(entries: &[Value]) -> Vec<OnScreenWindow> {
        let mut windows = Vec::new();
        for info in entries {
            if info["kCGWindowAlpha"].as_f64() == Some(0.0) {
                continue;
            }
            let bounds = &info["kCGWindowBounds"];
            let number = |value: &Value| value.as_f64().unwrap_or(0.0);
            windows.push(OnScreenWindow {
                window_id: info["kCGWindowNumber"].as_i64().unwrap_or(0),
                pid: info["kCGWindowOwnerPID"].as_i64().unwrap_or(0) as i32,
                owner_name: info["kCGWindowOwnerName"].as_str().unwrap_or_default().to_string(),
                title: info["kCGWindowName"].as_str().unwrap_or_default().to_string(),
                layer: info["kCGWindowLayer"].as_i64().unwrap_or(0),
                z_index: windows.len(),
                bounds: Rect::new(number(&bounds["X"]), number(&bounds["Y"]), number(&bounds["Width"]), number(&bounds["Height"])),
            });
        }
        windows
    }

    /// The active application: owner of the front window at the normal level.
    pub fn frontmost_pid(screen: &[OnScreenWindow]) -> Option<i32> {
        screen.iter().find(|window| window.layer == NORMAL_LEVEL && window.pid != 0).map(|window| window.pid)
    }
}

fn running() -> Vec<objc2::rc::Retained<NSRunningApplication>> {
    NSWorkspace::sharedWorkspace().runningApplications().iter().filter(|app| !app.isTerminated()).collect()
}

/// The app's title as its menu bar shows it (the AX title), else its localized name.
pub fn name(pid: i32, localized: Option<&str>) -> String {
    let title = Element::application(pid).attribute("AXTitle").map(|title| title.text().to_string()).unwrap_or_default();
    if title.is_empty() { localized.unwrap_or_default().to_string() } else { title }
}

/// The process with this bundle id. Several can share one (a headless Chrome next to the browser): the one with
/// on-screen windows wins, then the more user-facing activation policy.
pub fn by_bundle_id(bundle_id: &str) -> Option<i32> {
    let mut matches: Vec<(i32, isize)> = running()
        .iter()
        .filter(|app| app.bundleIdentifier().is_some_and(|id| id.to_string() == bundle_id))
        .map(|app| (app.processIdentifier(), app.activationPolicy().0))
        .collect();
    if matches.len() > 1 {
        let on_screen: Vec<i32> = OnScreenWindow::all().iter().filter(|window| window.is_application()).map(|window| window.pid).collect();
        matches.sort_by_key(|(pid, policy)| (!on_screen.contains(pid), *policy));
    }
    matches.first().map(|(pid, _)| *pid)
}

/// The process whose localized name, then whose AX title, is `name`, ignoring case.
pub fn by_name(name: &str) -> Option<i32> {
    let wanted = name.trim().to_lowercase();
    let running = running();
    let localized = |app: &NSRunningApplication| app.localizedName().map(|name| name.to_string());
    if let Some(app) = running.iter().find(|app| localized(app).is_some_and(|name| name.to_lowercase() == wanted)) {
        return Some(app.processIdentifier());
    }
    running.iter().find(|app| self::name(app.processIdentifier(), localized(app).as_deref()).to_lowercase() == wanted).map(|app| app.processIdentifier())
}

/// The app whose window is frontmost, per the window server (reliable without AppKit events).
pub fn frontmost() -> Option<i32> {
    OnScreenWindow::frontmost_pid(&OnScreenWindow::all())
}

/// Brings the app forward with every window. Cooperative activation can refuse a background caller; AXFrontmost
/// does not.
pub fn activate(pid: i32) -> bool {
    let mut activated = NSRunningApplication::runningApplicationWithProcessIdentifier(pid).is_some_and(|app| {
        #[allow(deprecated)]
        app.activateWithOptions(NSApplicationActivationOptions::ActivateAllWindows)
    });
    if Element::application(pid).set_bool("AXFrontmost", true) {
        activated = true;
    }
    activated
}

/// Finds the app by name, then by bundle id, and brings it forward; `false` when none is running. Blocks on
/// other apps: call it off the main thread.
pub fn activate_by_name(name: &str) -> bool {
    match by_name(name).or_else(|| by_bundle_id(name)) {
        Some(pid) => {
            activate(pid);
            true
        }
        None => false,
    }
}

/// `open -a` takes a name or a path; `open -b` a bundle id.
pub async fn launch(name: &str) -> bool {
    for flag in ["-a", "-b"] {
        let argv = vec!["/usr/bin/open".to_string(), flag.to_string(), name.to_string()];
        if crate::process::run(argv, Duration::from_secs(15)).await.is_ok_and(|run| run.status == 0) {
            return true;
        }
    }
    false
}

/// Applies `change` to the frontmost app's main window; whether there is one. Blocks on other apps: call it off
/// the main thread.
pub fn change_front_window(change: Option<FrameChange>) -> bool {
    let Some(window) = frontmost().and_then(|pid| Element::application(pid).attribute("AXMainWindow")?.element().cloned()) else { return false };
    match change {
        Some(FrameChange::Size(width, height)) => {
            window.set_size(width, height);
        }
        Some(FrameChange::Position(x, y)) => {
            window.set_position(x, y);
        }
        None => {}
    }
    true
}

/// Asks the processes to quit (or force-quits them); whether any was signalled. NSRunningApplication on the main
/// thread.
pub fn terminate(pids: &[i32], force: bool) -> bool {
    let signalled: Vec<bool> = pids
        .iter()
        .map(|pid| NSRunningApplication::runningApplicationWithProcessIdentifier(*pid).is_some_and(|app| if force { app.forceTerminate() } else { app.terminate() }))
        .collect();
    signalled.contains(&true)
}

/// Whether the policy is one that shows in the Dock or the menu bar.
pub fn user_facing(policy: NSApplicationActivationPolicy) -> bool {
    matches!(policy, NSApplicationActivationPolicy::Regular | NSApplicationActivationPolicy::Accessory)
}
