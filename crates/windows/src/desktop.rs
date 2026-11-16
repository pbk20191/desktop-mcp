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
pub struct WindowsDesktop;

impl WindowsDesktop {
    pub fn new() -> WindowsDesktop {
        WindowsDesktop
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

    fn running_apps(&self) -> Pending<Vec<RunningApp>> {
        ready(Vec::new())
    }

    fn accessibility_trusted(&self) -> bool {
        true
    }

    fn post(&self, _event: InputEvent) {}

    fn cursor_position(&self) -> (f64, f64) {
        (0.0, 0.0)
    }

    fn secure_input_at(&self, _x: i64, _y: i64) -> Pending<bool> {
        ready(false)
    }

    fn select_typing_layout(&self) -> Option<Box<dyn FnOnce()>> {
        None
    }

    fn launch_app(&self, _name: &str) -> Pending<bool> {
        ready(false)
    }

    fn activate_app(&self, _name: &str) -> Pending<bool> {
        ready(false)
    }

    fn change_front_window(&self, _change: Option<FrameChange>) -> Pending<bool> {
        ready(false)
    }

    fn terminate(&self, _pids: &[i32], _force: bool) -> Pending<bool> {
        ready(false)
    }

    fn window_source(&self) -> Rc<dyn WindowSource> {
        Rc::new(crate::windows_list::Win32Windows)
    }

    fn menu(&self, _app: Option<String>, _request: desktop_mcp_core::menu::Request) -> Pending<Result<String, String>> {
        ready(Err(NOT_YET.to_string()))
    }

    fn snapshot(&self, _use_vision: bool) -> Pending<desktop_mcp_core::snapshot::State> {
        ready(Default::default())
    }

    fn create_space(&self, _open_delay: f64, _close_after: bool) -> Pin<Box<dyn Future<Output = String> + '_>> {
        Box::pin(async { NOT_YET.to_string() })
    }

    fn read_text(&self, _request: desktop_mcp_core::ocr::Request) -> Pending<Result<Vec<desktop_mcp_core::ocr::TextLine>, String>> {
        ready(Err(NOT_YET.to_string()))
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
