//! Real windows and displays, read-only: the only actions tried are on a window that does not exist.
mod support;
use desktop_mcp_core::window::{Frame, FrameResult, WindowInfo, WindowSource};
use desktop_mcp_macos::ax::trusted;
use desktop_mcp_macos::main_loop::run;
use desktop_mcp_macos::windows::AxWindowSource;

fn windows_are_listed_with_ids_and_frames() {
    if !trusted() {
        return eprintln!("    skipped: Accessibility is not granted to the test runner");
    }
    let (windows, reading) = run(|_| async { (AxWindowSource.windows(None).await, AxWindowSource.reading(None).await) });
    assert!(windows.iter().all(|w| w.pid > 0 && w.id().starts_with(&format!("{}:", w.pid))), "{windows:?}");
    assert!(windows.iter().filter(|w| w.focused).count() <= 1);
    assert!(reading.windows.len() <= windows.len() + 2);
    if let Some(first) = windows.first().cloned() {
        let again = run(move |_| async move { AxWindowSource.windows_of(first.pid).await.into_iter().any(|w| first.names(&w)) });
        assert!(again, "a listed window is found again by its id");
    }
}

fn actions_on_a_gone_window_change_nothing() {
    let gone = WindowInfo { pid: std::process::id() as i32, index: 0, app: String::new(), title: String::new(), frame: Frame::default(), focused: false, minimized: false, fullscreen: false, number: Some(u32::MAX - 1) };
    let outcome = run(move |_| async move {
        let source = AxWindowSource;
        (source.focus(&gone).await, source.close(&gone).await, source.set_minimized(&gone, true).await, source.set_frame(&gone, Frame::new(0.0, 0.0, 10.0, 10.0)).await)
    });
    assert_eq!(outcome, (false, false, false, FrameResult::Failed));
    let unknown = run(|_| AxWindowSource.app_exists("No Such App desktop-mcp"));
    assert!(!unknown);
}

fn main() {
    support::run(&[
        ("windows_are_listed_with_ids_and_frames", windows_are_listed_with_ids_and_frames),
        ("actions_on_a_gone_window_change_nothing", actions_on_a_gone_window_change_nothing),
    ])
}
