//! The hook's decisions, without hooks.
#![cfg(windows)]
use desktop_mcp_windows::control::filter::{Filter, HookEvent, Kind, SIGNATURE};

const BACK: u32 = 0x08;
const SHIFT: u32 = 0xA0;
const CTRL: u32 = 0xA2;
const ALT: u32 = 0xA4;

fn key(kind: Kind, injected: bool, extra: usize) -> HookEvent {
    HookEvent { kind, injected, extra }
}

#[test]
fn our_input_passes_and_the_users_is_swallowed_while_suppressed() {
    let mut filter = Filter::new(500.0);
    let (now, deadline) = (100, 200);
    let ours = filter.decide(key(Kind::KeyDown(0x41), true, SIGNATURE), now, deadline);
    assert!(!ours.swallow && ours.report.is_none());
    let user = filter.decide(key(Kind::KeyDown(0x42), false, 0), now, deadline);
    assert!(user.swallow && user.report.is_some());
    let release = filter.decide(key(Kind::KeyUp(0x42), false, 0), 300, deadline);
    assert!(release.swallow, "a swallowed key's release is swallowed too, even after the deadline");
    let later = filter.decide(key(Kind::KeyDown(0x43), false, 0), 300, deadline);
    assert!(!later.swallow, "past the deadline input passes");
    assert!(filter.decide(key(Kind::KeyDown(0x44), true, 0), now, deadline).swallow, "unsigned injected input (remote desktop) counts as the user's");
}

#[test]
fn the_chord_takes_over() {
    let mut filter = Filter::new(500.0);
    let mut takeovers = 0;
    for vk in [CTRL, ALT, SHIFT, BACK] {
        takeovers += filter.decide(key(Kind::KeyDown(vk), false, 0), 0, 0).takeover as u32;
    }
    assert_eq!(takeovers, 1);
    assert!(filter.decide(key(Kind::KeyUp(BACK), false, 0), 0, 0).swallow, "the chord's keys never reach the app");
}
