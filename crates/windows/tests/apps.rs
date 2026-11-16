//! The application catalog (read only; nothing is launched).
#![cfg(windows)]
use desktop_mcp_windows::apps::{best_match, catalog, match_by_executable, AppEntry};

fn entry(name: &str) -> AppEntry {
    AppEntry { name: name.into(), parsing_name: format!("id-{name}") }
}

#[test]
fn best_match_prefers_exact_then_prefix_then_contains() {
    let entries = [entry("Notepad++"), entry("Notepad"), entry("Microsoft Edge"), entry("Edge Tools"), entry("Uninstall Teams"), entry("Microsoft Teams")];
    assert_eq!(best_match(&entries, "notepad").map(|found| found.name.as_str()), Some("Notepad"));
    assert_eq!(best_match(&entries, "note").map(|found| found.name.as_str()), Some("Notepad"), "shortest prefix match");
    assert_eq!(best_match(&entries, "edge").map(|found| found.name.as_str()), Some("Edge Tools"), "a prefix beats a substring");
    assert_eq!(best_match(&entries, "soft ed").map(|found| found.name.as_str()), Some("Microsoft Edge"));
    assert_eq!(best_match(&entries, "teams").map(|found| found.name.as_str()), Some("Microsoft Teams"), "not the uninstaller");
    assert!(best_match(&entries, "zzz").is_none());
    assert!(best_match(&entries, "  ").is_none());
}

#[test]
fn match_by_executable_takes_the_file_stem() {
    let targets = [(entry("메모장"), r"C:\Windows\System32\notepad.exe".to_string()), (entry("Notepad++"), r"C:\Program Files\Notepad++\notepad++.exe".to_string())];
    assert_eq!(match_by_executable(&targets, "notepad").map(|found| found.name.as_str()), Some("메모장"));
    assert_eq!(match_by_executable(&targets, "NOTEPAD.EXE").map(|found| found.name.as_str()), Some("메모장"));
    assert!(match_by_executable(&targets, "note").is_none(), "the whole stem, not a prefix");
}

#[test]
fn the_apps_folder_is_readable() {
    let entries = catalog();
    assert!(entries.len() > 5, "{} entries", entries.len());
    assert!(entries.iter().all(|entry| !entry.name.is_empty() && !entry.parsing_name.is_empty()));
}
