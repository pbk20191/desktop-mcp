mod support;
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_macos::desktop::MacDesktop;
use desktop_mcp_macos::main_loop::{run, turns};
use std::time::{Duration, Instant};

fn sleep_is_exact() {
    let before = turns();
    let took = run(|_| async {
        let start = Instant::now();
        MacDesktop::new().sleep(Duration::from_millis(120)).await; // no tick is set: this is not a tokio timer
        start.elapsed()
    });
    assert!(took >= Duration::from_millis(120) && took < Duration::from_millis(160), "took {took:?}");
    assert!(turns() - before <= 3, "{} runtime turns for one sleep", turns() - before);
}

fn zero_sleep_returns() {
    run(|_| MacDesktop::new().sleep(Duration::ZERO));
}

fn main() {
    support::run(&[("sleep_is_exact", sleep_is_exact), ("zero_sleep_returns", zero_sleep_returns)])
}
