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
    EnumWindows, GetClassNameW, GetForegroundWindow, GetWindow, GetWindowLongW, GetWindowRect, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsIconic,
    IsWindow, IsWindowVisible, IsZoomed, PostMessageW, SetWindowPos, ShowWindow, ShowWindowAsync, GWL_EXSTYLE, GWL_STYLE, GW_OWNER, SWP_NOACTIVATE, SWP_NOZORDER, SW_MINIMIZE,
    SW_RESTORE, WM_CLOSE, WS_EX_APPWINDOW, WS_EX_TOOLWINDOW, WS_THICKFRAME,
};

type Pending<T> = Pin<Box<dyn Future<Output = T>>>;

#[derive(Clone, Debug)]
pub struct TopWindow {
    pub hwnd: isize,
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

/// The applications' top-level windows in z-order (front first). Blocking: run it on the pool.
pub fn top_windows() -> Vec<TopWindow> {
    windows_where(is_app_window)
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

fn windows_where(keep: fn(HWND) -> bool) -> Vec<TopWindow> {
    let mut handles: Vec<HWND> = Vec::new();
    unsafe {
        let _ = EnumWindows(Some(collect), LPARAM(&mut handles as *mut _ as isize));
    }
    let foreground = unsafe { GetForegroundWindow() };
    handles
        .into_iter()
        .filter(|hwnd| keep(*hwnd))
        .filter_map(|hwnd| {
            let mut pid = 0u32;
            unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
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
    let mut pid = 0u32;
    unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
    (pid as i32 == window.pid).then_some(hwnd.0 as isize)
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
        Box::pin(async move { infos(pool::offload(top_windows).await.into_iter().filter(|window| app.as_deref().is_none_or(|app| window.is_app(app))).collect()) })
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
        Box::pin(async move { pool::offload(top_windows).await.iter().any(|window| window.is_app(&app)) })
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
