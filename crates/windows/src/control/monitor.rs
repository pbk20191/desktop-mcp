//! Physical input, watched on the main thread's message loop: low-level keyboard and mouse hooks (which swallow
//! input while the heartbeat keeps the deadline in the future) and a message-only window that receives Raw Input
//! for relative mouse motion. Callbacks only read atomics, decide with the filter and push into a bounded queue;
//! the heartbeat (`coordinator`) drains it on the thread pool.
use super::filter::{Filter, HookEvent, Kind};
use super::machine::Physical;
use std::cell::RefCell;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{sync_channel, Receiver, SyncSender, TrySendError};
use std::sync::OnceLock;
use windows::core::w;
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::SystemInformation::GetTickCount64;
use windows::Win32::UI::Input::{GetRawInputData, RegisterRawInputDevices, HRAWINPUT, RAWINPUT, RAWINPUTDEVICE, RAWINPUTHEADER, RIDEV_INPUTSINK, RID_INPUT, RIM_TYPEMOUSE};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, CreateWindowExW, DefWindowProcW, DestroyWindow, RegisterClassW, SetWindowsHookExW, UnhookWindowsHookEx, HC_ACTION, HHOOK, HWND_MESSAGE, KBDLLHOOKSTRUCT, LLKHF_INJECTED,
    LLKHF_LOWER_IL_INJECTED, LLMHF_INJECTED, LLMHF_LOWER_IL_INJECTED, MSLLHOOKSTRUCT, WH_KEYBOARD_LL, WH_MOUSE_LL, WINDOW_EX_STYLE, WM_INPUT, WM_KEYDOWN, WM_KEYUP, WM_LBUTTONDOWN,
    WM_LBUTTONUP, WM_MBUTTONDOWN, WM_MBUTTONUP, WM_MOUSEHWHEEL, WM_MOUSEMOVE, WM_MOUSEWHEEL, WM_RBUTTONDOWN, WM_RBUTTONUP, WM_SYSKEYDOWN, WM_SYSKEYUP, WM_XBUTTONDOWN, WM_XBUTTONUP,
    WNDCLASSW, WS_OVERLAPPED,
};

/// The queue between the hooks and the heartbeat.
pub const QUEUE: usize = 4096;

/// What the hooks and the Raw Input window saw.
#[derive(Clone, Copy, Debug)]
pub enum Seen {
    Input(Physical),
    Takeover,
    /// Relative motion from one physical mouse.
    Motion { device: isize, dx: i32, dy: i32 },
}

/// Milliseconds since boot, the clock of the deadline.
pub fn now_ms() -> u64 {
    unsafe { GetTickCount64() }
}

/// Input is swallowed while `now_ms()` is before this; only the heartbeat moves it.
static DEADLINE: AtomicU64 = AtomicU64::new(0);
/// Something went wrong in a callback or the queue overflowed: pass everything until the coordinator resets it.
static FAIL_OPEN: AtomicBool = AtomicBool::new(false);
static SENDER: OnceLock<SyncSender<Seen>> = OnceLock::new();

pub fn set_deadline(deadline_ms: u64) {
    DEADLINE.store(deadline_ms, Ordering::Release);
}

/// Whether a callback asked to fail open since the last call, clearing the request.
pub fn take_fail_open() -> bool {
    FAIL_OPEN.swap(false, Ordering::AcqRel)
}

thread_local! {
    static FILTER: RefCell<Filter> = RefCell::new(Filter::new(500.0));
}

fn push(seen: Seen) {
    if let Some(sender) = SENDER.get() {
        if let Err(TrySendError::Full(_)) = sender.try_send(seen) {
            FAIL_OPEN.store(true, Ordering::Release);
        }
    }
}

/// Decides one event; true when it is to be swallowed. Never panics out of a callback.
fn handle(event: HookEvent) -> bool {
    let decided = catch_unwind(AssertUnwindSafe(|| {
        let now = now_ms();
        let deadline = if FAIL_OPEN.load(Ordering::Acquire) { 0 } else { DEADLINE.load(Ordering::Acquire) };
        FILTER.with(|filter| filter.try_borrow_mut().map(|mut filter| filter.decide(event, now, deadline)).ok())
    }));
    match decided {
        Ok(Some(verdict)) => {
            if let Some(report) = verdict.report {
                push(Seen::Input(report));
            }
            if verdict.takeover {
                push(Seen::Takeover);
            }
            verdict.swallow
        }
        _ => {
            FAIL_OPEN.store(true, Ordering::Release);
            false
        }
    }
}

unsafe extern "system" fn keyboard(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code == HC_ACTION as i32 {
        let info = unsafe { &*(lparam.0 as *const KBDLLHOOKSTRUCT) };
        let kind = match wparam.0 as u32 {
            WM_KEYDOWN | WM_SYSKEYDOWN => Some(Kind::KeyDown(info.vkCode)),
            WM_KEYUP | WM_SYSKEYUP => Some(Kind::KeyUp(info.vkCode)),
            _ => None,
        };
        if let Some(kind) = kind {
            let injected = info.flags.0 & (LLKHF_INJECTED.0 | LLKHF_LOWER_IL_INJECTED.0) != 0;
            if handle(HookEvent { kind, injected, extra: info.dwExtraInfo }) {
                return LRESULT(1);
            }
        }
    }
    unsafe { CallNextHookEx(None, code, wparam, lparam) }
}

unsafe extern "system" fn mouse(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code == HC_ACTION as i32 {
        let info = unsafe { &*(lparam.0 as *const MSLLHOOKSTRUCT) };
        let side = if (info.mouseData >> 16) & 0xFFFF == 1 { 4 } else { 5 };
        let kind = match wparam.0 as u32 {
            WM_MOUSEMOVE => Some(Kind::Move { x: info.pt.x, y: info.pt.y }),
            WM_LBUTTONDOWN => Some(Kind::ButtonDown(1)),
            WM_LBUTTONUP => Some(Kind::ButtonUp(1)),
            WM_RBUTTONDOWN => Some(Kind::ButtonDown(2)),
            WM_RBUTTONUP => Some(Kind::ButtonUp(2)),
            WM_MBUTTONDOWN => Some(Kind::ButtonDown(3)),
            WM_MBUTTONUP => Some(Kind::ButtonUp(3)),
            WM_XBUTTONDOWN => Some(Kind::ButtonDown(side)),
            WM_XBUTTONUP => Some(Kind::ButtonUp(side)),
            WM_MOUSEWHEEL | WM_MOUSEHWHEEL => Some(Kind::Wheel),
            _ => None,
        };
        if let Some(kind) = kind {
            let injected = info.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED) != 0;
            if handle(HookEvent { kind, injected, extra: info.dwExtraInfo }) {
                return LRESULT(1);
            }
        }
    }
    unsafe { CallNextHookEx(None, code, wparam, lparam) }
}

unsafe extern "system" fn raw_window(hwnd: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if message == WM_INPUT {
        let _ = catch_unwind(|| {
            let mut input = RAWINPUT::default();
            let mut size = size_of::<RAWINPUT>() as u32;
            let read = unsafe { GetRawInputData(HRAWINPUT(lparam.0 as *mut _), RID_INPUT, Some(&mut input as *mut _ as *mut _), &mut size, size_of::<RAWINPUTHEADER>() as u32) };
            // The null device is injected input; absolute motion (tablets, remote desktops) is not a mouse travelling.
            if read != u32::MAX && input.header.dwType == RIM_TYPEMOUSE.0 && !input.header.hDevice.is_invalid() {
                let mouse = unsafe { input.data.mouse };
                if mouse.usFlags.0 & 1 == 0 && (mouse.lLastX != 0 || mouse.lLastY != 0) {
                    push(Seen::Motion { device: input.header.hDevice.0 as isize, dx: mouse.lLastX, dy: mouse.lLastY });
                }
            }
        });
    }
    unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
}

/// The hooks and the Raw Input window. Lives on the main thread; dropping it removes them.
pub struct Monitor {
    keyboard: HHOOK,
    mouse: HHOOK,
    window: HWND,
}

impl Monitor {
    /// Installs the hooks and the Raw Input window on this (the main) thread, and hands back the queue's receiver.
    pub fn start(takeover_pixels: f64) -> Result<(Monitor, Receiver<Seen>), String> {
        let (sender, receiver) = sync_channel(QUEUE);
        SENDER.set(sender).map_err(|_| "the monitor runs once per process".to_string())?;
        FILTER.with(|filter| *filter.borrow_mut() = Filter::new(takeover_pixels));
        let (keyboard, mouse) = install()?;
        let window = raw_input_window().map_err(|reason| {
            unsafe {
                let _ = UnhookWindowsHookEx(keyboard);
                let _ = UnhookWindowsHookEx(mouse);
            }
            reason
        })?;
        Ok((Monitor { keyboard, mouse, window }, receiver))
    }

    /// New hooks in before the old ones go, so no event slips between them (Windows drops a hook that once ran
    /// past LowLevelHooksTimeout; reinstalling keeps them alive).
    pub fn reinstall(&mut self) -> Result<(), String> {
        let (keyboard, mouse) = install()?;
        unsafe {
            let _ = UnhookWindowsHookEx(self.keyboard);
            let _ = UnhookWindowsHookEx(self.mouse);
        }
        self.keyboard = keyboard;
        self.mouse = mouse;
        Ok(())
    }
}

fn install() -> Result<(HHOOK, HHOOK), String> {
    let module = unsafe { GetModuleHandleW(None) }.map_err(|error| error.message())?;
    let keyboard = unsafe { SetWindowsHookExW(WH_KEYBOARD_LL, Some(keyboard), Some(module.into()), 0) }.map_err(|error| format!("keyboard hook: {}", error.message()))?;
    let mouse = unsafe { SetWindowsHookExW(WH_MOUSE_LL, Some(mouse), Some(module.into()), 0) }.map_err(|error| {
        unsafe {
            let _ = UnhookWindowsHookEx(keyboard);
        }
        format!("mouse hook: {}", error.message())
    })?;
    Ok((keyboard, mouse))
}

fn raw_input_window() -> Result<HWND, String> {
    let module = unsafe { GetModuleHandleW(None) }.map_err(|error| error.message())?;
    let class = WNDCLASSW { lpfnWndProc: Some(raw_window), hInstance: module.into(), lpszClassName: w!("DesktopMcpRawInput"), ..Default::default() };
    unsafe { RegisterClassW(&class) };
    // Message-only: never shown, never in the window list.
    let window = unsafe { CreateWindowExW(WINDOW_EX_STYLE(0), w!("DesktopMcpRawInput"), w!(""), WS_OVERLAPPED, 0, 0, 0, 0, Some(HWND_MESSAGE), None, Some(module.into()), None) }
        .map_err(|error| format!("raw input window: {}", error.message()))?;
    let device = RAWINPUTDEVICE { usUsagePage: 0x01, usUsage: 0x02, dwFlags: RIDEV_INPUTSINK, hwndTarget: window };
    if let Err(error) = unsafe { RegisterRawInputDevices(&[device], size_of::<RAWINPUTDEVICE>() as u32) } {
        unsafe {
            let _ = DestroyWindow(window);
        }
        return Err(format!("raw input: {}", error.message()));
    }
    Ok(window)
}

impl Drop for Monitor {
    fn drop(&mut self) {
        set_deadline(0);
        unsafe {
            let _ = UnhookWindowsHookEx(self.keyboard);
            let _ = UnhookWindowsHookEx(self.mouse);
            let _ = DestroyWindow(self.window);
        }
    }
}
