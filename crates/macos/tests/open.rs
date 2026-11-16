//! Finding applications. (Opening and revealing are not exercised: they would open windows on the desktop.)
mod support;
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_macos::desktop::MacDesktop;
use desktop_mcp_macos::main_loop::run;
use desktop_mcp_macos::open::find_application;

fn applications_are_found_in_nested_folders() {
    let root = tempfile::tempdir().unwrap();
    let root = root.path();
    let nested = root.join("Vendor/Tools/Foo.app");
    std::fs::create_dir_all(nested.join("Contents/Bar.app")).unwrap();
    let direct = root.join("Other/Direct.app");
    std::fs::create_dir_all(&direct).unwrap();
    std::fs::create_dir_all(root.join("A/Direct.app")).unwrap();
    std::fs::create_dir_all(root.join("1/2/3/Deep.app")).unwrap();
    std::fs::create_dir_all(root.join(".hidden/Hidden.app")).unwrap();
    std::fs::create_dir_all(root.join("Shallow.app/Inner.app")).unwrap();
    let roots = [root.join("Missing"), root.to_path_buf(), root.join("Other")];

    assert_eq!(find_application("Foo", &roots), Some(nested.clone()));
    assert_eq!(find_application("foo.app", &roots), Some(nested));
    // A direct child of a later root wins over a subfolder of an earlier one.
    assert_eq!(find_application("Direct", &roots), Some(direct));
    // App bundles are not searched inside, nor hidden folders, nor more than three levels down.
    assert_eq!(find_application("Bar", &roots), None);
    assert_eq!(find_application("Inner", &roots), None);
    assert_eq!(find_application("Hidden", &roots), None);
    assert_eq!(find_application("Deep", &roots), None);
    assert_eq!(find_application("Nope", &roots), None);
}

/// The completion handler's path without opening anything: the application does not exist.
fn a_failed_open_reports_the_systems_reason() {
    let directory = tempfile::tempdir().unwrap();
    let file = directory.path().join("a.txt");
    std::fs::write(&file, "").unwrap();
    let target = desktop_mcp_core::open::Target::File(file);
    let outcome = run(move |_| async move { MacDesktop::new().open(&target, Some(std::path::Path::new("/nonexistent/Nope.app"))).await });
    let reason = outcome.expect_err("no such application");
    assert!(!reason.is_empty(), "{reason:?}");
    eprintln!("    (the system said: {reason})");
}

fn main() {
    support::run(&[
        ("applications_are_found_in_nested_folders", applications_are_found_in_nested_folders),
        ("a_failed_open_reports_the_systems_reason", a_failed_open_reports_the_systems_reason),
    ])
}
