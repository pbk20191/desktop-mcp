//! Applications: the shell's Apps folder (the Start menu's list: desktop and packaged apps alike) and launching
//! one of them, or an executable, through the shell; and finding the running windows of an entry.
use crate::windows_list::TopWindow;
use windows::core::{GUID, HSTRING, PCWSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, PROPERTYKEY};
use windows::Win32::Storage::Packaging::Appx::GetPackageFamilyName;
use windows::Win32::System::Com::CoTaskMemFree;
use windows::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};
use windows::Win32::UI::Shell::{
    BHID_EnumItems, FOLDERID_AppsFolder, IEnumShellItems, IShellItem, IShellItem2, SHCreateItemFromParsingName, SHGetKnownFolderItem, ShellExecuteExW, KF_FLAG_DEFAULT, SEE_MASK_FLAG_NO_UI,
    SEE_MASK_NOASYNC, SHELLEXECUTEINFOW, SIGDN_NORMALDISPLAY, SIGDN_PARENTRELATIVEPARSING,
};
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AppEntry {
    /// What the Start menu shows.
    pub name: String,
    /// What `shell:AppsFolder\<parsing_name>` opens: an app user model id, or a path.
    pub parsing_name: String,
}

fn display_name(item: &IShellItem, kind: windows::Win32::UI::Shell::SIGDN) -> Option<String> {
    let name: PWSTR = unsafe { item.GetDisplayName(kind) }.ok()?;
    let text = unsafe { name.to_string() }.ok();
    unsafe { CoTaskMemFree(Some(name.0 as *const _)) };
    text
}

/// Every entry of the Apps folder. Blocking (COM, on the pool).
pub fn catalog() -> Vec<AppEntry> {
    crate::uia::ensure_mta();
    let Ok(folder) = (unsafe { SHGetKnownFolderItem::<IShellItem>(&FOLDERID_AppsFolder, KF_FLAG_DEFAULT, None) }) else { return Vec::new() };
    let Ok(items) = (unsafe { folder.BindToHandler::<_, IEnumShellItems>(None, &BHID_EnumItems) }) else { return Vec::new() };
    let mut entries = Vec::new();
    loop {
        let mut batch = [None];
        let mut fetched = 0u32;
        if unsafe { items.Next(&mut batch, Some(&mut fetched)) }.is_err() || fetched == 0 {
            break;
        }
        let Some(item) = batch[0].take() else { break };
        if let (Some(name), Some(parsing_name)) = (display_name(&item, SIGDN_NORMALDISPLAY), display_name(&item, SIGDN_PARENTRELATIVEPARSING)) {
            if !name.is_empty() && !parsing_name.is_empty() {
                entries.push(AppEntry { name, parsing_name });
            }
        }
    }
    entries
}

/// The entry `query` names: the same name ignoring case, else the shortest name starting with it, else the shortest
/// containing it that is not an uninstaller (unless the query asks for one).
pub fn best_match<'a>(entries: &'a [AppEntry], query: &str) -> Option<&'a AppEntry> {
    let query = query.trim().to_lowercase();
    if query.is_empty() {
        return None;
    }
    let lowered = |entry: &AppEntry| entry.name.to_lowercase();
    let shortest = |test: &dyn Fn(&str) -> bool| entries.iter().filter(|entry| test(&lowered(entry))).min_by_key(|entry| entry.name.chars().count());
    shortest(&|name| name == query).or_else(|| shortest(&|name| name.starts_with(&query))).or_else(|| shortest(&|name| name.contains(&query) && (!name.contains("uninstall") || query.contains("uninstall"))))
}

fn shell_execute(file: &str) -> bool {
    crate::uia::ensure_mta();
    let file = HSTRING::from(file);
    let verb = HSTRING::from("open");
    let mut info = SHELLEXECUTEINFOW {
        cbSize: size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI,
        lpVerb: PCWSTR(verb.as_ptr()),
        lpFile: PCWSTR(file.as_ptr()),
        nShow: SW_SHOWNORMAL.0,
        ..Default::default()
    };
    unsafe { ShellExecuteExW(&mut info) }.is_ok()
}

/// Opens `name`: an executable path as it is, else the Apps folder entry it names. Blocking.
pub fn launch(name: &str) -> bool {
    let path = std::path::Path::new(name.trim());
    if path.is_absolute() && path.is_file() {
        return shell_execute(name.trim());
    }
    let entries = catalog();
    match best_match(&entries, name) {
        Some(entry) => shell_execute(&format!(r"shell:AppsFolder\{}", entry.parsing_name)),
        None => false,
    }
}

/// `System.Link.TargetParsingPath`: the file a desktop app's Start menu entry opens.
const LINK_TARGET: PROPERTYKEY = PROPERTYKEY { fmtid: GUID::from_u128(0xb9b4b3fc_2b51_4a42_b5d8_324146afcf25), pid: 2 };

/// What runs an Apps folder entry: a packaged app's package family, or a desktop app's executable.
#[derive(Debug, PartialEq, Eq)]
enum Runs {
    Package(String),
    Executable(String),
}

fn runs(entry: &AppEntry) -> Option<Runs> {
    // An app user model id `<family>!<app>` is a packaged app's.
    if let Some((family, _)) = entry.parsing_name.split_once('!').filter(|_| !entry.parsing_name.contains('\\')) {
        return Some(Runs::Package(family.to_string()));
    }
    let item: IShellItem2 = unsafe { SHCreateItemFromParsingName(&HSTRING::from(format!(r"shell:AppsFolder\{}", entry.parsing_name)), None) }.ok()?;
    let target = unsafe { item.GetString(&LINK_TARGET) }.ok()?;
    let path = unsafe { target.to_string() }.ok();
    unsafe { CoTaskMemFree(Some(target.0 as *const _)) };
    path.filter(|path| path.to_ascii_lowercase().ends_with(".exe")).map(Runs::Executable)
}

fn package_family(pid: u32) -> Option<String> {
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) }.ok()?;
    let mut buffer = [0u16; 256];
    let mut length = buffer.len() as u32;
    let read = unsafe { GetPackageFamilyName(process, &mut length, Some(PWSTR(buffer.as_mut_ptr()))) };
    unsafe {
        let _ = CloseHandle(process);
    }
    (read.is_ok() && length > 0).then(|| String::from_utf16_lossy(&buffer[..length as usize - 1]))
}

/// The windows of the Apps folder entry `name` names (as [`launch`] picks it), for a name no running app goes by:
/// a Start menu name such as a localized one. Blocking (COM, on the pool).
pub fn windows_of<'a>(name: &str, windows: &'a [TopWindow]) -> Vec<&'a TopWindow> {
    let entries = catalog();
    let Some(runs) = best_match(&entries, name).and_then(runs) else { return Vec::new() };
    windows
        .iter()
        .filter(|window| match &runs {
            Runs::Package(family) => package_family(window.pid).is_some_and(|running| running.eq_ignore_ascii_case(family)),
            Runs::Executable(path) => crate::windows_list::image_path(window.pid).is_some_and(|running| running.eq_ignore_ascii_case(path)),
        })
        .collect()
}
