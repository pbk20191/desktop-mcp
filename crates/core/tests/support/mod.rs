//! What the core's integration tests share: a fake main loop, a fake desktop, and request helpers.
#![allow(dead_code)]
use bytes::Bytes;
use desktop_mcp_core::guest::{Activity, MainLoop, Tick};
use desktop_mcp_core::http::{App, Body, Security};
use desktop_mcp_core::shell::{Run, ShellMode};
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

/// A desktop whose `sleep` ends when the test releases it (or at once when no gate is set).
#[derive(Default)]
pub struct FakeDesktop {
    pub gate: RefCell<Option<tokio::sync::oneshot::Receiver<()>>>,
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
impl Desktop for FakeDesktop {
    fn search_files(&self, _search: desktop_mcp_core::spotlight::Search) -> Pin<Box<dyn Future<Output = Result<desktop_mcp_core::spotlight::Found, String>>>> {
        unreachable!()
    }
    fn list_shortcuts(&self, _folder: Option<&str>, _timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>> {
        unreachable!()
    }
    fn run_shortcut(&self, _name: &str, _input: Option<&str>, _timeout: Duration) -> Pin<Box<dyn Future<Output = Result<desktop_mcp_core::shortcuts::ShortcutRun, String>>>> {
        unreachable!()
    }
    fn notify(&self, _message: &str, _title: &str, _subtitle: Option<&str>, _sound: Option<&str>) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        unreachable!()
    }
    fn open(&self, _target: &desktop_mcp_core::open::Target, _app: Option<&std::path::Path>) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        unreachable!()
    }
    fn reveal(&self, _path: &std::path::Path) -> Pin<Box<dyn Future<Output = ()>>> {
        unreachable!()
    }
    fn find_application(&self, _name: &str) -> Pin<Box<dyn Future<Output = Option<desktop_mcp_core::open::Application>>>> {
        unreachable!()
    }
    fn read_clipboard(&self, _image_max_side: u32) -> Pin<Box<dyn Future<Output = desktop_mcp_core::clipboard::Clipboard>>> {
        Box::pin(async { Default::default() })
    }
    fn write_clipboard(&self, _contents: desktop_mcp_core::clipboard::ClipboardWrite) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        Box::pin(async { Err("no clipboard in these tests".to_string()) })
    }
    fn resolve(&self, _host: &str) -> Pin<Box<dyn Future<Output = Result<Vec<std::net::IpAddr>, String>>>> {
        Box::pin(async { Err("no network in these tests".to_string()) })
    }
    fn connect(&self, _host: &str, _port: u16, _tls: bool) -> Pin<Box<dyn Future<Output = Result<Box<dyn desktop_mcp_core::io::Stream>, String>>>> {
        Box::pin(async { Err("no network in these tests".to_string()) })
    }
    fn parse_html(&self, _body: &[u8], _charset: Option<&str>) -> Option<desktop_mcp_core::html::Element> {
        None
    }
    fn shell(&self, _command: &str, _mode: ShellMode, _timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>> {
        Box::pin(async { Err("no shell in these tests".to_string()) })
    }
    fn sleep(&self, _duration: Duration) -> Pin<Box<dyn Future<Output = ()>>> {
        let gate = self.gate.borrow_mut().take();
        Box::pin(async move {
            if let Some(gate) = gate {
                let _ = gate.await;
            }
        })
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
