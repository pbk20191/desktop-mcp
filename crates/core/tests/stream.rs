//! The stdio binding: one MCP session over a byte stream of newline-delimited JSON, as the Swift port's
//! `StdioServer` serves stdin/stdout and each Unix-socket connection.
mod support;
use desktop_mcp_core::mcp::Server;
use desktop_mcp_core::stream::serve_stream;
use serde_json::json;
use std::rc::Rc;
use std::time::Duration;
use support::*;

fn initialize() -> String {
    json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}).to_string()
}
fn wait(id: u64, seconds: u64) -> String {
    json!({"jsonrpc":"2.0","id":id,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":seconds}}}).to_string()
}
const INITIALIZED: &str = r#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#;

/// Starts a session on a fresh pipe; returns the pipe, the session's task and the desktop.
fn start() -> (Pipe, tokio::task::JoinHandle<()>, Rc<FakeDesktop>) {
    let pipe = Pipe::default();
    let desktop = Rc::new(FakeDesktop::default());
    let server = Server::new(desktop.clone(), &["Wait"]);
    let session = tokio::task::spawn_local(serve_stream(pipe.clone(), server));
    (pipe, session, desktop)
}

async fn handshake(pipe: &Pipe) {
    pipe.send_line(&initialize());
    let lines = pipe.wait_lines(1).await;
    assert_eq!(lines[0]["result"]["serverInfo"]["name"], "desktop-mcp");
    pipe.send_line(INITIALIZED);
}

#[test]
fn a_session_answers_line_by_line() {
    block_on(async {
        let (pipe, _session, _) = start();
        handshake(&pipe).await;
        pipe.send_line(r#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#);
        pipe.send_line(&wait(3, 1));
        let lines = pipe.wait_lines(3).await;
        assert_eq!(lines[1]["id"], 2);
        assert_eq!(lines[1]["result"]["tools"][0]["name"], "Wait");
        assert_eq!(lines[2]["result"]["content"][0]["text"], "Waited for 1 seconds.");
    })
}

#[test]
fn requests_read_before_eof_are_still_answered() {
    block_on(async {
        let (pipe, session, desktop) = start();
        handshake(&pipe).await;
        let release = desktop.hold_next_sleep();
        pipe.send_line(&wait(2, 1));
        while !desktop.sleeping() {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        pipe.close_input();
        tokio::time::sleep(Duration::from_millis(30)).await;
        assert!(!session.is_finished(), "the session waits for the call it has not answered");
        release.send(()).unwrap();
        let lines = pipe.wait_lines(2).await;
        assert_eq!(lines[1]["result"]["content"][0]["text"], "Waited for 1 seconds.");
        tokio::time::timeout(Duration::from_secs(2), session).await.expect("the session ends once everything is answered").unwrap();
    })
}

#[test]
fn eof_with_nothing_pending_ends_the_session() {
    block_on(async {
        let (pipe, session, _) = start();
        handshake(&pipe).await;
        pipe.close_input();
        tokio::time::timeout(Duration::from_secs(2), session).await.expect("the session ends at EOF").unwrap();
    })
}

#[test]
fn a_closed_output_ends_the_session_and_its_calls() {
    block_on(async {
        let (pipe, session, desktop) = start();
        handshake(&pipe).await;
        let mut release = desktop.hold_next_sleep();
        pipe.send_line(&wait(2, 1));
        pipe.send_line(r#"{"jsonrpc":"2.0","id":3,"method":"ping"}"#);
        while !desktop.sleeping() {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        pipe.close_output();
        pipe.send_line(r#"{"jsonrpc":"2.0","id":4,"method":"ping"}"#); // its answer cannot be written
        tokio::time::timeout(Duration::from_secs(2), session).await.expect("the session ends when its output is gone").unwrap();
        tokio::time::timeout(Duration::from_secs(2), release.closed()).await.expect("and the call in flight is cancelled");
    })
}

#[test]
fn a_cancelled_request_is_not_waited_for_at_eof() {
    block_on(async {
        let (pipe, session, desktop) = start();
        handshake(&pipe).await;
        let _release = desktop.hold_next_sleep();
        pipe.send_line(&wait(2, 1));
        while !desktop.sleeping() {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        pipe.send_line(r#"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}"#);
        pipe.close_input();
        tokio::time::timeout(Duration::from_secs(2), session).await.expect("a cancelled request expects no answer").unwrap();
    })
}

#[test]
fn bad_lines_get_errors_and_blank_lines_are_skipped() {
    block_on(async {
        let (pipe, _session, _) = start();
        handshake(&pipe).await;
        pipe.send_line("");
        pipe.send_line("{not json");
        pipe.send_line(&format!("[{}]", r#"{"jsonrpc":"2.0","id":5,"method":"ping"}"#));
        pipe.send_line(r#"{"jsonrpc":"2.0","id":6,"method":"ping"}"#);
        let lines = pipe.wait_lines(4).await;
        assert_eq!(lines[1]["error"]["code"], -32700);
        assert_eq!(lines[2]["error"]["code"], -32600);
        assert_eq!(lines[3]["id"], 6, "the session goes on");
    })
}

#[test]
fn a_line_split_across_reads_and_crlf_endings_work() {
    block_on(async {
        let (pipe, _session, _) = start();
        let init = initialize();
        let (first, second) = init.split_at(init.len() / 2);
        pipe.send_raw(first.as_bytes());
        tokio::time::sleep(Duration::from_millis(10)).await;
        pipe.send_raw(second.as_bytes());
        pipe.send_raw(b"\r\n");
        assert_eq!(pipe.wait_lines(1).await[0]["id"], 1);
    })
}

#[test]
fn concurrent_answers_are_whole_lines() {
    block_on(async {
        let (pipe, _session, _) = start();
        handshake(&pipe).await;
        for id in 10..30 {
            pipe.send_line(&json!({"jsonrpc":"2.0","id":id,"method":"tools/list"}).to_string());
        }
        let lines = pipe.wait_lines(21).await; // lines() panics on a line that is not whole JSON
        let mut ids: Vec<i64> = lines[1..].iter().map(|line| line["id"].as_i64().unwrap()).collect();
        ids.sort();
        assert_eq!(ids, (10..30).collect::<Vec<_>>());
    })
}

#[test]
fn a_long_call_is_answered_after_eof() {
    // rmcp gives calls five seconds once its input ends; a call read before EOF gets as long as it takes.
    block_on_paused(async {
        let (pipe, session, desktop) = start();
        handshake(&pipe).await;
        let mut release = desktop.hold_next_sleep();
        pipe.send_line(&wait(2, 60));
        while !desktop.sleeping() {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        pipe.close_input();
        tokio::time::sleep(Duration::from_secs(30)).await;
        assert!(!session.is_finished(), "the session ended before answering");
        assert!(tokio::time::timeout(Duration::from_millis(1), release.closed()).await.is_err(), "the call was cancelled");
        release.send(()).unwrap();
        assert_eq!(pipe.wait_lines(2).await[1]["result"]["content"][0]["text"], "Waited for 60 seconds.");
    })
}
