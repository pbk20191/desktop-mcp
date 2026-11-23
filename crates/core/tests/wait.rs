//! Wait until a window appears or closes, or an element appears.
mod support;
use desktop_mcp_core::element::FoundElement;
use desktop_mcp_core::window::{DisplayFrame, Frame, FrameResult, WindowInfo, WindowReading, WindowSource};
use serde_json::{json, Value};
use std::cell::RefCell;
use std::future::Future;
use std::pin::Pin;
use std::rc::Rc;
use support::{call_tool, texts, FakeDesktop};

type Pending<T> = Pin<Box<dyn Future<Output = T>>>;

/// Windows that change between looks: each `reading` takes the next list (the last repeats).
struct Looks(RefCell<Vec<WindowReading>>);

impl WindowSource for Looks {
    fn reading(&self, app: Option<&str>) -> Pending<WindowReading> {
        let mut looks = self.0.borrow_mut();
        let mut reading = if looks.len() > 1 { looks.remove(0) } else { looks[0].clone() };
        reading.windows.retain(|w| app.is_none_or(|app| w.app.eq_ignore_ascii_case(app)));
        Box::pin(async move { reading })
    }
    fn windows(&self, _: Option<&str>) -> Pending<Vec<WindowInfo>> { unreachable!() }
    fn windows_of(&self, _: i32) -> Pending<Vec<WindowInfo>> { unreachable!() }
    fn app_exists(&self, _: &str) -> Pending<bool> { unreachable!() }
    fn focus(&self, _: &WindowInfo) -> Pending<bool> { unreachable!() }
    fn set_minimized(&self, _: &WindowInfo, _: bool) -> Pending<bool> { unreachable!() }
    fn move_to(&self, _: &WindowInfo, _: f64, _: f64) -> Pending<bool> { unreachable!() }
    fn resize(&self, _: &WindowInfo, _: f64, _: f64) -> Pending<bool> { unreachable!() }
    fn close(&self, _: &WindowInfo) -> Pending<bool> { unreachable!() }
    fn set_frame(&self, _: &WindowInfo, _: Frame) -> Pending<FrameResult> { unreachable!() }
    fn set_full_screen(&self, _: &WindowInfo, _: bool) -> Pending<bool> { unreachable!() }
    fn displays(&self) -> Pending<Vec<DisplayFrame>> { unreachable!() }
}

fn window(app: &str, title: &str) -> WindowInfo {
    WindowInfo { pid: 1, index: 0, app: app.into(), title: title.into(), frame: Frame::new(10.0, 20.0, 800.0, 600.0), focused: false, minimized: false, fullscreen: false, number: Some(7) }
}
fn reading(windows: Vec<WindowInfo>, complete: bool) -> WindowReading {
    WindowReading { windows, complete }
}
fn with_looks(looks: Vec<WindowReading>) -> Rc<FakeDesktop> {
    Rc::new(FakeDesktop { window_source: Some(Rc::new(Looks(RefCell::new(looks)))), ..FakeDesktop::default() })
}
fn call(desktop: &Rc<FakeDesktop>, arguments: Value) -> Vec<String> {
    texts(&call_tool(desktop, &["Wait"], "Wait", arguments))
}

#[test]
fn a_window_that_appears_answers_with_its_list_line() {
    let desktop = with_looks(vec![reading(vec![window("Preview", "Untitled")], true), reading(vec![window("Preview", "Untitled"), window("Preview", "Quarterly report")], true)]);
    assert_eq!(call(&desktop, json!({"until": "window", "app": "Preview", "title": "REPORT"})), ["1:w7\tPreview\t\"Quarterly report\"\t[10,20,800,600]"]);
    let any = with_looks(vec![reading(vec![window("Notes", "Report")], true)]);
    assert_eq!(call(&any, json!({"until": "window", "title": "report"})), ["1:w7\tNotes\t\"Report\"\t[10,20,800,600]"]);
}

#[test]
fn a_window_that_closes_is_gone_only_when_its_app_answers() {
    let desktop = with_looks(vec![reading(vec![window("Preview", "a")], true), reading(vec![], false), reading(vec![], true)]);
    assert_eq!(call(&desktop, json!({"until": "window_gone", "app": "Preview"})), ["No window matches any more."]);
    assert_eq!(desktop.slept.borrow().len(), 2, "the busy look did not count as closed");
    let busy = with_looks(vec![reading(vec![], false)]);
    assert_eq!(call(&busy, json!({"until": "window_gone", "app": "Preview", "timeout": 0})), ["Error executing tool Wait: Timed out after 0s waiting for window in Preview to close."]);
    assert_eq!(call(&busy, json!({"until": "window", "app": "Preview", "timeout": 0})), ["Error executing tool Wait: Timed out after 0s waiting for window in Preview."]);
}

#[test]
fn an_element_answers_with_its_centre() {
    let found = FoundElement { role: "AXButton".into(), title: "Save\tnow".into(), center: (119.6, 340.2) };
    let desktop = Rc::new(FakeDesktop { element_looks: RefCell::new(vec![None, Some(found)]), ..FakeDesktop::default() });
    assert_eq!(call(&desktop, json!({"until": "element", "title": "save", "role": "AXButton", "timeout": 5})), ["AXButton \"Save now\" at [120,340]"]);
    // Each look may walk at least half a second, even at the deadline.
    let deadlines = desktop.element_deadlines.borrow();
    assert!(deadlines.iter().all(|d| d.saturating_duration_since(std::time::Instant::now()) > std::time::Duration::from_millis(300)), "{deadlines:?}");
}

#[test]
fn conditions_need_their_arguments_and_accessibility() {
    let desktop = Rc::new(FakeDesktop::default());
    for (arguments, message) in [
        (json!({"until": "element", "app": "Preview"}), "title or role required for element."),
        (json!({"until": "window"}), "app or title required for window."),
        (json!({"until": "window_gone", "title": " "}), "app or title required for window_gone."),
    ] {
        assert_eq!(call(&desktop, arguments), [format!("Error executing tool Wait: {message}")]);
    }
    let untrusted = Rc::new(FakeDesktop { untrusted: true, ..FakeDesktop::default() });
    let refused = format!("Error executing tool Wait: {}", desktop_mcp_core::permissions::ACCESSIBILITY_MESSAGE);
    for arguments in [json!({"until": "window", "app": "Preview"}), json!({"until": "window_gone", "app": "Preview"}), json!({"until": "element", "title": "Save"})] {
        assert_eq!(call(&untrusted, arguments), std::slice::from_ref(&refused));
    }
}
