//! The heartbeat, driven by hand with a fake queue (no hooks).
#![cfg(windows)]
use desktop_mcp_windows::control::coordinator::Coordinator;
use desktop_mcp_windows::control::machine::State;
use desktop_mcp_windows::control::monitor::Seen;
use desktop_mcp_windows::control::policy::Policy;
use std::time::Duration;

#[test]
fn a_takeover_mid_call_preempts_it_and_stops_suppression() {
    let (sender, receiver) = std::sync::mpsc::sync_channel(16);
    let coordinator = Coordinator::new(Policy::Relaxed, Duration::from_secs(10), 500.0, receiver);
    let lease = coordinator.begin("Click").expect("ready");
    coordinator.overlay_shown();
    coordinator.beat();
    assert!(coordinator.deadline() > desktop_mcp_windows::control::monitor::now_ms(), "suppressing while the AI acts");
    sender.send(Seen::Takeover).unwrap();
    coordinator.beat();
    assert_eq!(coordinator.state(), State::User);
    assert!(coordinator.preempted(&lease));
    assert_eq!(coordinator.deadline(), 0, "input passes again");
    coordinator.end(lease);
}
