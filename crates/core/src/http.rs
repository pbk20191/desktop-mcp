//! The HTTP app: Streamable HTTP on hyper, as the Swift port serves it (macos/README.md, "Endpoints").
//!
//! `POST /mcp` is stateless by default: one JSON-RPC message, answered with `application/json` by a server
//! made for that request alone, so ids from different clients never meet and nothing outlives the response.
//! Sessions are opt-in: an `initialize` whose `Accept` includes `text/event-stream` gets an `Mcp-Session-Id`,
//! and requests carrying it go to that session's server and are answered as server-sent events.
use crate::guest::Activity;
use crate::io::Listener;
use crate::mcp::{answer_stateless, Server};
use crate::oauth::{self, OAuth};
use crate::security::{constant_time_eq, is_loopback_host, IpNetwork};
use crate::session::{Session, Sessions};
use bytes::Bytes;
use http::header::{ACCEPT, ALLOW, AUTHORIZATION, CACHE_CONTROL, CONTENT_TYPE, HOST, LOCATION, ORIGIN, VARY, WWW_AUTHENTICATE};
use http::{HeaderMap, HeaderValue, Method, Request, Response, StatusCode};
use http_body_util::{BodyExt, Full};
use rmcp::model::{ClientJsonRpcMessage, JsonRpcMessage};
use std::convert::Infallible;
use std::net::IpAddr;
use std::pin::Pin;
use std::rc::Rc;

/// Every response's body: whole, or a stream of server-sent events. Not `Send`: it lives on the main thread.
pub type Body = Pin<Box<dyn http_body::Body<Data = Bytes, Error = Infallible>>>;

pub struct App {
    /// A fresh server, for one stateless request or one session.
    pub make_server: Box<dyn Fn() -> Server>,
    pub activity: Rc<Activity>,
    pub sessions: Rc<Sessions>,
    pub security: Security,
}

/// Who may call: checked before any route, in the original's order (Origin, `OPTIONS`, allowlist, auth).
#[derive(Default)]
pub struct Security {
    /// `Authorization: Bearer <key>` on every request but the public paths.
    pub auth_key: Option<String>,
    /// When not empty, only these client addresses (`/health` excepted).
    pub ip_allowlist: Vec<IpNetwork>,
    /// The built-in authorization server; its access tokens are accepted like the key.
    pub oauth: Option<Rc<OAuth>>,
}

/// The client's IP address, as the transport saw it. The connection task puts it in each request's extensions.
#[derive(Clone, Copy, Debug)]
pub struct PeerAddress(pub Option<IpAddr>);

/// Paths that need no authentication.
const PUBLIC_PATHS: [&str; 5] = ["/health", "/.well-known/oauth-authorization-server", "/oauth/register", "/oauth/authorize", "/oauth/token"];

const SESSION_ID: &str = "mcp-session-id";
const PARSE_ERROR: i64 = -32700;
const INVALID_REQUEST: i64 = -32600;
const INTERNAL_ERROR: i64 = -32603;

fn whole(status: StatusCode, content_type: Option<&'static str>, body: Bytes) -> Response<Body> {
    let mut response = Response::builder().status(status);
    if let Some(content_type) = content_type {
        response = response.header(CONTENT_TYPE, content_type);
    }
    response.body(Box::pin(Full::new(body)) as Body).expect("static headers are valid")
}

fn status_only(status: StatusCode) -> Response<Body> {
    whole(status, None, Bytes::new())
}

fn json(status: StatusCode, body: String) -> Response<Body> {
    whole(status, Some("application/json"), Bytes::from(body))
}

fn error(status: StatusCode, code: i64, message: &str) -> Response<Body> {
    json(status, serde_json::json!({"jsonrpc": "2.0", "id": null, "error": {"code": code, "message": message}}).to_string())
}

/// `{"error": "…"}`, the body of every refusal.
fn refusal(status: StatusCode, message: &str) -> Response<Body> {
    json(status, serde_json::json!({ "error": message }).to_string())
}

fn oauth_reply(reply: oauth::Reply) -> Response<Body> {
    let status = StatusCode::from_u16(reply.status).unwrap_or(StatusCode::INTERNAL_SERVER_ERROR);
    let mut response = match reply.body {
        Some(body) => json(status, body.to_string()),
        None => status_only(status),
    };
    if let Some(location) = reply.location.and_then(|location| HeaderValue::from_str(&location).ok()) {
        response.headers_mut().insert(LOCATION, location);
    }
    response
}

/// Loopback `http(s)` origins on any port.
fn is_allowed_origin(origin: &str) -> bool {
    let Some((scheme, rest)) = origin.split_once("://") else { return false };
    if !matches!(scheme.to_ascii_lowercase().as_str(), "http" | "https") {
        return false;
    }
    let authority = rest.split('/').next().unwrap_or_default();
    let host = match authority.strip_prefix('[') {
        Some(bracketed) => bracketed.split(']').next().unwrap_or_default(),
        None => authority.split(':').next().unwrap_or_default(),
    };
    !host.is_empty() && is_loopback_host(host)
}

fn with_session(mut response: Response<Body>, id: &str) -> Response<Body> {
    if let Ok(value) = HeaderValue::from_str(id) {
        response.headers_mut().insert(SESSION_ID, value);
    }
    response
}

fn event_stream(body: impl http_body::Body<Data = Bytes, Error = Infallible> + 'static, session: &str) -> Response<Body> {
    let response = Response::builder()
        .status(StatusCode::OK)
        .header(CONTENT_TYPE, "text/event-stream")
        .header(CACHE_CONTROL, "no-cache, no-transform")
        .body(Box::pin(body) as Body)
        .expect("static headers are valid");
    with_session(response, session)
}

/// The media types of a header, lowercased and without parameters.
fn media_types<'a>(headers: &'a HeaderMap, name: http::header::HeaderName) -> impl Iterator<Item = String> + 'a {
    headers
        .get_all(name)
        .into_iter()
        .filter_map(|value| value.to_str().ok())
        .flat_map(|value| value.split(','))
        .map(|item| item.split(';').next().unwrap_or_default().trim().to_ascii_lowercase())
}

/// Whether the header names `media_type` itself; a wildcard does not count, as in the Swift port.
fn names(headers: &HeaderMap, name: http::header::HeaderName, media_type: &str) -> bool {
    media_types(headers, name).any(|named| named == media_type)
}

/// A JSON-RPC message from a request body, or the error response for it.
fn parse(body: &[u8]) -> Result<(ClientJsonRpcMessage, serde_json::Value), Response<Body>> {
    let value: serde_json::Value = serde_json::from_slice(body).map_err(|_| error(StatusCode::BAD_REQUEST, PARSE_ERROR, "Parse error"))?;
    if value.is_array() {
        return Err(error(StatusCode::BAD_REQUEST, INVALID_REQUEST, "Invalid Request: JSON-RPC batches are not supported"));
    }
    let message = serde_json::from_value(value.clone()).map_err(|_| error(StatusCode::BAD_REQUEST, INVALID_REQUEST, "Invalid Request"))?;
    Ok((message, value))
}

fn is_initialize(value: &serde_json::Value) -> bool {
    value["method"] == "initialize" && !value["id"].is_null()
}

/// Answers one request.
pub async fn handle<B>(app: &App, request: Request<B>) -> Response<Body>
where
    B: http_body::Body,
    B::Error: std::fmt::Debug,
{
    // A browser names the page that made the request. Only pages served from this machine may talk to the
    // server; otherwise any website could drive an unauthenticated loopback server through the visitor's
    // browser (the MCP specification requires this check).
    let origin = request.headers().get(ORIGIN).cloned();
    if let Some(origin) = &origin {
        let text = origin.to_str().unwrap_or_default();
        if !is_allowed_origin(text) {
            return refusal(StatusCode::FORBIDDEN, &format!("Forbidden: origin '{text}' is not allowed"));
        }
    }
    if request.method() == Method::OPTIONS {
        let mut response = status_only(StatusCode::OK);
        let headers = response.headers_mut();
        headers.insert("access-control-allow-origin", origin.clone().unwrap_or(HeaderValue::from_static("*")));
        headers.insert("access-control-allow-methods", HeaderValue::from_static("*"));
        headers.insert("access-control-allow-headers", HeaderValue::from_static("*"));
        headers.insert(VARY, HeaderValue::from_static("origin"));
        return response;
    }
    let mut response = guarded(app, request).await;
    if let Some(origin) = origin {
        response.headers_mut().insert("access-control-allow-origin", origin);
        response.headers_mut().insert(VARY, HeaderValue::from_static("origin"));
    }
    response
}

async fn guarded<B>(app: &App, request: Request<B>) -> Response<Body>
where
    B: http_body::Body,
    B::Error: std::fmt::Debug,
{
    let path = request.uri().path().to_string();
    let security = &app.security;
    if !security.ip_allowlist.is_empty() && path != "/health" {
        let Some(address) = request.extensions().get::<PeerAddress>().and_then(|peer| peer.0) else {
            return refusal(StatusCode::FORBIDDEN, "Forbidden: missing client address");
        };
        if !security.ip_allowlist.iter().any(|network| network.contains(address)) {
            return refusal(StatusCode::FORBIDDEN, &format!("Forbidden: {address} is not in the IP allowlist"));
        }
    }
    if (security.auth_key.is_some() || security.oauth.is_some()) && !PUBLIC_PATHS.contains(&path.as_str()) {
        let header = request.headers().get(AUTHORIZATION).and_then(|value| value.to_str().ok()).unwrap_or_default();
        let token = header.strip_prefix("Bearer ");
        let valid = |token: &str| security.auth_key.as_deref().is_some_and(|key| constant_time_eq(token, key)) || security.oauth.as_ref().is_some_and(|oauth| oauth.validate(token));
        match token {
            Some(token) if valid(token) => {}
            Some(_) if security.auth_key.is_some() => return refusal(StatusCode::UNAUTHORIZED, "Invalid authentication token").tap_bearer(),
            None if security.auth_key.is_some() => return refusal(StatusCode::UNAUTHORIZED, "Missing or invalid Authorization header. Expected: Bearer <token>").tap_bearer(),
            _ => return refusal(StatusCode::UNAUTHORIZED, "Unauthorized"),
        }
    }
    if let Some(oauth) = &security.oauth {
        let method = request.method().clone();
        match (method.as_str(), path.as_str()) {
            ("GET", "/.well-known/oauth-authorization-server") => {
                let host = request.headers().get(HOST).and_then(|value| value.to_str().ok());
                return json(StatusCode::OK, oauth.metadata(host).to_string());
            }
            ("POST", "/oauth/register") => return oauth_reply(oauth.register()),
            ("GET", "/oauth/authorize") => return oauth_reply(oauth.authorize(request.uri().query().unwrap_or_default())),
            ("POST", "/oauth/token") => {
                let content_type = request.headers().get(CONTENT_TYPE).and_then(|value| value.to_str().ok()).unwrap_or_default().to_string();
                let body = request.into_body().collect().await.map(|collected| collected.to_bytes()).unwrap_or_default();
                return oauth_reply(oauth.token(&content_type, &body));
            }
            _ => {}
        }
    }
    route(app, request).await
}

trait BearerChallenge {
    fn tap_bearer(self) -> Self;
}

impl BearerChallenge for Response<Body> {
    /// Adds `WWW-Authenticate: Bearer`, which tells a client to authenticate with a bearer token.
    fn tap_bearer(mut self) -> Self {
        self.headers_mut().insert(WWW_AUTHENTICATE, HeaderValue::from_static("Bearer"));
        self
    }
}

async fn route<B>(app: &App, request: Request<B>) -> Response<Body>
where
    B: http_body::Body,
    B::Error: std::fmt::Debug,
{
    if !matches!(request.uri().path(), "/mcp" | "/mcp/") {
        return status_only(StatusCode::NOT_FOUND);
    }
    let (head, body) = request.into_parts();
    if let Some(id) = head.headers.get(SESSION_ID).and_then(|value| value.to_str().ok()) {
        let Some(session) = app.sessions.get(id) else {
            // What the Swift SDK answers for a session it has ended; clients initialise again on 404.
            return error(StatusCode::NOT_FOUND, INVALID_REQUEST, "Not Found: Session not found or expired");
        };
        return in_session(app, &session, &head.method, &head.headers, body).await;
    }
    if head.method != Method::POST {
        // Without a session there is no stream to open and nothing to delete.
        let mut response = error(StatusCode::METHOD_NOT_ALLOWED, INVALID_REQUEST, "Method Not Allowed");
        response.headers_mut().insert(ALLOW, HeaderValue::from_static("POST"));
        return response;
    }
    let accepts_events = names(&head.headers, ACCEPT, "text/event-stream");
    let Ok(body) = body.collect().await.map(|collected| collected.to_bytes()) else {
        return error(StatusCode::BAD_REQUEST, PARSE_ERROR, "Parse error: the request body could not be read");
    };
    // Sessions are opt-in: only an `initialize` that accepts server-sent events opens one.
    if accepts_events {
        if let Ok(value) = serde_json::from_slice::<serde_json::Value>(&body) {
            if is_initialize(&value) {
                return open_session(app, &head.headers, &body).await;
            }
        }
    }
    stateless(app, &head.headers, &body).await
}

async fn stateless(app: &App, headers: &HeaderMap, body: &[u8]) -> Response<Body> {
    if !names(headers, ACCEPT, "application/json") {
        return error(StatusCode::NOT_ACCEPTABLE, INVALID_REQUEST, "Not Acceptable: Client must accept application/json");
    }
    if !names(headers, CONTENT_TYPE, "application/json") {
        return error(StatusCode::UNSUPPORTED_MEDIA_TYPE, INVALID_REQUEST, "Unsupported Media Type: Content-Type must be application/json");
    }
    let (message, _) = match parse(body) {
        Ok(parsed) => parsed,
        Err(response) => return response,
    };
    let _in_flight = app.activity.request();
    match answer_stateless((app.make_server)(), message).await {
        Some(answer) => json(StatusCode::OK, serde_json::to_string(&answer).expect("rmcp messages serialize")),
        None => status_only(StatusCode::ACCEPTED),
    }
}

/// What a session `POST` must carry, as the Swift SDK checks it.
fn check_session_post(headers: &HeaderMap) -> Result<(), Response<Body>> {
    if !(names(headers, ACCEPT, "application/json") && names(headers, ACCEPT, "text/event-stream")) {
        return Err(error(StatusCode::NOT_ACCEPTABLE, INVALID_REQUEST, "Not Acceptable: Client must accept both application/json and text/event-stream"));
    }
    if !names(headers, CONTENT_TYPE, "application/json") {
        return Err(error(StatusCode::UNSUPPORTED_MEDIA_TYPE, INVALID_REQUEST, "Unsupported Media Type: Content-Type must be application/json"));
    }
    Ok(())
}

async fn open_session(app: &App, headers: &HeaderMap, body: &[u8]) -> Response<Body> {
    if let Err(response) = check_session_post(headers) {
        return response;
    }
    let (message, value) = match parse(body) {
        Ok(parsed) => parsed,
        Err(response) => return response,
    };
    let Some(session) = app.sessions.create((app.make_server)(), &app.activity) else {
        return error(StatusCode::SERVICE_UNAVAILABLE, INTERNAL_ERROR, "Too many sessions");
    };
    event_stream(app.sessions.call(&session, message, &value, &app.activity), &session.id)
}

async fn in_session<B>(app: &App, session: &Rc<Session>, method: &Method, headers: &HeaderMap, body: B) -> Response<Body>
where
    B: http_body::Body,
    B::Error: std::fmt::Debug,
{
    match *method {
        Method::POST => {
            if let Err(response) = check_session_post(headers) {
                return with_session(response, &session.id);
            }
            let Ok(body) = body.collect().await.map(|collected| collected.to_bytes()) else {
                return error(StatusCode::BAD_REQUEST, PARSE_ERROR, "Parse error: the request body could not be read");
            };
            let (message, value) = match parse(&body) {
                Ok(parsed) => parsed,
                Err(response) => return with_session(response, &session.id),
            };
            if is_initialize(&value) {
                return with_session(error(StatusCode::BAD_REQUEST, INVALID_REQUEST, "Invalid Request: Server already initialized"), &session.id);
            }
            if !matches!(message, JsonRpcMessage::Request(_)) {
                session.touch();
                session.notify(message, &value);
                return with_session(status_only(StatusCode::ACCEPTED), &session.id);
            }
            event_stream(app.sessions.call(session, message, &value, &app.activity), &session.id)
        }
        Method::GET => {
            if !names(headers, ACCEPT, "text/event-stream") {
                return with_session(error(StatusCode::NOT_ACCEPTABLE, INVALID_REQUEST, "Not Acceptable: Client must accept text/event-stream"), &session.id);
            }
            match app.sessions.open_stream(session) {
                Some(stream) => event_stream(stream, &session.id),
                None => with_session(error(StatusCode::CONFLICT, INVALID_REQUEST, "Conflict: Only one SSE stream is allowed per session"), &session.id),
            }
        }
        Method::DELETE => {
            app.sessions.end(&session.id);
            with_session(status_only(StatusCode::OK), &session.id)
        }
        _ => {
            let mut response = with_session(error(StatusCode::METHOD_NOT_ALLOWED, INVALID_REQUEST, "Method Not Allowed"), &session.id);
            response.headers_mut().insert(ALLOW, HeaderValue::from_static("GET, POST, DELETE"));
            response
        }
    }
}

/// Serves connections until the listener ends: one task per connection, HTTP/1.1 with keep-alive.
/// Must run inside the guest runtime's `LocalSet`.
pub async fn serve<L: Listener>(mut listener: L, app: Rc<App>) {
    while let Some(io) = listener.accept().await {
        let app = app.clone();
        tokio::task::spawn_local(async move {
            let _connection = app.activity.connection();
            let peer = PeerAddress(crate::io::Peer::peer_address(&io));
            let service = hyper::service::service_fn(|mut request: Request<hyper::body::Incoming>| {
                let app = app.clone();
                request.extensions_mut().insert(peer);
                async move { Ok::<_, Infallible>(handle(&app, request).await) }
            });
            // A connection that fails (the peer went away mid-message) only ends itself.
            let _ = hyper::server::conn::http1::Builder::new().serve_connection(io, service).await;
        });
    }
}
