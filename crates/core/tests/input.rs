//! Click, Type, Scroll, Move and Shortcut: the events they post, in order, and the pauses between them.
mod support;
use desktop_mcp_core::input::{Button, Event, Modifier};
use serde_json::{json, Value};
use std::rc::Rc;
use std::time::Duration;
use support::{call_tool, texts, FakeDesktop};

const TOOLS: &[&str] = &["Click", "Type", "Scroll", "Move", "Shortcut"];

fn call(desktop: &Rc<FakeDesktop>, name: &str, arguments: Value) -> Vec<String> {
    texts(&call_tool(desktop, TOOLS, name, arguments))
}
fn millis(desktop: &FakeDesktop) -> Vec<u128> {
    desktop.slept.borrow().iter().map(Duration::as_millis).collect()
}
fn down(x: f64, y: f64, button: Button, click: i64) -> Event {
    Event::Down { x, y, button, click }
}
fn up(x: f64, y: f64, button: Button, click: i64) -> Event {
    Event::Up { x, y, button, click }
}
fn key(key: &str, modifiers: &[Modifier]) -> [Event; 2] {
    [Event::KeyDown { key: key.to_string(), modifiers: modifiers.to_vec() }, Event::KeyUp { key: key.to_string(), modifiers: modifiers.to_vec() }]
}

#[test]
fn a_double_click_carries_its_click_states() {
    let desktop = Rc::new(FakeDesktop::default());
    assert_eq!(call(&desktop, "Click", json!({"loc": [1, 2], "button": "right", "clicks": 2})), ["Double right clicked at (1,2)."]);
    let b = Button::Right;
    assert_eq!(*desktop.posted.borrow(), [Event::Move { x: 1.0, y: 2.0 }, down(1.0, 2.0, b, 1), up(1.0, 2.0, b, 1), down(1.0, 2.0, b, 2), up(1.0, 2.0, b, 2)]);
    assert_eq!(millis(&desktop), [50, 30, 50]);
}

#[test]
fn without_accessibility_nothing_is_posted() {
    let desktop = Rc::new(FakeDesktop { untrusted: true, ..FakeDesktop::default() });
    for (name, arguments) in [
        ("Click", json!({"loc": [10, 10]})),
        ("Type", json!({"loc": [10, 10], "text": "x"})),
        ("Scroll", json!({"loc": [10, 10]})),
        ("Move", json!({"loc": [10, 10], "drag": true})),
        ("Shortcut", json!({"shortcut": "command+c"})),
    ] {
        assert_eq!(call(&desktop, name, arguments), [format!("Error executing tool {name}: {}", desktop_mcp_core::permissions::ACCESSIBILITY_MESSAGE)]);
    }
    assert!(desktop.posted.borrow().is_empty());
}

#[test]
fn malformed_shortcuts_press_nothing() {
    let desktop = Rc::new(FakeDesktop::default());
    assert_eq!(call(&desktop, "Shortcut", json!({"shortcut": "command+nosuchkey"})), ["Error executing tool Shortcut: Unknown key: nosuchkey"]);
    assert_eq!(call(&desktop, "Shortcut", json!({"shortcut": "command++c"})), ["Error executing tool Shortcut: shortcut must contain non-empty key names separated by '+'"]);
    assert!(desktop.posted.borrow().is_empty());
}

#[test]
fn typing_clicks_clears_places_the_caret_and_types() {
    let desktop = Rc::new(FakeDesktop::default());
    assert_eq!(call(&desktop, "Type", json!({"loc": [1, 2], "text": "hi\r\n\t", "clear": true, "caret_position": "end", "press_enter": true})), ["Typed hi\r\n\t at (1,2)."]);
    let expected: Vec<Event> = [
        vec![Event::Move { x: 1.0, y: 2.0 }, down(1.0, 2.0, Button::Left, 1), up(1.0, 2.0, Button::Left, 1)],
        key("a", &[Modifier::Command]).to_vec(),
        key("delete", &[]).to_vec(),
        key("right", &[Modifier::Command]).to_vec(),
        vec![Event::ReleaseModifiers, Event::Text("h".into()), Event::Text("i".into())],
        key("return", &[]).to_vec(),
        key("tab", &[]).to_vec(),
        key("return", &[]).to_vec(),
    ]
    .concat();
    assert_eq!(*desktop.posted.borrow(), expected);
    assert_eq!(millis(&desktop), [50, 100, 50, 50, 50, 50, 50, 20, 20, 10, 10, 10, 10, 50]);
}

#[test]
fn typing_into_a_password_field_never_echoes_the_text() {
    let desktop = Rc::new(FakeDesktop { secure_input: true, ..FakeDesktop::default() });
    assert_eq!(call(&desktop, "Type", json!({"loc": [10, 20], "text": "secret"})), ["Typed 6 characters into a secure field at (10,20)."]);
}

#[test]
fn an_abandoned_click_releases_the_button() {
    let desktop = Rc::new(FakeDesktop::default());
    let _held = desktop.hold_next_sleep();
    support::block_on(async {
        tokio::select! {
            biased;
            _ = desktop_mcp_core::input::click(desktop.as_ref(), 5, 6, Button::Left, 1) => panic!("the click finished while held"),
            _ = async {} => {}
        }
    });
    assert_eq!(*desktop.posted.borrow(), [Event::Move { x: 5.0, y: 6.0 }, down(5.0, 6.0, Button::Left, 1), up(5.0, 6.0, Button::Left, 1)]);
}
