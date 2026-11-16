//! Bringing a window to the foreground despite the foreground lock, bounded (the C# port's sequence, from upstream
//! Windows-MCP): skip a window that does not answer, restore it if minimized, `SetForegroundWindow`, and when that
//! does not take, attach to the foreground and target threads for one more try. Blocking: call on the pool.
use std::time::{Duration, Instant};
use windows::Win32::Foundation::HWND;
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::UI::WindowsAndMessaging::{
    AllowSetForegroundWindow, BringWindowToTop, GetForegroundWindow, GetWindowThreadProcessId, IsIconic, SetForegroundWindow, ShowWindowAsync, ASFW_ANY, SW_RESTORE,
};

fn poll(window: HWND, limit: Duration) -> bool {
    let deadline = Instant::now() + limit;
    loop {
        if unsafe { GetForegroundWindow() } == window {
            return true;
        }
        if Instant::now() > deadline {
            return false;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
}

/// Whether `hwnd` is the foreground window afterwards.
pub fn bring(hwnd: isize) -> bool {
    let window = HWND(hwnd as *mut _);
    if !crate::tree::answers(hwnd) {
        return false;
    }
    if unsafe { IsIconic(window) }.as_bool() {
        let _ = unsafe { ShowWindowAsync(window, SW_RESTORE) };
    }
    let _ = unsafe { SetForegroundWindow(window) };
    if poll(window, Duration::from_millis(500)) {
        return true;
    }
    let current = unsafe { GetCurrentThreadId() };
    let foreground = unsafe { GetForegroundWindow() };
    // A foreground window that does not answer would block the attach; leave it.
    if !foreground.is_invalid() && !crate::tree::answers(foreground.0 as isize) {
        return false;
    }
    let threads = [unsafe { GetWindowThreadProcessId(foreground, None) }, unsafe { GetWindowThreadProcessId(window, None) }];
    let mut attached = Vec::new();
    for thread in threads {
        if thread != 0 && thread != current && !attached.contains(&thread) && unsafe { windows::Win32::System::Threading::AttachThreadInput(current, thread, true) }.as_bool() {
            attached.push(thread);
        }
    }
    unsafe {
        let _ = AllowSetForegroundWindow(ASFW_ANY);
        let _ = BringWindowToTop(window);
        let _ = SetForegroundWindow(window);
    }
    for thread in attached {
        let _ = unsafe { windows::Win32::System::Threading::AttachThreadInput(current, thread, false) };
    }
    poll(window, Duration::from_secs(1))
}
