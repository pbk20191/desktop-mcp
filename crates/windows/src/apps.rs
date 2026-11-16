//! Applications: the shell's Apps folder (the Start menu's list: desktop and packaged apps alike) and launching
//! one of them, or an executable, through the shell; and finding the running windows of an entry.
use crate::windows_list::TopWindow;
use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};
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

/// A path's file name.
fn file_name(path: &str) -> &str {
    path.rsplit(['\\', '/']).next().unwrap_or(path)
}

/// The entry whose executable (`targets` pairs an entry with the path it runs) has `query` as its file name, with or
/// without `.exe`, ignoring case; the shortest-named one when several do.
pub fn match_by_executable<'a>(targets: &'a [(AppEntry, String)], query: &str) -> Option<&'a AppEntry> {
    let query = query.trim().to_lowercase();
    let query = query.strip_suffix(".exe").unwrap_or(&query);
    if query.is_empty() {
        return None;
    }
    targets
        .iter()
        .filter(|(_, path)| {
            let exe = file_name(path).to_lowercase();
            exe.strip_suffix(".exe").is_some_and(|stem| stem == query)
        })
        .map(|(entry, _)| entry)
        .min_by_key(|entry| entry.name.chars().count())
}

/// The executable an Apps folder entry runs: its shortcut's target, else the entry itself when it is one.
fn executable(entry: &AppEntry) -> Option<String> {
    match runs(entry) {
        Some(Runs::Executable(path)) => Some(path),
        Some(Runs::Package(_)) => None,
        None => entry.parsing_name.to_ascii_lowercase().ends_with(".exe").then(|| entry.parsing_name.clone()),
    }
}

/// What a launch opened, to find its window by: the name it was asked for, the executable's file name when known,
/// and what runs the Apps folder entry it opened.
#[derive(Clone, Debug)]
pub struct Launched {
    name: String,
    exe: Option<String>,
    runs: Option<Runs>,
}

impl Launched {
    /// Whether `window` is the launched app's. Blocking (process queries).
    fn owns(&self, window: &TopWindow) -> bool {
        window.is_app(&self.name) || self.exe.as_deref().is_some_and(|exe| window.is_app(exe)) || self.runs.as_ref().is_some_and(|runs| runs.owns(window))
    }
}

/// The executable the Run dialog would open for the bare command `name` (notepad, calc): its App Paths entry, else
/// `<name>.exe` in an absolute PATH folder. Never the working directory. Blocking.
fn command_path(name: &str) -> Option<String> {
    use windows::Win32::Foundation::ERROR_SUCCESS;
    use windows::Win32::System::Registry::{RegGetValueW, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, RRF_RT_REG_EXPAND_SZ, RRF_RT_REG_SZ};
    if name.is_empty() || name.contains(['\\', '/', ' ']) {
        return None;
    }
    let exe = if name.to_ascii_lowercase().ends_with(".exe") { name.to_string() } else { format!("{name}.exe") };
    let key = HSTRING::from(format!(r"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}"));
    for hive in [HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE] {
        let mut buffer = [0u16; 1024];
        let mut size = (buffer.len() * 2) as u32;
        let status = unsafe { RegGetValueW(hive, &key, PCWSTR::null(), RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ, None, Some(buffer.as_mut_ptr().cast()), Some(&mut size)) };
        if status == ERROR_SUCCESS {
            let value = String::from_utf16_lossy(&buffer[..(size as usize / 2)]);
            let value = value.trim_end_matches('\0').trim().trim_matches('"').to_string();
            if std::path::Path::new(&value).is_file() {
                return Some(value);
            }
        }
    }
    let path = std::env::var_os("PATH")?;
    std::env::split_paths(&path).filter(|folder| folder.is_absolute()).map(|folder| folder.join(&exe)).find(|candidate| candidate.is_file()).map(|found| found.to_string_lossy().into_owned())
}

/// Opens `name`: an executable path as it is; else the Apps folder entry with exactly that Start menu name, else the
/// one whose executable has that file name; else the command the Run dialog would open (App Paths, then PATH: notepad,
/// calc); else the Apps folder entry whose name starts with or contains it. So "notepad" opens Notepad, not
/// Notepad++. What it opened, or `None` when nothing did. Blocking.
pub fn launch(name: &str) -> Option<Launched> {
    let name = name.trim();
    let path = std::path::Path::new(name);
    let open_path = |path: &str| {
        let exe = Some(file_name(path).to_string());
        shell_execute(path).then(|| Launched { name: name.to_string(), exe, runs: Some(Runs::Executable(path.to_string())) })
    };
    if path.is_absolute() && path.is_file() {
        return open_path(name);
    }
    let entries = catalog();
    let open_entry = |entry: &AppEntry, exe: Option<String>| shell_execute(&format!(r"shell:AppsFolder\{}", entry.parsing_name)).then(|| Launched { name: name.to_string(), exe, runs: runs(entry) });
    let lowered = name.to_lowercase();
    if let Some(entry) = entries.iter().filter(|entry| entry.name.to_lowercase() == lowered).min_by_key(|entry| entry.name.chars().count()) {
        return open_entry(entry, None);
    }
    let targets: Vec<(AppEntry, String)> = entries.iter().filter_map(|entry| executable(entry).map(|path| (entry.clone(), path))).collect();
    if let Some(entry) = match_by_executable(&targets, name) {
        let exe = targets.iter().find(|(target, _)| target == entry).map(|(_, path)| file_name(path).to_string());
        return open_entry(entry, exe);
    }
    if let Some(command) = command_path(name) {
        return open_path(&command);
    }
    best_match(&entries, name).and_then(|entry| open_entry(entry, None))
}

/// The launched app's window among `windows` (front first), given the window handles listed before the launch: a
/// window of the app that was not there before; else one of its older windows now in front (a single-instance app
/// that activated its window instead of opening one), other than `front_before`, the one in front before the launch;
/// else a new window now in front (a command that started the app under another executable's name). Blocking.
pub fn launched_window(launched: &Launched, before: &[isize], front_before: Option<isize>, windows: &[TopWindow]) -> Option<isize> {
    let new = |window: &&TopWindow| !before.contains(&window.hwnd);
    windows
        .iter()
        .filter(new)
        .find(|window| launched.owns(window))
        .or_else(|| windows.iter().find(|window| window.focused && Some(window.hwnd) != front_before && launched.owns(window)))
        .or_else(|| windows.iter().filter(new).find(|window| window.focused))
        .map(|window| window.hwnd)
}

/// `System.Link.TargetParsingPath`: the file a desktop app's Start menu entry opens.
const LINK_TARGET: PROPERTYKEY = PROPERTYKEY { fmtid: GUID::from_u128(0xb9b4b3fc_2b51_4a42_b5d8_324146afcf25), pid: 2 };

/// What runs an Apps folder entry: a packaged app's package family, or a desktop app's executable.
#[derive(Clone, Debug, PartialEq, Eq)]
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

impl Runs {
    /// Whether `window`'s process is what runs the entry.
    fn owns(&self, window: &TopWindow) -> bool {
        match self {
            Runs::Package(family) => package_family(window.pid).is_some_and(|running| running.eq_ignore_ascii_case(family)),
            Runs::Executable(path) => crate::windows_list::image_path(window.pid).is_some_and(|running| running.eq_ignore_ascii_case(path)),
        }
    }
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

/// Name → the Start menu name of the entry it names and what runs it, with when it was read: polling callers (Wait)
/// read the Apps folder once a minute, not on every poll.
static RESOLVED: Mutex<Option<HashMap<String, (Instant, Option<(String, Option<Runs>)>)>>> = Mutex::new(None);
const RESOLVED_FOR: Duration = Duration::from_secs(60);

fn entry_named(name: &str) -> Option<(String, Option<Runs>)> {
    let key = name.trim().to_lowercase();
    if let Some((read, known)) = RESOLVED.lock().unwrap().get_or_insert_with(HashMap::new).get(&key) {
        if read.elapsed() < RESOLVED_FOR {
            return known.clone();
        }
    }
    let entries = catalog();
    let resolved = best_match(&entries, name).map(|entry| (entry.name.clone(), runs(entry)));
    RESOLVED.lock().unwrap().get_or_insert_with(HashMap::new).insert(key, (Instant::now(), resolved.clone()));
    resolved
}

/// Whether a UWP frame not yet resolved to its app, titled `title`, is the Start menu entry named `entry`: a frame
/// shows its app's display name ("계산기") until its core window attaches. Ignores case.
pub fn frame_titled_as(title: &str, entry: &str) -> bool {
    let title = title.trim();
    !title.is_empty() && title.to_lowercase() == entry.trim().to_lowercase()
}

/// The windows of the Apps folder entry `name` names (as [`launch`] picks it), for a name no running app goes by:
/// a Start menu name such as a localized one; an unresolved UWP frame by its title. Blocking (COM, on the pool).
pub fn windows_of<'a>(name: &str, windows: &'a [TopWindow]) -> Vec<&'a TopWindow> {
    let Some((entry, runs)) = entry_named(name) else { return Vec::new() };
    windows
        .iter()
        .filter(|window| if window.is_unresolved_frame() { frame_titled_as(&window.title, &entry) } else { runs.as_ref().is_some_and(|runs| runs.owns(window)) })
        .collect()
}
