//! The Spotlight tool's platform-independent parts: words, merging, formatting, previews.
use desktop_mcp_core::spotlight::{format, merge, preview, words, Item};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

fn item(path: &str, modified: Option<i64>, size: Option<u64>, text: Option<&str>) -> Item {
    let modified = modified.map(|seconds| if seconds >= 0 { UNIX_EPOCH + Duration::from_secs(seconds as u64) } else { UNIX_EPOCH - Duration::from_secs((-seconds) as u64) });
    Item { path: path.into(), kind: "Plain Text".into(), modified, size, text: text.map(Into::into) }
}

#[test]
fn words_are_split_on_whitespace_and_required() {
    assert_eq!(words("tax  2025\n"), Ok(vec!["tax".to_string(), "2025".to_string()]));
    for query in ["", "   \n\t"] {
        assert_eq!(words(query), Err("query is required.".to_string()));
    }
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
fn rows_past_the_limit_are_never_shown() {
    let first = vec![item("/name-1", Some(2), Some(10), None), item("/name-2", Some(1), Some(10), None)];
    let (items, total) = merge(first.clone(), 7, vec![], 40, 2);
    assert_eq!((items.clone(), total), (first.clone(), 47));
    assert_eq!(format(&items, total, false, false).lines().last(), Some("45 more not shown"));
    assert_eq!(merge(first.clone(), 2, vec![item("/c", Some(9), Some(10), None)], 1, 2).0, first);
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
fn a_missing_date_prints_a_dash() {
    let items = [item("/undated", None, Some(10), None), item("/dated", Some(0), Some(10), None)];
    assert_eq!(format(&items, 2, false, false), "/undated\tPlain Text\t-\t10\n/dated\tPlain Text\t1970-01-01T00:00:00Z\t10");
}

#[test]
fn a_stopped_search_says_so() {
    assert_eq!(format(&[item("/a", Some(0), Some(10), None)], 5, false, true), "/a\tPlain Text\t1970-01-01T00:00:00Z\t10\n4 more not shown\n(search stopped after 10 s; results may be incomplete)");
    assert_eq!(format(&[], 0, false, true), "No matches.\n(search stopped after 10 s; results may be incomplete)");
    assert_eq!(format(&[], 0, false, false), "No matches.");
}

#[test]
fn content_adds_the_preview_line() {
    let items = [item("/a.txt", Some(0), Some(10), Some("hello there")), item("/b.png", Some(-1), Some(10), None)];
    assert_eq!(format(&items, 2, true, false), "/a.txt\tPlain Text\t1970-01-01T00:00:00Z\t10\n  hello there\n/b.png\tPlain Text\t1969-12-31T23:59:59Z\t10");
    assert_eq!(format(&[item("/a.txt", Some(0), Some(10), Some("hello"))], 1, false, false), "/a.txt\tPlain Text\t1970-01-01T00:00:00Z\t10");
}

#[test]
fn dates_before_the_epoch_and_leap_days_format() {
    assert_eq!(format(&[item("/x", Some(951_782_400), Some(1), None)], 1, false, false), "/x\tPlain Text\t2000-02-29T00:00:00Z\t1");
    assert_eq!(format(&[item("/x", Some(-86_401), Some(1), None)], 1, false, false), "/x\tPlain Text\t1969-12-30T23:59:59Z\t1");
    let fraction = Item { modified: Some(UNIX_EPOCH + Duration::from_millis(1500)), ..item("/x", None, Some(1), None) };
    assert_eq!(format(&[fraction], 1, false, false), "/x\tPlain Text\t1970-01-01T00:00:01Z\t1");
    // Whole seconds round down, before 1970 too.
    let before = Item { modified: Some(UNIX_EPOCH - Duration::from_millis(500)), ..item("/x", None, Some(1), None) };
    assert_eq!(format(&[before], 1, false, false), "/x\tPlain Text\t1969-12-31T23:59:59Z\t1");
    let _ = SystemTime::now();
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
