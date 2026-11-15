//! What the core's integration tests share: a fake main loop, a fake desktop, and request helpers.
#![allow(dead_code)]
use bytes::Bytes;
use desktop_mcp_core::guest::{Activity, MainLoop, Tick};
use desktop_mcp_core::http::{App, Body};
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
    let (main_loop, desktop) = (Rc::new(FakeLoop::default()), Rc::new(FakeDesktop::default()));
    let for_server = desktop.clone();
    let app = Rc::new(App {
        make_server: Box::new(move || Server::new(for_server.clone(), &["Wait"])),
        activity: Activity::new(main_loop.clone()),
        sessions: Sessions::new(settings),
    });
    Fixture { app, main_loop, desktop }
}

/// Runs `future` on a current-thread runtime inside a `LocalSet`, as the server does.
pub fn block_on<T>(future: impl Future<Output = T>) -> T {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
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
