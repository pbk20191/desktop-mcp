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
fn input_reaches_the_frontmost_window_under_the_point() {
    let entries = [
        json!({"kCGWindowNumber": 1, "kCGWindowOwnerPID": 50, "kCGWindowOwnerName": "Dock", "kCGWindowLayer": 20, "kCGWindowBounds": {"X": 0, "Y": 0, "Width": 1000, "Height": 1100}}),
        json!({"kCGWindowNumber": 2, "kCGWindowOwnerPID": 51, "kCGWindowOwnerName": "Claude", "kCGWindowLayer": 0, "kCGWindowBounds": {"X": 0, "Y": 0, "Width": 500, "Height": 500}}),
        json!({"kCGWindowNumber": 3, "kCGWindowOwnerPID": 52, "kCGWindowOwnerName": "TextEdit", "kCGWindowLayer": 0, "kCGWindowBounds": {"X": 0, "Y": 0, "Width": 1000, "Height": 1000}}),
    ];
    let windows = OnScreenWindow::parse(&entries);
    let owner = |point| OnScreenWindow::at(&windows, point).map(|w| w.owner_name.as_str());
    assert_eq!(owner(Some((100.0, 100.0))), Some("Claude"), "the window in front, not the one meant");
    assert_eq!(owner(Some((700.0, 100.0))), Some("TextEdit"));
    assert_eq!(owner(Some((700.0, 1050.0))), Some("Dock"), "the Dock's screen-wide window only where no app's is");
    assert_eq!(owner(Some((2000.0, 0.0))), None);
    assert_eq!(owner(None), Some("Claude"), "keys go to the front window");
}

#[test]
fn the_finder_is_found_by_name_and_bundle_id() {
    let pid = by_bundle_id("com.apple.finder").expect("the Finder runs");
    assert_eq!(by_name("finder"), Some(pid));
    assert!(!name(pid, None).is_empty());
    assert_eq!(by_name("No Such App desktop-mcp"), None);
    assert_eq!(by_bundle_id("com.example.no-such-app"), None);
}

