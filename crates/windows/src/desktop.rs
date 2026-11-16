//! The tools' Windows side. W1 has what needs no desktop access (processes, network, clipboard, toasts); the
//! accessibility tree, input and windows come in later builds, and until then answer as having found nothing.
use crate::pool;
use desktop_mcp_core::app::FrameChange;
use desktop_mcp_core::clipboard::{Clipboard, ClipboardWrite};
use desktop_mcp_core::html::Element;
use desktop_mcp_core::input::Event as InputEvent;
use desktop_mcp_core::io::Stream;
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_core::open::{Application, Target as OpenTarget};
use desktop_mcp_core::permissions::{Automation, Permission, PermissionState};
use desktop_mcp_core::shell::{Run, ShellMode};
use desktop_mcp_core::shortcuts::ShortcutRun;
use desktop_mcp_core::spotlight::{Found, Search};
use desktop_mcp_core::wait::RunningApp;
use desktop_mcp_core::window::WindowSource;
use std::future::Future;
use std::net::IpAddr;
use std::path::Path;
use std::pin::Pin;
use std::rc::Rc;
use std::time::Duration;

type Pending<T> = Pin<Box<dyn Future<Output = T>>>;

const NOT_YET: &str = "not available on Windows yet";

fn ready<T: 'static>(value: T) -> Pending<T> {
    Box::pin(async move { value })
}

#[derive(Default)]
pub struct WindowsDesktop {
    /// Set when the system dropped posted input; read once by `take_input_failure`.
    dropped: std::cell::Cell<bool>,
}

impl WindowsDesktop {
    pub fn new() -> WindowsDesktop {
        WindowsDesktop::default()
    }
}

impl Desktop for WindowsDesktop {
    fn sleep(&self, duration: Duration) -> Pending<()> {
        Box::pin(pool::sleep(duration))
    }

    fn shell(&self, command: &str, mode: ShellMode, timeout: Duration) -> Pending<Result<Run, String>> {
        if mode == ShellMode::Osascript {
            return ready(Err("osascript is not available on Windows; commands run in PowerShell".to_string()));
        }
        let command = command.to_string();
        Box::pin(async move { crate::shell::run(&command, timeout).await })
    }

    fn resolve(&self, host: &str) -> Pending<Result<Vec<IpAddr>, String>> {
        let host = host.to_string();
        Box::pin(async move { crate::net::resolve(&host).await })
    }

    fn connect(&self, host: &str, port: u16, tls: bool) -> Pending<Result<Box<dyn Stream>, String>> {
        let host = host.to_string();
        Box::pin(async move { crate::net::SocketIo::connect(&host, port, tls).await.map(|io| Box::new(io) as Box<dyn Stream>) })
    }

    fn parse_html(&self, body: Vec<u8>, charset: Option<String>) -> Pending<Option<Element>> {
        Box::pin(pool::offload(move || crate::html::parse(&body, charset.as_deref())))
    }

    fn background(&self, work: Box<dyn FnOnce() + Send>) -> Pending<()> {
        Box::pin(pool::offload(work))
    }

    /// On a pool thread (Win32 clipboard), given up after a few seconds: a clipboard owner that does not answer
    /// never holds up the main loop.
    fn read_clipboard(&self, image_max_side: u32) -> Pending<Clipboard> {
        Box::pin(crate::clipboard::read(image_max_side))
    }

    fn write_clipboard(&self, contents: ClipboardWrite) -> Pending<Result<(), String>> {
        Box::pin(crate::clipboard::write(contents))
    }

    fn open(&self, _target: &OpenTarget, _app: Option<&Path>) -> Pending<Result<(), String>> {
        ready(Err(NOT_YET.to_string()))
    }

    fn reveal(&self, _path: &Path) -> Pending<()> {
        ready(())
    }

    fn find_application(&self, _name_or_bundle_id: &str) -> Pending<Option<Application>> {
        ready(None)
    }

    fn notify(&self, message: &str, title: &str, subtitle: Option<&str>, sound: Option<&str>) -> Pending<Result<(), String>> {
        ready(crate::notification::notify(message, title, subtitle, sound))
    }

    fn list_shortcuts(&self, _folder: Option<&str>, _timeout: Duration) -> Pending<Result<Run, String>> {
        ready(Err(NOT_YET.to_string()))
    }

    fn run_shortcut(&self, _name: &str, _input: Option<&str>, _timeout: Duration) -> Pending<Result<ShortcutRun, String>> {
        ready(Err(NOT_YET.to_string()))
    }

    fn search_files(&self, _search: Search) -> Pending<Result<Found, String>> {
        ready(Err(NOT_YET.to_string()))
    }

    /// Windows has no per-app grants like macOS's; UI Automation and input need none.
    fn permissions(&self) -> Pending<PermissionState> {
        ready(PermissionState { accessibility: true, screen_recording: true, automation: Automation::Granted })
    }

    fn request_permission(&self, _permission: Permission) -> Pending<()> {
        ready(())
    }

    /// The applications with a window, once each.
    fn running_apps(&self) -> Pending<Vec<RunningApp>> {
        Box::pin(async {
            let mut apps: Vec<RunningApp> = Vec::new();
            for window in pool::offload(crate::windows_list::top_windows).await {
                if !apps.iter().any(|app| app.pid == window.pid as i32) {
                    apps.push(RunningApp { pid: window.pid as i32, name: Some(window.app), bundle_id: Some(window.exe) });
                }
            }
            apps
        })
    }

    fn accessibility_trusted(&self) -> bool {
        true
    }

    /// Sent at once with `SendInput` (it only queues the events).
    fn post(&self, event: InputEvent) {
        if !crate::input::send(&event) {
            self.dropped.set(true);
        }
    }

    fn take_input_failure(&self) -> Option<String> {
        self.dropped.replace(false).then(|| {
            if crate::integrity::input_blocked_at(None) {
                crate::integrity::refusal(None)
            } else {
                "Windows did not deliver the input (the screen may be locked or a secure prompt is showing).".to_string()
            }
        })
    }

    fn cursor_position(&self) -> (f64, f64) {
        let mut point = windows::Win32::Foundation::POINT::default();
        let _ = unsafe { windows::Win32::UI::WindowsAndMessaging::GetCursorPos(&mut point) };
        (point.x as f64, point.y as f64)
    }

    /// Whether the element at the point is a password field (UI Automation, on the pool).
    fn secure_input_at(&self, x: i64, y: i64) -> Pending<bool> {
        Box::pin(pool::offload(move || crate::uia::is_password_at(x as i32, y as i32)))
    }

    fn input_refusal(&self, at: Option<(i64, i64)>) -> Option<String> {
        crate::integrity::input_blocked_at(at.map(|(x, y)| (x as i32, y as i32))).then(|| crate::integrity::refusal(at))
    }

    fn editing_keys(&self) -> &'static desktop_mcp_core::input::EditingKeys {
        &crate::input::EDITING
    }

    fn extra_keys(&self) -> &'static [&'static str] {
        crate::input::EXTRA_KEYS
    }

    fn select_typing_layout(&self) -> Option<Box<dyn FnOnce()>> {
        None
    }

    /// An executable path, or the app the Apps folder (Start menu) names; on the pool.
    fn launch_app(&self, name: &str) -> Pending<bool> {
        let name = name.to_string();
        Box::pin(pool::offload(move || crate::apps::launch(&name)))
    }

    /// The app's front window, brought to the foreground; the app by its Start menu name when no running app goes
    /// by `name`.
    fn activate_app(&self, name: &str) -> Pending<bool> {
        let name = name.to_string();
        Box::pin(pool::offload(move || {
            let windows = crate::windows_list::top_windows();
            let front = windows.iter().find(|window| window.is_app(&name)).or_else(|| crate::apps::windows_of(&name, &windows).into_iter().next());
            front.is_some_and(|window| crate::foreground::bring(window.hwnd))
        }))
    }

    /// The processes with a window of the Start menu entry `name` names.
    fn installed_app_pids(&self, name: &str) -> Pending<Vec<i32>> {
        let name = name.to_string();
        Box::pin(pool::offload(move || {
            let windows = crate::windows_list::top_windows();
            let mut pids: Vec<i32> = crate::apps::windows_of(&name, &windows).into_iter().map(|window| window.pid as i32).collect();
            pids.sort_unstable();
            pids.dedup();
            pids
        }))
    }

    /// Moves or resizes the focused window (or with `None` only looks for one).
    fn change_front_window(&self, change: Option<FrameChange>) -> Pending<bool> {
        Box::pin(async move {
            let windows = pool::offload(crate::windows_list::top_windows).await;
            let Some(front) = windows.into_iter().find(|window| window.focused) else { return false };
            let source = crate::windows_list::Win32Windows;
            let info = desktop_mcp_core::window::WindowInfo {
                pid: front.pid as i32,
                index: 0,
                app: front.app,
                title: front.title,
                frame: front.frame,
                focused: true,
                minimized: front.minimized,
                fullscreen: front.fullscreen,
                number: Some(front.hwnd as u32),
            };
            match change {
                None => true,
                Some(FrameChange::Position(x, y)) => source.move_to(&info, x, y).await,
                Some(FrameChange::Size(width, height)) => source.resize(&info, width, height).await,
            }
        })
    }

    /// `force`: ends the processes; otherwise asks each of their windows to close. The shell is never a target.
    fn terminate(&self, pids: &[i32], force: bool) -> Pending<bool> {
        let pids: Vec<u32> = pids.iter().map(|pid| *pid as u32).collect();
        Box::pin(pool::offload(move || {
            use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM, WPARAM};
            use windows::Win32::System::Threading::{OpenProcess, TerminateProcess, PROCESS_TERMINATE};
            use windows::Win32::UI::WindowsAndMessaging::{PostMessageW, WM_CLOSE};
            let windows = crate::windows_list::top_windows();
            let shell: Vec<u32> = windows.iter().filter(|window| window.exe.eq_ignore_ascii_case("explorer.exe")).map(|window| window.pid).collect();
            let mut signalled = false;
            for pid in pids.into_iter().filter(|pid| !shell.contains(pid)) {
                if force {
                    if let Ok(process) = unsafe { OpenProcess(PROCESS_TERMINATE, false, pid) } {
                        signalled |= unsafe { TerminateProcess(process, 1) }.is_ok();
                        unsafe {
                            let _ = CloseHandle(process);
                        }
                    }
                } else {
                    for window in windows.iter().filter(|window| window.pid == pid) {
                        signalled |= unsafe { PostMessageW(Some(HWND(window.hwnd as *mut _)), WM_CLOSE, WPARAM(0), LPARAM(0)) }.is_ok();
                    }
                }
            }
            signalled
        }))
    }

    fn window_source(&self) -> Rc<dyn WindowSource> {
        Rc::new(crate::windows_list::Win32Windows)
    }

    fn menu(&self, _app: Option<String>, _request: desktop_mcp_core::menu::Request) -> Pending<Result<String, String>> {
        ready(Err(NOT_YET.to_string()))
    }

    /// Windows and their UI Automation elements, read on the pool; with `use_vision`, the focused window's display
    /// captured with the interactive elements numbered on it.
    fn snapshot(&self, use_vision: bool) -> Pending<desktop_mcp_core::snapshot::State> {
        Box::pin(async move {
            let (walk, displays, desktop) = pool::offload(|| {
                let windows = crate::windows_list::top_windows();
                (crate::tree::walk(&windows, &crate::tree::Limits::default()), crate::displays::displays(), crate::virtual_desktops::current_desktop_line())
            })
            .await;
            let mut notes: Vec<String> = desktop.into_iter().collect();
            if walk.stopped_early {
                notes.push("The element list was cut short (too many elements or too slow to read); Window list narrows it to one app.".to_string());
            }
            let mut state = desktop_mcp_core::snapshot::State {
                active: walk.active.clone(),
                windows: walk.windows,
                tree: walk.tree,
                coordinate_space: crate::displays::coordinate_space(&displays),
                trusted: true,
                screenshot: None,
                notes,
                screenshot_hint: None,
            };
            if use_vision {
                let focused = walk.active.as_ref().map(|view| {
                    let b = &view.bounding_box;
                    desktop_mcp_core::window::Frame::new(b.left as f64, b.top as f64, b.width as f64, b.height as f64)
                });
                let display = focused.and_then(|frame| crate::ocr::display_of(&displays, frame)).or_else(|| displays.iter().find(|display| display.main)).cloned();
                let shot = match display {
                    Some(display) => crate::capture::capture_display(&display).await,
                    None => Err("no display".to_string()),
                };
                let elements = state.tree.interactive.clone();
                let image = match shot {
                    Ok(mut pixels) => pool::offload(move || {
                        crate::imaging::annotate(&mut pixels, &elements)?;
                        crate::imaging::encode_png(&pixels, None)
                    })
                    .await,
                    Err(reason) => Err(reason),
                };
                match image {
                    Ok(png) => state.screenshot = Some(png),
                    Err(reason) => {
                        state.screenshot_hint = Some(format!("Note: Screenshot capture failed ({reason}), so no annotated image is attached. The elements\nlisted above are still valid; call Snapshot again if you need the screenshot."))
                    }
                }
            }
            state
        })
    }

    fn create_space(&self, _open_delay: f64, _close_after: bool) -> Pin<Box<dyn Future<Output = String> + '_>> {
        Box::pin(async { NOT_YET.to_string() })
    }

    fn read_text(&self, request: desktop_mcp_core::ocr::Request) -> Pending<Result<Vec<desktop_mcp_core::ocr::TextLine>, String>> {
        Box::pin(async move {
            let focused = pool::offload(|| crate::windows_list::top_windows().into_iter().find(|window| window.focused && !window.minimized).map(|window| window.frame)).await;
            crate::ocr::read_text(request, focused).await
        })
    }

    /// An element of `app`'s windows (else the focused window's app) by UIA control type and name, on the pool.
    fn find_element(&self, app: Option<&str>, title: Option<&str>, role: Option<&str>, deadline: std::time::Instant) -> Pending<Option<desktop_mcp_core::element::FoundElement>> {
        let (app, title, role) = (app.map(str::to_string), title.map(str::to_string), role.map(str::to_string));
        Box::pin(pool::offload(move || {
            let windows = crate::windows_list::top_windows();
            let owner = match &app {
                Some(app) => windows.iter().find(|window| window.is_app(app)).map(|window| window.pid),
                None => windows.iter().find(|window| window.focused).map(|window| window.pid),
            }?;
            let mut fallback = None;
            for window in windows.iter().filter(|window| window.pid == owner && !window.minimized) {
                if !crate::tree::answers(window.hwnd) {
                    continue;
                }
                let Some(source) = crate::uia::WindowTree::new(window.hwnd) else { continue };
                if let Some((element, exact)) = crate::tree::find_in(&source, title.as_deref(), role.as_deref(), deadline) {
                    let (left, top, right, bottom) = element.rect;
                    let found = desktop_mcp_core::element::FoundElement { role: element.control_type, title: element.name, center: ((left + right) / 2.0, (top + bottom) / 2.0) };
                    if exact {
                        return Some(found);
                    }
                    fallback.get_or_insert(found);
                }
            }
            fallback
        }))
    }
}
