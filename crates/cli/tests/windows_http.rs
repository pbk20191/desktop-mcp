//! The Windows binary over streamable HTTP: the key is required, a request is answered, a taken port is an error.
#![cfg(windows)]
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

fn free_port() -> u16 {
    TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

fn start(port: u16) -> Child {
    Command::new(env!("CARGO_BIN_EXE_desktop-mcp"))
        .args(["serve", "--transport", "streamable-http", "--host", "127.0.0.1", "--port", &port.to_string(), "--auth-key", "k"])
        .env("DESKTOP_MCP_ALLOW_HTTP", "1")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap()
}

/// One HTTP/1.0 exchange; the whole answer.
fn exchange(port: u16, request: &str) -> String {
    let started = Instant::now();
    let mut stream = loop {
        match TcpStream::connect(("127.0.0.1", port)) {
            Ok(stream) => break stream,
            Err(_) if started.elapsed() < Duration::from_secs(10) => std::thread::sleep(Duration::from_millis(50)),
            Err(error) => panic!("cannot connect: {error}"),
        }
    };
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    stream.write_all(request.as_bytes()).unwrap();
    let mut answer = String::new();
    stream.read_to_string(&mut answer).unwrap();
    answer
}

fn post(body: &str, key: Option<&str>) -> String {
    let authorization = key.map(|key| format!("Authorization: Bearer {key}\r\n")).unwrap_or_default();
    format!("POST /mcp HTTP/1.0\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nAccept: application/json, text/event-stream\r\n{authorization}Content-Length: {}\r\n\r\n{body}", body.len())
}

#[test]
fn http_requires_the_key_and_answers() {
    let port = free_port();
    let mut server = start(port);
    let list = r#"{"jsonrpc":"2.0","id":1,"method":"tools/list"}"#;
    let refused = exchange(port, &post(list, None));
    let answered = exchange(port, &post(list, Some("k")));
    let _ = server.kill();
    assert!(refused.starts_with("HTTP/1.0 401"), "{refused}");
    assert!(answered.starts_with("HTTP/1.0 200") && answered.contains("\"Shell\""), "{answered}");
}

#[test]
fn a_taken_port_is_an_error() {
    let taken = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = taken.local_addr().unwrap().port();
    let server = start(port);
    let output = server.wait_with_output().unwrap();
    assert!(!output.status.success());
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("Error: cannot serve on 127.0.0.1:"), "{stderr}");
}
