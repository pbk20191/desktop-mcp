//! The MCP server, driven message by message with a fake desktop.
mod support;
use support::FakeDesktop;
use desktop_mcp_core::mcp::{answer_stateless, Server};
use desktop_mcp_core::shell::{Run, ShellMode};
use desktop_mcp_core::clipboard::{Clipboard, ClipboardWrite, Image};
use desktop_mcp_core::open::{Application, Target};
use desktop_mcp_core::shortcuts::ShortcutRun;
use desktop_mcp_core::spotlight::{Found, Item as FoundItem, Kind, Query};
use desktop_mcp_core::permissions::{format as permissions_report, Automation, PermissionState};
use desktop_mcp_core::wait::RunningApp;
use std::path::PathBuf;
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
fn an_app_that_quits_is_matched_by_bundle_id() {
    let desktop = looking(vec![vec![preview()], vec![preview()], vec![]]);
    assert_eq!(texts(&call(&desktop, "Wait", json!({"until": "app_quit", "app": "com.apple.Preview"}))), ["App com.apple.Preview quit."]);
    assert_eq!(desktop.looked.get(), 3);
}

#[test]
fn an_app_not_running_at_the_first_look_did_not_quit() {
    let desktop = looking(vec![vec![preview()]]);
    assert_eq!(texts(&call(&desktop, "Wait", json!({"until": "app_quit", "app": "Preveiw"}))), ["App Preveiw is not running."]);
}

#[test]
fn app_waits_time_out() {
    let desktop = looking(vec![vec![]]);
    assert_eq!(texts(&call(&desktop, "Wait", json!({"until": "app_running", "app": "Preview", "timeout": 0}))), ["Error executing tool Wait: Timed out after 0s waiting for app Preview to run."]);
    let desktop = looking(vec![vec![preview()]]);
    assert_eq!(texts(&call(&desktop, "Wait", json!({"until": "app_quit", "app": "Preview", "timeout": -5}))), ["Error executing tool Wait: Timed out after 0s waiting for app Preview to quit."]);
    assert_eq!(desktop.looked.get(), 1, "a zero timeout still looks once");
}

#[test]
fn app_waits_need_an_app() {
    let desktop = looking(vec![vec![preview()]]);
    assert_eq!(texts(&call(&desktop, "Wait", json!({"until": "app_running"}))), ["Error executing tool Wait: app required for app_running."]);
    assert_eq!(texts(&call(&desktop, "Wait", json!({"until": "app_quit", "app": "  "}))), ["Error executing tool Wait: app required for app_quit."]);
    assert_eq!(desktop.looked.get(), 0);
}

#[test]
fn apps_match_by_bundle_id_or_name() {
    use desktop_mcp_core::wait::matches;
    assert!(matches(Some("Preview"), Some("com.apple.Preview"), " preview "));
    assert!(matches(None, Some("com.apple.Preview"), "com.apple.Preview"));
    assert!(!matches(Some("Preview"), Some("com.apple.Preview"), "com.apple.preview"), "bundle ids match exactly");
    assert!(!matches(Some("Preview"), None, "Prev"));
    assert!(!matches(Some("Preview"), None, "  "));
    assert!(!matches(None, None, "Preview"));
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
fn a_url_is_opened_as_given() {
    let desktop = text_editor();
    assert_eq!(texts(&call(&desktop, "Open", json!({"target": "  https://example.com/a?b=1 "}))), ["Opened https://example.com/a?b=1."]);
    assert_eq!(*desktop.opened.borrow(), [(Target::Url("https://example.com/a?b=1".into()), None)]);
}

#[test]
fn an_application_can_be_named() {
    let directory = tempfile::tempdir().unwrap();
    let file = directory.path().join("a.txt");
    std::fs::write(&file, "").unwrap();
    let desktop = text_editor();
    assert_eq!(texts(&call(&desktop, "Open", json!({"target": file, "app": "TextEdit"}))), [format!("Opened {} with 텍스트 편집기.", file.display())]);
    assert_eq!(*desktop.opened.borrow(), [(Target::File(file), Some(PathBuf::from("/System/Applications/TextEdit.app")))]);
}

#[test]
fn a_file_is_revealed() {
    let directory = tempfile::tempdir().unwrap();
    let desktop = text_editor();
    let answer = call(&desktop, "Open", json!({"target": directory.path(), "mode": "reveal", "app": "TextEdit"}));
    assert_eq!(texts(&answer), [format!("Revealed {} in Finder.", directory.path().display())]);
    assert_eq!(*desktop.revealed.borrow(), [directory.path().to_path_buf()]);
    assert!(desktop.opened.borrow().is_empty());
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

#[test]
fn a_failed_notification_is_reported_not_raised() {
    let desktop = Rc::new(FakeDesktop { notify_failure: Some("execution error: nope"), ..FakeDesktop::default() });
    let answer = call(&desktop, "Notification", json!({"message": "m"}));
    assert_eq!(answer["result"]["isError"], false);
    assert_eq!(texts(&answer), ["Failed to send notification: execution error: nope"]);
}

#[test]
fn unknown_sounds_are_refused_by_the_schema() {
    let desktop = Rc::new(FakeDesktop::default());
    let answer = call(&desktop, "Notification", json!({"message": "m", "sound": "Trumpet"}));
    assert_eq!(answer["result"]["isError"], true, "{answer}");
    assert!(texts(&answer)[0].contains("unknown variant `Trumpet`"), "{answer}");
    assert!(desktop.notified.borrow().is_empty());
}

fn shortcuts(names: &'static str, run: Option<ShortcutRun>) -> Rc<FakeDesktop> {
    Rc::new(FakeDesktop { shortcut_names: names, shortcut_run: run, ..FakeDesktop::default() })
}
fn finished_shortcut(status: i32, output: &str, stderr: &str) -> Option<ShortcutRun> {
    Some(ShortcutRun { status, timed_out: false, output: output.into(), stderr: stderr.into() })
}

#[test]
fn shortcuts_are_listed_one_per_line() {
    let desktop = shortcuts("A\n\n B \n", None);
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"mode": "list"}))), ["A\nB"]);
    call(&desktop, "RunShortcut", json!({"mode": "list", "folder": "Work"}));
    assert_eq!(*desktop.shortcut_calls.borrow(), ["list None 60", "list Some(\"Work\") 60"]);
}

#[test]
fn a_failed_list_is_an_error() {
    let desktop = Rc::new(FakeDesktop { list_failure: Some(Run { stdout: String::new(), stderr: " boom\n".into(), status: 1, timed_out: false }), ..FakeDesktop::default() });
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"mode": "list"}))), ["Error executing tool RunShortcut: shortcuts list failed: boom"]);
    let desktop = Rc::new(FakeDesktop { list_failure: Some(Run { stdout: String::new(), stderr: String::new(), status: -9, timed_out: true }), ..FakeDesktop::default() });
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"mode": "list"}))), ["Error executing tool RunShortcut: shortcuts list timed out."]);
}

#[test]
fn a_shortcut_runs_with_its_input_and_reports_its_output() {
    let desktop = shortcuts("Resize\nOther\n", finished_shortcut(0, "<out>", ""));
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"name": "Resize", "input": "hello"}))), ["Shortcut 'Resize' finished with status 0.\n<out>"]);
    assert_eq!(*desktop.shortcut_calls.borrow(), ["list None 60", "run Resize Some(\"hello\") 60"]);
}

#[test]
fn a_shortcut_without_output_says_so() {
    let desktop = shortcuts("Resize\n", None);
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"name": "Resize"}))), ["Shortcut 'Resize' finished with status 0.\n(no output)"]);
    let desktop = shortcuts("Resize\n", finished_shortcut(1, "", " Error: nope\n"));
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"name": "Resize"}))), ["Shortcut 'Resize' finished with status 1.\n(no output)\nstderr:\nError: nope"]);
    let desktop = shortcuts("Resize\n", finished_shortcut(1, "partial", "Error"));
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"name": "Resize"}))), ["Shortcut 'Resize' finished with status 1.\npartial"]);
}

#[test]
fn an_unknown_shortcut_runs_nothing_and_suggests_close_names() {
    let desktop = shortcuts("Resize Image\nMake GIF\n", None);
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"name": "resize"}))), ["Error executing tool RunShortcut: No shortcut named 'resize'. Close matches: Resize Image"]);
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"name": "zzz"}))), ["Error executing tool RunShortcut: No shortcut named 'zzz'."]);
    assert!(desktop.shortcut_calls.borrow().iter().all(|call| call.starts_with("list")));
}

#[test]
fn running_needs_a_name() {
    let desktop = shortcuts("A\n", None);
    for arguments in [json!({}), json!({"name": ""})] {
        assert_eq!(texts(&call(&desktop, "RunShortcut", arguments)), ["Error executing tool RunShortcut: name is required for run."]);
    }
    assert!(desktop.shortcut_calls.borrow().is_empty());
}

#[test]
fn a_shortcut_that_outlives_its_timeout_is_left_running() {
    let desktop = shortcuts("Resize\n", Some(ShortcutRun { status: -9, timed_out: true, output: String::new(), stderr: String::new() }));
    assert_eq!(texts(&call(&desktop, "RunShortcut", json!({"name": "Resize", "timeout": 5}))), ["Stopped waiting for shortcut 'Resize' after 5s; it may still be running."]);
    call(&desktop, "RunShortcut", json!({"name": "Resize", "timeout": 100000}));
    assert_eq!(desktop.shortcut_calls.borrow().last().unwrap(), "run Resize None 240");
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

#[test]
fn a_stopped_name_search_skips_the_content_search() {
    let desktop = spotlight(vec![found(&["/n1"], 9, true)]);
    let answer = call(&desktop, "Spotlight", json!({"query": "a"}));
    assert_eq!(texts(&answer), ["/n1\tFolder\t-\t-\n8 more not shown\n(search stopped after 10 s; results may be incomplete)"]);
    assert_eq!(desktop.searches.borrow().len(), 1);
}

#[test]
fn the_content_search_gets_what_is_left_of_the_deadline() {
    let desktop = Rc::new(FakeDesktop { found: RefCell::new(vec![found(&[], 0, false), found(&[], 0, false)]), search_takes: Duration::from_millis(300), ..FakeDesktop::default() });
    call(&desktop, "Spotlight", json!({"query": "a"}));
    let left = desktop.searches.borrow()[1].timeout;
    assert!(left <= Duration::from_millis(9_700) && left > Duration::from_millis(9_000), "{left:?}");
}

#[test]
fn a_raw_query_is_passed_through_trimmed() {
    let desktop = spotlight(vec![found(&["/r"], 1, false)]);
    let answer = call(&desktop, "Spotlight", json!({"query": " kMDItemFSName == \"x\" ", "raw": true, "limit": 1000}));
    assert_eq!(texts(&answer), ["/r\tFolder\t-\t-"]);
    let searches = desktop.searches.borrow();
    assert_eq!((&searches[0].query, searches[0].shown.as_str(), searches[0].limit, searches[0].kind), (&Query::Raw("kMDItemFSName == \"x\"".into()), "kMDItemFSName == \"x\"", 200, Kind::Any));
}

#[test]
fn spotlight_refusals() {
    let desktop = spotlight(vec![Err("Invalid Spotlight query: kMDItem ==".into())]);
    for arguments in [json!({"query": "  "}), json!({"query": " ", "raw": true})] {
        assert_eq!(texts(&call(&desktop, "Spotlight", arguments)), ["Error executing tool Spotlight: query is required."]);
    }
    assert!(desktop.searches.borrow().is_empty());
    assert_eq!(texts(&call(&desktop, "Spotlight", json!({"query": "kMDItem ==", "raw": true}))), ["Error executing tool Spotlight: Invalid Spotlight query: kMDItem =="]);
    let answer = call(&desktop, "Spotlight", json!({"query": "a", "limit": 0}));
    assert_eq!(desktop.searches.borrow().last().unwrap().limit, 1);
    let _ = answer;
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
fn the_accessibility_message_says_how_to_grant_it() {
    assert_eq!(
        desktop_mcp_core::permissions::ACCESSIBILITY_MESSAGE,
        "Accessibility is not granted to desktop-mcp. Grant it in System Settings > Privacy & Security > Accessibility, then restart desktop-mcp."
    );
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
