//! The permission checks answer without prompting, and agree with the system.
use desktop_mcp_macos::permissions::check;
use std::time::{Duration, Instant};

#[test]
fn the_checks_answer_promptly_and_agree_with_the_system() {
    let start = Instant::now();
    let state = check();
    assert!(start.elapsed() < Duration::from_secs(5), "took {:?}", start.elapsed());
    assert_eq!(state.accessibility, unsafe { objc2_application_services::AXIsProcessTrusted() });
    assert_eq!(state.screen_recording, objc2_core_graphics::CGPreflightScreenCaptureAccess());
    assert_eq!(check(), state, "a second look sees the same");
}
