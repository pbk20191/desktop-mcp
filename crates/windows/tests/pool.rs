//! Offload, sleep and handle waits on the thread pool, driven by the main loop.
#[cfg(windows)]
fn main() {
    use desktop_mcp_windows::main_loop::run;
    use desktop_mcp_windows::pool::{offload, sleep, wait_for};
    use std::time::{Duration, Instant};
    use windows::Win32::System::Threading::{CreateEventW, SetEvent};

    run(|_| async {
        assert_eq!(offload(|| 6 * 7).await, 42);

        let started = Instant::now();
        sleep(Duration::from_millis(100)).await;
        let slept = started.elapsed();
        assert!(slept >= Duration::from_millis(95) && slept < Duration::from_millis(150), "slept {slept:?}");

        let event = unsafe { CreateEventW(None, true, false, None) }.unwrap();
        let raw = event.0 as usize;
        let setter = offload(move || {
            std::thread::sleep(Duration::from_millis(50));
            unsafe { SetEvent(windows::Win32::Foundation::HANDLE(raw as *mut _)) }.unwrap();
        });
        let started = Instant::now();
        wait_for(event).await;
        setter.await;
        assert!(started.elapsed() >= Duration::from_millis(45));
    });
    println!("pool: ok");
}

#[cfg(not(windows))]
fn main() {}
