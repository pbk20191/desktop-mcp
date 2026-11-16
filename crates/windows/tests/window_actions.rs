//! Window actions on windows this test creates and never shows.
#![cfg(windows)]
use desktop_mcp_core::window::{Frame, FrameResult, WindowInfo, WindowSource};
use desktop_mcp_windows::windows_list::Win32Windows;
use std::sync::atomic::{AtomicBool, Ordering};
use windows::core::w;
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetMessageW, RegisterClassW, MSG, WINDOW_EX_STYLE, WINDOW_STYLE, WM_CLOSE, WNDCLASSW, WS_OVERLAPPEDWINDOW,
    WS_POPUP,
};

static CLOSED: AtomicBool = AtomicBool::new(false);

unsafe extern "system" fn procedure(hwnd: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if message == WM_CLOSE {
        CLOSED.store(true, Ordering::SeqCst);
        return LRESULT(0);
    }
    unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
}

/// A hidden window of this test's own class (never `WS_VISIBLE`), owned by a thread that pumps its messages, as an
/// application's window is: moving it from another thread waits for that pump.
fn hidden(style: WINDOW_STYLE) -> HWND {
    // As the server: per-monitor DPI aware, so every coordinate is a physical pixel.
    let _ = unsafe { windows::Win32::UI::HiDpi::SetProcessDpiAwarenessContext(windows::Win32::UI::HiDpi::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let (sender, receiver) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let class = WNDCLASSW { lpfnWndProc: Some(procedure), lpszClassName: w!("DesktopMcpTestWindow"), ..Default::default() };
        unsafe { RegisterClassW(&class) };
        let hwnd = unsafe { CreateWindowExW(WINDOW_EX_STYLE(0), w!("DesktopMcpTestWindow"), w!("test"), style, 10, 10, 200, 150, None, None, None, None) }.unwrap();
        sender.send(hwnd.0 as isize).unwrap();
        let mut message = MSG::default();
        while unsafe { GetMessageW(&mut message, None, 0, 0) }.as_bool() {
            unsafe { DispatchMessageW(&message) };
        }
    });
    HWND(receiver.recv().unwrap() as *mut _)
}

fn info(hwnd: HWND) -> WindowInfo {
    WindowInfo { pid: std::process::id() as i32, index: 0, app: "test".into(), title: "test".into(), frame: Frame::new(0.0, 0.0, 0.0, 0.0), focused: false, minimized: false, fullscreen: false, number: Some(hwnd.0 as isize as u32) }
}

fn block_on<T>(future: impl std::future::Future<Output = T>) -> T {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    tokio::task::LocalSet::new().block_on(&runtime, future)
}

#[test]
fn set_frame_lands_where_asked() {
    let hwnd = hidden(WS_OVERLAPPEDWINDOW);
    let result = block_on(Win32Windows.set_frame(&info(hwnd), Frame::new(100.0, 120.0, 400.0, 300.0)));
    assert_eq!(result, FrameResult::Done);
    // The visible (DWM) frame is what was asked for; the window rectangle adds the invisible resize borders.
    let mut rect = RECT::default();
    unsafe { windows::Win32::Graphics::Dwm::DwmGetWindowAttribute(hwnd, windows::Win32::Graphics::Dwm::DWMWA_EXTENDED_FRAME_BOUNDS, &mut rect as *mut _ as *mut _, size_of::<RECT>() as u32) }.unwrap();
    assert_eq!((rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top), (100, 120, 400, 300));
}

#[test]
fn a_fixed_size_window_is_not_resized() {
    let hwnd = hidden(WS_POPUP);
    assert_eq!(block_on(Win32Windows.set_frame(&info(hwnd), Frame::new(10.0, 10.0, 999.0, 999.0))), FrameResult::NotResizable);
    assert!(block_on(Win32Windows.move_to(&info(hwnd), 50.0, 60.0)), "moving needs no resize");
}

#[test]
fn close_asks_the_window_to_close() {
    let hwnd = hidden(WS_OVERLAPPEDWINDOW);
    assert!(block_on(Win32Windows.close(&info(hwnd))));
    std::thread::sleep(std::time::Duration::from_millis(200));
    assert!(CLOSED.load(Ordering::SeqCst));
}
