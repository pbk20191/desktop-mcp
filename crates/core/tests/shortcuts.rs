//! Close matches for a mistyped shortcut name.
use desktop_mcp_core::shortcuts::close_matches;

#[test]
fn prefixes_come_first_then_containing_names_without_repeats() {
    let names: Vec<String> = ["My Resize", "Resize B", "resize a", "Other", "Resize B"].map(String::from).into();
    assert_eq!(close_matches("RESIZE", &names), ["Resize B", "resize a", "My Resize"]);
}

#[test]
fn at_most_twenty() {
    let names: Vec<String> = (0..30).map(|i| format!("item {i}")).collect();
    assert_eq!(close_matches("item", &names), names[..20]);
}
