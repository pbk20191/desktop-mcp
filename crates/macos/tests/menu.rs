//! The Finder's menu bar, listed only: nothing is selected.
use desktop_mcp_core::menu::Request;
use desktop_mcp_macos::ax::trusted;
use desktop_mcp_macos::menu::answer;

#[test]
fn the_finders_menu_bar_lists_and_paths_resolve() {
    if !trusted() {
        return eprintln!("    skipped: Accessibility is not granted to the test runner");
    }
    let bar = answer(Some("Finder"), None, Request::List { path: vec![], depth: 1 }).unwrap();
    let items: Vec<&str> = bar.lines().collect();
    assert!(items.len() >= 3 && items.iter().all(|line| !line.starts_with(' ')), "{bar}");
    // The second bar item is the app menu; listing below it by the Apple alias and by its own title both work.
    let apple = answer(Some("Finder"), None, Request::List { path: vec!["Apple".into()], depth: 1 }).unwrap();
    assert!(!apple.is_empty() && apple != "No menu items.", "{apple}");
    let below = answer(Some("Finder"), None, Request::List { path: vec![items[1].to_string()], depth: 2 }).unwrap();
    assert!(below.lines().count() >= 2, "{below}");
    let missing = answer(Some("Finder"), None, Request::List { path: vec!["No Such Menu".into()], depth: 1 }).unwrap_err();
    assert!(missing.starts_with("No menu item 'No Such Menu' under the menu bar. Available: "), "{missing}");
    assert_eq!(answer(Some("No Such App desktop-mcp"), None, Request::List { path: vec![], depth: 1 }), Err("Application 'No Such App desktop-mcp' not found.".into()));
}
