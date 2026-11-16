//! The application catalog (read only; nothing is launched).
#![cfg(windows)]
use desktop_mcp_windows::apps::{best_match, catalog, frame_titled_as, match_by_executable, match_by_package, AppEntry};

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
fn match_by_package_takes_the_english_name_in_the_app_id() {
    let packaged = |name: &str, id: &str| AppEntry { name: name.into(), parsing_name: id.into() };
    let entries = [
        packaged("계산기", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        packaged("설정", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"),
        packaged("WSL Settings", r"C:\Program Files\WSL\wslsettings\wslsettings.exe"),
        packaged("사진", "Microsoft.OneDrive.Photos_8wekyb3d8bbwe!OneDrive.App"),
    ];
    assert_eq!(match_by_package(&entries, "Calculator").map(|found| found.name.as_str()), Some("계산기"));
    assert_eq!(match_by_package(&entries, "settings").map(|found| found.name.as_str()), Some("설정"));
    assert_eq!(match_by_package(&entries, "photos").map(|found| found.name.as_str()), Some("사진"));
    assert!(match_by_package(&entries, "lator").is_none(), "a whole word");
}

#[test]
fn an_unresolved_frame_is_the_entry_it_is_titled_as() {
    assert!(frame_titled_as("계산기", "계산기"));
    assert!(frame_titled_as("Calculator", "calculator"));
    assert!(!frame_titled_as("Calculator - Notes", "Calculator"), "the whole title");
    assert!(!frame_titled_as("", ""));
}

#[test]
fn the_apps_folder_is_readable() {
    let entries = catalog();
    assert!(entries.len() > 5, "{} entries", entries.len());
    assert!(entries.iter().all(|entry| !entry.name.is_empty() && !entry.parsing_name.is_empty()));
}
