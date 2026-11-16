//! The top-level windows of applications (Win32 and DWM), and the core's `WindowSource` over them.
use crate::displays::frame;
use crate::pool;
use desktop_mcp_core::window::{DisplayFrame, Frame, FrameResult, WindowInfo, WindowReading, WindowSource};
use std::collections::HashMap;
use std::ffi::c_void;
use std::future::Future;
use std::pin::Pin;
use std::sync::Mutex;
use std::time::{Duration, Instant};
use windows::core::{BOOL, PCWSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM, RECT};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED, DWMWA_EXTENDED_FRAME_BOUNDS};
use windows::Win32::Graphics::Gdi::{GetMonitorInfoW, MonitorFromWindow, MONITORINFO, MONITOR_DEFAULTTONEAREST};
use windows::Win32::Storage::FileSystem::{GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW};
use windows::Win32::System::Threading::{OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION};
use windows::Win32::Foundation::WPARAM;
use windows::Win32::UI::WindowsAndMessaging::{
    EnumChildWindows, EnumWindows, GetClassNameW, GetForegroundWindow, GetWindow, GetWindowLongW, GetWindowRect, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsIconic,
    IsWindow, IsWindowVisible, IsZoomed, PostMessageW, SetWindowPos, ShowWindow, ShowWindowAsync, GWL_EXSTYLE, GWL_STYLE, GW_OWNER, SWP_NOACTIVATE, SWP_NOZORDER, SW_MINIMIZE,
    SW_RESTORE, WM_CLOSE, WS_EX_APPWINDOW, WS_EX_TOOLWINDOW, WS_THICKFRAME,
};

type Pending<T> = Pin<Box<dyn Future<Output = T>>>;

#[derive(Clone, Debug)]
pub struct TopWindow {
    pub hwnd: isize,
    /// The window's process; for a UWP app's frame, the hosted app's (the frame stays `hwnd`).
    pub pid: u32,
    /// The executable's file name (`notepad.exe`).
    pub exe: String,
    /// The application's name: the executable's file description, else its stem.
    pub app: String,
    pub title: String,
    pub frame: Frame,
    pub focused: bool,
    pub minimized: bool,
    pub fullscreen: bool,
}

impl TopWindow {
    /// Whether `app` names this window's application: its name or executable, with or without `.exe`, ignoring case.
    pub fn is_app(&self, app: &str) -> bool {
        let app = app.trim();
        let stem = self.exe.strip_suffix(".exe").unwrap_or(&self.exe);
        [self.app.as_str(), self.exe.as_str(), stem].iter().any(|name| name.eq_ignore_ascii_case(app))
    }

    /// Whether this is a UWP frame not yet resolved to the app it hosts: right after the app starts, its frame shows
    /// before its core window attaches, and until then it is ApplicationFrameHost.exe's.
    pub fn is_unresolved_frame(&self) -> bool {
        self.exe.eq_ignore_ascii_case(FRAME_HOST)
    }
}

unsafe extern "system" fn collect(hwnd: HWND, data: LPARAM) -> BOOL {
    unsafe { &mut *(data.0 as *mut Vec<HWND>) }.push(hwnd);
    true.into()
}

fn class_name(hwnd: HWND) -> String {
    let mut buffer = [0u16; 256];
    let length = unsafe { GetClassNameW(hwnd, &mut buffer) } as usize;
    String::from_utf16_lossy(&buffer[..length])
}

fn title(hwnd: HWND) -> String {
    let length = unsafe { GetWindowTextLengthW(hwnd) } as usize;
    let mut buffer = vec![0u16; length + 1];
    let copied = unsafe { GetWindowTextW(hwnd, &mut buffer) } as usize;
    String::from_utf16_lossy(&buffer[..copied])
}

/// A window on screen: visible, not cloaked (another virtual desktop, a suspended UWP app), not the desktop or
/// the taskbar.
fn is_shown(hwnd: HWND) -> bool {
    if !unsafe { IsWindowVisible(hwnd) }.as_bool() {
        return false;
    }
    let mut cloaked = 0u32;
    if unsafe { DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &mut cloaked as *mut _ as *mut c_void, 4) }.is_ok() && cloaked != 0 {
        return false;
    }
    !matches!(class_name(hwnd).as_str(), "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd")
}

/// A window an application shows to the user as its own: shown, not a tool window, unowned unless it asks for a
/// taskbar button, with a title.
fn is_app_window(hwnd: HWND) -> bool {
    if !is_shown(hwnd) {
        return false;
    }
    let style = unsafe { GetWindowLongW(hwnd, GWL_EXSTYLE) } as u32;
    let app_window = style & WS_EX_APPWINDOW.0 != 0;
    if style & WS_EX_TOOLWINDOW.0 != 0 && !app_window {
        return false;
    }
    if !app_window && unsafe { GetWindow(hwnd, GW_OWNER) }.is_ok_and(|owner| !owner.is_invalid()) {
        return false;
    }
    (unsafe { GetWindowTextLengthW(hwnd) }) > 0
}

fn window_frame(hwnd: HWND) -> RECT {
    let mut rect = RECT::default();
    if unsafe { DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &mut rect as *mut _ as *mut c_void, size_of::<RECT>() as u32) }.is_err() {
        let _ = unsafe { GetWindowRect(hwnd, &mut rect) };
    }
    rect
}

fn fills_its_monitor(hwnd: HWND, rect: &RECT) -> bool {
    let mut info = MONITORINFO { cbSize: size_of::<MONITORINFO>() as u32, ..Default::default() };
    let monitor = unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) };
    unsafe { GetMonitorInfoW(monitor, &mut info) }.as_bool() && info.rcMonitor == *rect
}

/// The process's executable path.
pub fn image_path(pid: u32) -> Option<String> {
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) }.ok()?;
    let mut buffer = [0u16; 1024];
    let mut length = buffer.len() as u32;
    let read = unsafe { QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, PWSTR(buffer.as_mut_ptr()), &mut length) };
    unsafe {
        let _ = CloseHandle(process);
    }
    read.ok()?;
    Some(String::from_utf16_lossy(&buffer[..length as usize]))
}

/// The `FileDescription` of the executable's version resource, in its first language.
fn file_description(path: &str) -> Option<String> {
    let wide: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
    let size = unsafe { GetFileVersionInfoSizeW(PCWSTR(wide.as_ptr()), None) };
    if size == 0 {
        return None;
    }
    let mut data = vec![0u8; size as usize];
    unsafe { GetFileVersionInfoW(PCWSTR(wide.as_ptr()), None, size, data.as_mut_ptr() as *mut c_void) }.ok()?;
    let query = |name: &str| -> Option<(*const c_void, u32)> {
        let name: Vec<u16> = name.encode_utf16().chain(Some(0)).collect();
        let (mut pointer, mut length) = (std::ptr::null_mut::<c_void>(), 0u32);
        unsafe { VerQueryValueW(data.as_ptr() as *const c_void, PCWSTR(name.as_ptr()), &mut pointer, &mut length) }.as_bool().then_some((pointer as *const c_void, length))
    };
    let (translation, length) = query(r"\VarFileInfo\Translation")?;
    if length < 4 {
        return None;
    }
    let (language, codepage) = unsafe { (*(translation as *const u16), *(translation as *const u16).add(1)) };
    let (text, length) = query(&format!(r"\StringFileInfo\{language:04x}{codepage:04x}\FileDescription"))?;
    let units = unsafe { std::slice::from_raw_parts(text as *const u16, length as usize) };
    let description = String::from_utf16_lossy(units).trim_end_matches('\0').trim().to_string();
    (!description.is_empty()).then_some(description)
}

/// Executable path → (file name, application name), read once per path.
static NAMES: Mutex<Option<HashMap<String, (String, String)>>> = Mutex::new(None);

fn names(pid: u32) -> (String, String) {
    let Some(path) = image_path(pid) else { return (String::new(), String::new()) };
    if let Some(known) = NAMES.lock().unwrap().get_or_insert_with(HashMap::new).get(&path) {
        return known.clone();
    }
    let exe = path.rsplit('\\').next().unwrap_or(&path).to_string();
    let stem = exe.strip_suffix(".exe").unwrap_or(&exe).to_string();
    let named = (exe, file_description(&path).unwrap_or(stem));
    NAMES.lock().unwrap().get_or_insert_with(HashMap::new).insert(path, named.clone());
    named
}

/// The windows of the app `app` names, as every tool taking an app name matches it: a running app's name or
/// executable, else (only when none goes by it) the Start menu entry it names, such as a localized name (a UWP frame
/// not yet resolved to its app by its title). Blocking (COM, on the pool).
pub fn app_windows<'a>(app: &str, windows: &'a [TopWindow]) -> Vec<&'a TopWindow> {
    let named: Vec<&TopWindow> = windows.iter().filter(|window| window.is_app(app)).collect();
    if !named.is_empty() {
        return named;
    }
    crate::apps::windows_of(app, windows)
}

/// The applications' top-level windows in z-order (front first). Blocking: run it on the pool.
pub fn top_windows() -> Vec<TopWindow> {
    windows_where(is_app_window)
}

/// How long [`top_windows_for`] waits for a just-started UWP app's frame to resolve.
const UNRESOLVED_WAIT: Duration = Duration::from_secs(1);

/// [`top_windows`], for an action on the app `app` names: while none of its windows is found other than an
/// unresolved UWP frame (see [`TopWindow::is_unresolved_frame`]) and such a frame is listed, lists again every
/// 100 ms for up to a second, so an app launched a moment ago is not answered as not found. Blocking.
pub fn top_windows_for(app: &str) -> Vec<TopWindow> {
    let deadline = Instant::now() + UNRESOLVED_WAIT;
    loop {
        let windows = top_windows();
        let found = app_windows(app, &windows).iter().any(|window| !window.is_unresolved_frame());
        if found || Instant::now() >= deadline || !windows.iter().any(TopWindow::is_unresolved_frame) {
            return windows;
        }
        std::thread::sleep(Duration::from_millis(100));
    }
}

/// Every shown top-level window with an area (dialogs, menus and popups too), in z-order. Blocking.
pub fn shown_windows() -> Vec<TopWindow> {
    windows_where(is_shown).into_iter().filter(|window| window.minimized || (window.frame.width > 0.0 && window.frame.height > 0.0)).collect()
}

/// The root window of the foreground window (a dialog itself, not its owner).
pub fn foreground_root() -> Option<isize> {
    let foreground = unsafe { GetForegroundWindow() };
    if foreground.is_invalid() {
        return None;
    }
    let root = unsafe { windows::Win32::UI::WindowsAndMessaging::GetAncestor(foreground, windows::Win32::UI::WindowsAndMessaging::GA_ROOT) };
    Some(if root.is_invalid() { foreground } else { root }.0 as isize)
}

/// The title and application name (as [`top_windows`] names it, a UWP frame's being the hosted app's) of the
/// top-level window at `point`, in input pixels, or of the foreground window. Cheap: no message is sent to another
/// process's window, so a hung one cannot stall it.
pub fn window_at(point: Option<(i32, i32)>) -> Option<(String, String)> {
    use windows::Win32::UI::WindowsAndMessaging::{GetAncestor, WindowFromPoint, GA_ROOT};
    let window = match point {
        Some((x, y)) => unsafe { WindowFromPoint(windows::Win32::Foundation::POINT { x, y }) },
        None => unsafe { GetForegroundWindow() },
    };
    if window.is_invalid() {
        return None;
    }
    let root = unsafe { GetAncestor(window, GA_ROOT) };
    let window = if root.is_invalid() { window } else { root };
    let (exe, app) = names(owner(window, &mut None));
    (!exe.is_empty()).then(|| (title(window), app))
}

fn top_level_handles() -> Vec<HWND> {
    let mut handles: Vec<HWND> = Vec::new();
    unsafe {
        let _ = EnumWindows(Some(collect), LPARAM(&mut handles as *mut _ as isize));
    }
    handles
}

fn process_of(hwnd: HWND) -> u32 {
    let mut pid = 0u32;
    unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
    pid
}

/// The frame ApplicationFrameHost.exe draws around a UWP app (Settings, Microsoft Store, Calculator).
const UWP_FRAME: &str = "ApplicationFrameWindow";
/// The UWP app's own window, in the app's process.
const UWP_CORE: &str = "Windows.UI.Core.CoreWindow";
/// The process that draws every UWP frame.
const FRAME_HOST: &str = "ApplicationFrameHost.exe";

/// The process a UWP frame hosts, from the frame's children as (class, process): the one whose core window runs in
/// another process than the frame's.
pub fn hosted_by(frame_pid: u32, children: &[(String, u32)]) -> Option<u32> {
    children.iter().find(|(class, pid)| class == UWP_CORE && *pid != frame_pid && *pid != 0).map(|(_, pid)| *pid)
}

/// The app process a UWP frame (`ApplicationFrameWindow`, owned by the shared ApplicationFrameHost.exe) hosts.
/// While the app runs, its core window is a child of the frame (EnumChildWindows walks them in the system, with no
/// recursion here). While it is minimized or suspended, Windows detaches the core window: it is then a top-level
/// (cloaked or hidden) window of the app's process, titled as the frame, so it is matched by title among
/// `top_level`. `None` when neither finds it: the frame stays ApplicationFrameHost.exe's (which is never ended).
fn hosted_app(frame: HWND, frame_pid: u32, top_level: &[HWND]) -> Option<u32> {
    let mut children: Vec<HWND> = Vec::new();
    unsafe {
        let _ = EnumChildWindows(Some(frame), Some(collect), LPARAM(&mut children as *mut _ as isize));
    }
    let children: Vec<(String, u32)> = children.into_iter().map(|child| (class_name(child), process_of(child))).collect();
    hosted_by(frame_pid, &children).or_else(|| {
        let name = title(frame);
        if name.is_empty() {
            return None;
        }
        let detached: Vec<(String, u32)> = top_level.iter().map(|window| (*window, class_name(*window))).filter(|(window, class)| class == UWP_CORE && title(*window) == name).map(|(window, class)| (class, process_of(window))).collect();
        hosted_by(frame_pid, &detached)
    })
}

/// The window's process, a UWP frame's being the hosted app's.
fn owner(hwnd: HWND, top_level: &mut Option<Vec<HWND>>) -> u32 {
    let pid = process_of(hwnd);
    if pid == 0 || class_name(hwnd) != UWP_FRAME {
        return pid;
    }
    hosted_app(hwnd, pid, top_level.get_or_insert_with(top_level_handles)).unwrap_or(pid)
}

fn windows_where(keep: fn(HWND) -> bool) -> Vec<TopWindow> {
    let handles = top_level_handles();
    let mut top_level = Some(handles.clone());
    let foreground = unsafe { GetForegroundWindow() };
    handles
        .into_iter()
        .filter(|hwnd| keep(*hwnd))
        .filter_map(|hwnd| {
            let pid = owner(hwnd, &mut top_level);
            let (exe, app) = names(pid);
            if pid == 0 || exe.is_empty() {
                return None;
            }
            let rect = window_frame(hwnd);
            let minimized = unsafe { IsIconic(hwnd) }.as_bool();
            Some(TopWindow { hwnd: hwnd.0 as isize, pid, exe, app, title: title(hwnd), frame: frame(&rect), focused: hwnd == foreground, minimized, fullscreen: !minimized && fills_its_monitor(hwnd, &rect) })
        })
        .collect()
}

/// The listed window, if it still exists and still belongs to the listed process.
fn find(window: &WindowInfo) -> Option<isize> {
    // HWNDs are 32-bit significant and sign-extended.
    let hwnd = HWND(window.number? as i32 as isize as *mut _);
    if !unsafe { IsWindow(Some(hwnd)) }.as_bool() {
        return None;
    }
    (owner(hwnd, &mut None) as i32 == window.pid).then_some(hwnd.0 as isize)
}

/// Runs `action` on the window on the pool (calls into another process can block); false when it is gone or does
/// not answer (a hung window would hold the pool thread).
fn act(window: &WindowInfo, action: impl FnOnce(HWND) -> bool + Send + 'static) -> Pending<bool> {
    let found = find(window);
    Box::pin(async move {
        match found {
            Some(hwnd) => pool::offload(move || crate::tree::answers(hwnd) && action(HWND(hwnd as *mut _))).await,
            None => false,
        }
    })
}

/// Waits, checking every 20 ms for up to a second, until `applied` holds for the window: its app applies a state
/// change asynchronously, and the next action or listing must see it. Whether it did.
fn settle(hwnd: HWND, applied: impl Fn(HWND) -> bool) -> bool {
    let deadline = Instant::now() + Duration::from_secs(1);
    while !applied(hwnd) {
        if Instant::now() > deadline {
            return false;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    true
}

/// Puts the window's visible frame at `frame`: its invisible resize borders (the window rectangle beyond the DWM
/// frame) are added back. A window without a sizing border keeps its size.
/// A minimized or maximized window back to its normal state, so its frame and borders can be measured.
fn restore(hwnd: HWND) {
    if unsafe { IsZoomed(hwnd) }.as_bool() || unsafe { IsIconic(hwnd) }.as_bool() {
        let _ = unsafe { ShowWindow(hwnd, SW_RESTORE) };
    }
}

fn place(hwnd: HWND, frame: Frame) -> FrameResult {
    restore(hwnd);
    let visible = window_frame(hwnd);
    let same_size = (visible.right - visible.left) as f64 == frame.width && (visible.bottom - visible.top) as f64 == frame.height;
    let style = unsafe { GetWindowLongW(hwnd, GWL_STYLE) } as u32;
    if !same_size && style & WS_THICKFRAME.0 == 0 {
        return FrameResult::NotResizable;
    }
    // Twice, measuring the borders each time: a window moved to a display of another scale resizes itself and its
    // borders on the way (WM_DPICHANGED).
    for _ in 0..2 {
        let visible = window_frame(hwnd);
        let mut outer = RECT::default();
        let _ = unsafe { GetWindowRect(hwnd, &mut outer) };
        let (left, top) = (visible.left - outer.left, visible.top - outer.top);
        let (right, bottom) = (outer.right - visible.right, outer.bottom - visible.bottom);
        let (x, y) = (frame.x as i32 - left, frame.y as i32 - top);
        let (width, height) = (frame.width as i32 + left + right, frame.height as i32 + top + bottom);
        if unsafe { SetWindowPos(hwnd, None, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE) }.is_err() {
            return FrameResult::Failed;
        }
    }
    FrameResult::Done
}

/// As the core lists windows: each app's windows numbered in z-order.
fn infos(windows: Vec<TopWindow>) -> Vec<WindowInfo> {
    let mut seen: HashMap<u32, usize> = HashMap::new();
    windows
        .into_iter()
        .map(|window| {
            let index = seen.entry(window.pid).or_insert(0);
            let info = WindowInfo { pid: window.pid as i32, index: *index, app: window.app, title: window.title, frame: window.frame, focused: window.focused, minimized: window.minimized, fullscreen: window.fullscreen, number: Some(window.hwnd as u32) };
            *index += 1;
            info
        })
        .collect()
}

/// The core's window source over [`top_windows`]. Acting on windows comes with the input tools; until then the
/// actions answer that nothing was done.
pub struct Win32Windows;

impl WindowSource for Win32Windows {
    fn windows(&self, app: Option<&str>) -> Pending<Vec<WindowInfo>> {
        let app = app.map(str::to_string);
        Box::pin(async move {
            infos(
                pool::offload(move || {
                    match app {
                        Some(app) => app_windows(&app, &top_windows_for(&app)).into_iter().cloned().collect(),
                        None => top_windows(),
                    }
                })
                .await,
            )
        })
    }
    fn reading(&self, app: Option<&str>) -> Pending<WindowReading> {
        let windows = self.windows(app);
        Box::pin(async move { WindowReading { windows: windows.await, complete: true } })
    }
    fn windows_of(&self, pid: i32) -> Pending<Vec<WindowInfo>> {
        Box::pin(async move { infos(pool::offload(top_windows).await.into_iter().filter(|window| window.pid as i32 == pid).collect()) })
    }
    fn app_exists(&self, app: &str) -> Pending<bool> {
        let app = app.to_string();
        Box::pin(pool::offload(move || !app_windows(&app, &top_windows_for(&app)).is_empty()))
    }
    fn focus(&self, window: &WindowInfo) -> Pending<bool> {
        act(window, |hwnd| crate::foreground::bring(hwnd.0 as isize))
    }
    fn set_minimized(&self, window: &WindowInfo, on: bool) -> Pending<bool> {
        act(window, move |hwnd| {
            let _ = unsafe { ShowWindowAsync(hwnd, if on { SW_MINIMIZE } else { SW_RESTORE }) };
            settle(hwnd, |hwnd| unsafe { IsIconic(hwnd) }.as_bool() == on)
        })
    }
    fn move_to(&self, window: &WindowInfo, x: f64, y: f64) -> Pending<bool> {
        act(window, move |hwnd| {
            restore(hwnd);
            let current = window_frame(hwnd);
            let frame = Frame::new(x, y, (current.right - current.left) as f64, (current.bottom - current.top) as f64);
            place(hwnd, frame) == FrameResult::Done
        })
    }
    fn resize(&self, window: &WindowInfo, width: f64, height: f64) -> Pending<bool> {
        act(window, move |hwnd| {
            restore(hwnd);
            let current = window_frame(hwnd);
            place(hwnd, Frame::new(current.left as f64, current.top as f64, width, height)) == FrameResult::Done
        })
    }
    fn close(&self, window: &WindowInfo) -> Pending<bool> {
        act(window, |hwnd| {
            let posted = unsafe { PostMessageW(Some(hwnd), WM_CLOSE, WPARAM(0), LPARAM(0)) }.is_ok();
            // Gone or hidden, unless the app asks first (to save): the request still stands.
            let _ = posted && settle(hwnd, |hwnd| !unsafe { IsWindow(Some(hwnd)) }.as_bool() || !unsafe { IsWindowVisible(hwnd) }.as_bool());
            posted
        })
    }
    fn responds(&self, window: &WindowInfo) -> Pending<bool> {
        let found = find(window);
        Box::pin(async move {
            match found {
                Some(hwnd) => pool::offload(move || crate::tree::answers(hwnd)).await,
                None => true,
            }
        })
    }
    fn set_frame(&self, window: &WindowInfo, frame: Frame) -> Pending<FrameResult> {
        let found = find(window);
        Box::pin(async move {
            match found {
                Some(hwnd) => pool::offload(move || if crate::tree::answers(hwnd) { place(HWND(hwnd as *mut _), frame) } else { FrameResult::Failed }).await,
                None => FrameResult::Failed,
            }
        })
    }
    /// Windows has no full screen mode for arbitrary windows.
    fn set_full_screen(&self, _window: &WindowInfo, _on: bool) -> Pending<bool> {
        Box::pin(async { false })
    }
    fn displays(&self) -> Pending<Vec<DisplayFrame>> {
        Box::pin(pool::offload(crate::displays::displays))
    }
}
