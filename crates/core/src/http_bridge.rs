//! `connect` over HTTP: a stdio MCP endpoint that posts every message to the streamable-http server and writes
//! what comes back, one JSON-RPC message per line.
//!
//! The bridge opens a session (it sends `initialize` accepting server-sent events) and follows it: a `GET`
//! listener hands on what the server starts on its own, and when the server has forgotten the session (404),
//! the first `initialize` is sent again for a new one, its reply dropped, and the message is sent once more.
//! Each line is forwarded on its own, so a slow call does not hold up the next. At end of input the listener
//! stops and the forwarded requests run to their end; a failed write to stdout ends everything.
use crate::launch_agent::HttpTarget;
use bytes::Bytes;
use http::{Method, Request, Response, StatusCode};
use http_body_util::{BodyExt, Full};
use hyper::body::Incoming;
use serde_json::Value;
use std::cell::RefCell;
use std::future::{poll_fn, Future};
use std::pin::Pin;
use std::rc::Rc;
use std::time::Duration;
use tokio::sync::mpsc;
use tokio::task::JoinHandle;

/// Opens connections to the server: the platform's transport.
pub trait Connect: 'static {
    type Io: hyper::rt::Read + hyper::rt::Write + Unpin + 'static;
    fn connect(&self) -> Pin<Box<dyn Future<Output = Result<Self::Io, String>>>>;
}

#[derive(Default)]
struct State {
    session_id: Option<String>,
    /// The first `initialize` forwarded: what a recovery sends again.
    initialize_line: Option<String>,
    /// A replay failed: the bridge has no session although it had one, and since a request without a session
    /// is never answered 404, the next request must recover first.
    needs_session: bool,
    /// Set at end of input: no new listener from then on.
    closed: bool,
    listener: Option<JoinHandle<()>>,
    listener_session: Option<String>,
    replays: usize,
    /// Bumped by every recovery, so requests that waited for one take its outcome instead of trying again.
    recoveries: u64,
}

pub struct HttpBridge<C: Connect> {
    target: HttpTarget,
    uri: http::Uri,
    connector: C,
    state: RefCell<State>,
    recovery: tokio::sync::Mutex<()>,
}

/// Where answers go: one line each, written in order.
type Output = mpsc::UnboundedSender<String>;

fn log(message: &str) {
    eprintln!("[desktop-mcp] {message}");
}

impl<C: Connect> HttpBridge<C> {
    pub fn new(target: HttpTarget, connector: C) -> HttpBridge<C> {
        let uri = target.url.parse().unwrap_or_else(|_| http::Uri::from_static("http://127.0.0.1/mcp"));
        HttpBridge { target, uri, connector, state: RefCell::default(), recovery: tokio::sync::Mutex::new(()) }
    }

    pub fn session_id(&self) -> Option<String> {
        self.state.borrow().session_id.clone()
    }

    /// Recoveries attempted.
    pub fn replays(&self) -> usize {
        self.state.borrow().replays
    }

    pub fn connector(&self) -> &C {
        &self.connector
    }

    async fn send(&self, method: Method, body: Option<&str>, session: Option<&str>) -> Result<Response<Incoming>, String> {
        let io = self.connector.connect().await?;
        let (mut sender, connection) = hyper::client::conn::http1::handshake::<_, Full<Bytes>>(io).await.map_err(|error| error.to_string())?;
        tokio::task::spawn_local(async move {
            let _ = connection.await;
        });
        let path = self.uri.path_and_query().map(|p| p.as_str()).unwrap_or("/mcp");
        let mut request = Request::builder().method(method.clone()).uri(path).header(http::header::HOST, self.uri.authority().map(|a| a.as_str()).unwrap_or("localhost"));
        request = if method == Method::POST {
            request.header(http::header::CONTENT_TYPE, "application/json").header(http::header::ACCEPT, "application/json, text/event-stream")
        } else {
            request.header(http::header::ACCEPT, "text/event-stream")
        };
        if let Some(key) = &self.target.auth_key {
            request = request.header(http::header::AUTHORIZATION, format!("Bearer {key}"));
        }
        if let Some(session) = session {
            request = request.header("mcp-session-id", session);
        }
        let request = request.body(Full::new(Bytes::from(body.unwrap_or_default().to_string()))).map_err(|error| error.to_string())?;
        sender.send_request(request).await.map_err(|error| error.to_string())
    }

    /// One message in, zero or more messages out: nothing for a notification or a response (202), the reply for a
    /// request (after whatever the server sends first on its event stream), or a JSON-RPC error carrying the
    /// failure when the server could not be reached, refused the request, or ended the response without the reply.
    async fn forward(self: &Rc<Self>, line: &str, output: &Output) {
        let body = line.trim();
        let value: Option<Value> = serde_json::from_str(body).ok();
        let is_initialize = value.as_ref().is_some_and(|v| v["method"] == "initialize" && !v["id"].is_null());
        if is_initialize {
            self.state.borrow_mut().initialize_line.get_or_insert_with(|| body.to_string());
        }
        let id = value.as_ref().filter(|v| v.get("method").is_some() && !v["id"].is_null()).map(|v| v["id"].clone());
        let answered = std::cell::Cell::new(false);
        let emit = |payload: String| {
            let reply = single_line(&payload);
            if let Some(id) = &id {
                if !answered.get() && is_response(&reply, id) {
                    answered.set(true);
                }
            }
            let _ = output.send(reply);
        };
        let outcome: Result<(), String> = async {
            let mut sent = self.session_id();
            let mut recovered = false;
            // A client's own `initialize` starts a session by itself.
            if sent.is_none() && !is_initialize && self.state.borrow().needs_session {
                recovered = true;
                sent = self.recover(None, output).await;
            }
            let mut response = self.send(Method::POST, Some(body), sent.as_deref()).await?;
            if response.status() == StatusCode::NOT_FOUND && !recovered {
                if let Some(stale) = sent.clone() {
                    if let Some(fresh) = self.recover(Some(&stale), output).await {
                        sent = Some(fresh.clone());
                        response = self.send(Method::POST, Some(body), Some(&fresh)).await?;
                    }
                }
            }
            if let Some(session) = response.headers().get("mcp-session-id").and_then(|v| v.to_str().ok()) {
                self.remember(session, sent.as_deref());
            }
            let status = response.status();
            if !status.is_success() {
                let head = read_head(response.into_body(), 500).await;
                return Err(format!("HTTP {}{}", status.as_u16(), if head.is_empty() { String::new() } else { format!(": {head}") }));
            }
            if status == StatusCode::ACCEPTED {
                return Ok(());
            }
            let is_event_stream = response.headers().get(http::header::CONTENT_TYPE).and_then(|v| v.to_str().ok()).is_some_and(|v| v.to_ascii_lowercase().starts_with("text/event-stream"));
            let mut body = response.into_body();
            if is_event_stream {
                let mut parser = SseParser::default();
                while let Some(frame) = body.frame().await {
                    let frame = frame.map_err(|error| error.to_string())?;
                    if let Some(data) = frame.data_ref() {
                        parser.feed(data).into_iter().for_each(&emit);
                    }
                }
                parser.finish().into_iter().for_each(&emit);
            } else {
                let bytes = body.collect().await.map_err(|error| error.to_string())?.to_bytes();
                if !bytes.is_empty() {
                    emit(String::from_utf8_lossy(&bytes).into_owned());
                }
            }
            // The server ended the response without answering (it restarted, or the session ended mid-call): the
            // client would otherwise wait forever. Not retried: the request may have had effects.
            if id.is_some() && !answered.get() {
                return Err("the server ended the response without a reply".into());
            }
            Ok(())
        }
        .await;
        if let Err(description) = outcome {
            let message = format!("desktop-mcp connect: {}: {description}", self.target.url);
            match id.filter(|_| !answered.get()) {
                // Nobody waits for an answer to a notification, and a request whose reply already went out must not
                // get a second one.
                None => log(&message),
                Some(id) => {
                    let _ = output.send(serde_json::json!({"jsonrpc": "2.0", "id": id, "error": {"code": -32000, "message": message}}).to_string());
                }
            }
        }
        self.follow(output);
    }

    /// Takes the session id a response carried, unless the session has moved on since the request was sent.
    fn remember(&self, id: &str, replacing: Option<&str>) {
        let mut state = self.state.borrow_mut();
        if state.session_id.as_deref() == replacing {
            state.session_id = Some(id.to_string());
            state.needs_session = false;
        }
    }

    /// A new session after the server answered 404 for `stale` (`None`: after a failed replay), or `None` when
    /// there is nothing to replay or the server would not start one. Requests that recover together share one
    /// replay.
    async fn recover(self: &Rc<Self>, stale: Option<&str>, output: &Output) -> Option<String> {
        let seen = self.state.borrow().recoveries;
        let _one_at_a_time = self.recovery.lock().await;
        {
            let state = self.state.borrow();
            if state.session_id.is_some() && state.session_id.as_deref() != stale {
                return state.session_id.clone();
            }
            if state.recoveries != seen {
                return state.session_id.clone();
            }
        }
        let line = self.state.borrow().initialize_line.clone()?;
        {
            let mut state = self.state.borrow_mut();
            state.session_id = None;
            stop_listening(&mut state);
            state.replays += 1;
        }
        let fresh = self.replay(&line).await;
        {
            let mut state = self.state.borrow_mut();
            state.recoveries += 1;
            state.session_id = fresh.clone();
            state.needs_session = fresh.is_none();
        }
        self.follow(output);
        fresh
    }

    /// Sends the remembered `initialize` without a session id and reads its reply to the end without forwarding
    /// it: the new session's id.
    async fn replay(&self, line: &str) -> Option<String> {
        let response = self.send(Method::POST, Some(line), None).await.ok().filter(|response| response.status().is_success())?;
        let id = response.headers().get("mcp-session-id")?.to_str().ok()?.to_string();
        let _ = response.into_body().collect().await;
        Some(id)
    }

    /// Keeps one `GET` listener on the current session: a new one when the session id first appears or changes,
    /// none once the bridge is closing.
    fn follow(self: &Rc<Self>, output: &Output) {
        let mut state = self.state.borrow_mut();
        if state.closed || state.session_id == state.listener_session {
            return;
        }
        stop_listening(&mut state);
        state.listener_session = state.session_id.clone();
        if let Some(session) = state.session_id.clone() {
            let (bridge, output) = (self.clone(), output.clone());
            state.listener = Some(tokio::task::spawn_local(async move { bridge.listen(&session, &output).await }));
        }
    }

    /// The session's `GET` stream: every payload goes to `output`. A stream that ends is opened again a second
    /// later while the session is unchanged; 404 (the session is gone) or 405 (no stream offered) ends listening.
    async fn listen(&self, session: &str, output: &Output) {
        loop {
            if let Ok(response) = self.send(Method::GET, None, Some(session)).await {
                let status = response.status();
                if status == StatusCode::NOT_FOUND || status == StatusCode::METHOD_NOT_ALLOWED {
                    return;
                }
                if status.is_success() {
                    let mut parser = SseParser::default();
                    let mut body = response.into_body();
                    while let Some(Ok(frame)) = body.frame().await {
                        if let Some(data) = frame.data_ref() {
                            for payload in parser.feed(data) {
                                let _ = output.send(single_line(&payload));
                            }
                        }
                    }
                }
            }
            tokio::time::sleep(Duration::from_secs(1)).await;
            let state = self.state.borrow();
            if state.closed || state.session_id.as_deref() != Some(session) {
                return;
            }
        }
    }

    /// Stops the listener; none starts afterwards.
    fn close(&self) {
        let mut state = self.state.borrow_mut();
        state.closed = true;
        stop_listening(&mut state);
    }
}

fn stop_listening(state: &mut State) {
    if let Some(listener) = state.listener.take() {
        listener.abort();
    }
    state.listener_session = None;
}

/// Runs the bridge on `stdio` until its input has ended and every request is answered, or stdout fails.
/// Must run inside the guest runtime's `LocalSet`.
pub async fn run_http_bridge<S, C>(stdio: S, bridge: Rc<HttpBridge<C>>)
where
    S: hyper::rt::Read + hyper::rt::Write + Unpin + 'static,
    C: Connect,
{
    let stdio = Rc::new(RefCell::new(stdio));
    let (output, mut lines_out) = mpsc::unbounded_channel::<String>();
    let (failed, stdout_gone) = tokio::sync::oneshot::channel::<()>();
    let writer = {
        let stdio = stdio.clone();
        tokio::task::spawn_local(async move {
            while let Some(line) = lines_out.recv().await {
                if write_all(&stdio, format!("{line}\n").as_bytes()).await.is_err() {
                    let _ = failed.send(());
                    return;
                }
            }
        })
    };
    let forwards: Rc<RefCell<Vec<JoinHandle<()>>>> = Rc::default();
    let work = {
        let (stdio, bridge, forwards, output) = (stdio.clone(), bridge.clone(), forwards.clone(), output.clone());
        async move {
            let mut pending = Vec::new();
            let mut storage = vec![0u8; 64 * 1024];
            let forward = |line: Vec<u8>| {
                let line = String::from_utf8_lossy(&line).into_owned();
                if line.trim().is_empty() {
                    return;
                }
                let (bridge, output) = (bridge.clone(), output.clone());
                let mut forwards = forwards.borrow_mut();
                forwards.retain(|task| !task.is_finished());
                forwards.push(tokio::task::spawn_local(async move { bridge.forward(&line, &output).await }));
            };
            loop {
                let count = {
                    let mut buffer = hyper::rt::ReadBuf::new(&mut storage);
                    match poll_fn(|cx| hyper::rt::Read::poll_read(Pin::new(&mut *stdio.borrow_mut()), cx, buffer.unfilled())).await {
                        Ok(()) => buffer.filled().len(),
                        Err(_) => 0,
                    }
                };
                if count == 0 {
                    // A last line without a newline still counts.
                    if !pending.is_empty() {
                        forward(std::mem::take(&mut pending));
                    }
                    break;
                }
                pending.extend_from_slice(&storage[..count]);
                while let Some(end) = pending.iter().position(|byte| *byte == b'\n') {
                    let line: Vec<u8> = pending.drain(..=end).collect();
                    forward(line[..line.len() - 1].to_vec());
                }
            }
            bridge.close();
            loop {
                let next = forwards.borrow_mut().pop();
                match next {
                    Some(task) => {
                        let _ = task.await;
                    }
                    None => break,
                }
            }
        }
    };
    tokio::select! {
        _ = work => {}
        _ = stdout_gone => {
            // Nobody reads what the rest would write.
            for task in forwards.borrow_mut().drain(..) {
                task.abort();
            }
        }
    }
    bridge.close();
    drop(output);
    let _ = writer.await;
}

async fn write_all<IO: hyper::rt::Write + Unpin>(io: &Rc<RefCell<IO>>, mut bytes: &[u8]) -> std::io::Result<()> {
    while !bytes.is_empty() {
        let written = poll_fn(|cx| hyper::rt::Write::poll_write(Pin::new(&mut *io.borrow_mut()), cx, bytes)).await?;
        if written == 0 {
            return Err(std::io::ErrorKind::WriteZero.into());
        }
        bytes = &bytes[written..];
    }
    Ok(())
}

/// The first `limit` bytes of a body, as text.
async fn read_head(mut body: Incoming, limit: usize) -> String {
    let mut head = Vec::new();
    while head.len() < limit {
        match body.frame().await {
            Some(Ok(frame)) => {
                if let Some(data) = frame.data_ref() {
                    head.extend_from_slice(data);
                }
            }
            _ => break,
        }
    }
    head.truncate(limit);
    String::from_utf8_lossy(&head).into_owned()
}

/// Whether `text` is the response (result or error) to the request with `id`.
fn is_response(text: &str, id: &Value) -> bool {
    serde_json::from_str::<Value>(text).is_ok_and(|message| message.get("method").is_none() && &message["id"] == id)
}

/// stdio framing is one message per line, so a payload must not contain raw newlines.
fn single_line(payload: &str) -> String {
    if !payload.contains(['\n', '\r']) {
        return payload.to_string();
    }
    match serde_json::from_str::<Value>(payload) {
        Ok(value) => value.to_string(),
        Err(_) => payload.replace('\r', "").replace('\n', " "),
    }
}

/// An incremental reader for the subset of server-sent events MCP uses: a line ends at "\n" (a "\r" before it
/// is dropped), only `data` fields are kept, a blank line ends the event, and an event with empty data (the
/// server's priming event) carries nothing.
#[derive(Default)]
pub struct SseParser {
    line: Vec<u8>,
    data: Option<Vec<String>>,
}

impl SseParser {
    /// The payloads of the events these bytes complete.
    pub fn feed(&mut self, bytes: &[u8]) -> Vec<String> {
        let mut payloads = Vec::new();
        for &byte in bytes {
            if byte != b'\n' {
                self.line.push(byte);
                continue;
            }
            if let Some(payload) = self.end_line() {
                payloads.push(payload);
            }
        }
        payloads
    }

    /// A last event without its blank line.
    pub fn finish(&mut self) -> Option<String> {
        if !self.line.is_empty() {
            let _ = self.end_line();
        }
        self.end_event()
    }

    fn end_line(&mut self) -> Option<String> {
        if self.line.last() == Some(&b'\r') {
            self.line.pop();
        }
        let line = std::mem::take(&mut self.line);
        if line.is_empty() {
            return self.end_event();
        }
        let text = String::from_utf8_lossy(&line);
        let value = text.strip_prefix("data:")?;
        self.data.get_or_insert_with(Vec::new).push(value.strip_prefix(' ').unwrap_or(value).to_string());
        None
    }

    fn end_event(&mut self) -> Option<String> {
        let payload = self.data.take()?.join("\n");
        (!payload.is_empty()).then_some(payload)
    }
}
