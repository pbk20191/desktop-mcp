//! The HTTP app: Streamable HTTP in its stateless form, on hyper.
//!
//! `POST /mcp` carries one JSON-RPC message and is answered with `application/json` by a server made for
//! that request alone, so ids from different clients never meet and nothing outlives the response.
use crate::guest::Activity;
use crate::io::Listener;
use crate::mcp::{answer_stateless, Server};
use bytes::Bytes;
use http::header::{ACCEPT, ALLOW, CONTENT_TYPE};
use http::{HeaderMap, Method, Request, Response, StatusCode};
use http_body_util::{BodyExt, Full};
use rmcp::model::ClientJsonRpcMessage;
use std::rc::Rc;

pub struct App {
    /// A fresh server for one request.
    pub make_server: Box<dyn Fn() -> Server>,
    pub activity: Rc<Activity>,
}

const PARSE_ERROR: i64 = -32700;
const INVALID_REQUEST: i64 = -32600;

fn status_only(status: StatusCode) -> Response<Full<Bytes>> {
    Response::builder().status(status).body(Full::default()).expect("a status is a valid response")
}

fn json(status: StatusCode, body: String) -> Response<Full<Bytes>> {
    Response::builder().status(status).header(CONTENT_TYPE, "application/json").body(Full::new(Bytes::from(body))).expect("static headers are valid")
}

fn error(status: StatusCode, code: i64, message: &str) -> Response<Full<Bytes>> {
    json(status, serde_json::json!({"jsonrpc": "2.0", "id": null, "error": {"code": code, "message": message}}).to_string())
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

/// Answers one request.
pub async fn handle<B>(app: &App, request: Request<B>) -> Response<Full<Bytes>>
where
    B: http_body::Body,
    B::Error: std::fmt::Debug,
{
    if !matches!(request.uri().path(), "/mcp" | "/mcp/") {
        return status_only(StatusCode::NOT_FOUND);
    }
    if request.method() != Method::POST {
        // Without sessions there is no stream to open and nothing to delete.
        let mut response = error(StatusCode::METHOD_NOT_ALLOWED, INVALID_REQUEST, "Method Not Allowed");
        response.headers_mut().insert(ALLOW, http::HeaderValue::from_static("POST"));
        return response;
    }
    // The header has to name the type, as in the Swift port: a wildcard is refused.
    if !media_types(request.headers(), ACCEPT).any(|accepted| accepted == "application/json") {
        return error(StatusCode::NOT_ACCEPTABLE, INVALID_REQUEST, "Not Acceptable: Client must accept application/json");
    }
    if !media_types(request.headers(), CONTENT_TYPE).any(|sent| sent == "application/json") {
        return error(StatusCode::UNSUPPORTED_MEDIA_TYPE, INVALID_REQUEST, "Unsupported Media Type: Content-Type must be application/json");
    }
    let body = match request.into_body().collect().await {
        Ok(collected) => collected.to_bytes(),
        Err(_) => return error(StatusCode::BAD_REQUEST, PARSE_ERROR, "Parse error: the request body could not be read"),
    };
    let value: serde_json::Value = match serde_json::from_slice(&body) {
        Ok(value) => value,
        Err(_) => return error(StatusCode::BAD_REQUEST, PARSE_ERROR, "Parse error"),
    };
    if value.is_array() {
        return error(StatusCode::BAD_REQUEST, INVALID_REQUEST, "Invalid Request: JSON-RPC batches are not supported");
    }
    let message: ClientJsonRpcMessage = match serde_json::from_value(value) {
        Ok(message) => message,
        Err(_) => return error(StatusCode::BAD_REQUEST, INVALID_REQUEST, "Invalid Request"),
    };
    let _in_flight = app.activity.request();
    match answer_stateless((app.make_server)(), message).await {
        Some(answer) => json(StatusCode::OK, serde_json::to_string(&answer).expect("rmcp messages serialize")),
        None => status_only(StatusCode::ACCEPTED),
    }
}

/// Serves connections until the listener ends: one task per connection, HTTP/1.1 with keep-alive.
/// Must run inside the guest runtime's `LocalSet`.
pub async fn serve<L: Listener>(mut listener: L, app: Rc<App>) {
    while let Some(io) = listener.accept().await {
        let app = app.clone();
        tokio::task::spawn_local(async move {
            let _connection = app.activity.connection();
            let service = hyper::service::service_fn(|request| {
                let app = app.clone();
                async move { Ok::<_, std::convert::Infallible>(handle(&app, request).await) }
            });
            // A connection that fails (the peer went away mid-message) only ends itself.
            let _ = hyper::server::conn::http1::Builder::new().serve_connection(io, service).await;
        });
    }
}
