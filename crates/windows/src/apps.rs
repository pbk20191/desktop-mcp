//! Applications: the shell's Apps folder (the Start menu's list: desktop and packaged apps alike) and launching
//! one of them, or an executable, through the shell.
use windows::core::{HSTRING, PCWSTR, PWSTR};
use windows::Win32::System::Com::CoTaskMemFree;
use windows::Win32::UI::Shell::{
    BHID_EnumItems, FOLDERID_AppsFolder, IEnumShellItems, IShellItem, SHGetKnownFolderItem, ShellExecuteExW, KF_FLAG_DEFAULT, SEE_MASK_FLAG_NO_UI, SEE_MASK_NOASYNC, SHELLEXECUTEINFOW,
    SIGDN_NORMALDISPLAY, SIGDN_PARENTRELATIVEPARSING,
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
/// containing it.
pub fn best_match<'a>(entries: &'a [AppEntry], query: &str) -> Option<&'a AppEntry> {
    let query = query.trim().to_lowercase();
    if query.is_empty() {
        return None;
    }
    let lowered = |entry: &AppEntry| entry.name.to_lowercase();
    let shortest = |test: &dyn Fn(&str) -> bool| entries.iter().filter(|entry| test(&lowered(entry))).min_by_key(|entry| entry.name.chars().count());
    shortest(&|name| name == query).or_else(|| shortest(&|name| name.starts_with(&query))).or_else(|| shortest(&|name| name.contains(&query)))
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
