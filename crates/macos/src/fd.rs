//! Byte streams on file descriptors: stdin/stdout for the stdio transport, and Unix-socket connections.
//!
//! Reads and writes go through `dispatch_io` channels on the main queue, so libdispatch, not this code, owns any
//! thread a blocking descriptor needs. Received data stays in `dispatch_data` until it is copied into the
//! reader's buffer, as in `NwIo`.
use block2::RcBlock;
use desktop_mcp_core::io::{Listener, Peer};
use dispatch2::{DispatchData, DispatchIO, DispatchIOCloseFlags, DispatchIOStreamType, DispatchObject, DispatchQueue, DispatchRetained, DispatchSource};
use std::cell::{Cell, RefCell};
use std::ffi::{c_char, c_int, c_void, CString};
use std::future::Future;
use std::net::IpAddr;
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::ptr::NonNull;
use std::rc::Rc;
use std::sync::Arc;
use std::task::{Context, Poll, Waker};
use tokio::sync::mpsc;

/// When this much is handed to the system and not yet written, writes wait.
const HIGH_WATER: usize = 256 * 1024;

#[derive(Default)]
struct State {
    /// Read and not yet consumed.
    queue: Option<DispatchRetained<DispatchData>>,
    reading: bool,
    end_of_stream: bool,
    read_waker: Option<Waker>,
    /// Bytes handed to `dispatch_io_write` and not yet written.
    unsent: usize,
    write_failed: bool,
    write_waker: Option<Waker>,
}

/// A byte stream on one descriptor (a socket) or two (stdin and stdout).
pub struct FdIo {
    reader: DispatchRetained<DispatchIO>,
    writer: DispatchRetained<DispatchIO>,
    /// The socket, to shut its write side down; `None` for pipes.
    socket: Option<c_int>,
    /// `Arc`: dispatch may release the handler blocks, which hold a clone each, on a thread of its own.
    state: Arc<RefCell<State>>,
}

fn channel(fd: c_int, close_fd: bool) -> DispatchRetained<DispatchIO> {
    let cleanup = RcBlock::new(move |_error: c_int| {
        if close_fd {
            unsafe { libc::close(fd) };
        }
    });
    let channel = unsafe { DispatchIO::new(DispatchIOStreamType::DISPATCH_IO_STREAM, fd, DispatchQueue::main(), &cleanup) };
    // Hand over whatever has arrived, however little.
    channel.set_low_water(1);
    channel
}

impl FdIo {
    /// A connected socket, which the stream now owns (and closes).
    pub fn socket(fd: c_int) -> FdIo {
        let on: c_int = 1;
        // A write to a socket whose peer has gone must fail with EPIPE, not end the process.
        unsafe { libc::setsockopt(fd, libc::SOL_SOCKET, libc::SO_NOSIGPIPE, (&on as *const c_int).cast(), std::mem::size_of::<c_int>() as u32) };
        let both = channel(fd, true);
        FdIo { reader: both.clone(), writer: both, socket: Some(fd), state: Arc::default() }
    }

    /// Reads from `input` and writes to `output`, closing them at the end when `close` says so. A write to a
    /// pipe whose reader has gone raises SIGPIPE: the process must ignore it.
    pub fn pipes(input: c_int, output: c_int, close: bool) -> FdIo {
        FdIo { reader: channel(input, close), writer: channel(output, close), socket: None, state: Arc::default() }
    }

    /// The process's stdin and stdout.
    pub fn stdio() -> FdIo {
        FdIo::pipes(libc::STDIN_FILENO, libc::STDOUT_FILENO, false)
    }
}

impl Drop for FdIo {
    fn drop(&mut self) {
        self.reader.close(DispatchIOCloseFlags::DISPATCH_IO_STOP);
        if self.socket.is_none() {
            self.writer.close(DispatchIOCloseFlags(0));
        }
    }
}

impl Peer for FdIo {
    fn peer_address(&self) -> Option<IpAddr> {
        None
    }
}

impl hyper::rt::Read for FdIo {
    fn poll_read(self: Pin<&mut Self>, cx: &mut Context<'_>, mut buffer: hyper::rt::ReadBufCursor<'_>) -> Poll<std::io::Result<()>> {
        let mut state = self.state.borrow_mut();
        if let Some(queue) = state.queue.take() {
            let total = queue.size();
            let cursor = &mut buffer as *mut hyper::rt::ReadBufCursor<'_> as usize; // used only during the apply
            let copied = Rc::new(Cell::new(0usize));
            let counter = copied.clone();
            let fill = RcBlock::new(move |_region: *mut c_void, _offset: usize, address: *const u8, size: usize| -> u8 {
                let buffer = unsafe { &mut *(cursor as *mut hyper::rt::ReadBufCursor<'_>) };
                let count = size.min(buffer.remaining());
                buffer.put_slice(unsafe { std::slice::from_raw_parts(address, count) });
                counter.set(counter.get() + count);
                (buffer.remaining() > 0) as u8
            });
            // Declared with plain pointers and a `u8` result where dispatch2 says `NonNull` and `bool`; same ABI.
            unsafe { queue.apply(RcBlock::as_ptr(&fill).cast()) };
            let count = copied.get();
            if count < total {
                state.queue = Some(unsafe { queue.subrange(count, total - count) });
            }
            return Poll::Ready(Ok(()));
        }
        if state.end_of_stream {
            return Poll::Ready(Ok(()));
        }
        state.read_waker = Some(cx.waker().clone());
        if !state.reading {
            state.reading = true;
            let shared = self.state.clone();
            // One read to the end of the stream; the handler runs for every piece that arrives.
            let handler = RcBlock::new(move |done: u8, data: *mut c_void, _error: c_int| {
                let mut state = shared.borrow_mut();
                if let Some(data) = NonNull::new(data.cast::<DispatchData>()) {
                    let data = unsafe { data.as_ref() };
                    if data.size() > 0 {
                        state.queue = Some(match state.queue.take() {
                            Some(queue) => queue.concat(data),
                            None => data.retain(),
                        });
                    }
                }
                if done != 0 {
                    state.end_of_stream = true;
                }
                if let Some(waker) = state.read_waker.take() {
                    waker.wake();
                }
            });
            unsafe { self.reader.read(0, usize::MAX, DispatchQueue::main(), RcBlock::as_ptr(&handler).cast()) };
        }
        Poll::Pending
    }
}

impl hyper::rt::Write for FdIo {
    fn poll_write(self: Pin<&mut Self>, cx: &mut Context<'_>, bytes: &[u8]) -> Poll<std::io::Result<usize>> {
        let mut state = self.state.borrow_mut();
        if state.write_failed {
            return Poll::Ready(Err(std::io::ErrorKind::BrokenPipe.into()));
        }
        if state.unsent >= HIGH_WATER {
            state.write_waker = Some(cx.waker().clone());
            return Poll::Pending;
        }
        if bytes.is_empty() {
            return Poll::Ready(Ok(0));
        }
        state.unsent += bytes.len();
        let (shared, length) = (self.state.clone(), bytes.len());
        let handler = RcBlock::new(move |done: u8, _remaining: *mut c_void, error: c_int| {
            if done == 0 {
                return;
            }
            let mut state = shared.borrow_mut();
            state.unsent -= length;
            if error != 0 {
                state.write_failed = true;
            }
            if let Some(waker) = state.write_waker.take() {
                waker.wake();
            }
        });
        let data = DispatchData::from_bytes(bytes); // copies: the caller only lends the bytes
        unsafe { self.writer.write(0, &data, DispatchQueue::main(), RcBlock::as_ptr(&handler).cast()) };
        Poll::Ready(Ok(bytes.len()))
    }

    fn poll_flush(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Poll::Ready(Ok(()))
    }

    /// Ready once everything is written; a socket's write side is then shut down, so its peer sees the end.
    fn poll_shutdown(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        let mut state = self.state.borrow_mut();
        if state.unsent > 0 && !state.write_failed {
            state.write_waker = Some(cx.waker().clone());
            return Poll::Pending;
        }
        if let Some(fd) = self.socket {
            unsafe { libc::shutdown(fd, libc::SHUT_WR) };
        }
        Poll::Ready(Ok(()))
    }
}

extern "C" {
    /// launchd's socket activation (`<launch.h>`): the descriptors of the plist's `Sockets` entry `name`.
    fn launch_activate_socket(name: *const c_char, fds: *mut *mut c_int, count: *mut usize) -> c_int;
}

/// A dispatch read source on a listening socket. Cancelling it closes the socket.
struct Accepting(DispatchRetained<DispatchSource>);

impl Drop for Accepting {
    fn drop(&mut self) {
        self.0.cancel();
    }
}

/// A Unix domain socket that accepts connections from processes of the same user only.
pub struct UnixListener {
    _accepting: Vec<Accepting>,
    connections: mpsc::UnboundedReceiver<FdIo>,
    /// The socket file this listener made, removed when it goes; `None` for launchd's.
    owned_path: Option<PathBuf>,
    activated: bool,
}

impl UnixListener {
    /// Listens on launchd's socket `launchd_name` when this process was started with one, else on `path`
    /// (mode 0600). A stale socket file is replaced; a live one, or a file that is not a socket, is refused.
    pub fn listen(path: &Path, launchd_name: Option<&str>) -> Result<UnixListener, String> {
        let (fds, owned_path, activated) = match launchd_name.map(activate).transpose()?.flatten() {
            Some(fds) => (fds, None, true),
            None => (vec![bind(path)?], Some(path.to_path_buf()), false),
        };
        let (sender, connections) = mpsc::unbounded_channel();
        let accepting = fds.into_iter().map(|fd| accept_on(fd, sender.clone(), path.display().to_string())).collect();
        Ok(UnixListener { _accepting: accepting, connections, owned_path, activated })
    }

    /// Whether launchd made the socket.
    pub fn activated_by_launchd(&self) -> bool {
        self.activated
    }
}

impl Drop for UnixListener {
    fn drop(&mut self) {
        if let Some(path) = &self.owned_path {
            let _ = std::fs::remove_file(path);
        }
    }
}

impl Listener for UnixListener {
    type Io = FdIo;

    fn accept(&mut self) -> impl Future<Output = Option<FdIo>> {
        self.connections.recv()
    }
}

fn last_error() -> String {
    std::io::Error::last_os_error().to_string()
}

/// launchd's sockets for `name`, or `None` when launchd did not start this process with one.
fn activate(name: &str) -> Result<Option<Vec<c_int>>, String> {
    let name = CString::new(name).map_err(|_| "invalid launchd socket name".to_string())?;
    let (mut fds, mut count) = (std::ptr::null_mut::<c_int>(), 0usize);
    let code = unsafe { launch_activate_socket(name.as_ptr(), &mut fds, &mut count) };
    let list = (!fds.is_null()).then(|| unsafe { std::slice::from_raw_parts(fds, count) }.to_vec()).unwrap_or_default();
    unsafe { libc::free(fds.cast()) };
    match code {
        0 if !list.is_empty() => Ok(Some(list)),
        0 => Err(format!("launchd has no sockets for {}", name.to_string_lossy())),
        libc::ENOENT | libc::ESRCH => Ok(None),
        code => Err(format!("launch_activate_socket: {}", std::io::Error::from_raw_os_error(code))),
    }
}

fn address(path: &Path) -> Result<libc::sockaddr_un, String> {
    let mut address: libc::sockaddr_un = unsafe { std::mem::zeroed() };
    let bytes = path.as_os_str().as_encoded_bytes();
    if bytes.len() >= address.sun_path.len() {
        return Err(format!("{}: longer than the {} bytes a socket path can have", path.display(), address.sun_path.len() - 1));
    }
    address.sun_family = libc::AF_UNIX as libc::sa_family_t;
    for (slot, byte) in address.sun_path.iter_mut().zip(bytes) {
        *slot = *byte as c_char;
    }
    Ok(address)
}

fn sockaddr_len() -> libc::socklen_t {
    std::mem::size_of::<libc::sockaddr_un>() as libc::socklen_t
}

fn bind(path: &Path) -> Result<c_int, String> {
    let address = address(path)?;
    match std::fs::symlink_metadata(path) {
        Ok(metadata) => {
            use std::os::unix::fs::FileTypeExt;
            if !metadata.file_type().is_socket() {
                return Err(format!("{} exists and is not a socket", path.display()));
            }
            replace_stale(path, &address)?;
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(format!("{}: {error}", path.display())),
    }
    unsafe {
        let fd = libc::socket(libc::AF_UNIX, libc::SOCK_STREAM, 0);
        if fd < 0 {
            return Err(format!("socket: {}", last_error()));
        }
        if libc::bind(fd, (&address as *const libc::sockaddr_un).cast(), sockaddr_len()) != 0 {
            let reason = format!("{}: {}", path.display(), last_error());
            libc::close(fd);
            return Err(reason);
        }
        let path_c = CString::new(path.as_os_str().as_encoded_bytes()).map_err(|_| "invalid socket path".to_string())?;
        if libc::chmod(path_c.as_ptr(), 0o600) != 0 || libc::listen(fd, 16) != 0 {
            let reason = format!("{}: {}", path.display(), last_error());
            libc::close(fd);
            libc::unlink(path_c.as_ptr());
            return Err(reason);
        }
        Ok(fd)
    }
}

/// Removes a socket file nobody listens on; refuses when someone does.
fn replace_stale(path: &Path, address: &libc::sockaddr_un) -> Result<(), String> {
    unsafe {
        let probe = libc::socket(libc::AF_UNIX, libc::SOCK_STREAM, 0);
        if probe < 0 {
            return Err(format!("socket: {}", last_error()));
        }
        let connected = libc::connect(probe, (address as *const libc::sockaddr_un).cast(), sockaddr_len()) == 0;
        let refused = std::io::Error::last_os_error().raw_os_error() == Some(libc::ECONNREFUSED);
        libc::close(probe);
        if connected {
            return Err("another server is listening on the socket".into());
        }
        if !refused {
            return Err(format!("{}: {}", path.display(), last_error()));
        }
    }
    match std::fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(format!("{}: {error}", path.display())),
    }
}

/// Accepts on `listener` whenever a connection waits, handing on those from this process's user.
fn accept_on(listener: c_int, sender: mpsc::UnboundedSender<FdIo>, name: String) -> Accepting {
    unsafe {
        let flags = libc::fcntl(listener, libc::F_GETFL);
        libc::fcntl(listener, libc::F_SETFL, flags | libc::O_NONBLOCK);
    }
    let kind = &raw const dispatch2::_dispatch_source_type_read;
    let source = unsafe { DispatchSource::new(kind.cast_mut(), listener as usize, 0, Some(DispatchQueue::main())) };
    let accept = RcBlock::new(move || loop {
        let fd = unsafe { libc::accept(listener, std::ptr::null_mut(), std::ptr::null_mut()) };
        if fd >= 0 {
            let (mut uid, mut gid) = (0, 0);
            if unsafe { libc::getpeereid(fd, &mut uid, &mut gid) } != 0 || uid != unsafe { libc::geteuid() } {
                eprintln!("[desktop-mcp] Refused a connection on {name} from another user");
                unsafe { libc::close(fd) };
                continue;
            }
            let size: c_int = 1 << 20;
            unsafe { libc::setsockopt(fd, libc::SOL_SOCKET, libc::SO_SNDBUF, (&size as *const c_int).cast(), std::mem::size_of::<c_int>() as u32) };
            if sender.send(FdIo::socket(fd)).is_err() {
                return;
            }
            continue;
        }
        match std::io::Error::last_os_error().raw_os_error() {
            Some(libc::EINTR) | Some(libc::ECONNABORTED) => continue,
            _ => return, // EAGAIN: nothing more waits; anything else is retried on the next event
        }
    });
    let closed = RcBlock::new(move || unsafe {
        libc::close(listener);
    });
    unsafe {
        source.set_event_handler_with_block(RcBlock::as_ptr(&accept));
        source.set_cancel_handler_with_block(RcBlock::as_ptr(&closed));
    }
    source.resume();
    Accepting(source)
}
