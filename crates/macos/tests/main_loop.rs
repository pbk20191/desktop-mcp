mod support;
use desktop_mcp_core::guest::Tick;
use desktop_mcp_macos::main_loop::{offload, run, thread_count, turns};
use objc2_core_foundation::{kCFRunLoopCommonModes, kCFRunLoopDefaultMode, CFAbsoluteTimeGetCurrent, CFRunLoop, CFRunLoopTimer};
use std::time::{Duration, Instant};

fn run_returns_the_output() {
    assert_eq!(run(|_| async { 7 }), 7);
}

fn wake_from_another_thread() {
    let start = Instant::now();
    let got = run(|_| async {
        let (tx, rx) = tokio::sync::oneshot::channel();
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(30));
            tx.send(5).unwrap();
        });
        rx.await.unwrap()
    });
    assert_eq!(got, 5);
    assert!(start.elapsed() < Duration::from_millis(200), "took {:?}", start.elapsed());
}

fn no_turns_while_idle() {
    let before = turns();
    run(|_| async {
        let (tx, rx) = tokio::sync::oneshot::channel();
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(300));
            tx.send(()).unwrap();
        });
        rx.await.unwrap()
    });
    // one turn follows each poll of the root (the first and the one after the wake), with a spare
    assert!(turns() - before <= 3, "{} turns while idle", turns() - before);
}

fn tokio_sleep_fires_under_slow_tick() {
    let start = Instant::now();
    run(|main_loop| async move {
        main_loop.set_tick(Tick::Slow);
        tokio::time::sleep(Duration::from_millis(100)).await;
        main_loop.set_tick(Tick::Off);
    });
    assert!(start.elapsed() < Duration::from_millis(1300), "took {:?}", start.elapsed());
}

fn tokio_sleep_fires_under_fast_tick() {
    let start = Instant::now();
    run(|main_loop| async move {
        main_loop.set_tick(Tick::Fast);
        tokio::time::sleep(Duration::from_millis(100)).await;
        main_loop.set_tick(Tick::Off);
    });
    let took = start.elapsed();
    assert!(took >= Duration::from_millis(100) && took < Duration::from_millis(220), "took {took:?}");
}

fn tokio_spawn_runs() {
    let got = run(|_| async { tokio::spawn(async { (support::on_main(), 3) }).await.unwrap() });
    assert_eq!(got, (true, 3));
}

fn offload_runs_off_main_and_returns() {
    let (worker_on_main, back_on_main) = run(|_| async {
        let worker = offload(support::on_main).await;
        (worker, support::on_main())
    });
    assert!(!worker_on_main);
    assert!(back_on_main);
}

fn no_threads_are_kept() {
    run(|_| offload(|| ())); // let dispatch create its first worker before counting
    std::thread::sleep(Duration::from_millis(50));
    let before = thread_count();
    run(|main_loop| async move {
        main_loop.set_tick(Tick::Fast);
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
        tokio::task::spawn_local(async move {
            for i in 0..3 {
                tokio::time::sleep(Duration::from_millis(10)).await;
                tx.send(i).unwrap();
            }
        });
        let mut sum = 0;
        while let Some(i) = rx.recv().await {
            sum += i;
        }
        main_loop.set_tick(Tick::Off);
        assert_eq!(sum, 3);
    });
    assert!(thread_count() <= before + 1, "threads {} → {}", before, thread_count());
}

extern "C-unwind" fn nested(_timer: *mut CFRunLoopTimer, _info: *mut std::ffi::c_void) {
    // A callback that runs the loop itself, like a modal session. The root future completes while it runs.
    CFRunLoop::run_in_mode(unsafe { kCFRunLoopDefaultMode }, 0.15, false);
}
fn nested_run_loop_is_not_stopped() {
    let timer = unsafe { CFRunLoopTimer::new(None, CFAbsoluteTimeGetCurrent() + 0.02, 0.0, 0, 0, Some(nested), std::ptr::null_mut()) }.unwrap();
    CFRunLoop::main().unwrap().add_timer(Some(&timer), unsafe { kCFRunLoopCommonModes });
    let start = Instant::now();
    let got = run(|_| async {
        let (tx, rx) = tokio::sync::oneshot::channel();
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(60)); // inside the nested run
            tx.send(9).unwrap();
        });
        rx.await.unwrap()
    });
    assert_eq!(got, 9);
    // The nested run was allowed to finish (it ends at about 170 ms), and we returned soon after.
    let took = start.elapsed();
    assert!(took >= Duration::from_millis(150) && took < Duration::from_millis(600), "took {took:?}");
}

fn panic_is_resumed() {
    let result = std::panic::catch_unwind(|| run(|_| async { panic!("expected by the test") }));
    assert!(result.is_err());
    assert_eq!(run(|_| async { 1 }), 1, "the executor works again after a panic");
}

fn main() {
    support::run(&[
        ("run_returns_the_output", run_returns_the_output),
        ("wake_from_another_thread", wake_from_another_thread),
        ("no_turns_while_idle", no_turns_while_idle),
        ("tokio_sleep_fires_under_slow_tick", tokio_sleep_fires_under_slow_tick),
        ("tokio_sleep_fires_under_fast_tick", tokio_sleep_fires_under_fast_tick),
        ("tokio_spawn_runs", tokio_spawn_runs),
        ("offload_runs_off_main_and_returns", offload_runs_off_main_and_returns),
        ("no_threads_are_kept", no_threads_are_kept),
        ("nested_run_loop_is_not_stopped", nested_run_loop_is_not_stopped),
        ("panic_is_resumed", panic_is_resumed),
    ])
}
