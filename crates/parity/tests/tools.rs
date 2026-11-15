//! The tools that need no display, as a client sees them. Every test holds for both ports.
use desktop_mcp_parity::{Launch, ServerUnderTest, JSON_HEADERS};
use serde_json::{json, Value};
use std::time::{Duration, Instant};

fn call(name: &str, arguments: Value) -> Value {
    json!({"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":name,"arguments":arguments}})
}
fn text(answer: &Value) -> &str {
    answer["result"]["content"][0]["text"].as_str().unwrap_or_else(|| panic!("no text in {answer}"))
}

#[test]
fn shell_combines_both_streams_and_the_status() {
    let server = ServerUnderTest::start();
    let answer = server.post(&call("Shell", json!({"command": "echo out; echo err >&2; exit 3"}))).json();
    assert_eq!(answer["result"]["isError"], false);
    assert_eq!(text(&answer), "Shell Response: out\nerr\nStatus Code: 3");
}

#[test]
fn shell_runs_applescript() {
    let server = ServerUnderTest::start();
    let answer = server.post(&call("Shell", json!({"command": "return 1 + 2", "mode": "osascript"}))).json();
    assert_eq!(text(&answer), "AppleScript Response: 3\nStatus Code: 0");
}

#[test]
fn shell_timeout_is_capped_by_the_environment() {
    let server = ServerUnderTest::launch(&Launch { env: &[("DESKTOP_MCP_MAX_TIMEOUT", "1")], ..Launch::default() });
    let start = Instant::now();
    let answer = server.post(&call("Shell", json!({"command": "echo before; sleep 30", "timeout": 20}))).json();
    assert!(start.elapsed() < Duration::from_secs(5), "took {:?}", start.elapsed());
    assert_eq!(
        text(&answer),
        "Shell Response: Command timed out after 1 seconds (requested 20s, capped by DESKTOP_MCP_MAX_TIMEOUT)\n--- partial output before timeout ---\nbefore\nStatus Code: -1"
    );
}

#[test]
fn shell_children_are_killable_and_read_no_stdin() {
    // The server ignores SIGPIPE and owns stdin under stdio; neither may reach the command.
    let server = ServerUnderTest::start();
    let answer = server.post(&call("Shell", json!({"command": "cat; kill -PIPE $$; echo survived"}))).json();
    assert_eq!(text(&answer), "Shell Response: \nStatus Code: -13");
}

#[test]
fn a_client_that_goes_away_stops_its_command() {
    let server = ServerUnderTest::start();
    let marker = std::env::temp_dir().join(format!("desktop-mcp-parity-{}", std::process::id()));
    let _ = std::fs::remove_file(&marker);
    let mut connection = server.connect();
    let command = format!("sleep 1; touch '{}'", marker.display());
    connection.send("POST", "/mcp", JSON_HEADERS, call("Shell", json!({"command": command})).to_string().as_bytes(), usize::MAX);
    std::thread::sleep(Duration::from_millis(300));
    drop(connection);
    std::thread::sleep(Duration::from_millis(1500));
    assert!(!marker.exists(), "the command kept running after its client left");
    assert_eq!(server.post(&call("Shell", json!({"command": "true"}))).status, 200, "and the server still serves");
}

#[test]
fn scrape_refuses_this_machine_and_other_schemes() {
    let server = ServerUnderTest::start();
    let port = server.port;
    for (url, reason) in [
        (format!("http://127.0.0.1:{port}/mcp"), "Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1"),
        ("http://[::1]/".to_string(), "Private, loopback, link-local, multicast, and reserved addresses are blocked: ::1"),
        ("file:///etc/passwd".to_string(), "URL scheme 'file' is not allowed; use http or https."),
        ("http://user:pass@example.com/".to_string(), "URLs with embedded credentials are not allowed."),
    ] {
        let answer = server.post(&call("Scrape", json!({"url": url}))).json();
        assert_eq!(answer["result"]["isError"], true, "{url}");
        assert_eq!(text(&answer), format!("Error executing tool Scrape: {reason}"), "{url}");
    }
}

/// Only arguments that are refused before the pasteboard is touched: the suite must not change the user's clipboard.
#[test]
fn clipboard_writes_are_checked_before_the_clipboard_is_touched() {
    let server = ServerUnderTest::start();
    for (arguments, message) in [
        (json!({"mode": "write", "text": "a", "paths": ["/tmp"]}), "Pass either text or paths, not both."),
        (json!({"mode": "write"}), "write needs text or paths."),
        (json!({"mode": "write", "paths": ["/nonexistent/desktop-mcp"]}), "No such file: /nonexistent/desktop-mcp"),
    ] {
        let answer = server.post(&call("Clipboard", arguments.clone())).json();
        assert_eq!(answer["result"]["isError"], true, "{arguments}");
        assert_eq!(text(&answer), format!("Error executing tool Clipboard: {message}"), "{arguments}");
    }
}
