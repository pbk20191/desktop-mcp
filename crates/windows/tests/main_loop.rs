//! The DispatcherQueue main loop. Runs on the main thread, one check after another (the counters are global).
#[cfg(windows)]
fn main() {
    use desktop_mcp_core::guest::Activity;
    use desktop_mcp_windows::main_loop::{queued_polls, run, turns};
    use std::time::{Duration, Instant};

    // The root's output comes back, and a tokio timer fires while a request keeps the fast tick on.
    let before = turns();
    let started = Instant::now();
    let output = run(|main_loop| async move {
        let activity = Activity::new(main_loop);
        let _request = activity.request();
        tokio::time::sleep(Duration::from_millis(100)).await;
        7
    });
    assert_eq!(output, 7);
    assert!(started.elapsed() < Duration::from_millis(400), "took {:?}", started.elapsed());
    assert!(turns() > before, "the guest runtime was never turned");

    // A burst of wakes from other threads queues the root's poll once, not once per wake.
    run(|_| async {
        let waker = std::future::poll_fn(|cx| std::task::Poll::Ready(cx.waker().clone())).await;
        let before = queued_polls();
        std::thread::scope(|scope| {
            for _ in 0..8 {
                let waker = waker.clone();
                scope.spawn(move || (0..125).for_each(|_| waker.wake_by_ref()));
            }
        });
        let queued = queued_polls() - before;
        assert!(queued <= 2, "{queued} polls queued for 1000 wakes");
    });
    // The main thread is a single-threaded apartment, as the WinRT clipboard needs.
    run(|_| async {
        use windows::Win32::System::Com::{CoGetApartmentType, APTTYPE, APTTYPEQUALIFIER, APTTYPE_MAINSTA, APTTYPE_STA};
        let (mut kind, mut qualifier) = (APTTYPE::default(), APTTYPEQUALIFIER::default());
        unsafe { CoGetApartmentType(&mut kind, &mut qualifier) }.unwrap();
        assert!(kind == APTTYPE_STA || kind == APTTYPE_MAINSTA, "{kind:?}");
    });
    // The STA thread runs work off the main thread, in an STA, always the same thread, and serves its awaits.
    run(|_| async {
        use desktop_mcp_windows::sta::StaThread;
        use windows::Win32::System::Com::{CoGetApartmentType, APTTYPE, APTTYPEQUALIFIER, APTTYPE_STA};
        use windows::Win32::System::Threading::GetCurrentThreadId;
        let main = unsafe { GetCurrentThreadId() };
        let probe = || async {
            desktop_mcp_windows::pool::sleep(Duration::from_millis(50)).await;
            let (mut kind, mut qualifier) = (APTTYPE::default(), APTTYPEQUALIFIER::default());
            unsafe { CoGetApartmentType(&mut kind, &mut qualifier) }.unwrap();
            (unsafe { GetCurrentThreadId() }, kind == APTTYPE_STA)
        };
        let (first, sta) = StaThread::shared().run(probe).await;
        let (second, _) = StaThread::shared().run(probe).await;
        assert!(first != main && first == second && sta, "main {main}, first {first}, second {second}, sta {sta}");
    });
    println!("main_loop: ok");
}

#[cfg(not(windows))]
fn main() {}
