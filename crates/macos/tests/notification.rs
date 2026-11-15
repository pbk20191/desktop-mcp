//! The AppleScript behind a notification. (None is posted: a banner would appear on the desktop.)
use desktop_mcp_macos::notification::script;

#[test]
fn the_script_quotes_every_part() {
    assert_eq!(script("m", "T", None, None), r#"display notification "m" with title "T""#);
    assert_eq!(script("say \"hi\"\\", "제목", Some("sub\nline"), Some("Glass")), r#"display notification "say \"hi\"\\" with title "제목" subtitle "sub\nline" sound name "Glass""#);
}
