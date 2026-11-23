//! The tools' behaviour on macOS.
use desktop_mcp_core::mcp::Desktop;
use crate::nw::NwIo;
use desktop_mcp_core::clipboard::{Clipboard, ClipboardWrite};
use desktop_mcp_core::html::Element;
use desktop_mcp_core::open::{Application, Target as OpenTarget};
use desktop_mcp_core::shortcuts::ShortcutRun;
use desktop_mcp_core::spotlight::{Found, Search};
use desktop_mcp_core::permissions::{Permission, PermissionState};
use desktop_mcp_core::wait::RunningApp;
use desktop_mcp_core::input::Event as InputEvent;
use desktop_mcp_core::app::FrameChange;
use std::path::Path;
use desktop_mcp_core::io::Stream;
use desktop_mcp_core::shell::{Run, ShellMode};
use std::net::IpAddr;
use dispatch2::{DispatchObject, DispatchRetained, DispatchSource, DispatchTime};
use std::future::Future;
use std::pin::Pin;
use std::time::Duration;

/// `DISPATCH_TIME_FOREVER` as a timer interval: the timer fires once.
const NO_REPEAT: u64 = u64::MAX;

/// A one-shot libdispatch timer on the main queue. Dropping it cancels it, so an abandoned wait leaves nothing
/// armed.
struct Timer(DispatchRetained<DispatchSource>);

impl Drop for Timer {
    fn drop(&mut self) {
        self.0.cancel();
    }
}

#[derive(Default)]
pub struct MacDesktop {
    /// The pasteboard the Clipboard tool uses; the general one when `None`.
    pasteboard: Option<String>,
}

impl MacDesktop {
    pub fn new() -> MacDesktop {
        MacDesktop::default()
    }

    /// Uses the named pasteboard instead of the general one.
    pub fn with_pasteboard(name: &str) -> MacDesktop {
        MacDesktop { pasteboard: Some(name.to_string()) }
    }
}

impl Desktop for MacDesktop {
    fn sleep(&self, duration: Duration) -> Pin<Box<dyn Future<Output = ()>>> {
        Box::pin(sleep(duration))
    }

    /// `/bin/bash -c`, or `osascript -e` (AppleScript, or JXA when the first line asks for it). Both are child
    /// processes: one per call, so calls run concurrently, a timeout kills the whole process group, and a crash
    /// in a script engine takes down nothing but that call.
    fn shell(&self, command: &str, mode: ShellMode, timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>> {
        let argv = match mode {
            ShellMode::Shell => vec!["/bin/bash".to_string(), "-c".to_string(), command.to_string()],
            ShellMode::Osascript => std::iter::once("/usr/bin/osascript".to_string()).chain(osascript_arguments(command)).collect(),
            ShellMode::Powershell => return Box::pin(async { Err("powershell is not available on macOS".to_string()) }),
        };
        Box::pin(crate::process::run(argv, timeout))
    }

    /// `getaddrinfo`, on a libdispatch worker: it blocks.
    fn resolve(&self, host: &str) -> Pin<Box<dyn Future<Output = Result<Vec<IpAddr>, String>>>> {
        let host = host.to_string();
        Box::pin(crate::main_loop::offload(move || resolve(&host)))
    }

    fn connect(&self, host: &str, port: u16, tls: bool) -> Pin<Box<dyn Future<Output = Result<Box<dyn Stream>, String>>>> {
        let host = host.to_string();
        Box::pin(async move { NwIo::connect(&host, port, tls).await.map(|io| Box::new(io) as Box<dyn Stream>) })
    }

    fn parse_html(&self, body: Vec<u8>, charset: Option<String>) -> Pin<Box<dyn Future<Output = Option<Element>>>> {
        Box::pin(crate::main_loop::offload(move || crate::html::parse(&body, charset.as_deref())))
    }

    fn background(&self, work: Box<dyn FnOnce() + Send>) -> Pin<Box<dyn Future<Output = ()>>> {
        Box::pin(crate::main_loop::offload(work))
    }

    fn read_clipboard(&self, image_max_side: u32) -> Pin<Box<dyn Future<Output = Clipboard>>> {
        let name = self.pasteboard.clone();
        Box::pin(crate::main_loop::offload(move || crate::clipboard::read(name.as_deref(), image_max_side)))
    }

    fn write_clipboard(&self, contents: ClipboardWrite) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        let name = self.pasteboard.clone();
        Box::pin(crate::main_loop::offload(move || crate::clipboard::write(name.as_deref(), contents)))
    }

    fn open(&self, target: &OpenTarget, app: Option<&Path>) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        Box::pin(crate::open::open(target.clone(), app.map(Path::to_path_buf)))
    }

    fn reveal(&self, path: &Path) -> Pin<Box<dyn Future<Output = ()>>> {
        crate::open::reveal(path);
        Box::pin(async {})
    }

    fn notify(&self, message: &str, title: &str, subtitle: Option<&str>, sound: Option<&str>) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        let (message, title, subtitle, sound) = (message.to_string(), title.to_string(), subtitle.map(str::to_string), sound.map(str::to_string));
        Box::pin(async move { crate::notification::notify(&message, &title, subtitle.as_deref(), sound.as_deref()).await })
    }

    fn list_shortcuts(&self, folder: Option<&str>, timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>> {
        let folder = folder.map(str::to_string);
        Box::pin(async move { crate::shortcuts::list(folder.as_deref(), timeout).await })
    }

    fn run_shortcut(&self, name: &str, input: Option<&str>, timeout: Duration) -> Pin<Box<dyn Future<Output = Result<ShortcutRun, String>>>> {
        let (name, input) = (name.to_string(), input.map(str::to_string));
        Box::pin(async move { crate::shortcuts::run(&name, input.as_deref(), timeout).await })
    }

    fn search_files(&self, search: Search) -> Pin<Box<dyn Future<Output = Result<Found, String>>>> {
        Box::pin(crate::main_loop::offload(move || crate::spotlight::search(search)))
    }

    fn permissions(&self) -> Pin<Box<dyn Future<Output = PermissionState>>> {
        Box::pin(crate::main_loop::offload(crate::permissions::check))
    }
    fn request_permission(&self, permission: Permission) -> Pin<Box<dyn Future<Output = ()>>> {
        Box::pin(crate::main_loop::offload(move || crate::permissions::request(permission)))
    }

    fn running_apps(&self) -> Pin<Box<dyn Future<Output = Vec<RunningApp>>>> {
        let apps = crate::open::running_apps();
        Box::pin(async move { apps })
    }

    fn accessibility_trusted(&self) -> bool {
        crate::ax::trusted()
    }

    fn window_at(&self, at: Option<(i64, i64)>) -> Option<(String, String)> {
        crate::windows::window_at(at.map(|(x, y)| (x as f64, y as f64)))
    }

    fn post(&self, event: InputEvent) {
        crate::input::post(&event);
    }

    fn cursor_position(&self) -> (f64, f64) {
        crate::input::cursor_position()
    }

    fn secure_input_at(&self, x: i64, y: i64) -> Pin<Box<dyn Future<Output = bool>>> {
        Box::pin(crate::main_loop::offload(move || crate::secure_field::at(x, y)))
    }

    fn select_typing_layout(&self) -> Option<Box<dyn FnOnce()>> {
        crate::input::select_typing_layout()
    }

    fn launch_app(&self, name: &str) -> Pin<Box<dyn Future<Output = bool>>> {
        let name = name.to_string();
        Box::pin(async move { crate::apps::launch(&name).await })
    }

    fn activate_app(&self, name: &str) -> Pin<Box<dyn Future<Output = bool>>> {
        let name = name.to_string();
        Box::pin(crate::main_loop::offload(move || crate::apps::activate_by_name(&name)))
    }

    fn change_front_window(&self, change: Option<FrameChange>) -> Pin<Box<dyn Future<Output = bool>>> {
        Box::pin(crate::main_loop::offload(move || crate::apps::change_front_window(change)))
    }

    fn terminate(&self, pids: &[i32], force: bool) -> Pin<Box<dyn Future<Output = bool>>> {
        let signalled = crate::apps::terminate(pids, force);
        Box::pin(async move { signalled })
    }

    fn window_source(&self) -> std::rc::Rc<dyn desktop_mcp_core::window::WindowSource> {
        std::rc::Rc::new(crate::windows::AxWindowSource)
    }

    fn menu(&self, app: Option<String>, request: desktop_mcp_core::menu::Request) -> Pin<Box<dyn Future<Output = Result<String, String>>>> {
        // The active app owns the menu bar even when it has no window; the frontmost window can be another's.
        let owner = if app.is_none() { crate::menu::menu_bar_owner() } else { None };
        Box::pin(crate::main_loop::offload(move || crate::menu::answer(app.as_deref(), owner, request)))
    }

    fn snapshot(&self, use_vision: bool) -> Pin<Box<dyn Future<Output = desktop_mcp_core::snapshot::State>>> {
        // The menu bar owner is read here, on the main thread; everything else on workers.
        let owner = crate::menu::menu_bar_owner();
        Box::pin(async move {
            let mut state = crate::main_loop::offload(move || crate::snapshot::state(owner)).await;
            if use_vision {
                crate::screenshot::ensure_permission();
                // Scaled from the Retina capture to the active display's points, so image and Click coordinates
                // agree.
                let scale = state.active.as_ref().map(|a| a.bounding_box).and_then(|b| crate::displays::containing((b.left + b.right) as f64 / 2.0, (b.top + b.bottom) as f64 / 2.0)).map_or(1.0, |d| (1.0 / d.scale).min(1.0));
                state.screenshot = crate::screenshot::annotated(state.tree.interactive.clone(), scale).await;
            }
            state
        })
    }

    fn create_space(&self, open_delay: f64, close_after: bool) -> Pin<Box<dyn Future<Output = String> + '_>> {
        Box::pin(crate::spaces::create(self, open_delay, close_after))
    }

    fn read_text(&self, request: desktop_mcp_core::ocr::Request) -> Pin<Box<dyn Future<Output = Result<Vec<desktop_mcp_core::ocr::TextLine>, String>>>> {
        Box::pin(crate::ocr::read(request))
    }

    fn find_element(&self, app: Option<&str>, title: Option<&str>, role: Option<&str>, deadline: std::time::Instant) -> Pin<Box<dyn Future<Output = Option<desktop_mcp_core::element::FoundElement>>>> {
        // The running apps and the menu bar owner are read here, on the main thread; the walk runs on a worker.
        let pids = crate::element::pids(app, if app.is_none() { crate::menu::menu_bar_owner() } else { None });
        let (title, role) = (title.map(str::to_string), role.map(str::to_string));
        Box::pin(crate::main_loop::offload(move || crate::element::find(&pids, title.as_deref(), role.as_deref(), deadline)))
    }

    fn watch(&self, what: desktop_mcp_core::wait::Watch<'_>) -> desktop_mcp_core::wait::Changes {
        crate::observer::watch(what)
    }

    fn find_application(&self, name_or_bundle_id: &str) -> Pin<Box<dyn Future<Output = Option<Application>>>> {
        let name = name_or_bundle_id.to_string();
        Box::pin(crate::main_loop::offload(move || crate::open::application(&name)))
    }
}

fn resolve(host: &str) -> Result<Vec<IpAddr>, String> {
    let name = std::ffi::CString::new(host).map_err(|_| "nodename nor servname provided, or not known".to_string())?;
    let mut hints: libc::addrinfo = unsafe { std::mem::zeroed() };
    hints.ai_socktype = libc::SOCK_STREAM;
    let mut first: *mut libc::addrinfo = std::ptr::null_mut();
    let rc = unsafe { libc::getaddrinfo(name.as_ptr(), std::ptr::null(), &hints, &mut first) };
    if rc != 0 {
        return Err(unsafe { std::ffi::CStr::from_ptr(libc::gai_strerror(rc)) }.to_string_lossy().into_owned());
    }
    let mut addresses = Vec::new();
    let mut cursor = first;
    while let Some(info) = unsafe { cursor.as_ref() } {
        match info.ai_family {
            libc::AF_INET => {
                let address = unsafe { &*(info.ai_addr as *const libc::sockaddr_in) };
                addresses.push(IpAddr::from(u32::from_be(address.sin_addr.s_addr).to_be_bytes()));
            }
            libc::AF_INET6 => {
                let address = unsafe { &*(info.ai_addr as *const libc::sockaddr_in6) };
                addresses.push(IpAddr::from(address.sin6_addr.s6_addr));
            }
            _ => {}
        }
        cursor = info.ai_next;
    }
    unsafe { libc::freeaddrinfo(first) };
    Ok(addresses)
}

/// osascript's arguments for a script given as text. The language comes from the first line: a shebang naming
/// JavaScript (`#!/usr/bin/osascript -l JavaScript`) or a `// language: JavaScript` comment selects JXA; anything
/// else is AppleScript. A shebang line is dropped, since `-e` would hand it to the interpreter; the comment
/// stays, being valid JavaScript.
pub fn osascript_arguments(source: &str) -> Vec<String> {
    let (first_line, rest) = match source.split_once('\n') {
        Some((first, rest)) => (first.strip_suffix('\r').unwrap_or(first), rest),
        None => (source, ""),
    };
    let lowered = first_line.to_lowercase();
    let java_script = lowered.contains("javascript") && (lowered.starts_with("#!") || (lowered.starts_with("//") && lowered.contains("lang")));
    let body = if first_line.starts_with("#!") { rest } else { source };
    let language = if java_script { vec!["-l".to_string(), "JavaScript".to_string()] } else { vec![] };
    language.into_iter().chain(["-e".to_string(), body.to_string()]).collect()
}

/// Completes after `duration` on a libdispatch timer, so it needs no tick of the guest runtime and is as exact
/// as the system allows (a leeway of a hundredth of the delay, at most 100 ms).
pub(crate) async fn sleep(duration: Duration) {
    // Delays libdispatch cannot express (centuries) never end; the request's cancellation does.
    let Ok(start) = DispatchTime::try_from(duration) else { return std::future::pending().await };
    let (fired, wait) = tokio::sync::oneshot::channel::<()>();
    let fired = std::sync::Mutex::new(Some(fired));
    let source = crate::source::background(&raw const dispatch2::_dispatch_source_type_timer, 0, 0, move || {
        if let Some(fired) = fired.lock().unwrap().take() {
            let _ = fired.send(());
        }
    }, None);
    let leeway = (duration.as_nanos() / 100).min(100_000_000) as u64;
    source.set_timer(start, NO_REPEAT, leeway);
    source.resume();
    let timer = Timer(source);
    let _ = wait.await;
    drop(timer);
}
