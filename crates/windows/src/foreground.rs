//! Bringing a window to the foreground despite the foreground lock, bounded (the C# port's sequence, from upstream
//! Windows-MCP): skip a window that does not answer, restore it if minimized, `SetForegroundWindow`, and when that
//! does not take, attach to the foreground and target threads for one more try, and when that does not take either,
//! send an input event of our own and try both once more. Blocking: call on the pool.
use std::time::{Duration, Instant};
use windows::Win32::Foundation::HWND;
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::UI::WindowsAndMessaging::{
    AllowSetForegroundWindow, GetForegroundWindow, SetWindowPos, HWND_TOP, SWP_ASYNCWINDOWPOS, SWP_NOMOVE, SWP_NOSIZE, GetWindowThreadProcessId, IsIconic, SetForegroundWindow, ShowWindowAsync, ASFW_ANY, SW_RESTORE,
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
        // The restore is asynchronous: a window still minimized does not take the foreground.
        let deadline = Instant::now() + Duration::from_millis(500);
        while unsafe { IsIconic(window) }.as_bool() && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
    }
    let _ = unsafe { SetForegroundWindow(window) };
    if poll(window, Duration::from_millis(500)) {
        return true;
    }
    if attached_attempt(window) && poll(window, Duration::from_millis(500)) {
        return true;
    }
    // Windows lets only the process that sent the last input event take the foreground while another (a UWP app
    // just activated) holds it. A press and release of an unassigned key makes that this process without acting on
    // any window; not Alt, whose lone tap puts the focused window in menu or access-key mode.
    if !crate::input::send_mask_tap() {
        return false;
    }
    let _ = unsafe { SetForegroundWindow(window) };
    if poll(window, Duration::from_millis(250)) {
        return true;
    }
    attached_attempt(window) && poll(window, Duration::from_millis(250))
}

/// `SetForegroundWindow` with this thread's input attached to the foreground and target threads' (their input
/// state shared, the foreground lock does not apply); false without trying when the foreground window does not
/// answer, as it would block the attach.
fn attached_attempt(window: HWND) -> bool {
    let current = unsafe { GetCurrentThreadId() };
    let foreground = unsafe { GetForegroundWindow() };
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
        // Asynchronous: a target that hangs now must not block while the input queues are attached.
        let _ = SetWindowPos(window, Some(HWND_TOP), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_ASYNCWINDOWPOS);
        let _ = SetForegroundWindow(window);
    }
    for thread in attached {
        let _ = unsafe { windows::Win32::System::Threading::AttachThreadInput(current, thread, false) };
    }
    true
}
