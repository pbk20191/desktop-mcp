//! Application windows through the Accessibility API. Every AX call runs on a libdispatch worker, and an action
//! finds its window again in the same call that acts on it.
use crate::apps::frontmost;
use crate::ax::{Element, Value};
use desktop_mcp_core::wait::matches;
use desktop_mcp_core::window::{DisplayFrame, Frame, FrameResult, WindowInfo, WindowReading, WindowSource};
use objc2_app_kit::{NSApplicationActivationPolicy, NSWorkspace};
use std::future::Future;
use std::pin::Pin;
use std::sync::OnceLock;

type Pending<T> = Pin<Box<dyn Future<Output = T>>>;

const INFO: [&str; 5] = ["AXTitle", "AXPosition", "AXSize", "AXMinimized", "AXFullScreen"];

/// `_AXUIElementGetWindow`, private, looked up at run time so that its absence only loses window numbers.
fn get_window() -> Option<unsafe extern "C" fn(*const std::ffi::c_void, *mut u32) -> i32> {
    static ADDRESS: OnceLock<usize> = OnceLock::new();
    let address = *ADDRESS.get_or_init(|| unsafe { libc::dlsym(libc::RTLD_DEFAULT, c"_AXUIElementGetWindow".as_ptr()) } as usize);
    (address != 0).then(|| unsafe { std::mem::transmute::<usize, unsafe extern "C" fn(*const std::ffi::c_void, *mut u32) -> i32>(address) })
}

/// The window server's number for a window element, when it can be read.
pub fn number(window: &Element) -> Option<u32> {
    let get = get_window()?;
    let mut number = 0u32;
    (unsafe { get(window.as_ptr(), &mut number) } == 0 && number != 0).then_some(number)
}

/// Whether the private symbol behind window numbers exists here.
pub fn numbers_available() -> bool {
    get_window().is_some()
}

/// The title and owner of the window input at `point` (or keys) reaches, from the window server. Its title needs
/// Screen Recording; without it the title is read through Accessibility, by window number, within the messaging
/// timeout.
pub fn window_at(point: Option<(f64, f64)>) -> Option<(String, String)> {
    let screen = crate::apps::OnScreenWindow::all();
    let window = crate::apps::OnScreenWindow::at(&screen, point)?;
    let mut title = window.title.clone();
    if title.is_empty() && window.is_application() {
        let number = u32::try_from(window.window_id).ok();
        let elements = application(window.pid).elements("AXWindows").unwrap_or_default();
        if let Some(found) = elements.iter().find(|element| number.is_some() && self::number(element) == number) {
            title = found.attribute("AXTitle").map(|value| value.text().to_string()).unwrap_or_default();
        }
    }
    Some((title, window.owner_name.clone()))
}

/// Regular apps matching `app` (all of them without one): pid and localized name (else bundle id).
fn regular_apps(app: Option<&str>) -> Vec<(i32, String)> {
    NSWorkspace::sharedWorkspace()
        .runningApplications()
        .iter()
        .filter(|running| !running.isTerminated() && running.activationPolicy() == NSApplicationActivationPolicy::Regular)
        .filter(|running| {
            let name = running.localizedName().map(|n| n.to_string());
            let bundle_id = running.bundleIdentifier().map(|b| b.to_string());
            app.is_none_or(|app| matches(name.as_deref(), bundle_id.as_deref(), app))
        })
        .map(|running| (running.processIdentifier(), running.localizedName().map(|n| n.to_string()).or_else(|| running.bundleIdentifier().map(|b| b.to_string())).unwrap_or_default()))
        .collect()
}

fn application(pid: i32) -> Element {
    let app = Element::application(pid);
    app.set_messaging_timeout(crate::tuning::AX_MESSAGING_TIMEOUT);
    app
}

fn get<'a>(found: &'a [(String, Value)], name: &str) -> Option<&'a Value> {
    found.iter().find(|(key, _)| key == name).map(|(_, value)| value)
}

fn info(pid: i32, app: &str, index: usize, element: &Element, found: &[(String, Value)], focused: Option<&Element>) -> WindowInfo {
    let (x, y) = match get(found, "AXPosition") {
        Some(Value::Point(x, y)) => (*x, *y),
        _ => (0.0, 0.0),
    };
    let (width, height) = match get(found, "AXSize") {
        Some(Value::Size(w, h)) => (*w, *h),
        _ => (0.0, 0.0),
    };
    WindowInfo {
        pid,
        index,
        app: app.to_string(),
        title: get(found, "AXTitle").map(|v| v.text().to_string()).unwrap_or_default(),
        frame: Frame::new(x, y, width, height),
        focused: focused == Some(element),
        minimized: get(found, "AXMinimized").is_some_and(Value::is_true),
        fullscreen: get(found, "AXFullScreen").is_some_and(Value::is_true),
        number: number(element),
    }
}

/// The app's windows whose role is AXWindow, with their indices in its window list; `None` when it does not
/// answer: a busy app is not one without windows.
fn read(pid: i32, app: &str, frontmost: Option<i32>) -> Option<Vec<WindowInfo>> {
    let element = application(pid);
    let elements = element.elements("AXWindows")?;
    let focused = (Some(pid) == frontmost).then(|| element.attribute("AXFocusedWindow").and_then(|v| v.element().cloned())).flatten();
    let mut windows = Vec::new();
    for (index, window) in elements.iter().enumerate() {
        let names: Vec<&str> = std::iter::once("AXRole").chain(INFO).collect();
        let found = window.attributes(&names)?;
        if get(&found, "AXRole").map(Value::text) != Some("AXWindow") {
            continue;
        }
        windows.push(info(pid, app, index, window, &found, focused.as_ref()));
    }
    Some(windows)
}

/// Finds `window`'s element again (by number, or by index with the same title and frame).
fn find(window: &WindowInfo) -> Option<Element> {
    let element = application(window.pid);
    let elements: Vec<(usize, Element)> = element
        .elements("AXWindows")?
        .into_iter()
        .enumerate()
        .filter(|(_, w)| w.attribute("AXRole").is_some_and(|role| role.text() == "AXWindow"))
        .collect();
    match window.number {
        Some(wanted) => elements.into_iter().find(|(_, w)| number(w) == Some(wanted)).map(|(_, w)| w),
        None => {
            let (index, found) = elements.into_iter().find(|(index, _)| *index == window.index)?;
            let now = info(window.pid, &window.app, index, &found, &found.attributes(&INFO).unwrap_or_default(), None);
            now.looks_like(window).then_some(found)
        }
    }
}

/// Runs `act` on the window's element off the main thread; `gone` when the window is gone.
fn acting<T: Send + 'static>(window: &WindowInfo, gone: T, act: impl FnOnce(&Element) -> T + Send + 'static) -> Pending<T> {
    let window = window.clone();
    Box::pin(crate::main_loop::offload(move || match find(&window) {
        Some(element) => act(&element),
        None => gone,
    }))
}

#[derive(Default)]
pub struct AxWindowSource;

impl AxWindowSource {
    /// Every matching app's windows, read concurrently (the first AX call to a process is slow); `None` per app that
    /// did not answer.
    async fn read_all(app: Option<&str>) -> Vec<Option<Vec<WindowInfo>>> {
        let app = app.map(str::to_string);
        let (apps, frontmost) = crate::main_loop::offload(move || (regular_apps(app.as_deref()), frontmost())).await;
        // Each offload starts on its own worker at once; awaiting them in turn still lets them run together.
        let reads: Vec<_> = apps.into_iter().map(|(pid, name)| crate::main_loop::offload(move || read(pid, &name, frontmost))).collect();
        let mut lists = Vec::new();
        for read in reads {
            lists.push(read.await);
        }
        lists
    }
}

impl WindowSource for AxWindowSource {
    fn windows(&self, app: Option<&str>) -> Pending<Vec<WindowInfo>> {
        let app = app.map(str::to_string);
        Box::pin(async move { Self::read_all(app.as_deref()).await.into_iter().flatten().flatten().collect() })
    }

    fn reading(&self, app: Option<&str>) -> Pending<WindowReading> {
        let app = app.map(str::to_string);
        Box::pin(async move {
            let lists = Self::read_all(app.as_deref()).await;
            let complete = lists.iter().all(Option::is_some);
            WindowReading { windows: lists.into_iter().flatten().flatten().collect(), complete }
        })
    }

    fn windows_of(&self, pid: i32) -> Pending<Vec<WindowInfo>> {
        Box::pin(crate::main_loop::offload(move || {
            let name = regular_apps(None).into_iter().find(|(p, _)| *p == pid).map(|(_, name)| name).unwrap_or_default();
            read(pid, &name, frontmost()).unwrap_or_default()
        }))
    }

    fn app_exists(&self, app: &str) -> Pending<bool> {
        let app = app.to_string();
        Box::pin(crate::main_loop::offload(move || !regular_apps(Some(&app)).is_empty()))
    }

    fn focus(&self, window: &WindowInfo) -> Pending<bool> {
        let pid = window.pid;
        acting(window, false, move |element| {
            if element.attribute("AXMinimized").is_some_and(|v| v.is_true()) {
                element.set_bool("AXMinimized", false);
            }
            let activated = crate::apps::activate(pid);
            let raised = element.perform("AXRaise");
            let main = element.set_bool("AXMain", true);
            activated || raised || main
        })
    }

    fn set_minimized(&self, window: &WindowInfo, on: bool) -> Pending<bool> {
        acting(window, false, move |element| element.set_bool("AXMinimized", on))
    }

    fn move_to(&self, window: &WindowInfo, x: f64, y: f64) -> Pending<bool> {
        acting(window, false, move |element| element.set_position(x, y))
    }

    fn resize(&self, window: &WindowInfo, width: f64, height: f64) -> Pending<bool> {
        acting(window, false, move |element| element.set_size(width, height))
    }

    fn close(&self, window: &WindowInfo) -> Pending<bool> {
        acting(window, false, |element| element.attribute("AXCloseButton").and_then(|b| b.element().cloned()).is_some_and(|button| button.perform("AXPress")))
    }

    fn set_frame(&self, window: &WindowInfo, frame: Frame) -> Pending<FrameResult> {
        acting(window, FrameResult::Failed, move |element| {
            // A fixed-size window refuses AXSize even for its own size: only set it when it changes.
            let (width, height) = match element.attribute("AXSize") {
                Some(Value::Size(w, h)) => (w, h),
                _ => (0.0, 0.0),
            };
            let resize = Frame::new(0.0, 0.0, width, height).rounded() != Frame::new(0.0, 0.0, frame.width, frame.height).rounded();
            if resize && !element.is_settable("AXSize") {
                return FrameResult::NotResizable;
            }
            let mut done = true;
            if resize {
                element.set_position(frame.x, frame.y);
                done = element.set_size(frame.width, frame.height);
            }
            done = element.set_position(frame.x, frame.y) && done;
            if done { FrameResult::Done } else { FrameResult::Failed }
        })
    }

    fn set_full_screen(&self, window: &WindowInfo, on: bool) -> Pending<bool> {
        acting(window, false, move |element| element.is_settable("AXFullScreen") && element.set_bool("AXFullScreen", on))
    }

    fn displays(&self) -> Pending<Vec<DisplayFrame>> {
        let frames = objc2::MainThreadMarker::new().map(crate::displays::frames).unwrap_or_default();
        Box::pin(async move { frames })
    }
}
