//! The Spotlight tool's platform-independent parts: words, merging, formatting, previews.
use desktop_mcp_core::spotlight::{format, merge, preview, Item};
use std::time::{Duration, UNIX_EPOCH};

fn item(path: &str, modified: Option<i64>, size: Option<u64>, text: Option<&str>) -> Item {
    let modified = modified.map(|seconds| if seconds >= 0 { UNIX_EPOCH + Duration::from_secs(seconds as u64) } else { UNIX_EPOCH - Duration::from_secs((-seconds) as u64) });
    Item { path: path.into(), kind: "Plain Text".into(), modified, size, text: text.map(Into::into) }
}

#[test]
fn name_matches_come_first() {
    let first = vec![item("/name-new", Some(20), Some(10), None), item("/name-old", Some(10), Some(10), None)];
    let second = vec![item("/content-newest", Some(300), Some(10), None), item("/content-newer", Some(200), Some(10), None), item("/content-old", Some(100), Some(10), None)];
    let (items, total) = merge(first, 2, second, 3, 4);
    assert_eq!(items.iter().map(|i| i.path.as_str()).collect::<Vec<_>>(), ["/name-new", "/name-old", "/content-newest", "/content-newer"]);
    assert_eq!(total, 5);
}

#[test]
fn format_keeps_the_given_order_and_counts_the_rest() {
    let items = [item("/a.txt", Some(1_700_000_000), Some(10), None), item("/b", Some(1_800_000_000), None, None), item("/c.txt", Some(1_750_000_000), Some(2048), None)];
    assert_eq!(
        format(&items, 4, false, false),
        "/a.txt\tPlain Text\t2023-11-14T22:13:20Z\t10\n/b\tPlain Text\t2027-01-15T08:00:00Z\t-\n/c.txt\tPlain Text\t2025-06-15T15:06:40Z\t2048\n1 more not shown"
    );
}

#[test]
fn the_preview_is_the_start_of_a_text_file() {
    let directory = tempfile::tempdir().unwrap();
    let file = directory.path().join("a.txt");
    let mut data = b"first\nsecond\r\n".to_vec();
    data.extend_from_slice(&[0xFF, 0xFE]);
    data.extend_from_slice("x".repeat(10_000).as_bytes());
    std::fs::write(&file, data).unwrap();
    let text = preview(&file).unwrap();
    assert_eq!(text.chars().count(), 300);
    assert!(text.starts_with("first second  \u{FFFD}\u{FFFD}xxx"), "{text}");
    std::fs::write(&file, "  \n\n ").unwrap();
    assert_eq!(preview(&file), None);
    assert_eq!(preview(&directory.path().join("missing")), None);
}
