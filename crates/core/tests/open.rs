//! What an Open target means.
use desktop_mcp_core::open::{resolve, Target};
use std::path::PathBuf;

#[test]
fn tilde_is_the_home_directory() {
    assert_eq!(resolve("~"), Ok(Target::File(PathBuf::from(std::env::var("HOME").unwrap()))));
}

#[test]
fn a_path_with_spaces_and_hangul_resolves() {
    let directory = tempfile::tempdir().unwrap();
    let file = directory.path().join("문서 a b.txt");
    std::fs::write(&file, "").unwrap();
    assert_eq!(resolve(file.to_str().unwrap()), Ok(Target::File(file.clone())));
}

#[test]
fn a_file_url_is_a_path() {
    let directory = tempfile::tempdir().unwrap();
    let file = directory.path().join("문서 a b.txt");
    std::fs::write(&file, "").unwrap();
    let escaped: String = file.to_str().unwrap().bytes().map(|b| if b.is_ascii_alphanumeric() || b"/._-".contains(&b) { (b as char).to_string() } else { format!("%{b:02X}") }).collect();
    assert_eq!(resolve(&format!("file://{escaped}")), Ok(Target::File(file.clone())));
    assert_eq!(resolve(&format!("FILE://localhost{escaped}")), Ok(Target::File(file)));
}

#[test]
fn urls_are_not_checked_on_disk() {
    assert_eq!(resolve("https://example.com/a?b=1"), Ok(Target::Url("https://example.com/a?b=1".into())));
    assert_eq!(resolve("x-apple.systempreferences:com.apple.preference.security"), Ok(Target::Url("x-apple.systempreferences:com.apple.preference.security".into())));
}

#[test]
fn what_is_not_a_url_is_a_path() {
    // One letter is not a scheme, and a URL holds no spaces.
    assert_eq!(resolve("C:/nope"), Err("No such file: C:/nope".to_string()));
    assert_eq!(resolve("https://a b"), Err("No such file: https://a b".to_string()));
}

#[test]
fn missing_and_empty_targets_are_refused() {
    assert_eq!(resolve("/nope/x"), Err("No such file: /nope/x".to_string()));
    for target in ["", "  "] {
        assert_eq!(resolve(target), Err("target is required.".to_string()));
    }
}
