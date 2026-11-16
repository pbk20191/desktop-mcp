//! The Snapshot text from a known state.
mod support;
use desktop_mcp_core::snapshot::{AppStatus, BoundingBox, Meta, State, TreeNode, TreeState, WindowView};
use serde_json::json;
use std::rc::Rc;
use support::{call_tool, texts, FakeDesktop};

fn node(name: &str, metadata: Vec<(String, Meta)>) -> TreeNode {
    TreeNode { bounding_box: BoundingBox::from_edges(10.9, 20.0, 51.0, 41.0), name: name.into(), control_type: "AXButton".into(), window_name: "Safari".into(), metadata }
}

#[test]
fn the_snapshot_text() {
    let view = WindowView { name: "Docs".into(), status: AppStatus::Active, bounding_box: BoundingBox::default(), pid: 1, bundle_id: "com.apple.Safari".into(), dialog: Some(("Save?".into(), false)) };
    let state = State {
        active: Some(view.clone()),
        windows: vec![view],
        tree: TreeState {
            interactive: vec![node("Back", vec![]), node("Go", vec![("value".into(), Meta::Text("a\"b/ü".into())), ("checked".into(), Meta::Bool(true)), ("n".into(), Meta::Float(2.0))])],
            scrollable: vec![node("Page", vec![])],
        },
        coordinate_space: "macOS logical points".into(),
        trusted: false,
        screenshot: None,
        ..State::default()
    };
    let desktop = Rc::new(FakeDesktop::default());
    *desktop.snapshot_state.borrow_mut() = state;
    let answer = call_tool(&desktop, &["Snapshot"], "Snapshot", json!({"use_vision": true}));
    let dialog = "Docs (com.apple.Safari) - Active - dialog: \"Save?\" (behind other windows; switch to the app to reach it)";
    let header = "# id|control_type|name|coords|metadata (when any), under the \"## window\" they are in";
    let expected = format!(
        "{}\nFocused Window:\n{dialog}\n\nInput Coordinate Space:\nmacOS logical points\n\nOpen Applications:\n{dialog}\n\nList of Interactive Elements:\n{header}\n## Safari\n0|AXButton|Back|(30,30)\n1|AXButton|Go|(30,30)|{{\"value\": \"a\\\"b/ü\", \"checked\": true, \"n\": 2}}\n\nList of Scrollable Elements:\n{header}\n## Safari\n2|AXButton|Page|(30,30)\n\nNote: Screenshot capture failed, so no annotated image is attached. The elements\nlisted above are still valid; call Snapshot again if you need the screenshot.\nIf this persists, grant Screen Recording in System Settings > Privacy & Security\n(or call Permissions with request=true).\n",
        desktop_mcp_core::permissions::ACCESSIBILITY_MESSAGE
    );
    assert_eq!(texts(&answer), [expected]);
}

#[test]
fn notes_and_hint_are_printed() {
    let state = State { coordinate_space: "physical pixels".into(), trusted: true, notes: vec!["Virtual desktop: Work (2 of 3)".into()], screenshot_hint: Some("Capture failed; call Snapshot again.".into()), ..State::default() };
    let text = desktop_mcp_core::snapshot::text(&state, true);
    assert!(text.contains("Input Coordinate Space:
physical pixels
Virtual desktop: Work (2 of 3)
"), "{text}");
    assert!(text.trim_end().ends_with("Capture failed; call Snapshot again."), "{text}");
    assert!(!text.contains("Screen Recording"), "{text}");
}
