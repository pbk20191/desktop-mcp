//! Opt-in sessions, as a client sees them. Every test holds for both ports.
use desktop_mcp_parity::{sse_events, sse_message, ServerUnderTest};
use serde_json::{json, Value};
use std::time::{Duration, Instant};

const BOTH: &[(&str, &str)] = &[("Accept", "application/json, text/event-stream"), ("Content-Type", "application/json")];

fn headers_with<'a>(base: &[(&'a str, &'a str)], session: &'a str) -> Vec<(&'a str, &'a str)> {
    let mut headers = base.to_vec();
    headers.push(("Mcp-Session-Id", session));
    headers
}

fn initialize() -> Value {
    json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"parity","version":"0"}}})
}

/// `initialize` with SSE accepted, then `notifications/initialized`; returns the session id.
fn open(server: &ServerUnderTest) -> String {
    let reply = server.request("POST", "/mcp", BOTH, initialize().to_string().as_bytes());
    assert_eq!(reply.status, 200);
    let id = reply.header("mcp-session-id").expect("a session id").to_string();
    let initialized = server.request("POST", "/mcp", &headers_with(BOTH, &id), br#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#);
    assert_eq!(initialized.status, 202);
    id
}

fn post_in(server: &ServerUnderTest, session: &str, message: &Value) -> desktop_mcp_parity::Reply {
    server.request("POST", "/mcp", &headers_with(BOTH, session), message.to_string().as_bytes())
}

#[test]
fn initialize_accepting_sse_opens_a_session() {
    let server = ServerUnderTest::start();
    let reply = server.request("POST", "/mcp", BOTH, initialize().to_string().as_bytes());
    assert_eq!(reply.status, 200);
    assert!(reply.header("content-type").unwrap().starts_with("text/event-stream"));
    assert!(reply.header("mcp-session-id").is_some_and(|id| !id.is_empty()));
    let events = sse_events(&reply.body);
    assert_eq!(events.first().map(|(_, data)| data.as_str()), Some(""), "an empty-data event comes first: {events:?}");
    assert_eq!(sse_message(&reply)["result"]["serverInfo"]["name"], "desktop-mcp");
}

#[test]
fn session_calls_are_answered_as_sse() {
    let server = ServerUnderTest::start();
    let id = open(&server);
    let listed = post_in(&server, &id, &json!({"jsonrpc":"2.0","id":2,"method":"tools/list"}));
    assert!(listed.header("content-type").unwrap().starts_with("text/event-stream"));
    assert!(sse_message(&listed)["result"]["tools"].as_array().unwrap().iter().any(|tool| tool["name"] == "Wait"));
    let waited = post_in(&server, &id, &json!({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":1}}}));
    assert_eq!(sse_message(&waited)["result"]["content"][0]["text"], "Waited for 1 seconds.");
}

#[test]
fn session_posts_must_accept_sse() {
    let server = ServerUnderTest::start();
    let id = open(&server);
    let reply = server.request("POST", "/mcp", &headers_with(&[("Accept", "application/json"), ("Content-Type", "application/json")], &id), br#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#);
    assert_eq!(reply.status, 406);
}

#[test]
fn an_unknown_session_is_404() {
    let server = ServerUnderTest::start();
    for method in ["POST", "GET", "DELETE"] {
        let accept = if method == "GET" { "text/event-stream" } else { "application/json, text/event-stream" };
        let reply = server.request(method, "/mcp", &[("Accept", accept), ("Content-Type", "application/json"), ("Mcp-Session-Id", "no-such-session")], br#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#);
        assert_eq!(reply.status, 404, "{method}");
    }
}

#[test]
fn one_get_stream_per_session() {
    let server = ServerUnderTest::start();
    let id = open(&server);
    let get = headers_with(&[("Accept", "text/event-stream")], &id);
    let mut first = server.connect();
    first.send("GET", "/mcp", &get, b"", usize::MAX);
    let head = first.read_head();
    assert_eq!(head.status, 200);
    assert!(head.header("content-type").unwrap().starts_with("text/event-stream"));
    assert_eq!(server.request("GET", "/mcp", &get, b"").status, 409, "a second stream while the first is open");
}

#[test]
fn delete_ends_the_session() {
    let server = ServerUnderTest::start();
    let id = open(&server);
    assert_eq!(server.request("DELETE", "/mcp", &headers_with(BOTH, &id), b"").status, 200);
    assert_eq!(post_in(&server, &id, &json!({"jsonrpc":"2.0","id":2,"method":"tools/list"})).status, 404);
}

#[test]
fn an_idle_session_times_out() {
    let server = ServerUnderTest::start_with(&["--session-timeout", "1"]);
    let id = open(&server);
    std::thread::sleep(Duration::from_secs(3));
    assert_eq!(post_in(&server, &id, &json!({"jsonrpc":"2.0","id":2,"method":"tools/list"})).status, 404);
}

#[test]
fn a_client_leaving_a_call_leaves_the_session_working() {
    let server = ServerUnderTest::start();
    let id = open(&server);
    let mut call = server.connect();
    let message = json!({"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":5}}});
    call.send("POST", "/mcp", &headers_with(BOTH, &id), message.to_string().as_bytes(), usize::MAX);
    std::thread::sleep(Duration::from_millis(200));
    drop(call);
    std::thread::sleep(Duration::from_millis(200));
    let start = Instant::now();
    let listed = post_in(&server, &id, &json!({"jsonrpc":"2.0","id":3,"method":"tools/list"}));
    assert_eq!(sse_message(&listed)["id"], 3);
    assert!(start.elapsed() < Duration::from_millis(500), "the session answered after {:?}", start.elapsed());
}
