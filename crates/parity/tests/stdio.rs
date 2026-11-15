//! The stdio binding, on stdin/stdout and on the Unix socket. Every test holds for both ports.
use desktop_mcp_parity::{run_stdio, socket_path, ServerUnderTest};
use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};
use std::os::unix::fs::PermissionsExt;
use std::os::unix::net::UnixStream;
use std::time::Duration;

fn initialize() -> Value {
    json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"parity","version":"0"}}})
}
fn initialized() -> Value {
    json!({"jsonrpc":"2.0","method":"notifications/initialized"})
}

#[test]
fn stdio_answers_what_it_read_then_exits_at_eof() {
    let call = json!({"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":1}}});
    let (status, lines) = run_stdio(&[], &[initialize(), initialized(), call]);
    assert_eq!(status.code(), Some(0), "{status:?}");
    let answer = |id: i64| lines.iter().find(|line| line["id"] == id).unwrap_or_else(|| panic!("no answer to {id} in {lines:?}")).clone();
    assert_eq!(answer(1)["result"]["serverInfo"]["name"], "desktop-mcp");
    assert_eq!(answer(2)["result"]["content"][0]["text"], "Waited for 1 seconds.", "a call read before EOF is still answered");
}

#[test]
fn stdio_is_the_default_transport() {
    let (status, lines) = run_stdio(&["serve"], &[initialize()]);
    assert_eq!(status.code(), Some(0));
    assert_eq!(lines.first().map(|line| line["id"].clone()), Some(json!(1)));
}

/// Writes `messages` as lines and reads `count` lines back.
fn converse(stream: &UnixStream, messages: &[Value], count: usize) -> Vec<Value> {
    let mut writer = stream.try_clone().unwrap();
    for message in messages {
        writer.write_all(format!("{message}\n").as_bytes()).unwrap();
    }
    let mut reader = BufReader::new(stream.try_clone().unwrap());
    (0..count)
        .map(|_| {
            let mut line = String::new();
            reader.read_line(&mut line).unwrap();
            serde_json::from_str(&line).unwrap_or_else(|_| panic!("not a JSON line: {line:?}"))
        })
        .collect()
}

#[test]
fn the_socket_serves_the_stdio_binding() {
    let path = socket_path("serve");
    let server = ServerUnderTest::start_with(&["--socket", path.to_str().unwrap()]);
    for _ in 0..100 {
        if path.exists() {
            break;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    assert_eq!(std::fs::metadata(&path).unwrap().permissions().mode() & 0o777, 0o600, "only the same user may connect");
    let stream = UnixStream::connect(&path).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let lines = converse(&stream, &[initialize(), initialized(), json!({"jsonrpc":"2.0","id":2,"method":"tools/list"})], 2);
    assert_eq!(lines[0]["result"]["serverInfo"]["name"], "desktop-mcp");
    assert!(lines[1]["result"]["tools"].as_array().unwrap().iter().any(|tool| tool["name"] == "Wait"));
    // Each connection is a session of its own.
    let second = UnixStream::connect(&path).unwrap();
    second.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    assert_eq!(converse(&second, &[initialize()], 1)[0]["id"], 1);
    let status = server.terminate();
    assert_eq!(status.code(), Some(0));
    assert!(!path.exists(), "the socket file goes with the server");
}

#[test]
fn a_socket_in_use_leaves_http_working() {
    let path = socket_path("busy");
    let _other = std::os::unix::net::UnixListener::bind(&path).unwrap();
    let server = ServerUnderTest::start_with(&["--socket", path.to_str().unwrap()]);
    let reply = server.post(&json!({"jsonrpc":"2.0","id":1,"method":"tools/list"}));
    assert_eq!(reply.status, 200);
    assert!(path.exists(), "the other server's socket is left alone");
}
