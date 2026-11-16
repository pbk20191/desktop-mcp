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
use desktop_mcp_core::window::{DisplayFrame, Frame, FrameResult, WindowInfo, WindowReading, WindowSource};
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
        Rc::new(NoWindows)
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

    fn find_element(&self, _app: Option<&str>, _title: Option<&str>, _role: Option<&str>, _deadline: std::time::Instant) -> Pending<Option<desktop_mcp_core::element::FoundElement>> {
        ready(None)
    }
}

/// No windows until the window tools come (W3).
struct NoWindows;

impl WindowSource for NoWindows {
    fn windows(&self, _app: Option<&str>) -> Pending<Vec<WindowInfo>> {
        ready(Vec::new())
    }
    fn reading(&self, _app: Option<&str>) -> Pending<WindowReading> {
        ready(WindowReading { windows: Vec::new(), complete: true })
    }
    fn windows_of(&self, _pid: i32) -> Pending<Vec<WindowInfo>> {
        ready(Vec::new())
    }
    fn app_exists(&self, _app: &str) -> Pending<bool> {
        ready(false)
    }
    fn focus(&self, _window: &WindowInfo) -> Pending<bool> {
        ready(false)
    }
    fn set_minimized(&self, _window: &WindowInfo, _on: bool) -> Pending<bool> {
        ready(false)
    }
    fn move_to(&self, _window: &WindowInfo, _x: f64, _y: f64) -> Pending<bool> {
        ready(false)
    }
    fn resize(&self, _window: &WindowInfo, _width: f64, _height: f64) -> Pending<bool> {
        ready(false)
    }
    fn close(&self, _window: &WindowInfo) -> Pending<bool> {
        ready(false)
    }
    fn set_frame(&self, _window: &WindowInfo, _frame: Frame) -> Pending<FrameResult> {
        ready(FrameResult::Failed)
    }
    fn set_full_screen(&self, _window: &WindowInfo, _on: bool) -> Pending<bool> {
        ready(false)
    }
    fn displays(&self) -> Pending<Vec<DisplayFrame>> {
        ready(Vec::new())
    }
}
