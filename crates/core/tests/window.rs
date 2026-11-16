//! The Window tool against a fake window source: the cases of the Swift port's WindowToolTests.
use desktop_mcp_core::window::{
    format, maximized_frame, moved_frame, parse_id, run, Arguments, DisplayFrame, Frame, FrameResult, Mode, WindowCache, WindowId, WindowInfo,
    WindowReading, WindowSource,
};
use std::cell::{Cell, RefCell};
use std::future::Future;
use std::pin::Pin;

type Pending<T> = Pin<Box<dyn Future<Output = T>>>;

/// Stands in for the Accessibility windows: a list (each app's windows indexed front to back, focus brings one to
/// the front), and a record of every action.
#[derive(Default)]
struct Fake {
    list: RefCell<Vec<WindowInfo>>,
    actions: RefCell<Vec<String>>,
    apps: Vec<&'static str>,
    /// Widths above this are clamped, as an app with a size limit would.
    max_width: Option<f64>,
    displays: Vec<DisplayFrame>,
    /// False for a fixed-size window.
    fixed_size: bool,
    /// Every action fails.
    refusing: bool,
    busy: Cell<bool>,
}

fn reindexed(list: Vec<WindowInfo>) -> Vec<WindowInfo> {
    let mut next = std::collections::HashMap::new();
    list.into_iter()
        .map(|mut w| {
            let index = next.entry(w.pid).or_insert(0);
            w.index = *index;
            *index += 1;
            w
        })
        .collect()
}

impl Fake {
    fn new(list: Vec<WindowInfo>) -> Fake {
        Fake { list: RefCell::new(reindexed(list)), ..Fake::default() }
    }
    fn set(&self, list: Vec<WindowInfo>) {
        *self.list.borrow_mut() = reindexed(list);
    }
    fn act(&self, name: &str, w: &WindowInfo, change: impl FnOnce(&mut WindowInfo)) -> bool {
        if self.refusing {
            return false;
        }
        let mut list = self.list.borrow_mut();
        let Some(found) = list.iter_mut().find(|x| x.pid == w.pid && w.names(x)) else { return false };
        self.actions.borrow_mut().push(format!("{name} {}", found.id()));
        change(found);
        true
    }
    fn ready<T: 'static>(value: T) -> Pending<T> {
        Box::pin(async move { value })
    }
}

impl WindowSource for Fake {
    fn windows(&self, app: Option<&str>) -> Pending<Vec<WindowInfo>> {
        if self.busy.get() {
            return Self::ready(vec![]);
        }
        let list = self.list.borrow();
        Self::ready(list.iter().filter(|w| app.is_none_or(|app| w.app.to_lowercase() == app.to_lowercase())).cloned().collect())
    }
    fn reading(&self, app: Option<&str>) -> Pending<WindowReading> {
        let windows = self.list.borrow().iter().filter(|w| !self.busy.get() && app.is_none_or(|app| w.app.to_lowercase() == app.to_lowercase())).cloned().collect();
        Self::ready(WindowReading { windows, complete: !self.busy.get() })
    }
    fn windows_of(&self, pid: i32) -> Pending<Vec<WindowInfo>> {
        Self::ready(self.list.borrow().iter().filter(|w| w.pid == pid).cloned().collect())
    }
    fn app_exists(&self, app: &str) -> Pending<bool> {
        Self::ready(self.apps.contains(&app) || self.list.borrow().iter().any(|w| w.app.to_lowercase() == app.to_lowercase()))
    }
    fn focus(&self, w: &WindowInfo) -> Pending<bool> {
        if !self.act("focus", w, |x| {
            x.minimized = false;
            x.focused = true;
        }) {
            return Self::ready(false);
        }
        let mut list = self.list.borrow_mut();
        let i = list.iter().position(|x| x.pid == w.pid && w.names(x)).unwrap();
        let front = list.remove(i);
        list.iter_mut().for_each(|x| x.focused = false);
        list.insert(0, front);
        let reordered = reindexed(std::mem::take(&mut *list));
        *list = reordered;
        Self::ready(true)
    }
    fn set_minimized(&self, w: &WindowInfo, on: bool) -> Pending<bool> {
        Self::ready(self.act(if on { "minimize" } else { "restore" }, w, |x| x.minimized = on))
    }
    fn move_to(&self, w: &WindowInfo, x: f64, y: f64) -> Pending<bool> {
        Self::ready(self.act("move", w, |w| (w.frame.x, w.frame.y) = (x, y)))
    }
    fn resize(&self, w: &WindowInfo, width: f64, height: f64) -> Pending<bool> {
        let width = self.max_width.map_or(width, |max| max.min(width));
        Self::ready(self.act("resize", w, |w| (w.frame.width, w.frame.height) = (width, height)))
    }
    fn close(&self, w: &WindowInfo) -> Pending<bool> {
        Self::ready(self.act("close", w, |_| {}))
    }
    fn set_frame(&self, w: &WindowInfo, frame: Frame) -> Pending<FrameResult> {
        let Some(current) = self.list.borrow().iter().find(|x| x.pid == w.pid && w.names(x)).cloned() else { return Self::ready(FrameResult::Failed) };
        if self.refusing {
            return Self::ready(FrameResult::Failed);
        }
        let same_size = Frame::new(0.0, 0.0, current.frame.width, current.frame.height).rounded() == Frame::new(0.0, 0.0, frame.width, frame.height).rounded();
        let result = if same_size {
            self.act("position", w, |x| (x.frame.x, x.frame.y) = (frame.x, frame.y))
        } else if self.fixed_size {
            return Self::ready(FrameResult::NotResizable);
        } else {
            self.act("frame", w, |x| x.frame = frame)
        };
        Self::ready(if result { FrameResult::Done } else { FrameResult::Failed })
    }
    fn set_full_screen(&self, w: &WindowInfo, on: bool) -> Pending<bool> {
        Self::ready(self.act(if on { "fullscreen" } else { "exit_fullscreen" }, w, |x| x.fullscreen = on))
    }
    fn displays(&self) -> Pending<Vec<DisplayFrame>> {
        Self::ready(self.displays.clone())
    }
}

fn window() -> WindowInfo {
    WindowInfo { pid: 1, index: 0, app: "Safari".into(), title: "Docs".into(), frame: Frame::new(10.0, 20.0, 800.0, 600.0), focused: false, minimized: false, fullscreen: false, number: None }
}
fn numbered(number: u32) -> WindowInfo {
    WindowInfo { number: Some(number), ..window() }
}

fn args(value: serde_json::Value) -> Arguments {
    serde_json::from_value(value).unwrap()
}

/// Runs the tool with Accessibility granted.
fn call(source: &Fake, cache: &RefCell<WindowCache>, value: serde_json::Value) -> Result<String, String> {
    futures_block_on(run(source, cache, true, args(value)))
}

fn futures_block_on<T>(future: impl Future<Output = T>) -> T {
    tokio::runtime::Builder::new_current_thread().build().unwrap().block_on(future)
}

fn displays() -> Vec<DisplayFrame> {
    vec![
        DisplayFrame { index: 0, name: "Built-in".into(), frame: Frame::new(0.0, 0.0, 1440.0, 900.0), visible: Frame::new(0.0, 25.0, 1440.0, 800.0), scale: 2.0, main: true },
        DisplayFrame { index: 1, name: "Left".into(), frame: Frame::new(-1920.0, 0.0, 1920.0, 1080.0), visible: Frame::new(-1920.0, 25.0, 1920.0, 1055.0), scale: 1.0, main: false },
    ]
}

#[test]
fn list_format() {
    let windows = [
        WindowInfo { pid: 123, focused: true, number: Some(456), ..window() },
        WindowInfo { pid: 123, index: 1, title: "Other".into(), frame: Frame::new(0.4, -0.6, 300.5, 200.0), minimized: true, fullscreen: true, ..window() },
        WindowInfo { pid: 9, app: "Notes".into(), title: "a\tb\nc".into(), ..window() },
    ];
    assert_eq!(format(&windows), "123:w456\tSafari\t\"Docs\"\t[10,20,800,600]\tfocused\n123:1\tSafari\t\"Other\"\t[0,-1,301,200]\tminimized,fullscreen\n9:0\tNotes\t\"a b c\"\t[10,20,800,600]");
    assert_eq!(format(&[]), "No windows.");
    let source = Fake::new(vec![WindowInfo { pid: 123, ..window() }]);
    assert_eq!(call(&source, &RefCell::default(), serde_json::json!({})), Ok("123:0\tSafari\t\"Docs\"\t[10,20,800,600]".into()));
}

#[test]
fn ids_parse() {
    assert_eq!(parse_id("123:w456"), Some(WindowId::Number(123, 456)));
    assert_eq!(parse_id("123:4"), Some(WindowId::Index(123, 4)));
    for id in ["123:wx", "123:", "x:1", "123:w", "123:w0", "123:w-1", "abc", "1", ":0", "1:-1", "0:0", "1:0:0"] {
        assert_eq!(parse_id(id), None, "{id}");
    }
}

#[test]
fn a_number_id_follows_its_window_through_a_reorder() {
    let source = Fake::new(vec![WindowInfo { title: "Untitled".into(), ..numbered(10) }, WindowInfo { title: "Untitled".into(), ..numbered(11) }]);
    let cache = RefCell::default();
    assert_eq!(call(&source, &cache, serde_json::json!({})).unwrap(), "1:w10\tSafari\t\"Untitled\"\t[10,20,800,600]\n1:w11\tSafari\t\"Untitled\"\t[10,20,800,600]");
    assert_eq!(call(&source, &cache, serde_json::json!({"mode": "focus", "id": "1:w11"})), Ok("Focused 1:w11.".into()));
    assert_eq!(source.list.borrow().iter().map(|w| (w.number, w.index)).collect::<Vec<_>>(), [(Some(11), 0), (Some(10), 1)]);
    assert_eq!(call(&source, &cache, serde_json::json!({"mode": "minimize", "id": "1:w11"})), Ok("Minimized 1:w11.".into()));
    assert!(source.list.borrow().iter().find(|w| w.number == Some(11)).unwrap().minimized);
    assert_eq!(*source.actions.borrow(), ["focus 1:w11", "minimize 1:w11"]);
}

#[test]
fn move_and_resize_report_the_applied_frame() {
    let source = Fake { max_width: Some(1440.0), ..Fake::new(vec![numbered(10)]) };
    let cache = RefCell::default();
    assert_eq!(call(&source, &cache, serde_json::json!({"mode": "move", "id": "1:w10", "loc": [5, 5]})), Ok("Moved 1:w10 to (5,5).".into()));
    assert_eq!(call(&source, &cache, serde_json::json!({"mode": "resize", "id": "1:w10", "size": [5000, 480]})), Ok("Resized 1:w10 to 1440×480.".into()));
}

#[test]
fn an_index_id_must_have_been_listed_and_be_unchanged() {
    let source = Fake::new(vec![window()]);
    assert_eq!(call(&source, &RefCell::default(), serde_json::json!({"mode": "focus", "id": "1:0"})), Err("Window 1:0 was not listed; call Window list first.".into()));
    let cache = RefCell::default();
    call(&source, &cache, serde_json::json!({})).unwrap();
    source.set(vec![WindowInfo { frame: Frame::new(11.0, 20.0, 800.0, 600.0), ..window() }]);
    assert_eq!(call(&source, &cache, serde_json::json!({"mode": "close", "id": "1:0"})), Err("Window 1:0 changed since it was listed; call Window list again.".into()));
    source.set(vec![WindowInfo { title: "Other".into(), ..window() }]);
    assert_eq!(call(&source, &cache, serde_json::json!({"mode": "close", "id": "1:0"})), Err("Window 1:0 changed since it was listed; call Window list again.".into()));
    // Sub-point moves are the same window.
    source.set(vec![WindowInfo { frame: Frame::new(10.2, 19.8, 800.3, 600.0), ..window() }]);
    assert_eq!(call(&source, &cache, serde_json::json!({"mode": "focus", "id": "1:0"})), Ok("Focused 1:0.".into()));
    // A closed window's index now names nothing.
    let source = Fake::new(vec![window(), WindowInfo { title: "Second".into(), ..window() }]);
    let cache = RefCell::default();
    call(&source, &cache, serde_json::json!({})).unwrap();
    source.set(vec![window()]);
    assert_eq!(call(&source, &cache, serde_json::json!({"mode": "minimize", "id": "1:1"})), Err("Window 1:1 changed since it was listed; call Window list again.".into()));
    assert!(source.actions.borrow().is_empty());
}

#[test]
fn maximize_uses_the_windows_display() {
    let frame = Frame::new(-900.0, 100.0, 800.0, 600.0);
    assert_eq!(maximized_frame(&frame, &displays()), Some(Frame::new(-1920.0, 25.0, 1920.0, 1055.0)));
    assert_eq!(maximized_frame(&Frame::new(100.0, 100.0, 400.0, 300.0), &displays()), Some(Frame::new(0.0, 25.0, 1440.0, 800.0)));
    assert_eq!(maximized_frame(&Frame::new(5000.0, 100.0, 400.0, 300.0), &displays()), Some(Frame::new(0.0, 25.0, 1440.0, 800.0)));
    assert_eq!(maximized_frame(&Frame::new(-4000.0, 2000.0, 400.0, 300.0), &displays()), Some(Frame::new(-1920.0, 25.0, 1920.0, 1055.0)));
    assert_eq!(maximized_frame(&Frame::default(), &[]), None);
    // Inside the left display near its edge, though nearer the main display's centre: the containing one wins.
    assert_eq!(maximized_frame(&Frame::new(-150.0, 50.0, 100.0, 100.0), &displays()), Some(Frame::new(-1920.0, 25.0, 1920.0, 1055.0)));
    let source = Fake { displays: displays(), ..Fake::new(vec![WindowInfo { frame, ..numbered(10) }]) };
    assert_eq!(call(&source, &RefCell::default(), serde_json::json!({"mode": "maximize", "id": "1:w10"})), Ok("Maximized 1:w10.".into()));
    assert_eq!(source.list.borrow()[0].frame, Frame::new(-1920.0, 25.0, 1920.0, 1055.0));
    assert_eq!(*source.actions.borrow(), ["frame 1:w10"]);
}

#[test]
fn moving_to_a_display_keeps_the_offset_and_clamps() {
    let (main, left) = (&displays()[0], &displays()[1]);
    assert_eq!(moved_frame(&Frame::new(100.0, 125.0, 800.0, 600.0), main, left), Frame::new(-1820.0, 125.0, 800.0, 600.0));
    assert_eq!(moved_frame(&Frame::new(100.0, 125.0, 2500.0, 600.0), main, left), Frame::new(-1920.0, 125.0, 1920.0, 600.0));
    assert_eq!(moved_frame(&Frame::new(-900.0, 500.0, 800.0, 600.0), left, main), Frame::new(640.0, 225.0, 800.0, 600.0));
    assert_eq!(moved_frame(&Frame::new(-1800.0, 100.0, 800.0, 1000.0), left, main), Frame::new(120.0, 25.0, 800.0, 800.0));
    let source = Fake { displays: displays(), ..Fake::new(vec![WindowInfo { frame: Frame::new(100.0, 125.0, 800.0, 600.0), ..numbered(10) }]) };
    assert_eq!(call(&source, &RefCell::default(), serde_json::json!({"mode": "display", "id": "1:w10", "display": 1})), Ok("Moved 1:w10 to display 1.".into()));
    assert_eq!(source.list.borrow()[0].frame, Frame::new(-1820.0, 125.0, 800.0, 600.0));
}

#[test]
fn every_mode_refuses_without_accessibility() {
    let source = Fake { displays: displays(), ..Fake::new(vec![numbered(10)]) };
    for arguments in [
        serde_json::json!({}),
        serde_json::json!({"mode": "maximize", "id": "1:w10"}),
        serde_json::json!({"mode": "fullscreen", "id": "1:w10"}),
        serde_json::json!({"mode": "display", "id": "1:w10", "display": 1}),
        serde_json::json!({"mode": "displays"}),
    ] {
        let refused = futures_block_on(run(&source, &RefCell::default(), false, args(arguments.clone())));
        assert_eq!(refused, Err(desktop_mcp_core::permissions::ACCESSIBILITY_MESSAGE.to_string()), "{arguments}");
    }
    assert!(source.actions.borrow().is_empty());
    let _ = Mode::List;
}
