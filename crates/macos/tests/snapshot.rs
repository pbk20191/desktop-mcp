//! A real snapshot, read-only: apps are listed, and with Accessibility, elements with unique ids.
use desktop_mcp_macos::snapshot::state;

#[test]
fn a_snapshot_lists_apps_and_elements() {
    let state = state(None);
    assert!(!state.coordinate_space.is_empty());
    if !state.trusted {
        return eprintln!("    skipped: Accessibility is not granted to the test runner");
    }
    assert!(!state.tree.interactive.is_empty(), "the Dock and the menu bar always have elements");
    let text = state.tree.interactive_text();
    assert!(text.starts_with("# id|window|control_type|name|coords|metadata\n0|"), "{text}");
}
