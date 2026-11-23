//! Opening, revealing and finding applications with NSWorkspace.
use desktop_mcp_core::open::{Application, Target};
use desktop_mcp_core::wait::RunningApp;
use objc2_app_kit::{NSApplicationActivationPolicy, NSRunningApplication, NSWorkspace, NSWorkspaceOpenConfiguration};
use objc2_foundation::{NSArray, NSError, NSFileManager, NSString, NSURL};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

/// Opens `target` with its default application or the one at `app`, and activates it.
pub async fn open(target: Target, app: Option<PathBuf>) -> Result<(), String> {
    let url = match &target {
        Target::Url(url) => NSURL::URLWithString(&NSString::from_str(url)).ok_or_else(|| format!("Invalid URL: {url}"))?,
        Target::File(path) => file_url(path),
    };
    let (done, finished) = tokio::sync::oneshot::channel::<Result<(), String>>();
    // NSWorkspace calls this once, on a queue of its own.
    let done = Mutex::new(Some(done));
    let handler = block2::RcBlock::new(move |_: *mut NSRunningApplication, error: *mut NSError| {
        let outcome = match unsafe { error.as_ref() } {
            Some(error) => Err(error.localizedDescription().to_string()),
            None => Ok(()),
        };
        if let Some(done) = done.lock().unwrap().take() {
            let _ = done.send(outcome);
        }
    });
    let configuration = NSWorkspaceOpenConfiguration::configuration();
    configuration.setActivates(true);
    let workspace = NSWorkspace::sharedWorkspace();
    match app {
        Some(app) => workspace.openURLs_withApplicationAtURL_configuration_completionHandler(&NSArray::from_retained_slice(&[url]), &file_url(&app), &configuration, Some(&handler)),
        None => workspace.openURL_configuration_completionHandler(&url, &configuration, Some(&handler)),
    }
    finished.await.unwrap_or_else(|_| Err("The system did not report whether it opened the target.".to_string()))
}

/// Shows `path` selected in Finder.
pub fn reveal(path: &Path) {
    NSWorkspace::sharedWorkspace().activateFileViewerSelectingURLs(&NSArray::from_retained_slice(&[file_url(path)]));
}

fn file_url(path: &Path) -> objc2::rc::Retained<NSURL> {
    NSURL::fileURLWithPath(&NSString::from_str(&path.to_string_lossy()))
}

/// By bundle id, then by name through LaunchServices (as `open -a` finds it), then by searching the application
/// folders. Blocks: call it off the main thread.
pub fn application(name_or_bundle_id: &str) -> Option<Application> {
    let workspace = NSWorkspace::sharedWorkspace();
    let name = NSString::from_str(name_or_bundle_id);
    let path = workspace
        .URLForApplicationWithBundleIdentifier(&name)
        .and_then(|url| url.path())
        .map(|path| PathBuf::from(path.to_string()))
        .or_else(|| path_for_application_name(&workspace, &name))
        .or_else(|| find_application(name_or_bundle_id, &application_folders()))?;
    let display = NSFileManager::defaultManager().displayNameAtPath(&NSString::from_str(&path.to_string_lossy())).to_string();
    let name = display.strip_suffix(".app").unwrap_or(&display).to_string();
    Some(Application { path, name })
}

/// Deprecated without a replacement that takes a name.
#[allow(deprecated)]
fn path_for_application_name(workspace: &NSWorkspace, name: &NSString) -> Option<PathBuf> {
    workspace.fullPathForApplication(name).map(|path| PathBuf::from(path.to_string()))
}

fn application_folders() -> Vec<PathBuf> {
    let mut folders: Vec<PathBuf> = ["/Applications", "/Applications/Utilities", "/System/Applications", "/System/Applications/Utilities"].map(PathBuf::from).into();
    if let Some(home) = std::env::var_os("HOME") {
        folders.push(PathBuf::from(home).join("Applications"));
    }
    folders
}

/// `<name>.app` directly in one of `roots` (in order), else the first one found in their subfolders, up to three
/// levels down, ignoring case. App bundles and hidden folders are not searched inside.
pub fn find_application(name: &str, roots: &[PathBuf]) -> Option<PathBuf> {
    let bundle = if name.to_lowercase().ends_with(".app") { name.to_string() } else { format!("{name}.app") };
    if let Some(direct) = roots.iter().map(|root| root.join(&bundle)).find(|direct| direct.exists()) {
        return Some(direct);
    }
    let wanted = bundle.to_lowercase();
    roots.iter().find_map(|root| search(root, &wanted, 1))
}

fn search(folder: &Path, wanted: &str, level: usize) -> Option<PathBuf> {
    let mut entries: Vec<_> = std::fs::read_dir(folder).ok()?.filter_map(Result::ok).collect();
    entries.sort_by_key(|entry| entry.file_name());
    for entry in &entries {
        let name = entry.file_name().to_string_lossy().to_lowercase();
        if name.starts_with('.') {
            continue;
        }
        if name == wanted {
            return Some(entry.path());
        }
        let is_folder = entry.file_type().is_ok_and(|kind| kind.is_dir());
        if is_folder && !name.ends_with(".app") && level < 3 {
            if let Some(found) = search(&entry.path(), wanted, level + 1) {
                return Some(found);
            }
        }
    }
    None
}

/// The app bundle's file name without `.app`: the English name of an app whose shown name is translated
/// (TextEdit.app shows as 텍스트 편집기) or shortened (Visual Studio Code.app as Code).
pub fn english_name(app: &NSRunningApplication) -> Option<String> {
    let file = app.bundleURL()?.lastPathComponent()?.to_string();
    file.strip_suffix(".app").map(str::to_string)
}

/// Apps with a Dock icon or a menu-bar presence; background-only processes are left out. NSWorkspace updates
/// this list on the main run loop: call it on the main thread.
pub fn running_apps() -> Vec<RunningApp> {
    NSWorkspace::sharedWorkspace()
        .runningApplications()
        .iter()
        .filter(|app| !app.isTerminated() && matches!(app.activationPolicy(), NSApplicationActivationPolicy::Regular | NSApplicationActivationPolicy::Accessory))
        .map(|app| RunningApp {
            pid: app.processIdentifier(),
            name: app.localizedName().map(|name| name.to_string()),
            bundle_id: app.bundleIdentifier().map(|id| id.to_string()),
            english_name: english_name(&app),
        })
        .collect()
}
