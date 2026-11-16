//! The application catalog (read only; nothing is launched).
#![cfg(windows)]
use desktop_mcp_windows::apps::{best_match, catalog, AppEntry};

fn entry(name: &str) -> AppEntry {
    AppEntry { name: name.into(), parsing_name: format!("id-{name}") }
}

#[test]
fn best_match_prefers_exact_then_prefix_then_contains() {
    let entries = [entry("Notepad++"), entry("Notepad"), entry("Microsoft Edge"), entry("Edge Tools")];
    assert_eq!(best_match(&entries, "notepad").map(|found| found.name.as_str()), Some("Notepad"));
    assert_eq!(best_match(&entries, "note").map(|found| found.name.as_str()), Some("Notepad"), "shortest prefix match");
    assert_eq!(best_match(&entries, "edge").map(|found| found.name.as_str()), Some("Edge Tools"), "a prefix beats a substring");
    assert_eq!(best_match(&entries, "soft ed").map(|found| found.name.as_str()), Some("Microsoft Edge"));
    assert!(best_match(&entries, "zzz").is_none());
    assert!(best_match(&entries, "  ").is_none());
}

#[test]
fn the_apps_folder_is_readable() {
    let entries = catalog();
    assert!(entries.len() > 5, "{} entries", entries.len());
    assert!(entries.iter().all(|entry| !entry.name.is_empty() && !entry.parsing_name.is_empty()));
}
