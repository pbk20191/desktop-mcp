//! The headless Desktop's pure parts and the Windows tool text.
#![cfg(windows)]
use desktop_mcp_core::mcp::{answer_stateless, Server};
use desktop_mcp_windows::desktop::WindowsDesktop;
use desktop_mcp_windows::notification::toast_xml;
use desktop_mcp_windows::platform::{WindowsPlatform, TOOLS};
use serde_json::json;
use std::rc::Rc;

#[test]
fn toast_xml_escapes_and_carries_subtitle_and_sound() {
    let xml = toast_xml("a < b & \"c\"", "Title", Some("Sub"), Some("Mail"));
    assert!(xml.contains("<text>Title</text><text>Sub</text><text>a &lt; b &amp; &quot;c&quot;</text>"), "{xml}");
    assert!(xml.contains(r#"<audio src="ms-winsoundevent:Notification.Mail"/>"#), "{xml}");
    assert!(!toast_xml("m", "T", None, None).contains("<audio"));
}

#[test]
fn windows_text_is_listed_and_windows_sounds_are_accepted() {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    let ask = |message: serde_json::Value| {
        let server = Server::with_platform(Rc::new(WindowsDesktop::new()), Rc::new(WindowsPlatform), TOOLS);
        let answer = tokio::task::LocalSet::new().block_on(&runtime, answer_stateless(server, serde_json::from_value(message).unwrap()));
        serde_json::to_value(answer.unwrap()).unwrap()
    };
    let listed = ask(json!({"jsonrpc":"2.0","id":1,"method":"tools/list"}));
    let tools = listed["result"]["tools"].as_array().unwrap();
    let names: Vec<&str> = tools.iter().map(|tool| tool["name"].as_str().unwrap()).collect();
    assert_eq!(names, ["Clipboard", "Notification", "Scrape", "Shell", "Wait"]);
    let shell = &tools[3];
    assert!(shell["description"].as_str().unwrap().starts_with("Execute commands on Windows in PowerShell"));
    assert_eq!(shell["inputSchema"]["properties"]["mode"]["enum"], json!(["powershell"]));
    assert_eq!(tools[1]["inputSchema"]["$defs"]["NotificationSound"]["enum"], json!(["Default", "IM", "Mail", "Reminder", "SMS"]));

    // osascript is refused on Windows.
    let called = ask(json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Shell","arguments":{"command":"x","mode":"osascript"}}}));
    assert!(called["result"]["content"][0]["text"].as_str().unwrap().contains("osascript is not available on Windows"), "{called}");
}
