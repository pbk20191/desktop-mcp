//! The App tool: launch, switch, resize, move, quit and force_quit.
mod support;
use desktop_mcp_core::app::{quit_targets, FrameChange};
use desktop_mcp_core::wait::RunningApp;
use serde_json::{json, Value};
use std::cell::RefCell;
use std::rc::Rc;
use std::time::Duration;
use support::{call_tool, texts, FakeDesktop};

fn call(desktop: &Rc<FakeDesktop>, arguments: Value) -> Vec<String> {
    texts(&call_tool(desktop, &["App"], "App", arguments))
}
fn app(pid: i32, name: &str, bundle_id: Option<&str>) -> RunningApp {
    RunningApp { pid, name: Some(name.into()), bundle_id: bundle_id.map(Into::into) }
}

#[test]
fn launching_by_name_or_bundle_id() {
    let desktop = Rc::new(FakeDesktop::default());
    assert_eq!(call(&desktop, json!({"name": "Safari"})), ["Launched Safari."]);
    assert_eq!(*desktop.launched.borrow(), ["Safari"]);
    let failing = Rc::new(FakeDesktop { launch_fails: true, ..FakeDesktop::default() });
    assert_eq!(call(&failing, json!({"mode": "launch", "name": "Nope"})), ["Failed to launch Nope."]);
    for arguments in [json!({}), json!({"name": ""})] {
        assert_eq!(call(&desktop, arguments), ["App name or bundle ID required for launch."]);
    }
}

#[test]
fn switching_brings_an_app_forward() {
    let desktop = Rc::new(FakeDesktop { activatable: vec!["Safari"], ..FakeDesktop::default() });
    assert_eq!(call(&desktop, json!({"mode": "switch", "name": "Safari"})), ["Switched to Safari."]);
    assert_eq!(*desktop.slept.borrow(), [Duration::from_millis(200)]);
    assert_eq!(call(&desktop, json!({"mode": "switch", "name": "Nope"})), ["Application 'Nope' not found."]);
    assert_eq!(call(&desktop, json!({"mode": "switch"})), ["App name or bundle ID required for switch."]);
}

#[test]
fn the_front_window_is_resized_or_moved() {
    let desktop = Rc::new(FakeDesktop { front_window: true, ..FakeDesktop::default() });
    assert_eq!(call(&desktop, json!({"mode": "resize", "window_size": [400, 300]})), ["Window resized."]);
    assert_eq!(call(&desktop, json!({"mode": "move", "window_loc": [10, 20]})), ["Window moved."]);
    assert_eq!(*desktop.frame_changes.borrow(), [FrameChange::Size(400.0, 300.0), FrameChange::Position(10.0, 20.0)]);
    assert_eq!(call(&desktop, json!({"mode": "resize"})), ["window_size required for resize mode."]);
    assert_eq!(call(&desktop, json!({"mode": "move"})), ["window_loc required for move mode."]);
    assert_eq!(call(&desktop, json!({"mode": "resize", "window_size": [1]})), ["Error executing tool App: window_size must be a list of exactly 2 integers [width, height]"]);
    assert_eq!(call(&desktop, json!({"mode": "move", "window_loc": [1, 2, 3]})), ["Error executing tool App: window_loc must be a list of exactly 2 integers [x, y]"]);
    assert_eq!(desktop.frame_changes.borrow().len(), 2);
}

#[test]
fn without_a_front_window_nothing_changes() {
    let desktop = Rc::new(FakeDesktop::default());
    assert_eq!(call(&desktop, json!({"mode": "resize", "window_size": [400, 300]})), ["No frontmost window to resize."]);
    assert_eq!(call(&desktop, json!({"mode": "move"})), ["No frontmost window to move."]);
    assert!(desktop.frame_changes.borrow().is_empty());
}

#[test]
fn window_changes_need_accessibility() {
    let desktop = Rc::new(FakeDesktop { untrusted: true, front_window: true, ..FakeDesktop::default() });
    let refused = format!("Error executing tool App: {}", desktop_mcp_core::permissions::ACCESSIBILITY_MESSAGE);
    assert_eq!(call(&desktop, json!({"mode": "resize", "window_size": [400, 300]})), [refused.clone()]);
    assert_eq!(call(&desktop, json!({"mode": "move", "window_loc": [0, 0]})), [refused]);
    assert!(desktop.frame_changes.borrow().is_empty());
}

#[test]
fn quit_asks_every_matching_process() {
    let running = vec![app(10, "Preview", Some("com.apple.Preview")), app(11, "preview", None), app(12, "Safari", None)];
    let desktop = Rc::new(FakeDesktop { looks: RefCell::new(vec![running]), ..FakeDesktop::default() });
    assert_eq!(call(&desktop, json!({"mode": "quit", "name": " Preview "})), ["Quit requested for Preview."]);
    assert_eq!(call(&desktop, json!({"mode": "force_quit", "name": "com.apple.Preview"})), ["Force-quit com.apple.Preview."]);
    assert_eq!(*desktop.terminated.borrow(), [(vec![10, 11], false), (vec![10], true)]);
}

#[test]
fn quit_refusals() {
    let desktop = Rc::new(FakeDesktop { looks: RefCell::new(vec![vec![app(1, "Safari", None)]]), ..FakeDesktop::default() });
    assert_eq!(call(&desktop, json!({"mode": "quit", "name": "Nope"})), ["Error executing tool App: Application 'Nope' is not running."]);
    for name in [json!(null), json!(""), json!("  ")] {
        assert_eq!(call(&desktop, json!({"mode": "force_quit", "name": name})), ["Error executing tool App: App name or bundle ID required for force_quit."]);
    }
    let failing = Rc::new(FakeDesktop { looks: RefCell::new(vec![vec![app(1, "Safari", None)]]), terminate_fails: true, ..FakeDesktop::default() });
    assert_eq!(call(&failing, json!({"mode": "quit", "name": "Safari"})), ["Error executing tool App: Could not quit Safari."]);
    assert_eq!(call(&failing, json!({"mode": "force_quit", "name": "Safari"})), ["Error executing tool App: Could not force-quit Safari."]);
    assert!(desktop.terminated.borrow().is_empty());
}

#[test]
fn quit_never_targets_this_process_or_loginwindow() {
    let running = vec![app(99, "Preview", None), app(10, "Preview", None), app(5, "loginwindow", Some("com.apple.loginwindow"))];
    assert_eq!(quit_targets("Preview", &running, 99), Ok(vec![10]));
    assert_eq!(quit_targets("loginwindow", &running, 99), Err("Application 'loginwindow' is not running.".to_string()));
    assert_eq!(quit_targets("com.apple.loginwindow", &running, 99), Err("Application 'com.apple.loginwindow' is not running.".to_string()));
    assert!(quit_targets("COM.APPLE.LOGINWINDOW", &running, 99).is_err());
}
