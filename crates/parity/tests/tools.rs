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

/// Only refusals: anything else would open windows on the desktop.
#[test]
fn open_refuses_what_it_cannot_open() {
    let server = ServerUnderTest::start();
    for (arguments, message) in [
        (json!({"target": " "}), "target is required."),
        (json!({"target": "/nonexistent/desktop-mcp"}), "No such file: /nonexistent/desktop-mcp"),
        (json!({"target": "https://example.com", "mode": "reveal"}), "reveal needs an existing file or folder path."),
        (json!({"target": "https://example.com", "app": "NoSuchApp-desktop-mcp"}), "Application 'NoSuchApp-desktop-mcp' not found."),
    ] {
        let answer = server.post(&call("Open", arguments.clone())).json();
        assert_eq!(answer["result"]["isError"], true, "{arguments}");
        assert_eq!(text(&answer), format!("Error executing tool Open: {message}"), "{arguments}");
    }
}

/// Read-only: listing, and a name that does not exist (which lists, then refuses). No shortcut is run.
#[test]
fn shortcuts_are_listed_and_unknown_names_refused() {
    let server = ServerUnderTest::start();
    let answer = server.post(&call("RunShortcut", json!({"mode": "list"}))).json();
    assert_eq!(answer["result"]["isError"], false, "{answer}");
    let answer = server.post(&call("RunShortcut", json!({"name": "zzz-no-such-shortcut-desktop-mcp"}))).json();
    assert_eq!(answer["result"]["isError"], true);
    assert_eq!(text(&answer), "Error executing tool RunShortcut: No shortcut named 'zzz-no-such-shortcut-desktop-mcp'.");
    let answer = server.post(&call("RunShortcut", json!({}))).json();
    assert_eq!(text(&answer), "Error executing tool RunShortcut: name is required for run.");
}

/// Spotlight only reads the index. Live answers are skipped when indexing is off.
#[test]
fn spotlight_finds_safari_and_refuses_bad_queries() {
    let server = ServerUnderTest::start();
    let answer = server.post(&call("Spotlight", json!({"query": "kMDItemFSName == \"Safari.app\"", "raw": true, "scope": ["/Applications"]}))).json();
    let rows = text(&answer);
    if rows != "No matches." {
        let row = rows.lines().next().unwrap();
        let fields: Vec<&str> = row.split('\t').collect();
        assert_eq!(fields.len(), 4, "{row}");
        assert!(fields[0].ends_with("/Safari.app") && fields[2].ends_with('Z'), "{row}");
    }
    let answer = server.post(&call("Spotlight", json!({"query": "kMDItem ==", "raw": true}))).json();
    assert_eq!(text(&answer), "Error executing tool Spotlight: Invalid Spotlight query: kMDItem ==");
    let answer = server.post(&call("Spotlight", json!({"query": "  "}))).json();
    assert_eq!(text(&answer), "Error executing tool Spotlight: query is required.");
}

/// Both ports see the same grants (the binaries differ, so this compares the report's shape, not the values).
#[test]
fn permissions_are_reported_without_prompting() {
    let server = ServerUnderTest::start();
    let report = text(&server.post(&call("Permissions", json!({}))).json()).to_string();
    let lines: Vec<&str> = report.lines().collect();
    assert_eq!(lines.len(), 5, "{report}");
    assert!(lines[0].starts_with("Accessibility: ") && lines[1].starts_with("Screen Recording: ") && lines[2].starts_with("Automation (System Events): "), "{report}");
    assert_eq!(lines[3..], ["Needs Accessibility: App (resize, move), Click, Type, Scroll, Move, Shortcut, Snapshot, Desktop, Window, Menu, Wait (until)", "Needs Screen Recording: Snapshot (use_vision), OCR"]);
}
