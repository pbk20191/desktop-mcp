//! What the core's integration tests share: a fake main loop, a fake desktop, and request helpers.
#![allow(dead_code)]
use bytes::Bytes;
use desktop_mcp_core::guest::{Activity, MainLoop, Tick};
use desktop_mcp_core::http::{App, Body, Security};
use desktop_mcp_core::shell::{Run, ShellMode};
use desktop_mcp_core::clipboard::{Clipboard, ClipboardWrite};
use desktop_mcp_core::open::{Application, Target};
use desktop_mcp_core::shortcuts::ShortcutRun;
use desktop_mcp_core::spotlight::{Found, Search};
use desktop_mcp_core::permissions::{Permission, PermissionState};
use desktop_mcp_core::wait::RunningApp;
use std::path::{Path, PathBuf};
use desktop_mcp_core::mcp::{Desktop, Server};
use desktop_mcp_core::session::{SessionSettings, Sessions};
use http::{Request, Response};
use http_body_util::{BodyExt, Full};
use std::cell::{Cell, RefCell};
use std::future::Future;
use std::pin::Pin;
use std::rc::Rc;
use std::time::Duration;

#[derive(Default)]
pub struct FakeLoop {
    pub tick: Cell<Option<Tick>>,
}
impl MainLoop for FakeLoop {
    fn kick(&self) {}
    fn set_tick(&self, rate: Tick) {
        self.tick.set(Some(rate))
    }
}

/// A notification asked for: message, title, subtitle, sound.
pub type Notified = (String, String, Option<String>, Option<String>);

/// A desktop that records what the tools ask of it and answers as each test sets it up.
#[derive(Default)]
pub struct FakeDesktop {
    pub snapshot_state: RefCell<desktop_mcp_core::snapshot::State>,
    /// What each `find_element` look answers (the last repeats), and the deadlines it was given.
    pub element_looks: RefCell<Vec<Option<desktop_mcp_core::element::FoundElement>>>,
    pub element_deadlines: RefCell<Vec<std::time::Instant>>,
    pub menu_requests: RefCell<Vec<(Option<String>, desktop_mcp_core::menu::Request)>>,
    pub window_source: Option<Rc<dyn desktop_mcp_core::window::WindowSource>>,
    pub launched: RefCell<Vec<String>>,
    pub launch_fails: bool,
    /// Apps `activate_app` finds, and the names it was asked for.
    pub activatable: Vec<&'static str>,
    pub activated: RefCell<Vec<String>>,
    pub front_window: bool,
    pub frame_changes: RefCell<Vec<desktop_mcp_core::app::FrameChange>>,
    pub terminated: RefCell<Vec<(Vec<i32>, bool)>>,
    pub terminate_fails: bool,
    /// Every input event posted, in order.
    pub posted: RefCell<Vec<desktop_mcp_core::input::Event>>,
    /// Answers as Windows does for editing chords and extra key names.
    pub windows_keys: bool,
    /// What `input_refusal` answers.
    pub refuse_input: Option<String>,
    /// What `window_at` answers: (title, app).
    pub window: Option<(String, String)>,
    /// What `take_input_failure` answers once.
    pub input_failure: RefCell<Option<String>>,
    /// Accessibility is refused when set.
    pub untrusted: bool,
    pub cursor: (f64, f64),
    /// Whether the point being typed at is a password field.
    pub secure_input: bool,
    /// Whether typing needs a keyboard layout switched (and so restored).
    pub switches_layout: bool,
    pub layout_restored: Rc<Cell<usize>>,
    /// When set, the next `sleep` waits for it (see `hold_next_sleep`).
    pub gate: RefCell<Option<tokio::sync::oneshot::Receiver<()>>>,
    pub slept: RefCell<Vec<Duration>>,
    pub shells: RefCell<Vec<(String, ShellMode, Duration)>>,
    pub shell_answer: RefCell<Option<Result<Run, String>>>,
    pub clipboard: RefCell<Clipboard>,
    pub image_limits: RefCell<Vec<u32>>,
    pub written: RefCell<Vec<ClipboardWrite>>,
    pub refuse_writes: bool,
    pub opened: RefCell<Vec<(Target, Option<PathBuf>)>>,
    pub revealed: RefCell<Vec<PathBuf>>,
    pub applications: Vec<(&'static str, Application)>,
    pub notified: RefCell<Vec<Notified>>,
    pub notify_failure: Option<&'static str>,
    pub shortcut_names: &'static str,
    pub list_failure: Option<Run>,
    pub shortcut_run: Option<ShortcutRun>,
    pub shortcut_calls: RefCell<Vec<String>>,
    pub searches: RefCell<Vec<Search>>,
    /// Answers for the name query, the content-only query and a raw query, in that order of use.
    pub found: RefCell<Vec<Result<Found, String>>>,
    /// How long each search pretends to take.
    pub search_takes: Duration,
    pub permissions: Option<PermissionState>,
    pub requested: RefCell<Vec<Permission>>,
    /// The running apps at each look; the last answer repeats.
    pub looks: RefCell<Vec<Vec<RunningApp>>>,
    pub looked: Cell<usize>,
}
impl Desktop for FakeDesktop {
    fn create_space(&self, _open_delay: f64, _close_after: bool) -> Pin<Box<dyn Future<Output = String> + '_>> {
        Box::pin(async { "Created new desktop space.".to_string() })
    }
    fn snapshot(&self, _use_vision: bool) -> Pin<Box<dyn Future<Output = desktop_mcp_core::snapshot::State>>> {
        let state = self.snapshot_state.borrow().clone();
        Box::pin(async move { state })
    }
    fn read_text(&self, _request: desktop_mcp_core::ocr::Request) -> Pin<Box<dyn Future<Output = Result<Vec<desktop_mcp_core::ocr::TextLine>, String>>>> {
        Box::pin(async { Ok(vec![]) })
    }
    fn find_element(&self, _app: Option<&str>, _title: Option<&str>, _role: Option<&str>, deadline: std::time::Instant) -> Pin<Box<dyn Future<Output = Option<desktop_mcp_core::element::FoundElement>>>> {
        self.element_deadlines.borrow_mut().push(deadline);
        let mut looks = self.element_looks.borrow_mut();
        let found = if looks.len() > 1 { looks.remove(0) } else { looks.first().cloned().flatten() };
        Box::pin(async move { found })
    }
    fn menu(&self, app: Option<String>, request: desktop_mcp_core::menu::Request) -> Pin<Box<dyn Future<Output = Result<String, String>>>> {
        self.menu_requests.borrow_mut().push((app, request));
        Box::pin(async { Ok("menu".to_string()) })
    }
    fn window_source(&self) -> Rc<dyn desktop_mcp_core::window::WindowSource> {
        self.window_source.clone().expect("a window source")
    }
    fn launch_app(&self, name: &str) -> Pin<Box<dyn Future<Output = bool>>> {
        self.launched.borrow_mut().push(name.to_string());
        let ok = !self.launch_fails;
        Box::pin(async move { ok })
    }
    fn activate_app(&self, name: &str) -> Pin<Box<dyn Future<Output = bool>>> {
        self.activated.borrow_mut().push(name.to_string());
        let found = self.activatable.contains(&name);
        Box::pin(async move { found })
    }
    fn change_front_window(&self, change: Option<desktop_mcp_core::app::FrameChange>) -> Pin<Box<dyn Future<Output = bool>>> {
        self.frame_changes.borrow_mut().extend(change);
        let found = self.front_window;
        Box::pin(async move { found })
    }
    fn terminate(&self, pids: &[i32], force: bool) -> Pin<Box<dyn Future<Output = bool>>> {
        self.terminated.borrow_mut().push((pids.to_vec(), force));
        let ok = !self.terminate_fails;
        Box::pin(async move { ok })
    }
    fn accessibility_trusted(&self) -> bool {
        !self.untrusted
    }
    fn editing_keys(&self) -> &'static desktop_mcp_core::input::EditingKeys {
        const WINDOWS: desktop_mcp_core::input::EditingKeys = desktop_mcp_core::input::EditingKeys { select_all: &["ctrl", "a"], line_start: &["ctrl", "home"], line_end: &["ctrl", "end"] };
        if self.windows_keys { &WINDOWS } else { &desktop_mcp_core::input::MAC_EDITING }
    }
    fn take_input_failure(&self) -> Option<String> {
        self.input_failure.borrow_mut().take()
    }
    fn input_refusal(&self, _at: Option<(i64, i64)>) -> Option<String> {
        self.refuse_input.clone()
    }
    fn window_at(&self, _at: Option<(i64, i64)>) -> Option<(String, String)> {
        self.window.clone()
    }
    fn extra_keys(&self) -> &'static [&'static str] {
        if self.windows_keys { &["win", "insert"] } else { &[] }
    }
    fn post(&self, event: desktop_mcp_core::input::Event) {
        self.posted.borrow_mut().push(event);
    }
    fn cursor_position(&self) -> (f64, f64) {
        self.cursor
    }
    fn secure_input_at(&self, _x: i64, _y: i64) -> Pin<Box<dyn Future<Output = bool>>> {
        let secure = self.secure_input;
        Box::pin(async move { secure })
    }
    fn select_typing_layout(&self) -> Option<Box<dyn FnOnce()>> {
        let restored = self.layout_restored.clone();
        self.switches_layout.then(|| Box::new(move || restored.set(restored.get() + 1)) as Box<dyn FnOnce()>)
    }
    fn running_apps(&self) -> Pin<Box<dyn Future<Output = Vec<RunningApp>>>> {
        self.looked.set(self.looked.get() + 1);
        let mut looks = self.looks.borrow_mut();
        let apps = if looks.len() > 1 { looks.remove(0) } else { looks.first().cloned().unwrap_or_default() };
        Box::pin(async move { apps })
    }
    fn permissions(&self) -> Pin<Box<dyn Future<Output = PermissionState>>> {
        let state = self.permissions.expect("a permission state");
        Box::pin(async move { state })
    }
    fn request_permission(&self, permission: Permission) -> Pin<Box<dyn Future<Output = ()>>> {
        self.requested.borrow_mut().push(permission);
        Box::pin(async {})
    }
    fn search_files(&self, search: Search) -> Pin<Box<dyn Future<Output = Result<Found, String>>>> {
        self.searches.borrow_mut().push(search);
        std::thread::sleep(self.search_takes);
        let answer = if self.found.borrow().is_empty() { Ok(Found::default()) } else { self.found.borrow_mut().remove(0) };
        Box::pin(async move { answer })
    }
    fn list_shortcuts(&self, folder: Option<&str>, timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>> {
        self.shortcut_calls.borrow_mut().push(format!("list {folder:?} {}", timeout.as_secs()));
        let run = self.list_failure.clone().unwrap_or(Run { stdout: self.shortcut_names.into(), stderr: String::new(), status: 0, timed_out: false });
        Box::pin(async move { Ok(run) })
    }
    fn run_shortcut(&self, name: &str, input: Option<&str>, timeout: Duration) -> Pin<Box<dyn Future<Output = Result<ShortcutRun, String>>>> {
        self.shortcut_calls.borrow_mut().push(format!("run {name} {input:?} {}", timeout.as_secs()));
        let run = self.shortcut_run.clone().unwrap_or(ShortcutRun { status: 0, timed_out: false, output: String::new(), stderr: String::new() });
        Box::pin(async move { Ok(run) })
    }
    fn notify(&self, message: &str, title: &str, subtitle: Option<&str>, sound: Option<&str>) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        self.notified.borrow_mut().push((message.into(), title.into(), subtitle.map(Into::into), sound.map(Into::into)));
        let outcome = self.notify_failure.map(|reason| Err(reason.to_string())).unwrap_or(Ok(()));
        Box::pin(async move { outcome })
    }
    fn open(&self, target: &Target, app: Option<&Path>) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        self.opened.borrow_mut().push((target.clone(), app.map(Path::to_path_buf)));
        Box::pin(async { Ok(()) })
    }
    fn reveal(&self, path: &Path) -> Pin<Box<dyn Future<Output = ()>>> {
        self.revealed.borrow_mut().push(path.to_path_buf());
        Box::pin(async {})
    }
    fn find_application(&self, name: &str) -> Pin<Box<dyn Future<Output = Option<Application>>>> {
        let found = self.applications.iter().find(|(known, _)| *known == name).map(|(_, application)| application.clone());
        Box::pin(async move { found })
    }
    fn read_clipboard(&self, image_max_side: u32) -> Pin<Box<dyn Future<Output = Clipboard>>> {
        self.image_limits.borrow_mut().push(image_max_side);
        let contents = self.clipboard.borrow().clone();
        Box::pin(async move { contents })
    }
    fn write_clipboard(&self, contents: ClipboardWrite) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        self.written.borrow_mut().push(contents);
        let refused = self.refuse_writes;
        Box::pin(async move { if refused { Err("Could not write to the clipboard.".to_string()) } else { Ok(()) } })
    }
    fn resolve(&self, _host: &str) -> Pin<Box<dyn Future<Output = Result<Vec<std::net::IpAddr>, String>>>> {
        Box::pin(async { Err("no network in these tests".to_string()) })
    }
    fn connect(&self, _host: &str, _port: u16, _tls: bool) -> Pin<Box<dyn Future<Output = Result<Box<dyn desktop_mcp_core::io::Stream>, String>>>> {
        Box::pin(async { Err("no network in these tests".to_string()) })
    }
    fn background(&self, work: Box<dyn FnOnce() + Send>) -> Pin<Box<dyn Future<Output = ()>>> {
        work();
        Box::pin(async {})
    }
    fn parse_html(&self, _body: Vec<u8>, _charset: Option<String>) -> Pin<Box<dyn Future<Output = Option<desktop_mcp_core::html::Element>>>> {
        Box::pin(async { None })
    }
    fn sleep(&self, duration: Duration) -> Pin<Box<dyn Future<Output = ()>>> {
        self.slept.borrow_mut().push(duration);
        let gate = self.gate.borrow_mut().take();
        Box::pin(async move {
            if let Some(gate) = gate {
                let _ = gate.await;
            }
        })
    }
    fn shell(&self, command: &str, mode: ShellMode, timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>> {
        self.shells.borrow_mut().push((command.to_string(), mode, timeout));
        let answer = self.shell_answer.borrow_mut().take().expect("a canned shell answer");
        Box::pin(async move { answer })
    }
}
impl FakeDesktop {
    pub fn answering(answer: Result<Run, String>) -> Rc<FakeDesktop> {
        let desktop = FakeDesktop::default();
        *desktop.shell_answer.borrow_mut() = Some(answer);
        Rc::new(desktop)
    }
}

impl FakeDesktop {
    /// The next `sleep` waits for the returned sender (or for it to be dropped).
    pub fn hold_next_sleep(&self) -> tokio::sync::oneshot::Sender<()> {
        let (release, gate) = tokio::sync::oneshot::channel();
        *self.gate.borrow_mut() = Some(gate);
        release
    }
    /// Whether a held `sleep` has started.
    pub fn sleeping(&self) -> bool {
        self.gate.borrow().is_none()
    }
}
pub struct Fixture {
    pub app: Rc<App>,
    pub main_loop: Rc<FakeLoop>,
    pub desktop: Rc<FakeDesktop>,
}

pub fn fixture() -> Fixture {
    fixture_with(SessionSettings::default())
}

pub fn fixture_with(settings: SessionSettings) -> Fixture {
    fixture_full(settings, Security::default())
}

pub fn fixture_secured(security: Security) -> Fixture {
    fixture_full(SessionSettings::default(), security)
}

pub fn fixture_full(settings: SessionSettings, security: Security) -> Fixture {
    let (main_loop, desktop) = (Rc::new(FakeLoop::default()), Rc::new(FakeDesktop::default()));
    let for_server = desktop.clone();
    let app = Rc::new(App {
        make_server: Box::new(move || Server::new(for_server.clone(), &["Wait"])),
        activity: Activity::new(main_loop.clone()),
        sessions: Sessions::new(settings),
        security,
    });
    Fixture { app, main_loop, desktop }
}

/// Runs `future` on a current-thread runtime inside a `LocalSet`, as the server does.
pub fn block_on<T>(future: impl Future<Output = T>) -> T {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    tokio::task::LocalSet::new().block_on(&runtime, future)
}

/// As [`block_on`], with tokio's clock paused: it jumps ahead whenever everything waits on a timer.
pub fn block_on_paused<T>(future: impl Future<Output = T>) -> T {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().start_paused(true).build().unwrap();
    tokio::task::LocalSet::new().block_on(&runtime, future)
}

pub fn body(text: &str) -> Full<Bytes> {
    Full::new(Bytes::from(text.to_string()))
}

pub async fn text_of(response: Response<Body>) -> String {
    String::from_utf8(response.into_body().collect().await.unwrap().to_bytes().to_vec()).unwrap()
}

pub async fn json_of(response: Response<Body>) -> serde_json::Value {
    let text = text_of(response).await;
    serde_json::from_str(&text).unwrap_or_else(|_| panic!("not JSON: {text:?}"))
}

/// Server-sent events as (event name, data) pairs.
pub fn events(text: &str) -> Vec<(Option<String>, String)> {
    text.split("\n\n")
        .filter(|block| !block.trim().is_empty())
        .map(|block| {
            let mut event = None;
            let mut data = Vec::new();
            for line in block.lines() {
                if let Some(name) = line.strip_prefix("event:") {
                    event = Some(name.trim().to_string());
                } else if let Some(value) = line.strip_prefix("data:") {
                    data.push(value.strip_prefix(' ').unwrap_or(value).to_string());
                }
            }
            (event, data.join("\n"))
        })
        .collect()
}

/// The JSON-RPC message an SSE response carries (its one non-empty data event).
pub async fn sse_message(response: Response<Body>) -> serde_json::Value {
    let text = text_of(response).await;
    let messages: Vec<_> = events(&text).into_iter().filter(|(_, data)| !data.is_empty()).collect();
    assert_eq!(messages.len(), 1, "one message expected in {text:?}");
    serde_json::from_str(&messages[0].1).unwrap()
}

pub const BOTH: &str = "application/json, text/event-stream";

pub fn request(method: &str, accept: &str, session: Option<&str>, message: &str) -> Request<Full<Bytes>> {
    let mut builder = Request::builder().method(method).uri("/mcp").header("Accept", accept).header("Content-Type", "application/json");
    if let Some(id) = session {
        builder = builder.header("Mcp-Session-Id", id);
    }
    builder.body(body(message)).unwrap()
}

pub fn initialize() -> String {
    serde_json::json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}).to_string()
}

/// An in-memory byte stream for the stdio binding: the test writes what the server reads and reads what it writes.
#[derive(Clone, Default)]
pub struct Pipe {
    state: Rc<RefCell<PipeState>>,
}

#[derive(Default)]
struct PipeState {
    input: std::collections::VecDeque<u8>,
    input_closed: bool,
    reader: Option<std::task::Waker>,
    output: Vec<u8>,
    output_closed: bool,
    writer_watch: Option<std::task::Waker>,
    shut_down: bool,
}

impl Pipe {
    pub fn send_line(&self, line: &str) {
        let mut state = self.state.borrow_mut();
        state.input.extend(line.as_bytes());
        state.input.push_back(b'\n');
        if let Some(waker) = state.reader.take() {
            waker.wake();
        }
    }
    pub fn send_raw(&self, bytes: &[u8]) {
        let mut state = self.state.borrow_mut();
        state.input.extend(bytes);
        if let Some(waker) = state.reader.take() {
            waker.wake();
        }
    }
    /// The client closes its end of the server's input (EOF).
    pub fn close_input(&self) {
        let mut state = self.state.borrow_mut();
        state.input_closed = true;
        if let Some(waker) = state.reader.take() {
            waker.wake();
        }
    }
    /// The client stops reading: further writes fail.
    pub fn close_output(&self) {
        self.state.borrow_mut().output_closed = true;
    }
    /// The bytes written so far.
    pub fn output(&self) -> Vec<u8> {
        self.state.borrow().output.clone()
    }
    /// Whether the other side shut its writing down (EOF for the reader at this end).
    pub fn shut_down(&self) -> bool {
        self.state.borrow().shut_down
    }
    /// The complete lines written so far, as JSON.
    pub fn lines(&self) -> Vec<serde_json::Value> {
        let state = self.state.borrow();
        let text = String::from_utf8_lossy(&state.output).into_owned();
        text.split_terminator('\n').map(|line| serde_json::from_str(line).unwrap_or_else(|_| panic!("not a JSON line: {line:?}"))).collect()
    }
    /// Waits until `count` lines have been written.
    pub async fn wait_lines(&self, count: usize) -> Vec<serde_json::Value> {
        for _ in 0..1000 {
            if self.lines().len() >= count {
                return self.lines();
            }
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        panic!("{} lines written, {count} expected: {:?}", self.lines().len(), self.lines());
    }
}

impl hyper::rt::Read for Pipe {
    fn poll_read(self: Pin<&mut Self>, cx: &mut std::task::Context<'_>, mut buffer: hyper::rt::ReadBufCursor<'_>) -> std::task::Poll<std::io::Result<()>> {
        let mut state = self.state.borrow_mut();
        if state.input.is_empty() {
            if state.input_closed {
                return std::task::Poll::Ready(Ok(()));
            }
            state.reader = Some(cx.waker().clone());
            return std::task::Poll::Pending;
        }
        let count = state.input.len().min(buffer.remaining());
        let bytes: Vec<u8> = state.input.drain(..count).collect();
        buffer.put_slice(&bytes);
        std::task::Poll::Ready(Ok(()))
    }
}

impl hyper::rt::Write for Pipe {
    fn poll_write(self: Pin<&mut Self>, _cx: &mut std::task::Context<'_>, bytes: &[u8]) -> std::task::Poll<std::io::Result<usize>> {
        let mut state = self.state.borrow_mut();
        if state.output_closed {
            return std::task::Poll::Ready(Err(std::io::ErrorKind::BrokenPipe.into()));
        }
        // A short write now and then, as a pipe does.
        let count = bytes.len().min(7);
        state.output.extend_from_slice(&bytes[..count]);
        if let Some(waker) = state.writer_watch.take() {
            waker.wake();
        }
        std::task::Poll::Ready(Ok(count))
    }
    fn poll_flush(self: Pin<&mut Self>, _cx: &mut std::task::Context<'_>) -> std::task::Poll<std::io::Result<()>> {
        std::task::Poll::Ready(Ok(()))
    }
    fn poll_shutdown(self: Pin<&mut Self>, _cx: &mut std::task::Context<'_>) -> std::task::Poll<std::io::Result<()>> {
        self.state.borrow_mut().shut_down = true;
        std::task::Poll::Ready(Ok(()))
    }
}

/// One direction of an in-memory connection.
#[derive(Default)]
struct Lane {
    bytes: std::collections::VecDeque<u8>,
    closed: bool,
    reader: Option<std::task::Waker>,
}

/// One end of an in-memory connection: reads what the other end writes.
pub struct Duplex {
    incoming: Rc<RefCell<Lane>>,
    outgoing: Rc<RefCell<Lane>>,
}

/// Two connected ends.
pub fn duplex() -> (Duplex, Duplex) {
    let (a, b) = (Rc::new(RefCell::new(Lane::default())), Rc::new(RefCell::new(Lane::default())));
    (Duplex { incoming: a.clone(), outgoing: b.clone() }, Duplex { incoming: b, outgoing: a })
}

impl Drop for Duplex {
    fn drop(&mut self) {
        let mut lane = self.outgoing.borrow_mut();
        lane.closed = true;
        if let Some(waker) = lane.reader.take() {
            waker.wake();
        }
    }
}

impl desktop_mcp_core::io::Peer for Duplex {
    fn peer_address(&self) -> Option<std::net::IpAddr> {
        Some(std::net::IpAddr::from([127, 0, 0, 1]))
    }
}

impl hyper::rt::Read for Duplex {
    fn poll_read(self: Pin<&mut Self>, cx: &mut std::task::Context<'_>, mut buffer: hyper::rt::ReadBufCursor<'_>) -> std::task::Poll<std::io::Result<()>> {
        let mut lane = self.incoming.borrow_mut();
        if lane.bytes.is_empty() {
            if lane.closed {
                return std::task::Poll::Ready(Ok(()));
            }
            lane.reader = Some(cx.waker().clone());
            return std::task::Poll::Pending;
        }
        let count = lane.bytes.len().min(buffer.remaining());
        let bytes: Vec<u8> = lane.bytes.drain(..count).collect();
        buffer.put_slice(&bytes);
        std::task::Poll::Ready(Ok(()))
    }
}

impl hyper::rt::Write for Duplex {
    fn poll_write(self: Pin<&mut Self>, _cx: &mut std::task::Context<'_>, bytes: &[u8]) -> std::task::Poll<std::io::Result<usize>> {
        let mut lane = self.outgoing.borrow_mut();
        if lane.closed {
            return std::task::Poll::Ready(Err(std::io::ErrorKind::BrokenPipe.into()));
        }
        lane.bytes.extend(bytes);
        if let Some(waker) = lane.reader.take() {
            waker.wake();
        }
        std::task::Poll::Ready(Ok(bytes.len()))
    }
    fn poll_flush(self: Pin<&mut Self>, _cx: &mut std::task::Context<'_>) -> std::task::Poll<std::io::Result<()>> {
        std::task::Poll::Ready(Ok(()))
    }
    fn poll_shutdown(self: Pin<&mut Self>, _cx: &mut std::task::Context<'_>) -> std::task::Poll<std::io::Result<()>> {
        let mut lane = self.outgoing.borrow_mut();
        lane.closed = true;
        if let Some(waker) = lane.reader.take() {
            waker.wake();
        }
        std::task::Poll::Ready(Ok(()))
    }
}

/// Connects to `app` in memory: every connection is served by hyper on the other end of a duplex.
#[derive(Clone)]
pub struct InMemory {
    pub app: Rc<App>,
    pub connections: Rc<Cell<usize>>,
    /// While true, connecting fails as an unreachable server would.
    pub down: Rc<Cell<bool>>,
}

impl InMemory {
    pub fn new(app: Rc<App>) -> InMemory {
        InMemory { app, connections: Rc::default(), down: Rc::default() }
    }
}

impl desktop_mcp_core::http_bridge::Connect for InMemory {
    type Io = Duplex;
    fn connect(&self) -> Pin<Box<dyn Future<Output = Result<Duplex, String>>>> {
        if self.down.get() {
            return Box::pin(async { Err("Connection refused".to_string()) });
        }
        self.connections.set(self.connections.get() + 1);
        let (client, server) = duplex();
        let app = self.app.clone();
        tokio::task::spawn_local(async move {
            let service = hyper::service::service_fn(move |request| {
                let app = app.clone();
                async move { Ok::<_, std::convert::Infallible>(desktop_mcp_core::http::handle(&app, request).await) }
            });
            let _ = hyper::server::conn::http1::Builder::new().serve_connection(server, service).await;
        });
        Box::pin(async move { Ok(client) })
    }
}

/// Sends one `tools/call` to a fresh server offering `tools`, and returns the answer as JSON.
pub fn call_tool(desktop: &Rc<FakeDesktop>, tools: &[&str], name: &str, arguments: serde_json::Value) -> serde_json::Value {
    let server = Server::new(desktop.clone(), tools);
    let message = serde_json::json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":name,"arguments":arguments}});
    let answer = block_on(desktop_mcp_core::mcp::answer_stateless(server, serde_json::from_value(message).unwrap()));
    serde_json::to_value(answer.expect("an answer")).unwrap()
}

/// The text blocks of a tool's answer.
pub fn texts(answer: &serde_json::Value) -> Vec<String> {
    answer["result"]["content"].as_array().unwrap_or_else(|| panic!("no content in {answer}")).iter().filter(|c| c["type"] == "text").map(|c| c["text"].as_str().unwrap().to_string()).collect()
}
