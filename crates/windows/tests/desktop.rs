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

#[test]
fn a_deeply_nested_page_parses_without_overflowing() {
    let page = "<div>".repeat(20_000) + "deep";
    let html = desktop_mcp_windows::html::parse(page.as_bytes(), None).expect("parsed");
    assert_eq!(html.name, "html");
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
