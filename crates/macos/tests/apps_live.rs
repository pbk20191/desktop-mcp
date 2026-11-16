//! Apps and the window server, read-only: nothing is launched, activated or quit.
use desktop_mcp_macos::apps::{by_bundle_id, by_name, name, OnScreenWindow, Rect};
use serde_json::json;

#[test]
fn window_entries_are_read_front_to_back_without_transparent_ones() {
    let entries = [
        json!({"kCGWindowNumber": 7, "kCGWindowOwnerPID": 42, "kCGWindowOwnerName": "Preview", "kCGWindowName": "a.pdf", "kCGWindowLayer": 0, "kCGWindowAlpha": 1, "kCGWindowBounds": {"X": 10, "Y": 20, "Width": 300, "Height": 200}}),
        json!({"kCGWindowNumber": 8, "kCGWindowOwnerPID": 1, "kCGWindowLayer": 25, "kCGWindowAlpha": 0}),
        json!({"kCGWindowNumber": 9, "kCGWindowOwnerPID": 43, "kCGWindowLayer": 24, "kCGWindowBounds": {"X": 0, "Y": 0, "Width": 1, "Height": 1}}),
    ];
    let windows = OnScreenWindow::parse(&entries);
    assert_eq!(windows.len(), 2);
    assert_eq!((windows[0].window_id, windows[0].pid, windows[0].owner_name.as_str(), windows[0].title.as_str(), windows[0].z_index), (7, 42, "Preview", "a.pdf", 0));
    assert_eq!(windows[0].bounds, Rect { left: 10.0, top: 20.0, right: 310.0, bottom: 220.0 });
    assert!(windows[0].is_application() && !windows[1].is_application());
    assert_eq!(windows[1].z_index, 1);
    assert_eq!(OnScreenWindow::frontmost_pid(&windows), Some(42));
}

#[test]
fn the_finder_is_found_by_name_and_bundle_id() {
    let pid = by_bundle_id("com.apple.finder").expect("the Finder runs");
    assert_eq!(by_name("finder"), Some(pid));
    assert!(!name(pid, None).is_empty());
    assert_eq!(by_name("No Such App desktop-mcp"), None);
    assert_eq!(by_bundle_id("com.example.no-such-app"), None);
}

