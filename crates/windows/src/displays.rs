//! The displays, in physical pixels of the virtual screen (the process is per-monitor DPI aware).
use desktop_mcp_core::window::{DisplayFrame, Frame};
use windows::core::BOOL;
use windows::Win32::Foundation::{LPARAM, RECT};
use windows::Win32::Graphics::Gdi::{EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITORINFO, MONITORINFOEXW};
use windows::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI};
use windows::Win32::UI::WindowsAndMessaging::MONITORINFOF_PRIMARY;

pub fn frame(rect: &RECT) -> Frame {
    Frame::new(rect.left as f64, rect.top as f64, (rect.right - rect.left) as f64, (rect.bottom - rect.top) as f64)
}

unsafe extern "system" fn collect(monitor: HMONITOR, _dc: HDC, _rect: *mut RECT, data: LPARAM) -> BOOL {
    unsafe { &mut *(data.0 as *mut Vec<HMONITOR>) }.push(monitor);
    true.into()
}

/// Each display, left to right (then top to bottom): its frame, work area, scale and whether it is the primary.
pub fn displays() -> Vec<DisplayFrame> {
    let mut monitors: Vec<HMONITOR> = Vec::new();
    unsafe {
        let _ = EnumDisplayMonitors(None, None, Some(collect), LPARAM(&mut monitors as *mut _ as isize));
    }
    let mut displays: Vec<DisplayFrame> = monitors
        .into_iter()
        .filter_map(|monitor| {
            let mut info = MONITORINFOEXW { monitorInfo: MONITORINFO { cbSize: size_of::<MONITORINFOEXW>() as u32, ..Default::default() }, ..Default::default() };
            unsafe { GetMonitorInfoW(monitor, &mut info as *mut _ as *mut MONITORINFO) }.ok().ok()?;
            let (mut dpi_x, mut dpi_y) = (96u32, 96u32);
            let _ = unsafe { GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut dpi_x, &mut dpi_y) };
            let length = info.szDevice.iter().position(|unit| *unit == 0).unwrap_or(info.szDevice.len());
            Some(DisplayFrame {
                index: 0,
                name: String::from_utf16_lossy(&info.szDevice[..length]).trim_start_matches(r"\\.\").to_string(),
                frame: frame(&info.monitorInfo.rcMonitor),
                visible: frame(&info.monitorInfo.rcWork),
                scale: dpi_x as f64 / 96.0,
                main: info.monitorInfo.dwFlags & MONITORINFOF_PRIMARY != 0,
            })
        })
        .collect();
    displays.sort_by(|a, b| a.frame.x.total_cmp(&b.frame.x).then(a.frame.y.total_cmp(&b.frame.y)));
    for (index, display) in displays.iter_mut().enumerate() {
        display.index = index;
    }
    displays
}

/// The snapshot's "Input Coordinate Space" text.
pub fn coordinate_space(displays: &[DisplayFrame]) -> String {
    let each: Vec<String> = displays
        .iter()
        .map(|display| {
            let f = display.frame;
            format!("display {} [{},{},{},{}] scale {}{}", display.index, f.x, f.y, f.width, f.height, display.scale, if display.main { " (primary)" } else { "" })
        })
        .collect();
    format!("Windows physical pixels of the virtual screen, origin at the primary display's top-left; {}", each.join("; "))
}
