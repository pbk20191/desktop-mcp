//! Opt-in MCP sessions: one rmcp service per session, kept between requests.
//!
//! A session's service talks to the HTTP side through [`SessionTransport`]. Answers go to the `POST` that
//! asked (by JSON-RPC id), anything the server starts on its own goes to the session's `GET` stream, held
//! back while no stream is open. Sessions end when the client deletes them, when they sit idle longer than
//! the timeout, when they have sent more than their byte budget, or to make room under the cap.
use crate::guest::{Activity, ActivityGuard};
use crate::mcp::Server;
use bytes::Bytes;
use rmcp::model::{ClientJsonRpcMessage, JsonRpcMessage, ServerJsonRpcMessage};
use rmcp::{Peer, RoleServer, ServiceExt};
use std::cell::{Cell, RefCell};
use std::collections::{HashMap, VecDeque};
use std::future::Future;
use std::pin::Pin;
use std::rc::{Rc, Weak};
use std::sync::{Arc, Mutex, MutexGuard};
use std::task::{Context, Poll, Waker};
use std::time::{Duration, Instant};
use tokio::sync::{mpsc, oneshot};
use tokio::task::JoinHandle;

#[derive(Clone, Debug)]
pub struct SessionSettings {
    /// How long a session may go without a `POST` before it ends; `None` keeps it. A running call counts as
    /// activity, an open `GET` stream does not.
    pub idle_timeout: Option<Duration>,
    /// Bytes a session may send (answers and stream events) before it ends.
    pub byte_budget: usize,
    /// At most this many sessions; the least recently used idle one makes room for a new one.
    pub max_sessions: usize,
}

impl Default for SessionSettings {
    fn default() -> Self {
        SessionSettings { idle_timeout: Some(Duration::from_secs(1800)), byte_budget: 64 * 1024 * 1024, max_sessions: 64 }
    }
}

/// What a session's transport shares with the HTTP side. The transport must be `Send` (rmcp asks for it even
/// with its `local` feature), so this is behind a mutex; it is only ever locked on the main thread.
#[derive(Default)]
struct Shared {
    /// Requests waiting for their answer, by JSON-RPC id (as JSON text).
    pending: HashMap<String, oneshot::Sender<ServerJsonRpcMessage>>,
    /// Server-initiated messages, as SSE events, for the `GET` stream.
    backlog: VecDeque<Bytes>,
    get_waker: Option<Waker>,
    bytes_sent: usize,
    next_event_id: u64,
    ended: bool,
}

impl Shared {
    /// One SSE event: an empty-data event when `data` is `None`, as the Swift SDK sends first on every stream.
    fn event(&mut self, data: Option<&str>) -> Bytes {
        self.next_event_id += 1;
        let id = self.next_event_id;
        let text = match data {
            Some(data) => format!("id: {id}\nevent: message\ndata: {data}\n\n"),
            None => format!("id: {id}\ndata: \n\n"),
        };
        self.bytes_sent += text.len();
        Bytes::from(text)
    }
}

/// A request's key in `pending`: its id as JSON text, the same for the request and its answer.
fn key_of(message: &serde_json::Value) -> String {
    message["id"].to_string()
}

/// The key of the request `item` answers, read from the message itself (no copy of its result); `None` when it
/// answers none. An error with no id gets `null`, as [`key_of`] gives for a missing one.
pub(crate) fn answered(item: &ServerJsonRpcMessage) -> Option<String> {
    let id = match item {
        JsonRpcMessage::Response(response) => serde_json::to_string(&response.id),
        JsonRpcMessage::Error(error) => serde_json::to_string(&error.id),
        _ => return None,
    };
    Some(id.expect("request ids serialize"))
}

struct SessionTransport {
    incoming: mpsc::UnboundedReceiver<ClientJsonRpcMessage>,
    shared: Arc<Mutex<Shared>>,
}

impl rmcp::transport::Transport<RoleServer> for SessionTransport {
    type Error = std::io::Error;

    fn send(&mut self, item: ServerJsonRpcMessage) -> impl Future<Output = Result<(), Self::Error>> + Send + 'static {
        let mut shared = self.shared.lock().expect("the session lock is never poisoned");
        if let Some(key) = answered(&item) {
            // No one waiting: the request's client went away, or cancelled it.
            if let Some(waiting) = shared.pending.remove(&key) {
                let _ = waiting.send(item);
            }
        } else if !shared.ended {
            let event = shared.event(Some(&serde_json::to_string(&item).expect("rmcp messages serialize")));
            shared.backlog.push_back(event);
            if let Some(waker) = shared.get_waker.take() {
                waker.wake();
            }
        }
        std::future::ready(Ok(()))
    }

    fn receive(&mut self) -> impl Future<Output = Option<ClientJsonRpcMessage>> + Send {
        self.incoming.recv()
    }

    async fn close(&mut self) -> Result<(), Self::Error> {
        Ok(())
    }
}

pub(crate) struct Session {
    pub(crate) id: String,
    shared: Arc<Mutex<Shared>>,
    to_server: RefCell<Option<mpsc::UnboundedSender<ClientJsonRpcMessage>>>,
    service: RefCell<Option<JoinHandle<()>>>,
    peer: Rc<RefCell<Option<Peer<RoleServer>>>>,
    budget: usize,
    last_request_at: Cell<Instant>,
    in_flight: Cell<usize>,
    get_open: Cell<bool>,
    get_generation: Cell<u64>,
    /// Keeps the guest runtime turning (slowly) while the session lives: its service may own timers.
    alive: RefCell<Option<ActivityGuard>>,
}

impl Session {
    fn shared(&self) -> MutexGuard<'_, Shared> {
        self.shared.lock().expect("the session lock is never poisoned")
    }

    fn over_budget(&self) -> bool {
        self.shared().bytes_sent > self.budget
    }

    /// A `POST` arrived: the session is active.
    pub(crate) fn touch(&self) {
        self.last_request_at.set(Instant::now());
    }

    /// Hands a message that expects no answer to the service. A client's `notifications/cancelled` also ends
    /// the cancelled request's stream: rmcp sends no answer for it.
    pub(crate) fn notify(&self, message: ClientJsonRpcMessage, value: &serde_json::Value) {
        if value["method"] == "notifications/cancelled" {
            let cancelled = value["params"]["requestId"].to_string();
            self.shared().pending.remove(&cancelled);
        }
        if let Some(to_server) = self.to_server.borrow().as_ref() {
            let _ = to_server.send(message);
        }
    }

    fn end(&self) {
        {
            let mut shared = self.shared();
            shared.ended = true;
            shared.pending.clear(); // the calls' streams end
            if let Some(waker) = shared.get_waker.take() {
                waker.wake(); // and so does the GET stream
            }
        }
        self.to_server.borrow_mut().take();
        // Dropping the running service cancels its token, and with it every call still running.
        if let Some(service) = self.service.borrow_mut().take() {
            service.abort();
        }
        self.alive.borrow_mut().take();
    }
}

pub struct Sessions {
    settings: SessionSettings,
    sessions: RefCell<HashMap<String, Rc<Session>>>,
    sweeper: RefCell<Option<JoinHandle<()>>>,
    me: Weak<Sessions>,
}

impl Sessions {
    pub fn new(settings: SessionSettings) -> Rc<Sessions> {
        Rc::new_cyclic(|me| Sessions { settings, sessions: RefCell::default(), sweeper: RefCell::default(), me: me.clone() })
    }

    pub fn count(&self) -> usize {
        self.sessions.borrow().len()
    }

    /// The server's side of a running session, for messages the server starts.
    pub fn peer(&self, id: &str) -> Option<Peer<RoleServer>> {
        self.sessions.borrow().get(id)?.peer.borrow().clone()
    }

    /// Starts a session for `server`; `None` when the cap is reached and every session has a call in flight.
    pub(crate) fn create(&self, server: Server, activity: &Rc<Activity>) -> Option<Rc<Session>> {
        while self.count() >= self.settings.max_sessions {
            let oldest = self.sessions.borrow().values().filter(|s| s.in_flight.get() == 0).min_by_key(|s| s.last_request_at.get()).map(|s| s.id.clone());
            self.end(&oldest?);
        }
        let (to_server, incoming) = mpsc::unbounded_channel();
        let shared = Arc::new(Mutex::new(Shared::default()));
        let session = Rc::new(Session {
            id: uuid::Uuid::new_v4().to_string(),
            shared: shared.clone(),
            to_server: RefCell::new(Some(to_server)),
            service: RefCell::default(),
            peer: Rc::default(),
            budget: self.settings.byte_budget,
            last_request_at: Cell::new(Instant::now()),
            in_flight: Cell::new(0),
            get_open: Cell::new(false),
            get_generation: Cell::new(0),
            alive: RefCell::new(Some(activity.connection())),
        });
        let peer = session.peer.clone();
        let service = tokio::task::spawn_local(async move {
            if let Ok(running) = server.serve(SessionTransport { incoming, shared }).await {
                *peer.borrow_mut() = Some(running.peer().clone());
                let _ = running.waiting().await;
            }
        });
        *session.service.borrow_mut() = Some(service);
        self.sessions.borrow_mut().insert(session.id.clone(), session.clone());
        self.start_sweeper();
        Some(session)
    }

    /// A live session that may take requests: an unknown, ended or over-budget one is `None` (404 for the
    /// client, which then initialises again).
    pub(crate) fn get(&self, id: &str) -> Option<Rc<Session>> {
        self.sessions.borrow().get(id).filter(|session| !session.over_budget()).cloned()
    }

    /// Ends a session as `DELETE` would: its calls are cancelled and its streams end.
    pub fn end(&self, id: &str) {
        let session = self.sessions.borrow_mut().remove(id);
        if let Some(session) = session {
            session.end();
        }
    }

    /// Ends the sessions with nothing in flight that have been idle too long or have sent too much.
    fn sweep(&self) {
        let now = Instant::now();
        let ending: Vec<String> = self
            .sessions
            .borrow()
            .values()
            .filter(|s| s.in_flight.get() == 0)
            .filter(|s| self.settings.idle_timeout.is_some_and(|timeout| now.duration_since(s.last_request_at.get()) > timeout) || s.over_budget())
            .map(|s| s.id.clone())
            .collect();
        for id in ending {
            self.end(&id);
        }
    }

    /// One sweeper while any session lives; it stops with the last one, so an idle server has no timer.
    fn start_sweeper(&self) {
        if self.sweeper.borrow().as_ref().is_some_and(|sweeper| !sweeper.is_finished()) {
            return;
        }
        let interval = self.settings.idle_timeout.map_or(Duration::from_secs(60), |timeout| timeout.min(Duration::from_secs(60)));
        let me = self.me.clone();
        *self.sweeper.borrow_mut() = Some(tokio::task::spawn_local(async move {
            loop {
                tokio::time::sleep(interval).await;
                let Some(sessions) = me.upgrade() else { return };
                sessions.sweep();
                if sessions.count() == 0 {
                    return;
                }
            }
        }));
    }

    /// Sends `message` (a request) to the session and returns the body of its SSE answer.
    pub(crate) fn call(&self, session: &Rc<Session>, message: ClientJsonRpcMessage, value: &serde_json::Value, activity: &Rc<Activity>) -> CallBody {
        let key = key_of(value);
        let (tx, answer) = oneshot::channel();
        session.shared().pending.insert(key.clone(), tx);
        session.in_flight.set(session.in_flight.get() + 1);
        session.touch();
        if let Some(to_server) = session.to_server.borrow().as_ref() {
            let _ = to_server.send(message);
        }
        CallBody {
            session: session.clone(),
            sessions: self.me.upgrade().expect("the store outlives its requests"),
            key,
            request_id: value["id"].clone(),
            answer,
            primed: false,
            finished: false,
            _in_flight: activity.request(),
        }
    }

    /// The `GET` stream, or `None` when one is already open.
    pub(crate) fn open_stream(&self, session: &Rc<Session>) -> Option<GetBody> {
        if session.get_open.replace(true) {
            return None;
        }
        let generation = session.get_generation.get() + 1;
        session.get_generation.set(generation);
        Some(GetBody { session: session.clone(), generation, primed: false })
    }
}

/// The body of a session `POST` that carries a request: an empty-data event at once, then the answer.
/// Dropped before the answer (the client went away), it cancels the request; the session lives on.
pub(crate) struct CallBody {
    session: Rc<Session>,
    sessions: Rc<Sessions>,
    key: String,
    request_id: serde_json::Value,
    answer: oneshot::Receiver<ServerJsonRpcMessage>,
    primed: bool,
    finished: bool,
    _in_flight: ActivityGuard,
}

impl CallBody {
    /// The request is over: answered, abandoned (`cancel`), or ended with its session.
    fn finish(&mut self, cancel: bool) {
        self.finished = true;
        if cancel && self.session.shared().pending.remove(&self.key).is_some() {
            let cancelled = serde_json::json!({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": self.request_id, "reason": "The client closed the request."}});
            if let (Ok(message), Some(to_server)) = (serde_json::from_value(cancelled), self.session.to_server.borrow().as_ref()) {
                let _ = to_server.send(message);
            }
        }
        let session = &self.session;
        session.in_flight.set(session.in_flight.get() - 1);
        session.touch();
        // A session over its budget ends between requests, never in the middle of another one's answer.
        if session.in_flight.get() == 0 && session.over_budget() {
            self.sessions.end(&session.id);
        }
    }
}

impl http_body::Body for CallBody {
    type Data = Bytes;
    type Error = std::convert::Infallible;

    fn poll_frame(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Result<http_body::Frame<Bytes>, Self::Error>>> {
        if self.finished {
            return Poll::Ready(None);
        }
        if !self.primed {
            self.primed = true;
            let event = self.session.shared().event(None);
            return Poll::Ready(Some(Ok(http_body::Frame::data(event))));
        }
        match Pin::new(&mut self.answer).poll(cx) {
            Poll::Ready(Ok(answer)) => {
                let event = self.session.shared().event(Some(&serde_json::to_string(&answer).expect("rmcp messages serialize")));
                self.finish(false);
                Poll::Ready(Some(Ok(http_body::Frame::data(event))))
            }
            // The session ended, or the client cancelled the request: the stream ends without an answer.
            Poll::Ready(Err(_)) => {
                self.finish(false);
                Poll::Ready(None)
            }
            Poll::Pending => Poll::Pending,
        }
    }
}

impl Drop for CallBody {
    fn drop(&mut self) {
        if !self.finished {
            self.finish(true);
        }
    }
}

/// The session's `GET` stream: an empty-data event, then every server-initiated message. Ends with the session.
pub(crate) struct GetBody {
    session: Rc<Session>,
    generation: u64,
    primed: bool,
}

impl http_body::Body for GetBody {
    type Data = Bytes;
    type Error = std::convert::Infallible;

    fn poll_frame(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Result<http_body::Frame<Bytes>, Self::Error>>> {
        let session = self.session.clone();
        let mut shared = session.shared();
        if shared.ended {
            return Poll::Ready(None);
        }
        let event = if !self.primed {
            self.primed = true;
            Some(shared.event(None))
        } else {
            shared.backlog.pop_front()
        };
        match event {
            Some(event) => Poll::Ready(Some(Ok(http_body::Frame::data(event)))),
            None => {
                shared.get_waker = Some(cx.waker().clone());
                Poll::Pending
            }
        }
    }
}

impl Drop for GetBody {
    fn drop(&mut self) {
        // The reader went away; a newer stream may already have replaced this one.
        if self.session.get_generation.get() == self.generation {
            self.session.get_open.set(false);
        }
    }
}
