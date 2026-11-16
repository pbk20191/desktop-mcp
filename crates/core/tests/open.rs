//! What an Open target means.
use desktop_mcp_core::open::{resolve, Target};

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
fn missing_and_empty_targets_are_refused() {
    assert_eq!(resolve("/nope/x"), Err("No such file: /nope/x".to_string()));
    for target in ["", "  "] {
        assert_eq!(resolve(target), Err("target is required.".to_string()));
    }
}
