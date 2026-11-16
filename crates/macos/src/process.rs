//! Child processes that block no thread while they run: exit is watched by a libdispatch process source and
//! the timeout by a libdispatch timer, both on the main queue.
use crate::desktop::sleep;
use desktop_mcp_core::shell::Run;
use dispatch2::{DispatchObject, DispatchQueue, DispatchRetained, DispatchSource, DispatchTime};
use std::ffi::CString;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::os::fd::{AsRawFd, FromRawFd};
use std::sync::{Arc, Mutex};
use std::time::Duration;

/// `<spawn.h>`'s `POSIX_SPAWN_SETSID`, which `libc` does not declare.
const POSIX_SPAWN_SETSID: libc::c_int = 0x0400;
/// `DISPATCH_PROC_EXIT`.
const PROC_EXIT: usize = 0x8000_0000;
/// Output kept per stream; a command that floods its output cannot exhaust memory.
const MAX_OUTPUT_BYTES: u64 = 10 * 1024 * 1024;

/// Runs `argv` with stdin from /dev/null and output captured to temporary files, killing its process group
/// after `timeout` or when the future is dropped.
///
/// - stdin is /dev/null: under the stdio transport fd 0 is the JSON-RPC stream, and a child that reads stdin
///   (sudo, ssh, a bare `cat`) would swallow the client's messages.
/// - Output goes to files, not pipes: a backgrounded grandchild (`cmd &`) keeps a pipe open after the direct
///   child exits, which would hold the call open until the timeout.
/// - The child gets its own session, so a timeout or cancellation kills the whole tree.
pub async fn run(argv: Vec<String>, timeout: Duration) -> Result<Run, String> {
    let stdout = temp_file()?;
    let stderr = temp_file()?;
    let pid = spawn(&argv, stdout.as_raw_fd(), stderr.as_raw_fd())?;
    let mut child = Child { pid, exited: false };
    let mut exit = watch_exit(pid);
    let (status, timed_out) = tokio::select! {
        biased;
        status = &mut exit => (status.unwrap_or(-1), false),
        () = sleep(timeout) => {
            unsafe { libc::killpg(pid, libc::SIGKILL) };
            (exit.await.unwrap_or(-1), true)
        }
    };
    child.exited = true;
    // Up to 10 MiB per stream: read on a worker, off the event loop.
    let (stdout, stderr) = crate::main_loop::offload(move || (read(stdout), read(stderr))).await;
    Ok(Run { stdout, stderr, status, timed_out })
}

/// Kills the child's process group if the call is abandoned before the child exits. The exit watcher stays
/// armed and reaps it.
struct Child {
    pid: libc::pid_t,
    exited: bool,
}

impl Drop for Child {
    fn drop(&mut self) {
        if !self.exited {
            unsafe { libc::killpg(self.pid, libc::SIGKILL) };
        }
    }
}

fn spawn(argv: &[String], stdout: libc::c_int, stderr: libc::c_int) -> Result<libc::pid_t, String> {
    let program = &argv[0];
    let failed = |reason: &str| format!("Failed to start {program}: {reason}");
    let args = argv.iter().map(|arg| CString::new(arg.as_str())).collect::<Result<Vec<_>, _>>().map_err(|_| failed("an argument contains a NUL byte"))?;
    let mut pointers: Vec<*mut libc::c_char> = args.iter().map(|arg| arg.as_ptr().cast_mut()).collect();
    pointers.push(std::ptr::null_mut());
    unsafe {
        let mut actions: libc::posix_spawn_file_actions_t = std::ptr::null_mut();
        libc::posix_spawn_file_actions_init(&mut actions);
        libc::posix_spawn_file_actions_addopen(&mut actions, 0, c"/dev/null".as_ptr(), libc::O_RDONLY, 0);
        libc::posix_spawn_file_actions_adddup2(&mut actions, stdout, 1);
        libc::posix_spawn_file_actions_adddup2(&mut actions, stderr, 2);

        // A new session (so killpg reaches grandchildren), and every descriptor not named above closed, so the
        // child cannot hold the protocol stream open. Signal dispositions and the mask go back to default: this
        // process ignores SIGPIPE, and an ignored disposition would otherwise survive exec into every command.
        let mut attributes: libc::posix_spawnattr_t = std::ptr::null_mut();
        libc::posix_spawnattr_init(&mut attributes);
        let mut all_signals: libc::sigset_t = 0;
        libc::sigfillset(&mut all_signals);
        libc::posix_spawnattr_setsigdefault(&mut attributes, &all_signals);
        let mut no_signals: libc::sigset_t = 0;
        libc::sigemptyset(&mut no_signals);
        libc::posix_spawnattr_setsigmask(&mut attributes, &no_signals);
        let flags = POSIX_SPAWN_SETSID | libc::POSIX_SPAWN_CLOEXEC_DEFAULT | libc::POSIX_SPAWN_SETSIGDEF | libc::POSIX_SPAWN_SETSIGMASK;
        libc::posix_spawnattr_setflags(&mut attributes, flags as libc::c_short);

        let mut pid = 0;
        let rc = libc::posix_spawn(&mut pid, args[0].as_ptr(), &actions, &attributes, pointers.as_ptr(), *libc::_NSGetEnviron());
        libc::posix_spawn_file_actions_destroy(&mut actions);
        libc::posix_spawnattr_destroy(&mut attributes);
        if rc != 0 {
            return Err(failed(&strerror(rc)));
        }
        Ok(pid)
    }
}

fn strerror(code: libc::c_int) -> String {
    unsafe { std::ffi::CStr::from_ptr(libc::strerror(code)) }.to_string_lossy().into_owned()
}

/// Completes with the exit status once `pid` exits, reaping it. Keeps watching after the receiver is dropped,
/// so an abandoned child is still reaped.
///
/// The exit event can arrive while the child is still finishing its exit, before `waitpid` can reap it, and a
/// child already exiting when the source registers may never raise one. So the event reaps with a blocking
/// `waitpid` (the child has exited: it returns at once), and a timer checks without blocking at once and then
/// every [`crate::tuning::REAP_BACKSTOP`] until the child is reaped.
fn watch_exit(pid: libc::pid_t) -> tokio::sync::oneshot::Receiver<i32> {
    let (exited, exit) = tokio::sync::oneshot::channel();
    let main = DispatchQueue::main();
    let proc_kind = &raw const dispatch2::_dispatch_source_type_proc;
    let source = unsafe { DispatchSource::new(proc_kind.cast_mut(), pid as usize, PROC_EXIT, Some(main)) };
    let timer_kind = &raw const dispatch2::_dispatch_source_type_timer;
    let backstop = unsafe { DispatchSource::new(timer_kind.cast_mut(), 0, 0, Some(main)) };
    // Shared by both handlers; whichever sees the exit first reaps. The handlers hold the sources until they cancel
    // them, which releases the handlers.
    let pending = Arc::new(Mutex::new(Some(exited)));
    let reap: Arc<dyn Fn(bool) + Send + Sync> = Arc::new({
        let sources: [DispatchRetained<DispatchSource>; 2] = [source.clone(), backstop.clone()];
        move |exited: bool| {
            let mut pending = pending.lock().unwrap();
            if pending.is_none() {
                return;
            }
            let mut status = 0;
            let result = loop {
                let result = unsafe { libc::waitpid(pid, &mut status, if exited { 0 } else { libc::WNOHANG }) };
                if result >= 0 || std::io::Error::last_os_error().raw_os_error() != Some(libc::EINTR) {
                    break result;
                }
            };
            if result == pid || (result < 0 && std::io::Error::last_os_error().raw_os_error() == Some(libc::ECHILD)) {
                sources.iter().for_each(|source| source.cancel());
                let _ = pending.take().unwrap().send(if result == pid { decode(status) } else { -1 });
            }
        }
    });
    let on_exit = block2::RcBlock::new({
        let reap = reap.clone();
        move || reap(true)
    });
    let on_tick = block2::RcBlock::new(move || reap(false));
    unsafe {
        source.set_event_handler_with_block(block2::RcBlock::as_ptr(&on_exit));
        backstop.set_event_handler_with_block(block2::RcBlock::as_ptr(&on_tick));
    }
    backstop.set_timer(DispatchTime::NOW, crate::tuning::REAP_BACKSTOP.as_nanos() as u64, crate::tuning::REAP_BACKSTOP.as_nanos() as u64 / 10);
    source.resume();
    backstop.resume();
    exit
}

/// Exit code, or the negated signal number (Python's `returncode` convention).
fn decode(status: libc::c_int) -> i32 {
    let signal = status & 0x7f;
    if signal == 0 { (status >> 8) & 0xff } else { -signal }
}

/// An unlinked temporary file: it lives exactly as long as its descriptor.
fn temp_file() -> Result<File, String> {
    let template = std::env::temp_dir().join("desktop-mcp.XXXXXX");
    let mut template = CString::new(template.into_os_string().into_encoded_bytes()).map_err(|_| "Could not create a temporary file".to_string())?.into_bytes_with_nul();
    let fd = unsafe { libc::mkstemp(template.as_mut_ptr().cast()) };
    if fd < 0 {
        return Err("Could not create a temporary file".to_string());
    }
    unsafe { libc::unlink(template.as_ptr().cast()) };
    Ok(unsafe { File::from_raw_fd(fd) })
}

/// The file's contents, at most [`MAX_OUTPUT_BYTES`], decoded leniently.
fn read(mut file: File) -> String {
    let mut data = Vec::new();
    if file.seek(SeekFrom::Start(0)).is_ok() {
        let _ = (&mut file).take(MAX_OUTPUT_BYTES).read_to_end(&mut data);
    }
    let mut text = String::from_utf8_lossy(&data).into_owned();
    if data.len() as u64 >= MAX_OUTPUT_BYTES {
        text += &format!("\n...[output truncated at {MAX_OUTPUT_BYTES} bytes]");
    }
    text
}
