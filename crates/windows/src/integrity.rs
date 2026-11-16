//! Integrity levels. User Interface Privilege Isolation drops input sent to a window of a process that runs at a
//! higher integrity level (an app run as administrator), silently; input aimed there is refused instead.
use windows::Win32::Foundation::{CloseHandle, ERROR_ACCESS_DENIED, HANDLE, HWND, POINT};
use windows::Win32::Security::{GetSidSubAuthority, GetSidSubAuthorityCount, GetTokenInformation, TokenIntegrityLevel, TOKEN_MANDATORY_LABEL, TOKEN_QUERY};
use windows::Win32::System::Threading::{OpenProcess, OpenProcessToken, PROCESS_QUERY_LIMITED_INFORMATION};
use windows::Win32::UI::WindowsAndMessaging::{GetAncestor, GetForegroundWindow, GetWindowThreadProcessId, WindowFromPoint, GA_ROOT};

/// What reading a process's level found.
#[derive(Debug, PartialEq, Eq)]
pub enum Level {
    Known(u32),
    /// The token cannot be read from here: the usual case for an elevated process seen from a standard one.
    Denied,
    Unknown,
}

fn token_level(token: HANDLE) -> Option<u32> {
    let mut size = 0u32;
    let _ = unsafe { GetTokenInformation(token, TokenIntegrityLevel, None, 0, &mut size) };
    // u64s: the buffer holds a TOKEN_MANDATORY_LABEL, which needs pointer alignment.
    let mut buffer = vec![0u64; (size as usize).div_ceil(8)];
    unsafe { GetTokenInformation(token, TokenIntegrityLevel, Some(buffer.as_mut_ptr() as *mut _), size, &mut size) }.ok()?;
    let label = unsafe { &*(buffer.as_ptr() as *const TOKEN_MANDATORY_LABEL) };
    let count = unsafe { *GetSidSubAuthorityCount(label.Label.Sid) };
    Some(unsafe { *GetSidSubAuthority(label.Label.Sid, count as u32 - 1) })
}

fn level(pid: u32) -> Level {
    let Ok(process) = (unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) }) else { return Level::Unknown };
    let mut token = HANDLE::default();
    let opened = unsafe { OpenProcessToken(process, TOKEN_QUERY, &mut token) };
    unsafe {
        let _ = CloseHandle(process);
    }
    match opened {
        Ok(()) => {
            let found = token_level(token);
            unsafe {
                let _ = CloseHandle(token);
            }
            found.map(Level::Known).unwrap_or(Level::Unknown)
        }
        Err(error) if error.code() == ERROR_ACCESS_DENIED.to_hresult() => Level::Denied,
        Err(_) => Level::Unknown,
    }
}

/// The integrity level (a mandatory-label RID such as 0x2000 for medium) of process `pid`, when readable.
pub fn level_of(pid: u32) -> Option<u32> {
    match level(pid) {
        Level::Known(rid) => Some(rid),
        _ => None,
    }
}

/// Whether input aimed at the window at `point` (or, with `None`, at the foreground window) would be dropped
/// because its process runs at a higher integrity level than this one.
pub fn input_blocked_at(point: Option<(i32, i32)>) -> bool {
    let window: HWND = match point {
        Some((x, y)) => unsafe { GetAncestor(WindowFromPoint(POINT { x, y }), GA_ROOT) },
        None => unsafe { GetForegroundWindow() },
    };
    if window.is_invalid() {
        return false;
    }
    let mut pid = 0u32;
    unsafe { GetWindowThreadProcessId(window, Some(&mut pid)) };
    let Some(own) = level_of(std::process::id()) else { return false };
    match level(pid) {
        Level::Known(theirs) => theirs > own,
        Level::Denied => own < 0x3000,
        Level::Unknown => false,
    }
}

/// The refusal, worded for the model.
pub fn refusal(point: Option<(i64, i64)>) -> String {
    let target = point.map(|(x, y)| format!("The window at ({x},{y})")).unwrap_or_else(|| "The focused window".to_string());
    format!("{target} runs as administrator; desktop-mcp cannot send input to it unless desktop-mcp runs as administrator too.")
}
