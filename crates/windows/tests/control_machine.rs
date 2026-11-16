//! The control model: states, the lease, takeover, idle return, and the JSON the client sees.
#![cfg(windows)]
use desktop_mcp_windows::control::machine::{Code, Machine, Physical, State};
use desktop_mcp_windows::control::policy::{gated, Policy};
use desktop_mcp_windows::control::status::rejection;
use serde_json::json;
use std::sync::{Arc, Mutex};
use std::time::Duration;

fn machine() -> (Machine, Arc<Mutex<Duration>>) {
    let now = Arc::new(Mutex::new(Duration::ZERO));
    let clock = now.clone();
    (Machine::new(Policy::Relaxed, Duration::from_secs(10), Box::new(move || *clock.lock().unwrap())), now)
}

fn advance(now: &Mutex<Duration>, seconds: f64) {
    *now.lock().unwrap() += Duration::from_secs_f64(seconds);
}

#[test]
fn ready_to_ai_and_back_with_the_lease() {
    let (mut machine, now) = machine();
    assert_eq!(machine.state(), State::Ready);
    let lease = machine.begin_call().unwrap();
    assert_eq!(machine.state(), State::Ai);
    machine.end_call(lease);
    advance(&now, 14.0);
    machine.tick();
    assert_eq!(machine.state(), State::Ai, "the 15 s lease holds");
    advance(&now, 1.5);
    machine.tick();
    assert_eq!(machine.state(), State::Ready);
}

#[test]
fn physical_input_takes_over_and_idles_back() {
    let (mut machine, now) = machine();
    let lease = machine.begin_call().unwrap();
    machine.overlay_shown();
    machine.takeover();
    assert_eq!(machine.state(), State::User);
    assert!(machine.preempted(&lease));
    assert_eq!(machine.begin_call().unwrap_err(), Code::UserControl);
    advance(&now, 9.0);
    machine.tick();
    assert_eq!(machine.state(), State::User);
    advance(&now, 1.5);
    machine.tick();
    assert_eq!(machine.state(), State::Ready);
}

#[test]
fn a_key_down_in_ready_hands_control_to_the_user() {
    let (mut machine, _) = machine();
    machine.physical(Physical::Down(0x41));
    assert_eq!(machine.state(), State::User);
}

#[test]
fn held_input_refuses_a_call() {
    let (mut machine, now) = machine();
    machine.physical(Physical::Down(0x10));
    advance(&now, 11.0);
    machine.tick();
    assert_eq!(machine.state(), State::User, "a held key keeps the user in control");
    machine.physical(Physical::Up(0x10));
    advance(&now, 10.5);
    machine.tick();
    assert_eq!(machine.state(), State::Ready);
    machine.physical_held(true);
    assert_eq!(machine.begin_call().unwrap_err(), Code::PhysicalInputHeld);
}

#[test]
fn leaving_the_input_desktop_fails_open() {
    let (mut machine, now) = machine();
    machine.begin_call().unwrap();
    machine.overlay_shown();
    assert!(machine.wants_suppression());
    machine.desktop_lost();
    assert_eq!(machine.state(), State::Unavailable);
    assert!(!machine.wants_suppression());
    assert_eq!(machine.begin_call().unwrap_err(), Code::ControlUnavailable);
    machine.desktop_back();
    assert_eq!(machine.state(), State::User, "never straight back to ai");
    advance(&now, 10.5);
    machine.tick();
    assert_eq!(machine.state(), State::Ready);
}

#[test]
fn suppression_waits_for_the_indicator() {
    let (mut machine, _) = machine();
    machine.begin_call().unwrap();
    assert!(!machine.wants_suppression());
    machine.physical(Physical::Down(0x41));
    assert_eq!(machine.state(), State::User, "input before the indicator shows hands control back");
}

#[test]
fn rejection_json_matches_upstream() {
    let (mut machine, _) = machine();
    machine.physical(Physical::Down(0x41));
    machine.physical(Physical::Up(0x41));
    let text = rejection(Code::UserControl, &machine.status(), None);
    assert_eq!(text, r#"{"code": "USER_CONTROL", "status": {"state": "user", "generation": 1, "wait_seconds": 10.0, "enabled": true, "policy": "relaxed", "idle_timeout_seconds": 10.0}}"#);
    assert!(rejection(Code::ControlPreempted, &machine.status(), Some(3)).ends_with(r#", "executed_steps": 3}"#));
}

#[test]
fn relaxed_and_strict_policies() {
    let none = serde_json::Map::new();
    let mode = |value: &str| json!({ "mode": value }).as_object().unwrap().clone();
    assert!(gated(Policy::Relaxed, "Click", &none));
    assert!(gated(Policy::Relaxed, "App", &none));
    assert!(!gated(Policy::Relaxed, "Window", &none), "list by default");
    assert!(gated(Policy::Relaxed, "Window", &mode("move")));
    assert!(!gated(Policy::Relaxed, "Clipboard", &none) && gated(Policy::Relaxed, "Clipboard", &mode("write")));
    assert!(!gated(Policy::Relaxed, "Snapshot", &none) && !gated(Policy::Relaxed, "Shell", &none));
    assert!(gated(Policy::Strict, "Snapshot", &none) && gated(Policy::Strict, "Shell", &none));
    assert!(!gated(Policy::Strict, "ControlStatus", &none));
}
