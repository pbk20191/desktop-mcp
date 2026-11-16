//! Finding elements in a real app's windows, read-only.
mod support;
use desktop_mcp_core::window::WindowSource;
use desktop_mcp_macos::ax::trusted;
use desktop_mcp_macos::element::find;
use desktop_mcp_macos::main_loop::run;
use desktop_mcp_macos::windows::AxWindowSource;
use std::time::{Duration, Instant};

fn a_window_is_found_by_role_and_a_missing_title_is_not() {
    if !trusted() {
        return eprintln!("    skipped: Accessibility is not granted to the test runner");
    }
    let Some(window) = run(|_| AxWindowSource.windows(None)).into_iter().find(|w| !w.minimized) else { return eprintln!("    skipped: no windows") };
    let found = find(&[window.pid], None, Some("AXWindow"), Instant::now() + Duration::from_secs(5)).expect("its window");
    assert_eq!(found.role, "AXWindow");
    let started = Instant::now();
    assert_eq!(find(&[window.pid], Some("no such title desktop-mcp 1234"), None, Instant::now() + Duration::from_secs(2)), None);
    assert!(started.elapsed() < Duration::from_secs(4), "took {:?}", started.elapsed());
}

fn main() {
    support::run(&[("a_window_is_found_by_role_and_a_missing_title_is_not", a_window_is_found_by_role_and_a_missing_title_is_not)])
}
