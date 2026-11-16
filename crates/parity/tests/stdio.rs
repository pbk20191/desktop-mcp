//! The stdio binding on stdin/stdout. Every test holds for both ports. (The Unix socket is disabled in the Rust
//! port until clients are verified; its tests are in git history.)
use desktop_mcp_parity::run_stdio;
use serde_json::{json, Value};

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
