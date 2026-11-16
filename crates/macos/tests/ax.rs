//! Live Accessibility reads of the Finder: read-only, skipped without the Accessibility grant.
use desktop_mcp_macos::ax::{trusted, Element, Value};

fn finder() -> Option<Element> {
    if !trusted() {
        eprintln!("    skipped: Accessibility is not granted to the test runner");
        return None;
    }
    let pid = desktop_mcp_macos::open::running_apps().into_iter().find(|app| app.bundle_id.as_deref() == Some("com.apple.finder"))?.pid;
    Some(Element::application(pid))
}

#[test]
fn an_application_answers_its_attributes() {
    let Some(finder) = finder() else { return };
    assert_eq!(finder.attribute("AXRole"), Some(Value::Text("AXApplication".into())));
    let found = finder.attributes(&["AXRole", "AXTitle", "AXNoSuchAttribute"]).expect("the Finder answers");
    let names: Vec<&str> = found.iter().map(|(name, _)| name.as_str()).collect();
    assert!(names.contains(&"AXRole") && !names.contains(&"AXNoSuchAttribute"), "{found:?}");
    assert!(finder.pid().is_some_and(|pid| pid > 0));
}

#[test]
fn windows_are_elements_with_frames() {
    let Some(finder) = finder() else { return };
    for window in finder.elements("AXWindows").expect("the Finder answers") {
        assert!(matches!(window.attribute("AXPosition"), Some(Value::Point(..))), "{:?}", window.attribute("AXPosition"));
        assert!(matches!(window.attribute("AXSize"), Some(Value::Size(..))));
        // The Finder lists its desktop (an AXScrollArea) among its windows.
        assert!(window.attribute("AXRole").is_some_and(|role| !role.text().is_empty()));
    }
}
