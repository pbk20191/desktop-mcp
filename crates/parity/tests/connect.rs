//! `connect`, the stdio bridge for clients that only spawn stdio servers. Every test holds for both ports.
use desktop_mcp_parity::{run_stdio, run_stdio_with, run_to_exit, socket_path, ServerUnderTest};
use serde_json::{json, Value};
use std::time::Duration;

fn conversation() -> Vec<Value> {
    vec![
        json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"parity","version":"0"}}}),
        json!({"jsonrpc":"2.0","method":"notifications/initialized"}),
        json!({"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":1}}}),
    ]
}

fn served_socket() -> (ServerUnderTest, std::path::PathBuf) {
    let path = socket_path("connect");
    let server = ServerUnderTest::start_with(&["--socket", path.to_str().unwrap()]);
    for _ in 0..100 {
        if path.exists() {
            break;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    (server, path)
}

fn check_answers(lines: &[Value]) {
    let answer = |id: i64| lines.iter().find(|line| line["id"] == id).unwrap_or_else(|| panic!("no answer to {id} in {lines:?}")).clone();
    assert_eq!(answer(1)["result"]["serverInfo"]["name"], "desktop-mcp");
    assert_eq!(answer(2)["result"]["content"][0]["text"], "Waited for 1 seconds.", "a call read before EOF is answered");
}

#[test]
fn connect_over_a_socket() {
    let (_server, path) = served_socket();
    let (status, lines) = run_stdio(&["connect", "--socket", path.to_str().unwrap()], &conversation());
    assert_eq!(status.code(), Some(0), "{status:?}");
    check_answers(&lines);
}

#[test]
fn connect_finds_the_installed_agents_socket() {
    let (_server, path) = served_socket();
    let plist = format!(
        r#"<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>com.desktop-mcp.server</string>
<key>ProgramArguments</key><array><string>/x</string><string>serve</string><string>--transport</string><string>streamable-http</string><string>--host</string><string>127.0.0.1</string><string>--port</string><string>1</string><string>--socket</string><string>{}</string></array>
</dict></plist>"#,
        path.display()
    );
    let (status, lines) = run_stdio_with(&["connect"], &conversation(), &[("Library/LaunchAgents/com.desktop-mcp.server.plist", &plist)]);
    assert_eq!(status.code(), Some(0), "{status:?}");
    check_answers(&lines);
}

#[test]
fn connect_without_an_agent_says_how_to_install_one() {
    let (status, stderr) = run_to_exit(&["connect"]);
    assert!(!status.success());
    assert!(stderr.contains("desktop-mcp install"), "{stderr}");
}

#[test]
fn url_and_socket_are_exclusive() {
    let (status, stderr) = run_to_exit(&["connect", "--url", "http://127.0.0.1:1/mcp", "--socket", "/tmp/x.sock"]);
    assert!(!status.success());
    assert!(stderr.contains("exclusive"), "{stderr}");
}

#[test]
fn a_socket_nobody_serves_fails() {
    let path = socket_path("nobody");
    let (status, stderr) = run_to_exit(&["connect", "--socket", path.to_str().unwrap()]);
    assert!(!status.success());
    assert!(stderr.contains("desktop-mcp connect:"), "{stderr}");
}

#[test]
fn connect_over_http() {
    let server = ServerUnderTest::start();
    let url = format!("http://127.0.0.1:{}/mcp", server.port);
    let (status, lines) = run_stdio(&["connect", "--url", &url], &conversation());
    assert_eq!(status.code(), Some(0), "{status:?}");
    check_answers(&lines);
}

#[test]
fn connect_over_http_sends_the_key() {
    let server = ServerUnderTest::start_with(&["--auth-key", "s3cret"]);
    let url = format!("http://127.0.0.1:{}/mcp", server.port);
    let (status, lines) = run_stdio(&["connect", "--url", &url, "--auth-key", "s3cret"], &conversation());
    assert_eq!(status.code(), Some(0));
    check_answers(&lines);
    let (_, refused) = run_stdio(&["connect", "--url", &url, "--auth-key", "wrong"], &conversation()[..1]);
    let message = refused[0]["error"]["message"].as_str().unwrap_or_default();
    assert_eq!(refused[0]["id"], 1);
    assert!(message.contains("HTTP 401"), "{message}");
}

#[test]
fn an_unreachable_server_answers_with_errors() {
    let port = std::net::TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port(); // free, nobody listens
    let (status, lines) = run_stdio(&["connect", "--url", &format!("http://127.0.0.1:{port}/mcp")], &conversation());
    assert_eq!(status.code(), Some(0), "the bridge ends with its input");
    let errors: Vec<&Value> = lines.iter().filter(|line| line["error"]["code"] == -32000).collect();
    assert_eq!(errors.len(), 2, "both requests are answered with an error, the notification is not: {lines:?}");
    assert!(errors[0]["error"]["message"].as_str().unwrap().starts_with("desktop-mcp connect: "));
}

#[test]
fn a_refused_socket_falls_back_to_the_agents_http_address() {
    let server = ServerUnderTest::start();
    let missing = socket_path("gone");
    let plist = format!(
        r#"<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>ProgramArguments</key><array><string>/x</string><string>serve</string><string>--transport</string><string>streamable-http</string><string>--host</string><string>127.0.0.1</string><string>--port</string><string>{}</string><string>--socket</string><string>{}</string></array>
</dict></plist>"#,
        server.port,
        missing.display()
    );
    let (status, lines) = run_stdio_with(&["connect"], &conversation(), &[("Library/LaunchAgents/com.desktop-mcp.server.plist", &plist)]);
    assert_eq!(status.code(), Some(0), "{status:?}");
    check_answers(&lines);
}
