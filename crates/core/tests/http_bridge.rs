//! `connect` over HTTP, against the HTTP app in memory. Ported from the Swift port's StdioBridgeTests.
mod support;
use desktop_mcp_core::http::Security;
use desktop_mcp_core::http_bridge::{run_http_bridge, HttpBridge};
use desktop_mcp_core::launch_agent::HttpTarget;
use desktop_mcp_core::session::SessionSettings;
use serde_json::{json, Value};
use std::rc::Rc;
use std::time::Duration;
use support::*;

fn initialize() -> String {
    json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}).to_string()
}
const INITIALIZED: &str = r#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#;

struct Bridge {
    stdio: Pipe,
    task: tokio::task::JoinHandle<()>,
    bridge: Rc<HttpBridge<InMemory>>,
    fixture: Fixture,
}

fn start_with(security: Security, key: Option<&str>) -> Bridge {
    let fixture = fixture_full(SessionSettings::default(), security);
    let connector = InMemory::new(fixture.app.clone());
    let target = HttpTarget { url: "http://127.0.0.1:8000/mcp".into(), auth_key: key.map(str::to_string) };
    let bridge = Rc::new(HttpBridge::new(target, connector));
    let stdio = Pipe::default();
    let task = tokio::task::spawn_local(run_http_bridge(stdio.clone(), bridge.clone()));
    Bridge { stdio, task, bridge, fixture }
}

fn start() -> Bridge {
    start_with(Security::default(), None)
}

async fn handshake(bridge: &Bridge) {
    bridge.stdio.send_line(&initialize());
    assert_eq!(bridge.stdio.wait_lines(1).await[0]["result"]["serverInfo"]["name"], "desktop-mcp");
    bridge.stdio.send_line(INITIALIZED);
}

fn answer(lines: &[Value], id: i64) -> Value {
    lines.iter().find(|line| line["id"] == id).unwrap_or_else(|| panic!("no answer to {id} in {lines:?}")).clone()
}

#[test]
fn requests_go_through_a_session_and_answers_come_back_one_per_line() {
    block_on(async {
        let bridge = start();
        handshake(&bridge).await;
        assert_eq!(bridge.fixture.app.sessions.count(), 1, "the bridge holds a session");
        bridge.stdio.send_line(r#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#);
        bridge.stdio.send_line(r#"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":1}}}"#);
        let lines = bridge.stdio.wait_lines(3).await;
        assert_eq!(answer(&lines, 2)["result"]["tools"][0]["name"], "Wait");
        assert_eq!(answer(&lines, 3)["result"]["content"][0]["text"], "Waited for 1 seconds.");
    })
}

#[test]
fn a_slow_call_does_not_hold_up_the_next() {
    block_on(async {
        let bridge = start();
        handshake(&bridge).await;
        let release = bridge.fixture.desktop.hold_next_sleep();
        bridge.stdio.send_line(r#"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":1}}}"#);
        while !bridge.fixture.desktop.sleeping() {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        bridge.stdio.send_line(r#"{"jsonrpc":"2.0","id":3,"method":"ping"}"#);
        let lines = bridge.stdio.wait_lines(2).await;
        assert_eq!(lines[1]["id"], 3, "ping answered while the call runs");
        release.send(()).unwrap();
        assert_eq!(answer(&bridge.stdio.wait_lines(3).await, 2)["result"]["content"][0]["text"], "Waited for 1 seconds.");
    })
}

#[test]
fn a_lost_session_is_replaced_by_replaying_initialize() {
    block_on(async {
        let bridge = start();
        handshake(&bridge).await;
        let first = bridge.bridge.session_id().expect("a session");
        bridge.fixture.app.sessions.end(&first); // the server forgot it (restart, eviction)
        bridge.stdio.send_line(r#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#);
        let lines = bridge.stdio.wait_lines(2).await;
        assert_eq!(answer(&lines, 2)["result"]["tools"][0]["name"], "Wait", "answered on a new session");
        assert_ne!(bridge.bridge.session_id(), Some(first));
        assert_eq!(bridge.bridge.replays(), 1);
        assert_eq!(lines.len(), 2, "the replayed initialize's reply is not forwarded");
    })
}

#[test]
fn server_initiated_messages_arrive_from_the_get_stream() {
    block_on(async {
        let bridge = start();
        handshake(&bridge).await;
        let id = bridge.bridge.session_id().unwrap();
        let peer = loop {
            if let Some(peer) = bridge.fixture.app.sessions.peer(&id) {
                break peer;
            }
            tokio::time::sleep(Duration::from_millis(2)).await;
        };
        peer.notify_tool_list_changed().await.unwrap();
        let lines = bridge.stdio.wait_lines(2).await;
        assert_eq!(lines[1]["method"], "notifications/tools/list_changed");
    })
}

#[test]
fn an_unreachable_server_answers_requests_with_errors() {
    block_on(async {
        let bridge = start();
        bridge.bridge.connector().down.set(true);
        bridge.stdio.send_line(&initialize());
        bridge.stdio.send_line(INITIALIZED);
        let lines = bridge.stdio.wait_lines(1).await;
        assert_eq!(lines[0]["id"], 1);
        assert_eq!(lines[0]["error"]["code"], -32000);
        let message = lines[0]["error"]["message"].as_str().unwrap();
        assert!(message.starts_with("desktop-mcp connect: http://127.0.0.1:8000/mcp: ") && message.contains("Connection refused"), "{message}");
        tokio::time::sleep(Duration::from_millis(30)).await;
        assert_eq!(bridge.stdio.lines().len(), 1, "a notification gets no answer");
    })
}

#[test]
fn http_failures_are_reported_with_their_status() {
    block_on(async {
        let bridge = start_with(Security { auth_key: Some("right".into()), ..Security::default() }, Some("wrong"));
        bridge.stdio.send_line(&initialize());
        let lines = bridge.stdio.wait_lines(1).await;
        let message = lines[0]["error"]["message"].as_str().unwrap();
        assert!(message.contains("HTTP 401"), "{message}");
    })
}

#[test]
fn the_key_is_sent() {
    block_on(async {
        let bridge = start_with(Security { auth_key: Some("right".into()), ..Security::default() }, Some("right"));
        handshake(&bridge).await;
    })
}

#[test]
fn eof_waits_for_the_calls_in_flight() {
    block_on(async {
        let bridge = start();
        handshake(&bridge).await;
        let release = bridge.fixture.desktop.hold_next_sleep();
        bridge.stdio.send_line(r#"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":1}}}"#);
        while !bridge.fixture.desktop.sleeping() {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        bridge.stdio.close_input();
        tokio::time::sleep(Duration::from_millis(30)).await;
        assert!(!bridge.task.is_finished());
        release.send(()).unwrap();
        assert_eq!(answer(&bridge.stdio.wait_lines(2).await, 2)["result"]["content"][0]["text"], "Waited for 1 seconds.");
        tokio::time::timeout(Duration::from_secs(2), bridge.task).await.expect("the bridge ends once the calls are answered").unwrap();
    })
}

#[test]
fn a_closed_stdout_ends_the_bridge() {
    block_on(async {
        let bridge = start();
        handshake(&bridge).await;
        bridge.stdio.close_output();
        bridge.stdio.send_line(r#"{"jsonrpc":"2.0","id":2,"method":"ping"}"#);
        tokio::time::timeout(Duration::from_secs(2), bridge.task).await.expect("the bridge ends when nobody reads").unwrap();
    })
}
