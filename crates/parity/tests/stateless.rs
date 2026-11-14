//! The stateless Streamable HTTP transport, as a client sees it. Every test holds for both ports unless it
//! says otherwise.
use desktop_mcp_parity::{ServerUnderTest, JSON_HEADERS};
use serde_json::{json, Value};
use std::time::{Duration, Instant};

fn call(name: &str, arguments: Value) -> Value {
    json!({"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":name,"arguments":arguments}})
}
fn list() -> Value {
    json!({"jsonrpc":"2.0","id":1,"method":"tools/list"})
}

#[test]
fn initialize_answers_with_the_requested_version() {
    let server = ServerUnderTest::start();
    let reply = server.post(&json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"parity","version":"0"}}}));
    assert_eq!(reply.status, 200);
    assert!(reply.header("content-type").unwrap().starts_with("application/json"));
    assert_eq!(reply.header("mcp-session-id"), None, "a JSON-only Accept must not open a session");
    let answer = reply.json();
    assert_eq!(answer["id"], 1);
    assert_eq!(answer["result"]["protocolVersion"], "2025-11-25");
    assert_eq!(answer["result"]["serverInfo"]["name"], "desktop-mcp");
}

#[test]
fn tools_list_names_wait() {
    let server = ServerUnderTest::start();
    let answer = server.post(&list()).json();
    let tools = answer["result"]["tools"].as_array().expect("a tools array");
    assert!(tools.iter().any(|tool| tool["name"] == "Wait"), "Wait is listed");
}

#[test]
fn wait_one_second() {
    let server = ServerUnderTest::start();
    let start = Instant::now();
    let answer = server.post(&call("Wait", json!({"duration": 1}))).json();
    let took = start.elapsed();
    assert_eq!(answer["result"]["content"][0]["text"], "Waited for 1 seconds.");
    assert!(took >= Duration::from_millis(900) && took < Duration::from_millis(1600), "took {took:?}");
}

#[test]
fn wait_without_duration_is_an_error_result() {
    let server = ServerUnderTest::start();
    let answer = server.post(&call("Wait", json!({}))).json();
    assert_eq!(answer["result"]["isError"], true);
    assert_eq!(answer["result"]["content"][0]["text"], "Error executing tool Wait: Missing required argument 'duration'");
}

#[test]
fn two_requests_on_one_connection() {
    let server = ServerUnderTest::start();
    let mut connection = server.connect();
    for id in [1, 2] {
        let reply = connection.request("POST", "/mcp", JSON_HEADERS, json!({"jsonrpc":"2.0","id":id,"method":"tools/list"}).to_string().as_bytes());
        assert_eq!(reply.status, 200);
        assert_eq!(reply.json()["id"], id);
    }
}

#[test]
fn concurrent_requests_are_independent() {
    let server = ServerUnderTest::start();
    let mut slow = server.connect();
    slow.send("POST", "/mcp", JSON_HEADERS, call("Wait", json!({"duration": 1})).to_string().as_bytes(), usize::MAX);
    std::thread::sleep(Duration::from_millis(100));
    let start = Instant::now();
    let fast = server.post(&list());
    assert_eq!(fast.status, 200);
    assert!(start.elapsed() < Duration::from_millis(300), "tools/list waited {:?} behind a running Wait", start.elapsed());
    assert_eq!(slow.read_reply().json()["result"]["content"][0]["text"], "Waited for 1 seconds.");
}

#[test]
fn large_body_in_many_segments() {
    let server = ServerUnderTest::start();
    let message = json!({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":0,"title":"x".repeat(200 * 1024)}}});
    let mut connection = server.connect();
    connection.send("POST", "/mcp", JSON_HEADERS, message.to_string().as_bytes(), 1024);
    let reply = connection.read_reply();
    assert_eq!(reply.status, 200);
    assert_eq!(reply.json()["result"]["content"][0]["text"], "Waited for 0 seconds.");
}

#[test]
fn status_codes() {
    let server = ServerUnderTest::start();
    let body = list().to_string();
    let request = |method: &str, path: &str, headers: &[(&str, &str)], body: &str| server.request(method, path, headers, body.as_bytes());
    assert_eq!(request("POST", "/mcp/", JSON_HEADERS, &body).status, 200, "the trailing slash is accepted");
    assert_eq!(request("POST", "/mcp", &[("Content-Type", "application/json")], &body).status, 406, "no Accept");
    assert_eq!(request("POST", "/mcp", &[("Accept", "text/html"), ("Content-Type", "application/json")], &body).status, 406, "Accept without JSON");
    assert_eq!(request("POST", "/mcp", &[("Accept", "*/*"), ("Content-Type", "application/json")], &body).status, 406, "a wildcard Accept");
    assert_eq!(request("POST", "/mcp", &[("Accept", "application/json"), ("Content-Type", "text/plain")], &body).status, 415);
    assert_eq!(request("POST", "/mcp", JSON_HEADERS, &format!("[{body}]")).status, 400, "a batch");
    assert_eq!(request("POST", "/mcp", JSON_HEADERS, "{nope").status, 400, "invalid JSON");
    for method in ["GET", "DELETE"] {
        let reply = request(method, "/mcp", &[("Accept", "application/json")], "");
        assert_eq!(reply.status, 405, "{method}");
        assert_eq!(reply.header("allow"), Some("POST"), "{method}");
    }
    let notification = request("POST", "/mcp", JSON_HEADERS, r#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#);
    assert_eq!(notification.status, 202);
    assert!(notification.body.is_empty());
    assert_eq!(request("POST", "/nope", JSON_HEADERS, &body).status, 404);
}

#[test]
fn unknown_tool_is_invalid_params() {
    let server = ServerUnderTest::start();
    let reply = server.post(&call("Nope", json!({})));
    assert_eq!(reply.json()["error"]["code"], -32602);
}

#[test]
fn server_exits_zero_on_sigterm() {
    let server = ServerUnderTest::start();
    assert_eq!(server.post(&list()).status, 200);
    let status = server.terminate();
    assert_eq!(status.code(), Some(0), "{status:?}");
}

/// Rust port only: with nothing connected, no timer runs.
#[test]
fn idle_server_does_not_spin() {
    let server = ServerUnderTest::start();
    assert_eq!(server.post(&list()).status, 200);
    if !server.is_rust() {
        eprintln!("skipped: not the Rust port");
        return;
    }
    std::thread::sleep(Duration::from_millis(500)); // let the connection of the request above close
    let before = server.wakeups();
    std::thread::sleep(Duration::from_secs(3));
    let woken = server.wakeups() - before;
    // A forgotten fast tick would be 60 wakeups, a forgotten slow tick 3.
    assert!(woken <= 1, "an idle server was woken {woken} times in 3 s");
}

/// Rust port only (the Swift port was seen truncating some `Connection: close` replies, which is outside this
/// suite): a client that asks for the connection to be closed still gets the whole answer.
#[test]
fn connection_close_gets_the_whole_reply() {
    let server = ServerUnderTest::start();
    if !server.is_rust() {
        eprintln!("skipped: not the Rust port");
        return;
    }
    for _ in 0..5 {
        let mut connection = server.connect();
        let headers = [("Accept", "application/json"), ("Content-Type", "application/json"), ("Connection", "close")];
        connection.send("POST", "/mcp", &headers, list().to_string().as_bytes(), usize::MAX);
        let received = String::from_utf8_lossy(&connection.read_to_close()).into_owned();
        assert!(received.starts_with("HTTP/1.1 200"), "got {:?}", &received[..received.len().min(80)]);
        let body = received.split("\r\n\r\n").nth(1).unwrap_or_default();
        assert!(serde_json::from_str::<Value>(body).is_ok(), "the body is cut short: {} bytes", body.len());
    }
}

/// Rust port only: a client that gives up on a call leaves no timer and no tick behind.
#[test]
fn an_abandoned_wait_leaves_nothing_behind() {
    let server = ServerUnderTest::start();
    if !server.is_rust() {
        eprintln!("skipped: not the Rust port");
        return;
    }
    for seconds in [2, 3] {
        let mut connection = server.connect();
        connection.send("POST", "/mcp", JSON_HEADERS, call("Wait", json!({"duration": seconds})).to_string().as_bytes(), usize::MAX);
        std::thread::sleep(Duration::from_millis(150));
        drop(connection);
    }
    std::thread::sleep(Duration::from_millis(500));
    let before = server.wakeups();
    std::thread::sleep(Duration::from_secs(3)); // both waits would have ended inside this window
    let woken = server.wakeups() - before;
    assert!(woken <= 1, "after two abandoned calls the server was woken {woken} times in 3 s");
    assert_eq!(server.post(&list()).status, 200, "and it still serves");
}

/// Rust port only: serving requests starts no threads (the main thread plus libdispatch's workers).
#[test]
fn serving_starts_no_threads() {
    let server = ServerUnderTest::start();
    if !server.is_rust() {
        eprintln!("skipped: not the Rust port");
        return;
    }
    for _ in 0..30 {
        assert_eq!(server.post(&call("Wait", json!({"duration": 0}))).status, 200);
    }
    let threads = server.threads();
    assert!(threads <= 4, "{threads} threads after 30 requests");
}
