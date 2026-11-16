//! The MCP server, driven message by message with a fake desktop.
mod support;
use support::FakeDesktop;
use desktop_mcp_core::mcp::{answer_stateless, Server};
use desktop_mcp_core::shell::{Run, ShellMode};
use desktop_mcp_core::clipboard::Clipboard;
use desktop_mcp_core::open::{Application, Target};
use desktop_mcp_core::shortcuts::ShortcutRun;
use desktop_mcp_core::spotlight::{Found, Item as FoundItem, Kind, Query};
use desktop_mcp_core::permissions::{format as permissions_report, Automation, PermissionState};
use desktop_mcp_core::wait::RunningApp;
use serde_json::{json, Value};
use std::cell::RefCell;
use std::rc::Rc;
use std::time::Duration;

fn ask(desktop: &Rc<FakeDesktop>, message: Value) -> Option<Value> {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    let local = tokio::task::LocalSet::new();
    let server = Server::new(desktop.clone(), &["Wait", "Shell", "Scrape", "Clipboard", "Open", "Notification", "RunShortcut", "Spotlight", "Permissions", "Click", "Type", "Scroll", "Move", "Shortcut", "App", "Window", "Menu"]);
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
    assert_eq!(names, ["App", "Click", "Clipboard", "Menu", "Move", "Notification", "Open", "Permissions", "RunShortcut", "Scrape", "Scroll", "Shell", "Shortcut", "Spotlight", "Type", "Wait", "Window"]);
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

fn preview() -> RunningApp {
    RunningApp { pid: 42, name: Some("Preview".into()), bundle_id: Some("com.apple.Preview".into()) }
}
fn looking(looks: Vec<Vec<RunningApp>>) -> Rc<FakeDesktop> {
    Rc::new(FakeDesktop { looks: RefCell::new(looks), ..FakeDesktop::default() })
}

#[test]
fn an_app_that_starts_is_waited_for_with_backoff() {
    let desktop = looking(vec![vec![], vec![], vec![], vec![], vec![preview()]]);
    assert_eq!(texts(&call(&desktop, "Wait", json!({"until": "app_running", "app": "preview", "duration": 60}))), ["App preview is running."]);
    assert_eq!(desktop.looked.get(), 5);
    let millis: Vec<u128> = desktop.slept.borrow().iter().map(Duration::as_millis).collect();
    assert_eq!(millis, [100, 200, 400, 800], "the duration is ignored with until");
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

fn with_clipboard(contents: Clipboard) -> Rc<FakeDesktop> {
    let desktop = FakeDesktop::default();
    *desktop.clipboard.borrow_mut() = contents;
    Rc::new(desktop)
}
fn texts(answer: &Value) -> Vec<String> {
    answer["result"]["content"].as_array().unwrap().iter().filter(|c| c["type"] == "text").map(|c| c["text"].as_str().unwrap().to_string()).collect()
}

#[test]
fn clipboard_text_is_read() {
    let desktop = with_clipboard(Clipboard { text: Some("안녕 hi".into()), ..Clipboard::default() });
    assert_eq!(texts(&call(&desktop, "Clipboard", json!({"mode": "read"}))), ["안녕 hi"]);
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

fn text_editor() -> Rc<FakeDesktop> {
    Rc::new(FakeDesktop { applications: vec![("TextEdit", Application { path: "/System/Applications/TextEdit.app".into(), name: "텍스트 편집기".into() })], ..FakeDesktop::default() })
}

#[test]
fn a_file_is_opened_with_its_default_application() {
    let directory = tempfile::tempdir().unwrap();
    let file = directory.path().join("문서 a b.txt");
    std::fs::write(&file, "").unwrap();
    let desktop = text_editor();
    assert_eq!(texts(&call(&desktop, "Open", json!({"target": file}))), [format!("Opened {}.", file.display())]);
    assert_eq!(*desktop.opened.borrow(), [(Target::File(file), None)]);
}

#[test]
fn open_refusals() {
    let desktop = text_editor();
    for (arguments, message) in [
        (json!({"target": "  "}), "target is required."),
        (json!({"target": "/nope/x"}), "No such file: /nope/x"),
        (json!({"target": "https://example.com", "mode": "reveal"}), "reveal needs an existing file or folder path."),
        (json!({"target": "https://example.com", "app": "NoSuchApp"}), "Application 'NoSuchApp' not found."),
    ] {
        let answer = call(&desktop, "Open", arguments.clone());
        assert_eq!(answer["result"]["isError"], true, "{arguments}");
        assert_eq!(texts(&answer), [format!("Error executing tool Open: {message}")], "{arguments}");
    }
    assert!(desktop.opened.borrow().is_empty() && desktop.revealed.borrow().is_empty());
}

#[test]
fn a_notification_is_sent_with_a_default_title() {
    let desktop = Rc::new(FakeDesktop::default());
    assert_eq!(texts(&call(&desktop, "Notification", json!({"message": "Build \"done\""}))), ["Notification sent: [Notification] Build \"done\""]);
    let answer = call(&desktop, "Notification", json!({"message": "m", "title": "T", "subtitle": "s", "sound": "Glass"}));
    assert_eq!(texts(&answer), ["Notification sent: [T] m"]);
    assert_eq!(
        *desktop.notified.borrow(),
        [("Build \"done\"".into(), "Notification".into(), None, None), ("m".into(), "T".into(), Some("s".into()), Some("Glass".into()))]
    );
}

fn shortcuts(names: &'static str, run: Option<ShortcutRun>) -> Rc<FakeDesktop> {
    Rc::new(FakeDesktop { shortcut_names: names, shortcut_run: run, ..FakeDesktop::default() })
}
fn finished_shortcut(status: i32, output: &str, stderr: &str) -> Option<ShortcutRun> {
    Some(ShortcutRun { status, timed_out: false, output: output.into(), stderr: stderr.into() })
}

#[test]
fn a_shortcut_runs_with_its_input_and_reports_its_output() {
    let desktop = shortcuts("Resize\nOther\n", finished_shortcut(0, "<out>", ""));
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"name": "Resize", "input": "hello"}))), ["Shortcut 'Resize' finished with status 0.\n<out>"]);
    assert_eq!(*desktop.shortcut_calls.borrow(), ["list None 60", "run Resize Some(\"hello\") 60"]);
}

#[test]
fn an_unknown_shortcut_runs_nothing_and_suggests_close_names() {
    let desktop = shortcuts("Resize Image\nMake GIF\n", None);
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"name": "resize"}))), ["Error executing tool RunShortcut: No shortcut named 'resize'. Close matches: Resize Image"]);
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"name": "zzz"}))), ["Error executing tool RunShortcut: No shortcut named 'zzz'."]);
    assert!(desktop.shortcut_calls.borrow().iter().all(|call| call.starts_with("list")));
}

fn found(paths: &[&str], total: usize, stopped: bool) -> Result<Found, String> {
    let items = paths.iter().map(|path| FoundItem { path: path.to_string(), kind: "Folder".into(), modified: None, size: None, text: None }).collect();
    Ok(Found { items, total, stopped })
}
fn spotlight(answers: Vec<Result<Found, String>>) -> Rc<FakeDesktop> {
    Rc::new(FakeDesktop { found: RefCell::new(answers), ..FakeDesktop::default() })
}

#[test]
fn spotlight_lists_name_matches_then_content_only_matches() {
    let desktop = spotlight(vec![found(&["/n1", "/n2"], 2, false), found(&["/c1"], 4, false)]);
    let answer = call(&desktop, "Spotlight", json!({"query": " tax  2025 ", "limit": 3, "scope": ["~/Documents"], "kind": "pdf", "content": true}));
    assert_eq!(texts(&answer), ["/n1\tFolder\t-\t-\n/n2\tFolder\t-\t-\n/c1\tFolder\t-\t-\n3 more not shown"]);
    let searches = desktop.searches.borrow();
    let words = vec!["tax".to_string(), "2025".to_string()];
    assert_eq!((&searches[0].query, searches[0].shown.as_str(), searches[0].limit, searches[0].kind, searches[0].content), (&Query::Names(words.clone()), " tax  2025 ", 3, Kind::Pdf, true));
    assert_eq!(searches[0].scope, ["~/Documents"]);
    assert_eq!(searches[0].timeout, Duration::from_secs(10));
    assert_eq!((&searches[1].query, searches[1].limit), (&Query::ContentOnly(words), 1));
    assert!(searches[1].timeout <= Duration::from_secs(10));
}

const NEEDS: &str = "Needs Accessibility: App (resize, move), Click, Type, Scroll, Move, Shortcut, Snapshot, Desktop, Window, Menu, Wait (until)\nNeeds Screen Recording: Snapshot (use_vision), OCR";

#[test]
fn the_permission_report() {
    let state = |accessibility, screen_recording, automation| PermissionState { accessibility, screen_recording, automation };
    assert_eq!(permissions_report(state(true, true, Automation::Granted)), format!("Accessibility: granted\nScreen Recording: granted\nAutomation (System Events): granted\n{NEEDS}"));
    assert_eq!(permissions_report(state(true, false, Automation::NotDetermined)), format!("Accessibility: granted\nScreen Recording: not granted\nAutomation (System Events): not determined\n{NEEDS}"));
    assert_eq!(permissions_report(state(false, true, Automation::Denied)), format!("Accessibility: not granted\nScreen Recording: granted\nAutomation (System Events): not granted\n{NEEDS}"));
    let seen = state(false, false, Automation::NotDetermined);
    let desktop = Rc::new(FakeDesktop { permissions: Some(seen), ..FakeDesktop::default() });
    assert_eq!(texts(&call(&desktop, "Permissions", json!({}))), [permissions_report(seen)]);
}

#[test]
fn menu_requests_reach_the_platform_after_their_checks() {
    use desktop_mcp_core::menu::Request;
    let desktop = Rc::new(FakeDesktop::default());
    assert_eq!(texts(&call(&desktop, "Menu", json!({}))), ["menu"]);
    assert_eq!(texts(&call(&desktop, "Menu", json!({"mode": "select", "app": "Safari", "path": ["File", "New"]}))), ["menu"]);
    assert_eq!(texts(&call(&desktop, "Menu", json!({"mode": "select"}))), ["Error executing tool Menu: path required for select mode."]);
    assert_eq!(*desktop.menu_requests.borrow(), [(None, Request::List { path: vec![], depth: 2 }), (Some("Safari".into()), Request::Select { path: vec!["File".into(), "New".into()] })]);
    let untrusted = Rc::new(FakeDesktop { untrusted: true, ..FakeDesktop::default() });
    assert_eq!(texts(&call(&untrusted, "Menu", json!({}))), [format!("Error executing tool Menu: {}", desktop_mcp_core::permissions::ACCESSIBILITY_MESSAGE)]);
    assert!(untrusted.menu_requests.borrow().is_empty());
}
