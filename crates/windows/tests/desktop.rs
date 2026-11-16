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
        let server = Server::with_platform(Rc::new(WindowsDesktop::new()), Rc::new(WindowsPlatform::new(Rc::new(WindowsDesktop::new()))), TOOLS);
        let answer = tokio::task::LocalSet::new().block_on(&runtime, answer_stateless(server, serde_json::from_value(message).unwrap()));
        serde_json::to_value(answer.unwrap()).unwrap()
    };
    let listed = ask(json!({"jsonrpc":"2.0","id":1,"method":"tools/list"}));
    let tools = listed["result"]["tools"].as_array().unwrap();
    let names: Vec<&str> = tools.iter().map(|tool| tool["name"].as_str().unwrap()).collect();
    assert_eq!(names, ["App", "Batch", "Click", "Clipboard", "Move", "MultiEdit", "MultiSelect", "Notification", "Scrape", "Screenshot", "Scroll", "Shell", "Shortcut", "Snapshot", "Type", "Wait", "Window"]);
    let shell = tools.iter().find(|tool| tool["name"] == "Shell").unwrap();
    assert!(shell["description"].as_str().unwrap().starts_with("Execute commands on Windows in PowerShell"));
    assert_eq!(shell["inputSchema"]["properties"]["mode"]["enum"], json!(["powershell"]));
    assert_eq!(tools.iter().find(|tool| tool["name"] == "Notification").unwrap()["inputSchema"]["$defs"]["NotificationSound"]["enum"], json!(["Default", "IM", "Mail", "Reminder", "SMS"]));

    // osascript is refused on Windows.
    let called = ask(json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Shell","arguments":{"command":"x","mode":"osascript"}}}));
    assert!(called["result"]["content"][0]["text"].as_str().unwrap().contains("osascript is not available on Windows"), "{called}");
}

#[test]
fn dropfiles_and_dib_wrapping() {
    use desktop_mcp_windows::clipboard::{bmp_file, dropfiles};
    // DROPFILES: 20-byte header (offset 20, wide), then NUL-separated UTF-16 paths and a final NUL.
    let payload = dropfiles(&[std::path::PathBuf::from(r"C:\a.txt"), std::path::PathBuf::from(r"C:\한")]);
    assert_eq!(&payload[..4], &20u32.to_le_bytes());
    assert_eq!(&payload[16..20], &1u32.to_le_bytes());
    let wide: Vec<u16> = payload[20..].chunks(2).map(|pair| u16::from_le_bytes([pair[0], pair[1]])).collect();
    assert_eq!(String::from_utf16(&wide).unwrap(), "C:\\a.txt\0C:\\한\0\0");
    // A 32-bit BI_RGB DIB (40-byte header, no palette): the pixels start right after the 14-byte file header and it.
    let mut dib = vec![0u8; 40 + 4];
    dib[..4].copy_from_slice(&40u32.to_le_bytes());
    dib[14..16].copy_from_slice(&32u16.to_le_bytes());
    let bmp = bmp_file(&dib).unwrap();
    assert_eq!(&bmp[..2], b"BM");
    assert_eq!(&bmp[10..14], &54u32.to_le_bytes());
    assert_eq!(bmp.len(), 14 + dib.len());
}

#[test]
fn snapshot_reads_the_live_desktop_with_windows_wording() {
    let _ = unsafe { windows::Win32::UI::HiDpi::SetProcessDpiAwarenessContext(windows::Win32::UI::HiDpi::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    let server = Server::with_platform(Rc::new(WindowsDesktop::new()), Rc::new(WindowsPlatform::new(Rc::new(WindowsDesktop::new()))), TOOLS);
    let message = json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Snapshot","arguments":{"use_vision": false}}});
    let answer = tokio::task::LocalSet::new().block_on(&runtime, answer_stateless(server, serde_json::from_value(message).unwrap()));
    let answer = serde_json::to_value(answer.unwrap()).unwrap();
    let text = answer["result"]["content"][0]["text"].as_str().unwrap();
    assert!(text.contains("Focused Window:") && text.contains("Open Applications:") && text.contains("physical pixels"), "{text}");
    assert!(!text.contains("System Settings") && !text.contains("Screen Recording"), "{text}");
    let listed = serde_json::to_value(tokio::task::LocalSet::new().block_on(&runtime, answer_stateless(Server::with_platform(Rc::new(WindowsDesktop::new()), Rc::new(WindowsPlatform::new(Rc::new(WindowsDesktop::new()))), TOOLS), serde_json::from_value(json!({"jsonrpc":"2.0","id":1,"method":"tools/list"})).unwrap())).unwrap()).unwrap();
    let wait = listed["result"]["tools"].as_array().unwrap().iter().find(|tool| tool["name"] == "Wait").unwrap();
    assert!(wait["description"].as_str().unwrap().contains("UI Automation control type"), "{wait}");
}

#[test]
fn shortcut_and_app_speak_windows() {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    let server = Server::with_platform(Rc::new(WindowsDesktop::new()), Rc::new(WindowsPlatform::new(Rc::new(WindowsDesktop::new()))), TOOLS);
    let listed = tokio::task::LocalSet::new().block_on(&runtime, answer_stateless(server, serde_json::from_value(json!({"jsonrpc":"2.0","id":1,"method":"tools/list"})).unwrap()));
    let listed = serde_json::to_value(listed.unwrap()).unwrap();
    let description = |name: &str| listed["result"]["tools"].as_array().unwrap().iter().find(|tool| tool["name"] == name).unwrap()["description"].as_str().unwrap().to_string();
    assert!(description("Shortcut").contains("win+r") && !description("Shortcut").contains("command"), "{}", description("Shortcut"));
    assert!(description("App").contains("Start menu"), "{}", description("App"));
}

#[test]
fn multi_tools_refuse_bad_locations_before_any_input() {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    let call = |name: &str, arguments: serde_json::Value| {
        let server = Server::with_platform(Rc::new(WindowsDesktop::new()), Rc::new(WindowsPlatform::new(Rc::new(WindowsDesktop::new()))), TOOLS);
        let message = json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":name,"arguments":arguments}});
        let answer = tokio::task::LocalSet::new().block_on(&runtime, answer_stateless(server, serde_json::from_value(message).unwrap()));
        serde_json::to_value(answer.unwrap()).unwrap()["result"]["content"][0]["text"].as_str().unwrap().to_string()
    };
    assert_eq!(call("MultiSelect", json!({"locs": [[1, 2], [1]]})), "Error executing tool MultiSelect: each location must be [x, y]");
    assert_eq!(call("MultiEdit", json!({"locs": [[1, 2]]})), "Error executing tool MultiEdit: each entry must be [x, y, text]");
    assert_eq!(call("MultiSelect", json!({"locs": []})), "Error executing tool MultiSelect: locs is empty");
}
