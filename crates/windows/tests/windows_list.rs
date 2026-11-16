//! Windows and displays of the live desktop, read only.
#![cfg(windows)]
use desktop_mcp_windows::{displays, windows_list};

fn per_monitor_aware() {
    use windows::Win32::UI::HiDpi::{SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2};
    // Fails harmlessly when already set.
    let _ = unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
}

#[test]
fn top_windows_are_sane() {
    per_monitor_aware();
    let windows = windows_list::top_windows();
    assert!(!windows.is_empty(), "a desktop session has windows");
    for window in &windows {
        assert!(!window.title.is_empty() && window.pid != 0 && !window.exe.is_empty(), "{window:?}");
        assert!(window.minimized || (window.frame.width > 0.0 && window.frame.height > 0.0), "{window:?}");
    }
    assert!(windows.iter().filter(|window| window.focused).count() <= 1);
}

#[test]
fn a_uwp_frame_is_its_core_window_s_app() {
    let child = |class: &str, pid: u32| (class.to_string(), pid);
    let children = [child("ApplicationFrameTitleBarWindow", 10), child("Windows.UI.Core.CoreWindow", 42)];
    assert_eq!(windows_list::hosted_by(10, &children), Some(42));
    assert_eq!(windows_list::hosted_by(10, &[child("Windows.UI.Core.CoreWindow", 10)]), None, "not the host's own");
    assert_eq!(windows_list::hosted_by(10, &[]), None);
}

#[test]
fn display_frames_are_physical() {
    use windows::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, SM_CXSCREEN, SM_CYSCREEN};
    per_monitor_aware();
    let displays = displays::displays();
    let main = displays.iter().find(|display| display.main).expect("a primary display");
    let (width, height) = unsafe { (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN)) };
    assert_eq!((main.frame.x, main.frame.y, main.frame.width, main.frame.height), (0.0, 0.0, width as f64, height as f64));
    assert!(displays.windows(2).all(|pair| pair[0].frame.x <= pair[1].frame.x), "left to right");
    assert!(displays::coordinate_space(&displays).contains("physical pixels"));
}

#[test]
fn desktop_line_names_and_counts() {
    use desktop_mcp_windows::virtual_desktops::desktop_line;
    let (a, b, c) = (1u128, 2u128, 3u128);
    let names = [(b, "Work".to_string())];
    assert_eq!(desktop_line(&[a, b, c], &names, b).as_deref(), Some("Virtual desktop: Work (2 of 3)"));
    assert_eq!(desktop_line(&[a, b, c], &names, c).as_deref(), Some("Virtual desktop: Desktop 3 (3 of 3)"));
    assert_eq!(desktop_line(&[a, b, c], &names, 9), None);
    assert_eq!(desktop_line(&[a], &names, a), None, "a single desktop");
}
