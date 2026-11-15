//! Running applications, and waiting on one that is always there.
mod support;
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_macos::desktop::MacDesktop;
use desktop_mcp_macos::main_loop::run;

fn the_finder_is_running() {
    let apps = run(|_| async { MacDesktop::new().running_apps().await });
    for bundle_id in ["com.apple.finder"] {
        let app = apps.iter().find(|app| app.bundle_id.as_deref() == Some(bundle_id)).unwrap_or_else(|| panic!("{bundle_id} in {apps:?}"));
        assert!(app.pid > 0 && app.name.as_deref().is_some_and(|name| !name.is_empty()), "{app:?}");
    }
}

fn waiting_for_a_running_app_answers_at_once() {
    let text = run(|_| async { desktop_mcp_core::wait::until_app(&MacDesktop::new(), Some("com.apple.finder"), true, 5).await });
    assert_eq!(text, Ok("App com.apple.finder is running.".to_string()));
}

fn main() {
    support::run(&[
        ("the_finder_is_running", the_finder_is_running),
        ("waiting_for_a_running_app_answers_at_once", waiting_for_a_running_app_answers_at_once),
    ])
}
