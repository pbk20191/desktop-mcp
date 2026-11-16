//! What an Open target means.
use desktop_mcp_core::open::{resolve, Target};

#[test]
fn a_file_url_is_a_path() {
    let directory = tempfile::tempdir().unwrap();
    let file = directory.path().join("문서 a b.txt");
    std::fs::write(&file, "").unwrap();
    // A Windows path is written the way a file URL carries it: /C:/Users/...
    let url_path = file.to_str().unwrap().replace('\\', "/");
    let url_path = if url_path.starts_with('/') { url_path } else { format!("/{url_path}") };
    let escaped: String = url_path.bytes().map(|b| if b.is_ascii_alphanumeric() || b"/._-".contains(&b) { (b as char).to_string() } else { format!("%{b:02X}") }).collect();
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
