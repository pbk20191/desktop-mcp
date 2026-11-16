//! The Window tool: list application windows and act on one by the id a listing gave it. The platform reads and
//! changes the windows through [`WindowSource`].
use rmcp::schemars;
use std::cell::RefCell;
use std::collections::{HashMap, VecDeque};
use std::future::Future;
use std::pin::Pin;

type Pending<T> = Pin<Box<dyn Future<Output = T>>>;

/// A rectangle in points, origin top-left of the main display, y down.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Frame {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

impl Frame {
    pub const fn new(x: f64, y: f64, width: f64, height: f64) -> Frame {
        Frame { x, y, width, height }
    }
    /// As whole points `[x, y, w, h]`.
    pub fn rounded(&self) -> [i64; 4] {
        [self.x, self.y, self.width, self.height].map(|v| v.round() as i64)
    }
    fn max_x(&self) -> f64 {
        self.x + self.width
    }
    fn max_y(&self) -> f64 {
        self.y + self.height
    }
    fn mid(&self) -> (f64, f64) {
        (self.x + self.width / 2.0, self.y + self.height / 2.0)
    }
    fn contains(&self, (x, y): (f64, f64)) -> bool {
        self.width > 0.0 && self.height > 0.0 && x >= self.x && x < self.max_x() && y >= self.y && y < self.max_y()
    }
}

/// One application window as seen at listing time.
#[derive(Clone, Debug, PartialEq)]
pub struct WindowInfo {
    pub pid: i32,
    /// Position in the app's window list (z-order, so not stable).
    pub index: usize,
    pub app: String,
    pub title: String,
    pub frame: Frame,
    pub focused: bool,
    pub minimized: bool,
    pub fullscreen: bool,
    /// The window server's number, stable for the window's lifetime; `None` when it cannot be read.
    pub number: Option<u32>,
}

impl WindowInfo {
    /// `<pid>:w<number>`, or `<pid>:<index>` when the number is unknown.
    pub fn id(&self) -> String {
        match self.number {
            Some(number) => format!("{}:w{number}", self.pid),
            None => format!("{}:{}", self.pid, self.index),
        }
    }

    /// Whether `other` is still this window by appearance: same title and same frame to the point. Only used for
    /// index ids, which have nothing better.
    pub fn looks_like(&self, other: &WindowInfo) -> bool {
        self.title == other.title && self.frame.rounded() == other.frame.rounded()
    }

    /// Whether `other` is the window this one names: by number when known, else by index.
    pub fn names(&self, other: &WindowInfo) -> bool {
        match self.number {
            Some(number) => other.number == Some(number),
            None => other.number.is_none() && other.index == self.index,
        }
    }
}

/// What [`WindowSource::set_frame`] did.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FrameResult {
    Done,
    /// The size had to change and the window cannot be resized; nothing was changed.
    NotResizable,
    Failed,
}

/// What `Wait` sees of the windows: those read, and whether every app asked answered.
#[derive(Clone, Debug, PartialEq)]
pub struct WindowReading {
    pub windows: Vec<WindowInfo>,
    /// False when an app did not answer (busy), so its windows are unknown rather than none.
    pub complete: bool,
}

/// One display as windows see it.
#[derive(Clone, Debug, PartialEq)]
pub struct DisplayFrame {
    /// Position in left-to-right order.
    pub index: usize,
    pub name: String,
    pub frame: Frame,
    /// The frame without the menu bar and the Dock.
    pub visible: Frame,
    pub scale: f64,
    pub main: bool,
}

impl DisplayFrame {
    /// From Cocoa screen rects (origin bottom-left of the main display, y up); `main_height` is the main display's
    /// height.
    pub fn from_cocoa(index: usize, name: &str, frame: Frame, visible: Frame, main_height: f64, scale: f64, main: bool) -> DisplayFrame {
        let flip = |r: Frame| Frame::new(r.x, main_height - (r.y + r.height), r.width, r.height);
        DisplayFrame { index, name: name.to_string(), frame: flip(frame), visible: flip(visible), scale, main }
    }
}

/// The windows of running apps and the actions on one of them. Actions find the window again first and answer
/// `false` (or [`FrameResult::Failed`]) when it is gone.
pub trait WindowSource {
    /// Windows of regular apps, all of them or those of `app` (name ignoring case, or exact bundle id). An app that
    /// does not answer has none.
    fn windows(&self, app: Option<&str>) -> Pending<Vec<WindowInfo>>;
    /// `windows`, telling an app that did not answer apart from one without windows.
    fn reading(&self, app: Option<&str>) -> Pending<WindowReading>;
    fn windows_of(&self, pid: i32) -> Pending<Vec<WindowInfo>>;
    /// Whether a regular app matching `app` is running.
    fn app_exists(&self, app: &str) -> Pending<bool>;
    /// Restores the window first when it is minimized.
    fn focus(&self, window: &WindowInfo) -> Pending<bool>;
    fn set_minimized(&self, window: &WindowInfo, on: bool) -> Pending<bool>;
    fn move_to(&self, window: &WindowInfo, x: f64, y: f64) -> Pending<bool>;
    fn resize(&self, window: &WindowInfo, width: f64, height: f64) -> Pending<bool>;
    /// Presses its close button.
    fn close(&self, window: &WindowInfo) -> Pending<bool>;
    /// Position, then size, then position again (an app may clamp the size against the old position). The size is
    /// only set when it changes; a window whose size cannot be set is left alone unless only its position changes.
    fn set_frame(&self, window: &WindowInfo, frame: Frame) -> Pending<FrameResult>;
    /// Changes nothing when the app does not let full screen be set.
    fn set_full_screen(&self, window: &WindowInfo, on: bool) -> Pending<bool>;
    /// The displays, left to right.
    fn displays(&self) -> Pending<Vec<DisplayFrame>>;
    /// Whether the window's app answers, asked after an action failed (Windows skips a window that does not); a
    /// window that is gone counts as answering.
    fn responds(&self, _window: &WindowInfo) -> Pending<bool> {
        Box::pin(async { true })
    }
}

/// The windows seen by the last 64 listings, newest first on lookup.
#[derive(Default)]
pub struct WindowCache {
    listings: VecDeque<HashMap<String, WindowInfo>>,
}

impl WindowCache {
    pub const CAPACITY: usize = 64;

    pub fn remember(&mut self, windows: &[WindowInfo]) {
        if windows.is_empty() {
            return;
        }
        self.listings.push_back(windows.iter().map(|window| (window.id(), window.clone())).collect());
        while self.listings.len() > Self::CAPACITY {
            self.listings.pop_front();
        }
    }

    pub fn listed(&self, id: &str) -> Option<WindowInfo> {
        self.listings.iter().rev().find_map(|listing| listing.get(id).cloned())
    }
}

thread_local! {
    /// The cache the server's Window tool uses: listings outlive the request (and the server) that made them.
    pub static CACHE: std::rc::Rc<RefCell<WindowCache>> = std::rc::Rc::default();
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WindowId {
    Number(i32, u32),
    Index(i32, usize),
}

/// `<pid>:w<number>` or `<pid>:<index>`.
pub fn parse_id(id: &str) -> Option<WindowId> {
    let digits = |text: &str| !text.is_empty() && text.bytes().all(|b| b.is_ascii_digit());
    let (pid, rest) = id.split_once(':')?;
    if !digits(pid) || rest.contains(':') {
        return None;
    }
    let pid: i32 = pid.parse().ok().filter(|pid| *pid > 0)?;
    match rest.strip_prefix('w') {
        Some(number) if digits(number) => number.parse().ok().filter(|n| *n > 0).map(|n| WindowId::Number(pid, n)),
        Some(_) => None,
        None if digits(rest) => rest.parse().ok().map(|index| WindowId::Index(pid, index)),
        None => None,
    }
}

/// Tabs and line breaks as spaces, so a name stays on its line and in its field.
pub fn flat(text: &str) -> String {
    text.chars().map(|c| if matches!(c, '\t' | '\n' | '\r' | '\u{0B}' | '\u{0C}' | '\u{85}' | '\u{2028}' | '\u{2029}') { ' ' } else { c }).collect()
}

/// `[x,y,w,h]` in whole points.
pub fn box_text(frame: &Frame) -> String {
    format!("[{}]", frame.rounded().map(|v| v.to_string()).join(","))
}

/// One `<id>\t<app>\t"<title>"\t[x,y,w,h]\t<flags>` line per window; no flags, no last tab.
pub fn format(windows: &[WindowInfo]) -> String {
    if windows.is_empty() {
        return "No windows.".to_string();
    }
    let lines: Vec<String> = windows
        .iter()
        .map(|w| {
            let mut fields = vec![w.id(), flat(&w.app), format!("\"{}\"", flat(&w.title)), box_text(&w.frame)];
            let flags: Vec<&str> = [(w.focused, "focused"), (w.minimized, "minimized"), (w.fullscreen, "fullscreen")].iter().filter(|(on, _)| *on).map(|(_, name)| *name).collect();
            if !flags.is_empty() {
                fields.push(flags.join(","));
            }
            fields.join("\t")
        })
        .collect();
    lines.join("\n")
}

/// One `<index>\t<name>\t[x,y,w,h]\tvisible [x,y,w,h]\tscale <s>` line per display, `\tmain` after the main one.
pub fn format_displays(displays: &[DisplayFrame]) -> String {
    if displays.is_empty() {
        return "No displays.".to_string();
    }
    let lines: Vec<String> = displays
        .iter()
        .map(|d| {
            let scale = if d.scale == d.scale.round() { (d.scale as i64).to_string() } else { d.scale.to_string() };
            let mut fields = vec![d.index.to_string(), flat(&d.name), box_text(&d.frame), format!("visible {}", box_text(&d.visible)), format!("scale {scale}")];
            if d.main {
                fields.push("main".to_string());
            }
            fields.join("\t")
        })
        .collect();
    lines.join("\n")
}

/// The display containing the window's centre, else the one whose centre is nearest.
pub fn display_of<'a>(window: &Frame, displays: &'a [DisplayFrame]) -> Option<&'a DisplayFrame> {
    let centre = window.mid();
    if let Some(containing) = displays.iter().find(|d| d.frame.contains(centre)) {
        return Some(containing);
    }
    let distance = |d: &DisplayFrame| {
        let (x, y) = d.frame.mid();
        (x - centre.0).hypot(y - centre.1)
    };
    displays.iter().min_by(|a, b| distance(a).total_cmp(&distance(b)))
}

/// The visible frame of the window's display.
pub fn maximized_frame(window: &Frame, displays: &[DisplayFrame]) -> Option<Frame> {
    display_of(window, displays).map(|d| d.visible)
}

/// `window` at the same offset from `to`'s visible frame as from `from`'s, size kept, then shrunk and shifted to
/// lie inside `to`'s visible frame.
pub fn moved_frame(window: &Frame, from: &DisplayFrame, to: &DisplayFrame) -> Frame {
    let target = to.visible;
    let (width, height) = (window.width.min(target.width), window.height.min(target.height));
    let x = target.x + (window.x - from.visible.x);
    let y = target.y + (window.y - from.visible.y);
    Frame::new(x.max(target.x).min(target.max_x() - width), y.max(target.y).min(target.max_y() - height), width, height)
}

/// The window `id` names now. A number id only needs the window to still exist; an index id must still have the
/// title and frame it was listed with.
pub async fn resolve(id: &str, source: &dyn WindowSource, cache: &RefCell<WindowCache>) -> Result<WindowInfo, String> {
    match parse_id(id) {
        None => Err(format!("Invalid window id: {id}")),
        Some(WindowId::Number(pid, number)) => {
            source.windows_of(pid).await.into_iter().find(|w| w.number == Some(number)).ok_or_else(|| format!("Window {id} no longer exists; call Window list again."))
        }
        Some(WindowId::Index(pid, index)) => {
            let listed = cache.borrow().listed(id).ok_or_else(|| format!("Window {id} was not listed; call Window list first."))?;
            source
                .windows_of(pid)
                .await
                .into_iter()
                .find(|w| w.number.is_none() && w.index == index)
                .filter(|current| current.looks_like(&listed))
                .ok_or_else(|| format!("Window {id} changed since it was listed; call Window list again."))
        }
    }
}

// What the Window tool does. (A plain comment: a doc comment would reach the schema the model reads.)
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Mode {
    #[default]
    List,
    Focus,
    Move,
    Resize,
    Minimize,
    Restore,
    Close,
    Maximize,
    Fullscreen,
    ExitFullscreen,
    Display,
    Displays,
}

impl Mode {
    fn name(self) -> String {
        serde_json::to_value(self).ok().and_then(|v| v.as_str().map(str::to_string)).unwrap_or_default()
    }
}

// The Window tool's arguments. (A plain comment: a doc comment would reach the schema the model reads.)
#[derive(Clone, Debug, Default, serde::Deserialize, schemars::JsonSchema)]
pub struct Arguments {
    #[serde(default)]
    pub mode: Mode,
    pub app: Option<String>,
    pub id: Option<String>,
    pub loc: Option<Vec<i64>>,
    pub size: Option<Vec<i64>>,
    pub display: Option<i64>,
}

/// Reads the window again after its frame changed; an index id needs the new frame cached.
async fn remember(window: &WindowInfo, source: &dyn WindowSource, cache: &RefCell<WindowCache>) {
    if window.number.is_some() {
        return;
    }
    if let Some(applied) = source.windows_of(window.pid).await.into_iter().find(|w| window.names(w)) {
        cache.borrow_mut().remember(&[applied]);
    }
}

pub async fn run(source: &dyn WindowSource, cache: &RefCell<WindowCache>, trusted: bool, args: Arguments) -> Result<String, String> {
    let require_accessibility = || if trusted { Ok(()) } else { Err(crate::permissions::ACCESSIBILITY_MESSAGE.to_string()) };
    let mode = args.mode;
    if mode == Mode::List {
        require_accessibility()?;
        let windows = source.windows(args.app.as_deref()).await;
        if let Some(app) = &args.app {
            if windows.is_empty() && !source.app_exists(app).await {
                return Err(format!("Application '{app}' not found."));
            }
        }
        cache.borrow_mut().remember(&windows);
        return Ok(format(&windows));
    }
    if mode == Mode::Displays {
        require_accessibility()?;
        return Ok(format_displays(&source.displays().await));
    }
    let name = mode.name();
    let id = args.id.clone().ok_or_else(|| format!("id required for {name} mode."))?;
    let mut display_index = 0;
    if mode == Mode::Display {
        display_index = args.display.ok_or("display required for display mode.")?;
    }
    let pair = |values: &Option<Vec<i64>>, field: &str, message: &str| -> Result<(i64, i64), String> {
        match values.as_deref() {
            None => Err(format!("{field} required for {name} mode.")),
            Some([a, b]) => Ok((*a, *b)),
            Some(_) => Err(message.to_string()),
        }
    };
    let mut loc = (0, 0);
    let mut size = (0, 0);
    if mode == Mode::Move {
        loc = pair(&args.loc, "loc", "loc must be a list of 2 integers [x, y].")?;
    }
    if mode == Mode::Resize {
        size = pair(&args.size, "size", "size must be 2 positive integers [width, height].")?;
        if size.0 <= 0 || size.1 <= 0 {
            return Err("size must be 2 positive integers [width, height].".to_string());
        }
    }
    require_accessibility()?;
    let window = resolve(&id, source, cache).await?;
    let acted = act(source, cache, &window, &id, mode, loc, size, display_index).await;
    if acted.is_err() && !source.responds(&window).await {
        return Err(format!("Window {id} is not responding; try again when it answers."));
    }
    acted
}

/// Applies `mode` to the resolved window.
#[allow(clippy::too_many_arguments)]
async fn act(source: &dyn WindowSource, cache: &RefCell<WindowCache>, window: &WindowInfo, id: &str, mode: Mode, loc: (i64, i64), size: (i64, i64), display_index: i64) -> Result<String, String> {
    let name = mode.name();
    let failed = |mode: &str| format!("Could not {mode} window {id}.");
    let in_full_screen = format!("Window {id} is in full screen; use exit_fullscreen first.");
    let minimized = format!("Window {id} is minimized; use restore first.");
    match mode {
        Mode::Focus => {
            if !source.focus(&window).await {
                return Err(failed(&name));
            }
            Ok(format!("Focused {id}."))
        }
        Mode::Minimize | Mode::Restore => {
            let on = mode == Mode::Minimize;
            if window.minimized != on && !source.set_minimized(&window, on).await {
                return Err(failed(&name));
            }
            Ok(if on { format!("Minimized {id}.") } else { format!("Restored {id}.") })
        }
        Mode::Move | Mode::Resize => {
            let done = if mode == Mode::Move { source.move_to(&window, loc.0 as f64, loc.1 as f64).await } else { source.resize(&window, size.0 as f64, size.1 as f64).await };
            if !done {
                return Err(failed(&name));
            }
            // Report what the app applied (it may clamp); index ids need the new frame cached.
            let mut frame = if mode == Mode::Move { [loc.0, loc.1, 0, 0] } else { [0, 0, size.0, size.1] };
            if let Some(applied) = source.windows_of(window.pid).await.into_iter().find(|w| window.names(w)) {
                frame = applied.frame.rounded();
                if applied.number.is_none() {
                    cache.borrow_mut().remember(&[applied]);
                }
            }
            Ok(if mode == Mode::Move { format!("Moved {id} to ({},{}).", frame[0], frame[1]) } else { format!("Resized {id} to {}×{}.", frame[2], frame[3]) })
        }
        Mode::Maximize => {
            if window.fullscreen {
                return Err(in_full_screen);
            }
            if window.minimized {
                return Err(minimized);
            }
            let target = maximized_frame(&window.frame, &source.displays().await).ok_or_else(|| failed(&name))?;
            match source.set_frame(&window, target).await {
                FrameResult::Done => {}
                FrameResult::NotResizable => return Err(format!("Could not maximize window {id}: it cannot be resized.")),
                FrameResult::Failed => return Err(failed(&name)),
            }
            remember(&window, source, cache).await;
            Ok(format!("Maximized {id}."))
        }
        Mode::Fullscreen | Mode::ExitFullscreen => {
            let on = mode == Mode::Fullscreen;
            if window.fullscreen != on && !source.set_full_screen(&window, on).await {
                return Err(failed(if on { "fullscreen" } else { "exit fullscreen" }));
            }
            Ok(if on { format!("Full screen {id}.") } else { format!("Left full screen {id}.") })
        }
        Mode::Display => {
            if window.fullscreen {
                return Err(in_full_screen);
            }
            if window.minimized {
                return Err(minimized);
            }
            let displays = source.displays().await;
            let target = displays.iter().find(|d| d.index as i64 == display_index).ok_or_else(|| format!("No display {display_index}; see Window displays."))?;
            let could_not = format!("Could not move window {id} to display {display_index}");
            let current = display_of(&window.frame, &displays).ok_or_else(|| format!("{could_not}."))?;
            match source.set_frame(&window, moved_frame(&window.frame, current, target)).await {
                FrameResult::Done => {}
                FrameResult::NotResizable => return Err(format!("{could_not}: it does not fit and cannot be resized.")),
                FrameResult::Failed => return Err(format!("{could_not}.")),
            }
            remember(&window, source, cache).await;
            Ok(format!("Moved {id} to display {display_index}."))
        }
        Mode::Close => {
            if !source.close(&window).await {
                return Err(failed(&name));
            }
            Ok(format!("Close requested for {id} (\"{}\").", flat(&window.title)))
        }
        Mode::List | Mode::Displays => unreachable!("answered above"),
    }
}
