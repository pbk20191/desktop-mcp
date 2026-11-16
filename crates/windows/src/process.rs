//! Child processes whose stdout and stderr come back on named pipes read with overlapped I/O on the thread pool.
//!
//! Our end of each pipe is opened `FILE_FLAG_OVERLAPPED` and bound to a `TP_IO`; the child's end is an ordinary
//! inheritable handle, and only the three stdio handles are inherited (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`). The
//! child runs in a job: a timeout or a dropped call terminates the job, so whatever the command started ends too.
//! A normal exit leaves what the command started running (an app it opened, for example); its output is read until
//! the pipes close, or for a short grace after the exit when something it started still holds them.
use crate::pool;
use desktop_mcp_core::shell::Run;
use std::cell::RefCell;
use std::ffi::{c_void, OsStr, OsString};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::path::Path;
use std::rc::Rc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;
use tokio::sync::oneshot;
use windows::core::{PCWSTR, PWSTR};
use windows::Win32::Foundation::{ERROR_IO_PENDING, GENERIC_READ, GENERIC_WRITE, HANDLE};
use windows::Win32::Security::SECURITY_ATTRIBUTES;
use windows::Win32::Storage::FileSystem::{CreateFileW, ReadFile, FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING, PIPE_ACCESS_INBOUND, FILE_FLAGS_AND_ATTRIBUTES};
use windows::Win32::System::JobObjects::{AssignProcessToJobObject, CreateJobObjectW, TerminateJobObject};
use windows::Win32::System::Pipes::{CreateNamedPipeW, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE, PIPE_WAIT};
use windows::Win32::System::Threading::{
    CancelThreadpoolIo, CloseThreadpoolIo, CreateProcessW, CreateThreadpoolIo, DeleteProcThreadAttributeList, GetExitCodeProcess, InitializeProcThreadAttributeList, ResumeThread,
    StartThreadpoolIo, UpdateProcThreadAttribute, WaitForThreadpoolIoCallbacks, CREATE_NO_WINDOW, CREATE_SUSPENDED, CREATE_UNICODE_ENVIRONMENT, EXTENDED_STARTUPINFO_PRESENT,
    LPPROC_THREAD_ATTRIBUTE_LIST, PROCESS_INFORMATION, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, PTP_CALLBACK_INSTANCE, PTP_IO, STARTF_USESTDHANDLES, STARTUPINFOEXW,
};
use windows::Win32::System::IO::{CancelIoEx, OVERLAPPED};

/// How long output is still read after the process exits, when something it started holds the pipes.
const GRACE: Duration = Duration::from_millis(200);
const READ_SIZE: usize = 64 * 1024;

pub struct Spawn<'a> {
    pub program: &'a Path,
    pub args: &'a [OsString],
    pub env: Vec<(OsString, OsString)>,
}

/// This process's environment.
pub fn inherited_environment() -> Vec<(OsString, OsString)> {
    std::env::vars_os().collect()
}

fn wide(text: &OsStr) -> Vec<u16> {
    text.encode_wide().chain(std::iter::once(0)).collect()
}

/// One argument quoted the way `CommandLineToArgvW` reads it back.
fn quote(argument: &OsStr, line: &mut Vec<u16>) {
    let text: Vec<u16> = argument.encode_wide().collect();
    let plain = !text.is_empty() && !text.iter().any(|&c| c == ' ' as u16 || c == '\t' as u16 || c == '"' as u16);
    if plain {
        line.extend(text);
        return;
    }
    line.push('"' as u16);
    let mut backslashes = 0;
    for c in text {
        if c == '\\' as u16 {
            backslashes += 1;
            continue;
        }
        let doubled = if c == '"' as u16 { backslashes * 2 + 1 } else { backslashes };
        line.extend(std::iter::repeat_n('\\' as u16, doubled));
        backslashes = 0;
        line.push(c);
    }
    line.extend(std::iter::repeat_n('\\' as u16, backslashes * 2));
    line.push('"' as u16);
}

fn command_line(program: &Path, args: &[OsString]) -> Vec<u16> {
    let mut line = Vec::new();
    quote(program.as_os_str(), &mut line);
    for argument in args {
        line.push(' ' as u16);
        quote(argument, &mut line);
    }
    line.push(0);
    line
}

/// `KEY=value\0…\0\0`, sorted ignoring case as Windows expects.
fn environment_block(mut env: Vec<(OsString, OsString)>) -> Vec<u16> {
    env.sort_by_key(|(key, _)| key.to_string_lossy().to_uppercase());
    let mut block = Vec::new();
    for (key, value) in env {
        block.extend(key.encode_wide());
        block.push('=' as u16);
        block.extend(value.encode_wide());
        block.push(0);
    }
    block.push(0);
    block
}

static PIPES: AtomicUsize = AtomicUsize::new(0);

/// Our overlapped read end and the child's inheritable write end.
fn pipe() -> Result<(OwnedHandle, OwnedHandle), String> {
    let name = format!(r"\\.\pipe\desktop-mcp-{}-{}", std::process::id(), PIPES.fetch_add(1, Ordering::Relaxed));
    let name = wide(name.as_ref());
    let server = unsafe {
        CreateNamedPipeW(
            PCWSTR(name.as_ptr()),
            PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1,
            0,
            READ_SIZE as u32,
            0,
            None,
        )
    };
    if server.is_invalid() {
        return Err(format!("cannot create a pipe: {}", windows::core::Error::from_thread()));
    }
    let server = unsafe { OwnedHandle::from_raw_handle(server.0) };
    let inherit = SECURITY_ATTRIBUTES { nLength: size_of::<SECURITY_ATTRIBUTES>() as u32, lpSecurityDescriptor: std::ptr::null_mut(), bInheritHandle: true.into() };
    let client = unsafe { CreateFileW(PCWSTR(name.as_ptr()), GENERIC_WRITE.0, FILE_SHARE_READ | FILE_SHARE_WRITE, Some(&inherit), OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES(0), None) }
        .map_err(|error| format!("cannot open a pipe: {error}"))?;
    Ok((server, unsafe { OwnedHandle::from_raw_handle(client.0) }))
}

fn null_input() -> Result<OwnedHandle, String> {
    let inherit = SECURITY_ATTRIBUTES { nLength: size_of::<SECURITY_ATTRIBUTES>() as u32, lpSecurityDescriptor: std::ptr::null_mut(), bInheritHandle: true.into() };
    let name = wide("NUL".as_ref());
    let handle = unsafe { CreateFileW(PCWSTR(name.as_ptr()), GENERIC_READ.0, FILE_SHARE_READ | FILE_SHARE_WRITE, Some(&inherit), OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES(0), None) }
        .map_err(|error| format!("cannot open NUL: {error}"))?;
    Ok(unsafe { OwnedHandle::from_raw_handle(handle.0) })
}

/// One overlapped read; the callback owns it once it is issued.
#[repr(C)]
struct Op {
    overlapped: OVERLAPPED,
    buffer: Vec<u8>,
    done: Option<oneshot::Sender<(u32, usize, Vec<u8>)>>,
}

unsafe extern "system" fn read_done(_instance: PTP_CALLBACK_INSTANCE, _context: *mut c_void, overlapped: *mut c_void, result: u32, bytes: usize, _io: PTP_IO) {
    let mut op = unsafe { Box::from_raw(overlapped as *mut Op) };
    if let Some(done) = op.done.take() {
        let _ = done.send((result, bytes, std::mem::take(&mut op.buffer)));
    }
}

/// Our end of a pipe, bound to the thread pool.
struct PipeReader {
    handle: OwnedHandle,
    io: PTP_IO,
}

impl PipeReader {
    fn new(handle: OwnedHandle) -> Result<PipeReader, String> {
        let io = unsafe { CreateThreadpoolIo(HANDLE(handle.as_raw_handle()), Some(read_done), None, None) }.map_err(|error| format!("CreateThreadpoolIo: {error}"))?;
        Ok(PipeReader { handle, io })
    }

    /// Appends to `into` until the pipe closes.
    async fn read_to_end(&self, into: &RefCell<Vec<u8>>) {
        loop {
            let (sender, receiver) = oneshot::channel();
            let op = Box::into_raw(Box::new(Op { overlapped: OVERLAPPED::default(), buffer: vec![0; READ_SIZE], done: Some(sender) }));
            unsafe { StartThreadpoolIo(self.io) };
            let issued = unsafe { ReadFile(HANDLE(self.handle.as_raw_handle()), Some(&mut (*op).buffer), None, Some(&mut (*op).overlapped)) };
            if let Err(error) = issued {
                if error.code() != ERROR_IO_PENDING.to_hresult() {
                    // Failed to start (a closed pipe): no completion comes.
                    unsafe {
                        CancelThreadpoolIo(self.io);
                        drop(Box::from_raw(op));
                    }
                    return;
                }
            }
            let Ok((result, bytes, buffer)) = receiver.await else { return };
            if result != 0 || bytes == 0 {
                return;
            }
            into.borrow_mut().extend_from_slice(&buffer[..bytes]);
        }
    }
}

impl Drop for PipeReader {
    fn drop(&mut self) {
        unsafe {
            let _ = CancelIoEx(HANDLE(self.handle.as_raw_handle()), None);
            WaitForThreadpoolIoCallbacks(self.io, false);
            CloseThreadpoolIo(self.io);
        }
    }
}

/// A started process and its job; terminates the job when dropped before the process has finished.
struct Child {
    process: OwnedHandle,
    job: OwnedHandle,
    finished: bool,
}

impl Child {
    fn terminate(&self) {
        unsafe {
            let _ = TerminateJobObject(HANDLE(self.job.as_raw_handle()), 1);
        }
    }
}

impl Drop for Child {
    fn drop(&mut self) {
        if !self.finished {
            self.terminate();
        }
    }
}

fn start(spawn: &Spawn<'_>, input: &OwnedHandle, output: &OwnedHandle, error: &OwnedHandle) -> Result<Child, String> {
    let job = unsafe { CreateJobObjectW(None, PCWSTR::null()) }.map_err(|error| format!("CreateJobObject: {error}"))?;
    let job = unsafe { OwnedHandle::from_raw_handle(job.0) };
    let handles = [HANDLE(input.as_raw_handle()), HANDLE(output.as_raw_handle()), HANDLE(error.as_raw_handle())];
    let mut size = 0usize;
    unsafe {
        let _ = InitializeProcThreadAttributeList(None, 1, None, &mut size);
    }
    let mut storage = vec![0u8; size];
    let list = LPPROC_THREAD_ATTRIBUTE_LIST(storage.as_mut_ptr() as *mut c_void);
    unsafe { InitializeProcThreadAttributeList(Some(list), 1, None, &mut size) }.map_err(|error| format!("InitializeProcThreadAttributeList: {error}"))?;
    let result = (|| {
        unsafe { UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST as usize, Some(handles.as_ptr() as *const c_void), size_of_val(&handles), None, None) }
            .map_err(|error| format!("UpdateProcThreadAttribute: {error}"))?;
        let mut startup = STARTUPINFOEXW::default();
        startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = handles[0];
        startup.StartupInfo.hStdOutput = handles[1];
        startup.StartupInfo.hStdError = handles[2];
        startup.lpAttributeList = list;
        let program = wide(spawn.program.as_os_str());
        let mut line = command_line(spawn.program, spawn.args);
        let environment = environment_block(spawn.env.clone());
        let mut information = PROCESS_INFORMATION::default();
        unsafe {
            CreateProcessW(
                PCWSTR(program.as_ptr()),
                Some(PWSTR(line.as_mut_ptr())),
                None,
                None,
                true,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                Some(environment.as_ptr() as *const c_void),
                PCWSTR::null(),
                &startup.StartupInfo,
                &mut information,
            )
        }
        .map_err(|error| format!("cannot start {}: {error}", spawn.program.display()))?;
        let process = unsafe { OwnedHandle::from_raw_handle(information.hProcess.0) };
        let thread = unsafe { OwnedHandle::from_raw_handle(information.hThread.0) };
        let child = Child { process, job, finished: false };
        if unsafe { AssignProcessToJobObject(HANDLE(child.job.as_raw_handle()), HANDLE(child.process.as_raw_handle())) }.is_err() {
            // Not in the job: a timeout then ends only this process.
            unsafe {
                let _ = windows::Win32::System::Threading::TerminateProcess(HANDLE(child.process.as_raw_handle()), 1);
            }
            return Err("cannot put the process in a job".to_string());
        }
        unsafe { ResumeThread(HANDLE(thread.as_raw_handle())) };
        Ok(child)
    })();
    unsafe { DeleteProcThreadAttributeList(list) };
    result
}

/// Runs `spawn` with stdin from NUL, collecting stdout and stderr, until it exits or `timeout` passes (then its job
/// is terminated and `timed_out` set). Dropping the future terminates the job too.
pub async fn run(spawn: Spawn<'_>, timeout: Duration) -> Result<Run, String> {
    let (out_server, out_client) = pipe()?;
    let (err_server, err_client) = pipe()?;
    let input = null_input()?;
    let mut child = start(&spawn, &input, &out_client, &err_client)?;
    // Only the child holds the write ends now, so the pipes close when it (and what it started) is gone.
    drop((input, out_client, err_client));
    let (stdout, stderr) = (Rc::new(RefCell::new(Vec::new())), Rc::new(RefCell::new(Vec::new())));
    let readers = {
        let (out_reader, err_reader) = (PipeReader::new(out_server)?, PipeReader::new(err_server)?);
        let (stdout, stderr) = (stdout.clone(), stderr.clone());
        async move {
            tokio::join!(out_reader.read_to_end(&stdout), err_reader.read_to_end(&stderr));
        }
    };
    let mut readers = std::pin::pin!(readers);
    let mut read_all = false;
    let waiting = async {
        tokio::select! {
            _ = pool::wait_for(HANDLE(child.process.as_raw_handle())) => false,
            _ = pool::sleep(timeout) => true,
        }
    };
    let mut waiting = std::pin::pin!(waiting);
    // Reading goes on while waiting, or a full pipe would stall the child.
    let timed_out = tokio::select! {
        timed_out = &mut waiting => timed_out,
        _ = &mut readers => {
            read_all = true;
            waiting.await
        }
    };
    if timed_out {
        child.terminate();
    }
    child.finished = true;
    // Drain: until both pipes close, or the grace passes (something the command started holds them).
    if !read_all {
        tokio::select! {
            _ = &mut readers => {}
            _ = pool::sleep(GRACE) => {}
        }
    }
    drop(readers);
    let mut status = 0u32;
    if timed_out {
        status = 1;
    } else {
        unsafe {
            let _ = GetExitCodeProcess(HANDLE(child.process.as_raw_handle()), &mut status);
        }
    }
    let text = |bytes: &Rc<RefCell<Vec<u8>>>| String::from_utf8_lossy(&bytes.borrow()).into_owned();
    Ok(Run { stdout: text(&stdout), stderr: text(&stderr), status: status as i32, timed_out })
}
