//! The Windows binary over stdio: tools/list as pinned in `snapshots/windows-tools.json` (OCR included;
//! `UPDATE_SNAPSHOTS=1` rewrites it), one Shell call, then end of input ends the process with status 0.
#![cfg(windows)]
use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

#[test]
fn stdio_lists_runs_shell_and_exits_at_end_of_input() {
    let mut child = Command::new(env!("CARGO_BIN_EXE_desktop-mcp")).args(["serve", "--transport", "stdio"]).env("DESKTOP_MCP_INCLUDE_TOOLS", "OCR").env_remove("DESKTOP_MCP_EXCLUDE_TOOLS").stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null()).spawn().unwrap();
    let mut stdin = child.stdin.take().unwrap();
    for message in [
        json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}),
        json!({"jsonrpc":"2.0","method":"notifications/initialized"}),
        json!({"jsonrpc":"2.0","id":2,"method":"tools/list"}),
        json!({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"Shell","arguments":{"command":"Write-Output hi"}}}),
    ] {
        writeln!(stdin, "{message}").unwrap();
    }
    drop(stdin);
    let started = Instant::now();
    let answers: Vec<Value> = BufReader::new(child.stdout.take().unwrap()).lines().map(|line| serde_json::from_str(&line.unwrap()).unwrap()).collect();
    let status = child.wait().unwrap();
    assert!(status.success(), "{status}");
    assert!(started.elapsed() < Duration::from_secs(30));

    let by_id = |id: i64| answers.iter().find(|answer| answer["id"] == id).unwrap_or_else(|| panic!("no answer {id} in {answers:?}"));
    let listed = serde_json::to_string_pretty(&by_id(2)["result"]).unwrap() + "\n";
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/snapshots/windows-tools.json");
    if std::env::var_os("UPDATE_SNAPSHOTS").is_some() {
        std::fs::write(path, &listed).unwrap();
    }
    let expected = std::fs::read_to_string(path).unwrap_or_default().replace("\r\n", "\n");
    assert!(listed == expected, "tools/list changed; review it, then rerun with UPDATE_SNAPSHOTS=1.\n{listed}");
    let text = by_id(3)["result"]["content"][0]["text"].as_str().unwrap();
    assert!(text.starts_with("Shell Response: ") && text.contains("hi") && text.ends_with("Status Code: 0"), "{text}");
}
