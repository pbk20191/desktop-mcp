//! The x64 binary emulated on an ARM64 machine hands over to the native `desktop-mcp-arm64.exe` beside it, which the
//! Windows extension bundles: the server then runs at native speed and sees the machine as it is.
//!
//! The arm64 build never switches, and the child is started with the switch turned off, so a mislabelled sibling
//! cannot start another.
use std::ffi::c_void;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::path::PathBuf;
use windows::core::{BOOL, PCWSTR};
use windows::Win32::Foundation::HANDLE;
use windows::Win32::System::Console::{SetConsoleCtrlHandler, CTRL_BREAK_EVENT, CTRL_C_EVENT};
use windows::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
    JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK,
};
use windows::Win32::System::SystemInformation::{IMAGE_FILE_MACHINE, IMAGE_FILE_MACHINE_ARM64};
use windows::Win32::System::Threading::{GetCurrentProcess, IsWow64Process2};

/// Set to 1 to keep the x64 binary running under emulation.
const ENV_NO_SWITCH: &str = "DESKTOP_MCP_NO_ARCH_SWITCH";
const ARM64_BINARY: &str = "desktop-mcp-arm64.exe";

/// The native binary to run instead of this one, if this is the x64 build on an ARM64 machine and it is bundled.
fn native_sibling() -> Option<PathBuf> {
    if !cfg!(target_arch = "x86_64") || std::env::var(ENV_NO_SWITCH).is_ok_and(|value| value == "1") {
        return None;
    }
    let (mut process, mut native) = (IMAGE_FILE_MACHINE::default(), IMAGE_FILE_MACHINE::default());
    unsafe { IsWow64Process2(GetCurrentProcess(), &mut process, Some(&mut native)) }.ok()?;
    if native != IMAGE_FILE_MACHINE_ARM64 {
        return None;
    }
    let sibling = std::env::current_exe().ok()?.with_file_name(ARM64_BINARY);
    sibling.is_file().then_some(sibling)
}

/// Ctrl+C and Ctrl+Break reach the child on the same console; this process waits for it to end instead.
unsafe extern "system" fn ignore_interrupts(event: u32) -> BOOL {
    (event == CTRL_C_EVENT || event == CTRL_BREAK_EVENT).into()
}

/// A job that ends its process when this one ends; what that process starts is left out of it.
fn kill_on_close_job() -> Option<OwnedHandle> {
    let job = unsafe { CreateJobObjectW(None, PCWSTR::null()) }.ok()?;
    let job = unsafe { OwnedHandle::from_raw_handle(job.0) };
    let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
    unsafe { SetInformationJobObject(HANDLE(job.as_raw_handle()), JobObjectExtendedLimitInformation, &limits as *const _ as *const c_void, size_of_val(&limits) as u32) }.ok()?;
    Some(job)
}

/// Runs the native sibling, if there is one, with this process's arguments and standard handles, and exits with its
/// status. Returns when this binary should serve itself.
pub fn run_native_sibling() {
    let Some(sibling) = native_sibling() else { return };
    let job = kill_on_close_job();
    let mut child = match std::process::Command::new(&sibling).args(std::env::args_os().skip(1)).env(ENV_NO_SWITCH, "1").spawn() {
        Ok(child) => child,
        Err(error) => {
            crate::log(&format!("Cannot start {}: {error}; running emulated", sibling.display()));
            return;
        }
    };
    if let Some(job) = &job {
        let _ = unsafe { AssignProcessToJobObject(HANDLE(job.as_raw_handle()), HANDLE(child.as_raw_handle())) };
    }
    let _ = unsafe { SetConsoleCtrlHandler(Some(ignore_interrupts), true) };
    let code = match child.wait() {
        Ok(status) => status.code().unwrap_or(1),
        Err(_) => 1,
    };
    std::process::exit(code);
}
