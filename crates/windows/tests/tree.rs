//! The UI Automation walk: its rules and budget on a synthetic tree, and the probe that skips hung windows.
#![cfg(windows)]
use desktop_mcp_windows::tree::{classify, walk_window, ElementProps, Limits, TreeSource};
use std::time::Duration;

fn props(control_type: &str) -> ElementProps {
    ElementProps { control_type: control_type.into(), name: "x".into(), enabled: true, control_element: true, rect: (0.0, 0.0, 10.0, 10.0), legacy_role: Some(0x2B), ..ElementProps::default() }
}

#[test]
fn classify_rules() {
    assert!(classify(&props("Button"), false).interactive, "a push button");
    assert!(!classify(&ElementProps { enabled: false, ..props("Button") }, false).interactive, "disabled");
    assert!(!classify(&ElementProps { rect: (0.0, 0.0, 0.0, 0.0), ..props("Button") }, false).interactive, "no area");
    assert!(!classify(&ElementProps { legacy_role: None, ..props("Text") }, false).interactive, "plain text");
    assert!(classify(&ElementProps { keyboard_focusable: true, legacy_role: None, ..props("Image") }, false).interactive, "a focusable image outside a browser");
    assert!(!classify(&ElementProps { keyboard_focusable: false, ..props("DataItem") }, true).interactive, "an unfocusable browser data item");
    assert!(classify(&ElementProps { vertically_scrollable: true, ..props("Pane") }, false).scrollable);
    assert!(!classify(&ElementProps { vertically_scrollable: true, offscreen: true, ..props("Pane") }, false).scrollable);
}

/// A tree whose root has `children` buttons.
struct Wide {
    children: usize,
}

impl TreeSource for Wide {
    type Node = usize;
    fn root(&self) -> Option<usize> {
        Some(0)
    }
    fn children(&self, node: &usize, limit: usize) -> Vec<usize> {
        if *node == 0 { (1..=self.children.min(limit)).collect() } else { Vec::new() }
    }
    fn props(&self, node: &usize) -> ElementProps {
        if *node == 0 { props("Pane") } else { props("Button") }
    }
}

#[test]
fn budget_stops_the_walk() {
    let limits = Limits { children_per_parent: 300, elements: 100, deadline: Duration::from_secs(5) };
    let walk = walk_window(&Wide { children: 10_000 }, "Win", false, (0.0, 0.0, 100.0, 100.0), &limits);
    assert!(walk.stopped_early);
    assert!(walk.interactive.len() <= 100, "{}", walk.interactive.len());
    let walk = walk_window(&Wide { children: 50 }, "Win", false, (0.0, 0.0, 100.0, 100.0), &limits);
    assert!(!walk.stopped_early);
    assert_eq!(walk.interactive.len(), 50);
}

#[test]
fn a_hung_window_does_not_answer_the_probe() {
    use windows::core::w;
    use windows::Win32::UI::WindowsAndMessaging::{CreateWindowExW, WINDOW_EX_STYLE, WS_POPUP};
    // A window (never shown) whose thread stops pumping messages.
    let (sender, receiver) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let hwnd = unsafe { CreateWindowExW(WINDOW_EX_STYLE(0), w!("STATIC"), w!(""), WS_POPUP, 0, 0, 1, 1, None, None, None, None) }.unwrap();
        sender.send(hwnd.0 as isize).unwrap();
        std::thread::sleep(Duration::from_secs(3));
    });
    let hwnd = receiver.recv().unwrap();
    let started = std::time::Instant::now();
    assert!(!desktop_mcp_windows::tree::answers(hwnd));
    assert!(started.elapsed() < Duration::from_millis(400), "{:?}", started.elapsed());
}

#[test]
fn the_live_desktop_walks_within_its_deadline() {
    let _ = unsafe { windows::Win32::UI::HiDpi::SetProcessDpiAwarenessContext(windows::Win32::UI::HiDpi::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let started = std::time::Instant::now();
    let windows = desktop_mcp_windows::windows_list::top_windows();
    let walk = desktop_mcp_windows::tree::walk(&windows, &Limits::default());
    println!("{} windows, {} interactive, {} scrollable, stopped early: {}, {:?}", walk.windows.len(), walk.tree.interactive.len(), walk.tree.scrollable.len(), walk.stopped_early, started.elapsed());
    assert!(started.elapsed() < Duration::from_secs(6));
}
