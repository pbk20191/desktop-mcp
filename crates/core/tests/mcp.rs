//! The MCP server, driven message by message with a fake desktop.
use desktop_mcp_core::mcp::{answer_stateless, Desktop, Server};
use desktop_mcp_core::shell::{Run, ShellMode};
use desktop_mcp_core::clipboard::{Clipboard, ClipboardWrite, Image};
use serde_json::{json, Value};
use std::cell::RefCell;
use std::future::Future;
use std::pin::Pin;
use std::rc::Rc;
use std::time::Duration;

#[derive(Default)]
struct FakeDesktop {
    slept: RefCell<Vec<Duration>>,
    shells: RefCell<Vec<(String, ShellMode, Duration)>>,
    shell_answer: RefCell<Option<Result<Run, String>>>,
    clipboard: RefCell<Clipboard>,
    image_limits: RefCell<Vec<u32>>,
    written: RefCell<Vec<ClipboardWrite>>,
    refuse_writes: bool,
}
impl Desktop for FakeDesktop {
    fn read_clipboard(&self, image_max_side: u32) -> Pin<Box<dyn Future<Output = Clipboard>>> {
        self.image_limits.borrow_mut().push(image_max_side);
        let contents = self.clipboard.borrow().clone();
        Box::pin(async move { contents })
    }
    fn write_clipboard(&self, contents: ClipboardWrite) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        self.written.borrow_mut().push(contents);
        let refused = self.refuse_writes;
        Box::pin(async move { if refused { Err("Could not write to the clipboard.".to_string()) } else { Ok(()) } })
    }
    fn resolve(&self, _host: &str) -> Pin<Box<dyn Future<Output = Result<Vec<std::net::IpAddr>, String>>>> {
        Box::pin(async { Err("no network in these tests".to_string()) })
    }
    fn connect(&self, _host: &str, _port: u16, _tls: bool) -> Pin<Box<dyn Future<Output = Result<Box<dyn desktop_mcp_core::io::Stream>, String>>>> {
        Box::pin(async { Err("no network in these tests".to_string()) })
    }
    fn parse_html(&self, _body: &[u8], _charset: Option<&str>) -> Option<desktop_mcp_core::html::Element> {
        None
    }
    fn sleep(&self, duration: Duration) -> Pin<Box<dyn Future<Output = ()>>> {
        self.slept.borrow_mut().push(duration);
        Box::pin(async {})
    }
    fn shell(&self, command: &str, mode: ShellMode, timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>> {
        self.shells.borrow_mut().push((command.to_string(), mode, timeout));
        let answer = self.shell_answer.borrow_mut().take().expect("a canned shell answer");
        Box::pin(async move { answer })
    }
}
impl FakeDesktop {
    fn answering(answer: Result<Run, String>) -> Rc<FakeDesktop> {
        let desktop = FakeDesktop::default();
        *desktop.shell_answer.borrow_mut() = Some(answer);
        Rc::new(desktop)
    }
}

/// Sends one message to a fresh server and returns the answer as JSON.
fn ask(desktop: &Rc<FakeDesktop>, message: Value) -> Option<Value> {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    let local = tokio::task::LocalSet::new();
    let server = Server::new(desktop.clone(), &["Wait", "Shell", "Scrape", "Clipboard"]);
    local.block_on(&runtime, async move {
        let answer = answer_stateless(server, serde_json::from_value(message).unwrap()).await;
        answer.map(|message| serde_json::to_value(message).unwrap())
    })
}
fn call(desktop: &Rc<FakeDesktop>, name: &str, arguments: Value) -> Value {
    ask(desktop, json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":name,"arguments":arguments}})).expect("an answer")
}
fn listed_tools(desktop: &Rc<FakeDesktop>) -> Vec<Value> {
    let answer = ask(desktop, json!({"jsonrpc":"2.0","id":1,"method":"tools/list"})).expect("an answer");
    answer["result"]["tools"].as_array().expect("a tools array").clone()
}

#[test]
fn only_the_requested_tools_are_listed() {
    let desktop = Rc::new(FakeDesktop::default());
    let names: Vec<String> = listed_tools(&desktop).iter().map(|tool| tool["name"].as_str().unwrap().to_string()).collect();
    assert_eq!(names, ["Clipboard", "Scrape", "Shell", "Wait"]);
    let desktop = Rc::new(FakeDesktop::default());
    let server = Server::new(desktop.clone(), &["Wait"]);
    let runtime = tokio::runtime::Builder::new_current_thread().build().unwrap();
    let answer = tokio::task::LocalSet::new().block_on(&runtime, answer_stateless(server, serde_json::from_value(json!({"jsonrpc":"2.0","id":1,"method":"tools/list"})).unwrap()));
    assert_eq!(serde_json::to_value(answer).unwrap()["result"]["tools"].as_array().unwrap().len(), 1);
}

#[test]
fn tools_list_snapshot() {
    let desktop = Rc::new(FakeDesktop::default());
    let listed = serde_json::to_string_pretty(&listed_tools(&desktop)).unwrap() + "\n";
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/snapshots/tools.json");
    if std::env::var_os("UPDATE_SNAPSHOTS").is_some() {
        std::fs::write(path, &listed).unwrap();
    }
    let expected = std::fs::read_to_string(path).unwrap_or_default();
    assert!(listed == expected, "tools/list changed; review it, then rerun with UPDATE_SNAPSHOTS=1.\n{listed}");
}

#[test]
fn initialize_is_answered() {
    let desktop = Rc::new(FakeDesktop::default());
    let answer = ask(&desktop, json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"0"}}})).unwrap();
    assert_eq!(answer["result"]["serverInfo"]["name"], "desktop-mcp");
    assert_eq!(answer["result"]["protocolVersion"], "2025-11-25");
    assert!(answer["result"]["capabilities"]["tools"].is_object());
}

#[test]
fn wait_sleeps_and_reports() {
    let desktop = Rc::new(FakeDesktop::default());
    let answer = call(&desktop, "Wait", json!({"duration": 3}));
    assert_eq!(answer["result"]["content"][0]["text"], "Waited for 3 seconds.");
    assert_eq!(answer["result"]["isError"], false);
    assert_eq!(*desktop.slept.borrow(), [Duration::from_secs(3)]);
}

#[test]
fn negative_duration_does_not_sleep_backwards() {
    let desktop = Rc::new(FakeDesktop::default());
    let answer = call(&desktop, "Wait", json!({"duration": -2}));
    assert_eq!(answer["result"]["content"][0]["text"], "Waited for -2 seconds.");
    assert_eq!(*desktop.slept.borrow(), [Duration::ZERO]);
}

#[test]
fn wait_without_duration_is_an_error_result() {
    let desktop = Rc::new(FakeDesktop::default());
    let answer = call(&desktop, "Wait", json!({}));
    assert_eq!(answer["result"]["isError"], true);
    assert_eq!(answer["result"]["content"][0]["text"], "Error executing tool Wait: Missing required argument 'duration'");
    assert!(desktop.slept.borrow().is_empty());
}

#[test]
fn wait_until_is_refused_for_now() {
    let desktop = Rc::new(FakeDesktop::default());
    let answer = call(&desktop, "Wait", json!({"until": "app_running", "app": "Finder"}));
    assert_eq!(answer["result"]["isError"], true);
    assert_eq!(answer["result"]["content"][0]["text"], "Error executing tool Wait: Wait until 'app_running' is not available in this build.");
}

#[test]
fn unknown_tool_is_a_jsonrpc_error() {
    let desktop = Rc::new(FakeDesktop::default());
    let answer = call(&desktop, "Nope", json!({}));
    assert_eq!(answer["error"]["code"], -32602);
}

#[test]
fn notification_gets_no_answer() {
    let desktop = Rc::new(FakeDesktop::default());
    assert_eq!(ask(&desktop, json!({"jsonrpc":"2.0","method":"notifications/initialized"})), None);
}

fn finished(stdout: &str, status: i32) -> Result<Run, String> {
    Ok(Run { stdout: stdout.into(), stderr: String::new(), status, timed_out: false })
}

#[test]
fn shell_runs_the_command_with_the_default_mode_and_timeout() {
    let desktop = FakeDesktop::answering(finished("hi\n", 0));
    let answer = call(&desktop, "Shell", json!({"command": "echo hi"}));
    assert_eq!(answer["result"]["isError"], false);
    assert_eq!(answer["result"]["content"][0]["text"], "Shell Response: hi\nStatus Code: 0");
    assert_eq!(*desktop.shells.borrow(), [("echo hi".to_string(), ShellMode::Shell, Duration::from_secs(10))]);
}

#[test]
fn osascript_mode_is_labelled_applescript() {
    let desktop = FakeDesktop::answering(finished("3", 0));
    let answer = call(&desktop, "Shell", json!({"command": "return 1 + 2", "mode": "osascript", "timeout": 5}));
    assert_eq!(answer["result"]["content"][0]["text"], "AppleScript Response: 3\nStatus Code: 0");
    assert_eq!(*desktop.shells.borrow(), [("return 1 + 2".to_string(), ShellMode::Osascript, Duration::from_secs(5))]);
}

#[test]
fn a_failing_command_is_still_a_successful_call() {
    let desktop = FakeDesktop::answering(finished("", 2));
    let answer = call(&desktop, "Shell", json!({"command": "false"}));
    assert_eq!(answer["result"]["isError"], false);
    assert_eq!(answer["result"]["content"][0]["text"], "Shell Response: \nStatus Code: 2");
}

#[test]
fn a_negative_timeout_runs_with_none() {
    let desktop = FakeDesktop::answering(Ok(Run { stdout: String::new(), stderr: String::new(), status: -9, timed_out: true }));
    let answer = call(&desktop, "Shell", json!({"command": "sleep 1", "timeout": -3}));
    assert_eq!(desktop.shells.borrow()[0].2, Duration::ZERO);
    assert_eq!(answer["result"]["content"][0]["text"], "Shell Response: Command timed out after -3 seconds\nStatus Code: -1");
}

#[test]
fn shell_needs_a_command() {
    let desktop = Rc::new(FakeDesktop::default());
    let answer = call(&desktop, "Shell", json!({}));
    assert!(answer["error"].is_object() || answer["result"]["isError"] == true, "{answer}");
    assert!(desktop.shells.borrow().is_empty());
}

fn with_clipboard(contents: Clipboard) -> Rc<FakeDesktop> {
    let desktop = FakeDesktop::default();
    *desktop.clipboard.borrow_mut() = contents;
    Rc::new(desktop)
}
fn texts(answer: &Value) -> Vec<String> {
    answer["result"]["content"].as_array().unwrap().iter().filter(|c| c["type"] == "text").map(|c| c["text"].as_str().unwrap().to_string()).collect()
}

#[test]
fn an_empty_clipboard_says_so() {
    let desktop = with_clipboard(Clipboard::default());
    let answer = call(&desktop, "Clipboard", json!({}));
    assert_eq!(texts(&answer), ["The clipboard is empty."]);
    assert_eq!(*desktop.image_limits.borrow(), [1568]);
}

#[test]
fn clipboard_text_is_read() {
    let desktop = with_clipboard(Clipboard { text: Some("안녕 hi".into()), ..Clipboard::default() });
    assert_eq!(texts(&call(&desktop, "Clipboard", json!({"mode": "read"}))), ["안녕 hi"]);
}

#[test]
fn long_clipboard_text_is_truncated() {
    let desktop = with_clipboard(Clipboard { text: Some("a".repeat(100_010)), ..Clipboard::default() });
    assert_eq!(texts(&call(&desktop, "Clipboard", json!({}))), [format!("{}… (truncated, 100010 characters)", "a".repeat(100_000))]);
}

#[test]
fn clipboard_text_and_files_are_reported_together() {
    let desktop = with_clipboard(Clipboard { text: Some("a.txt".into()), files: vec!["/tmp/a.txt".into(), "/tmp/문서 b.txt".into()], image: None });
    assert_eq!(texts(&call(&desktop, "Clipboard", json!({}))), ["a.txt\n\nFiles:\n/tmp/a.txt\n/tmp/문서 b.txt"]);
}

#[test]
fn a_clipboard_image_comes_as_png_with_its_size() {
    let desktop = with_clipboard(Clipboard { image: Some(Image { png: vec![1, 2, 3], width: 1568, height: 523 }), ..Clipboard::default() });
    let answer = call(&desktop, "Clipboard", json!({}));
    assert_eq!(texts(&answer), ["Image (1568×523)."]);
    let image = &answer["result"]["content"][1];
    assert_eq!((image["type"].as_str(), image["mimeType"].as_str(), image["data"].as_str()), (Some("image"), Some("image/png"), Some("AQID")));
    let desktop = with_clipboard(Clipboard { text: Some("caption".into()), image: Some(Image { png: vec![0], width: 4, height: 2 }), ..Clipboard::default() });
    assert_eq!(texts(&call(&desktop, "Clipboard", json!({}))), ["caption"]);
}

#[test]
fn clipboard_text_is_written() {
    let desktop = Rc::new(FakeDesktop::default());
    assert_eq!(texts(&call(&desktop, "Clipboard", json!({"mode": "write", "text": "안녕 hi"}))), ["Copied 5 characters."]);
    assert_eq!(*desktop.written.borrow(), [ClipboardWrite::Text("안녕 hi".into())]);
}

#[test]
fn clipboard_files_are_written_when_they_exist() {
    let directory = tempfile::tempdir().unwrap();
    let (first, second) = (directory.path().join("a.txt"), directory.path().join("문서 b.txt"));
    std::fs::write(&first, "x").unwrap();
    std::fs::write(&second, "x").unwrap();
    let desktop = Rc::new(FakeDesktop::default());
    let paths = [first.to_str().unwrap(), second.to_str().unwrap()];
    assert_eq!(texts(&call(&desktop, "Clipboard", json!({"mode": "write", "paths": paths}))), ["Copied 2 files."]);
    assert_eq!(*desktop.written.borrow(), [ClipboardWrite::Files(vec![first.clone(), second])]);
    assert_eq!(texts(&call(&desktop, "Clipboard", json!({"mode": "write", "paths": [first.to_str().unwrap()], "text": null}))), ["Copied 1 file."]);
}

#[test]
fn a_tilde_path_is_expanded() {
    let desktop = Rc::new(FakeDesktop::default());
    let home = std::env::var("HOME").unwrap();
    let answer = call(&desktop, "Clipboard", json!({"mode": "write", "paths": ["~/definitely-not-here-desktop-mcp"]}));
    assert_eq!(texts(&answer), [format!("Error executing tool Clipboard: No such file: {home}/definitely-not-here-desktop-mcp")]);
}

#[test]
fn clipboard_writes_are_checked() {
    let desktop = Rc::new(FakeDesktop::default());
    for (arguments, message) in [
        (json!({"mode": "write", "paths": ["/nope"]}), "No such file: /nope"),
        (json!({"mode": "write", "text": "a", "paths": ["/tmp"]}), "Pass either text or paths, not both."),
        (json!({"mode": "write"}), "write needs text or paths."),
        (json!({"mode": "write", "paths": []}), "write needs text or paths."),
    ] {
        let answer = call(&desktop, "Clipboard", arguments.clone());
        assert_eq!(answer["result"]["isError"], true, "{arguments}");
        assert_eq!(texts(&answer), [format!("Error executing tool Clipboard: {message}")], "{arguments}");
    }
    assert!(desktop.written.borrow().is_empty());
    let refusing = Rc::new(FakeDesktop { refuse_writes: true, ..FakeDesktop::default() });
    assert_eq!(texts(&call(&refusing, "Clipboard", json!({"mode": "write", "text": "a"}))), ["Error executing tool Clipboard: Could not write to the clipboard."]);
}
