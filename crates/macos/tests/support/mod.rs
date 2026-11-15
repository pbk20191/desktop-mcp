//! A test runner that keeps every test on the main thread, one after another: the code under test owns the
//! main run loop. (`cargo test` and libtest-mimic both run tests on other threads.)
#![allow(dead_code)]

pub type Test = (&'static str, fn());

/// Runs the tests whose name contains the first non-flag argument (all without one). Exits non-zero on failure.
pub fn run(tests: &[Test]) -> ! {
    let filter = std::env::args().skip(1).find(|a| !a.starts_with('-'));
    let mut failed = Vec::new();
    let mut ran = 0;
    for (name, test) in tests {
        if filter.as_deref().is_some_and(|f| !name.contains(f)) {
            continue;
        }
        ran += 1;
        // A test that hangs (a tokio timer with no tick, say) fails instead of stalling the run.
        let finished = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
        let watched = finished.clone();
        let name_for_watchdog = *name;
        std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_secs(60));
            if !watched.load(std::sync::atomic::Ordering::SeqCst) {
                println!("test {name_for_watchdog} ... FAILED (no result within 60 s)");
                std::process::exit(101);
            }
        });
        let outcome = std::panic::catch_unwind(test);
        finished.store(true, std::sync::atomic::Ordering::SeqCst);
        match outcome {
            Ok(()) => println!("test {name} ... ok"),
            Err(_) => {
                println!("test {name} ... FAILED");
                failed.push(*name);
            }
        }
    }
    println!("\ntest result: {}. {} passed; {} failed", if failed.is_empty() { "ok" } else { "FAILED" }, ran - failed.len(), failed.len());
    std::process::exit(if failed.is_empty() { 0 } else { 101 })
}

pub fn on_main() -> bool {
    unsafe { libc::pthread_main_np() == 1 }
}
