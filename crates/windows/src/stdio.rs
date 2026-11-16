//! stdin and stdout as one byte stream for `serve_stream`.
//!
//! stdin may be a console or a pipe opened without overlapped I/O, so it is read with a blocking `ReadFile` on a
//! pool thread, one read at a time. Writes go to stdout from a pool thread too, one at a time and in order; a
//! write completes when its bytes are in stdout, so a failed write is reported for the message that failed. A read
//! waiting for input that never comes, or a write to a client that stops reading, would keep its thread and the
//! process alive, so cancelling (on drop, or by a [`Canceller`]) calls `CancelSynchronousIo` on the threads inside
//! those calls.
use crate::pool::offload;
use std::future::Future;
use std::io;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::pin::Pin;
use std::sync::{Arc, Mutex};
use std::task::{ready, Context, Poll};
use std::time::Duration;
use windows::Win32::Foundation::{CloseHandle, DuplicateHandle, DUPLICATE_SAME_ACCESS, ERROR_BROKEN_PIPE, ERROR_HANDLE_EOF, ERROR_OPERATION_ABORTED, HANDLE};
use windows::Win32::Storage::FileSystem::{ReadFile, WriteFile};
use windows::Win32::System::Console::{GetStdHandle, STD_INPUT_HANDLE, STD_OUTPUT_HANDLE};
use windows::Win32::System::IO::CancelSynchronousIo;
use windows::Win32::System::Threading::{GetCurrentProcess, GetCurrentThread};

const READ_SIZE: usize = 64 * 1024;

/// What the reading and writing threads and the canceller share.
#[derive(Default)]
struct IoState {
    cancelled: bool,
    /// The thread inside `ReadFile`, while it is.
    reader: Option<isize>,
    /// The thread inside `WriteFile`, while it is.
    writer: Option<isize>,
}

/// Which of [`IoState`]'s threads a blocking call is.
type Slot = fn(&mut IoState) -> &mut Option<isize>;

/// Runs the blocking `call` with this thread recorded in `slot`, so a cancel can end it; `None` once cancelled.
fn cancellable<T>(shared: &Mutex<IoState>, slot: Slot, call: impl FnOnce() -> T) -> io::Result<Option<T>> {
    {
        let mut state = shared.lock().unwrap();
        if state.cancelled {
            return Ok(None);
        }
        let mut thread = HANDLE::default();
        unsafe { DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &mut thread, 0, false, DUPLICATE_SAME_ACCESS) }.map_err(io::Error::other)?;
        *slot(&mut state) = Some(thread.0 as isize);
    }
    let result = call();
    if let Some(thread) = slot(&mut shared.lock().unwrap()).take() {
        unsafe {
            let _ = CloseHandle(HANDLE(thread as *mut _));
        }
    }
    Ok(Some(result))
}

/// Ends a pending read and write and every later one. `Send`: callable from anywhere.
#[derive(Clone)]
pub struct Canceller(Arc<Mutex<IoState>>);

impl Canceller {
    pub fn cancel(&self) {
        self.0.lock().unwrap().cancelled = true;
        // The cancel can land before a thread has entered its call; keep at it until the threads are out.
        for _ in 0..1000 {
            let state = self.0.lock().unwrap();
            if state.reader.is_none() && state.writer.is_none() {
                return;
            }
            for thread in [state.reader, state.writer].into_iter().flatten() {
                unsafe {
                    let _ = CancelSynchronousIo(HANDLE(thread as *mut _));
                }
            }
            drop(state);
            std::thread::sleep(Duration::from_millis(1));
        }
    }
}

type Pending<T> = Pin<Box<dyn Future<Output = io::Result<T>>>>;

pub struct StdIo {
    input: Arc<OwnedHandle>,
    output: Arc<OwnedHandle>,
    shared: Arc<Mutex<IoState>>,
    reading: Option<Pending<Vec<u8>>>,
    buffered: Vec<u8>,
    ended: bool,
    /// The write in progress and how many bytes it was given.
    writing: Option<(Pending<()>, usize)>,
}

impl StdIo {
    /// This process's stdin and stdout; an error when it was started without one.
    pub fn stdio() -> io::Result<StdIo> {
        let duplicate = |which| -> io::Result<OwnedHandle> {
            let handle = unsafe { GetStdHandle(which) }.map_err(io::Error::other)?;
            let mut copy = HANDLE::default();
            unsafe { DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), &mut copy, 0, false, DUPLICATE_SAME_ACCESS) }.map_err(io::Error::other)?;
            Ok(unsafe { OwnedHandle::from_raw_handle(copy.0) })
        };
        Ok(StdIo::from_handles(duplicate(STD_INPUT_HANDLE)?, duplicate(STD_OUTPUT_HANDLE)?))
    }

    /// Any readable and writable handles.
    pub fn from_handles(input: OwnedHandle, output: OwnedHandle) -> StdIo {
        StdIo { input: Arc::new(input), output: Arc::new(output), shared: Arc::default(), reading: None, buffered: Vec::new(), ended: false, writing: None }
    }

    pub fn canceller(&self) -> Canceller {
        Canceller(self.shared.clone())
    }

    /// Ends a pending read and write now; later reads find the end of input and later writes fail.
    pub fn cancel(&self) {
        self.canceller().cancel()
    }
}

fn blocking_read(input: &OwnedHandle, shared: &Mutex<IoState>) -> io::Result<Vec<u8>> {
    let mut buffer = vec![0u8; READ_SIZE];
    let mut read = 0u32;
    let Some(result) = cancellable(shared, |state| &mut state.reader, || unsafe { ReadFile(HANDLE(input.as_raw_handle()), Some(&mut buffer), Some(&mut read), None) })? else {
        return Ok(Vec::new());
    };
    match result {
        Ok(()) => {
            buffer.truncate(read as usize);
            Ok(buffer)
        }
        Err(error) if [ERROR_OPERATION_ABORTED, ERROR_BROKEN_PIPE, ERROR_HANDLE_EOF].iter().any(|code| error.code() == code.to_hresult()) => Ok(Vec::new()),
        Err(error) => Err(io::Error::other(error)),
    }
}

impl hyper::rt::Read for StdIo {
    fn poll_read(mut self: Pin<&mut Self>, cx: &mut Context<'_>, mut buf: hyper::rt::ReadBufCursor<'_>) -> Poll<io::Result<()>> {
        loop {
            if !self.buffered.is_empty() {
                let count = self.buffered.len().min(buf.remaining());
                buf.put_slice(&self.buffered[..count]);
                self.buffered.drain(..count);
                return Poll::Ready(Ok(()));
            }
            if self.ended {
                return Poll::Ready(Ok(()));
            }
            if self.reading.is_none() {
                let (input, shared) = (self.input.clone(), self.shared.clone());
                self.reading = Some(Box::pin(offload(move || blocking_read(&input, &shared))));
            }
            let bytes = ready!(self.reading.as_mut().unwrap().as_mut().poll(cx));
            self.reading = None;
            let bytes = bytes?;
            if bytes.is_empty() {
                self.ended = true;
            }
            self.buffered = bytes;
        }
    }
}

fn blocking_write(output: &OwnedHandle, shared: &Mutex<IoState>, mut rest: &[u8]) -> io::Result<()> {
    while !rest.is_empty() {
        let mut written = 0u32;
        let Some(result) = cancellable(shared, |state| &mut state.writer, || unsafe { WriteFile(HANDLE(output.as_raw_handle()), Some(rest), Some(&mut written), None) })? else {
            return Err(io::ErrorKind::BrokenPipe.into());
        };
        result.map_err(io::Error::other)?;
        rest = &rest[written as usize..];
    }
    Ok(())
}

impl StdIo {
    /// Waits for the write in progress; its byte count once it is done (0 when there is none).
    fn poll_written(&mut self, cx: &mut Context<'_>) -> Poll<io::Result<usize>> {
        let Some((writing, length)) = self.writing.as_mut() else { return Poll::Ready(Ok(0)) };
        let result = ready!(writing.as_mut().poll(cx));
        let length = *length;
        self.writing = None;
        Poll::Ready(result.map(|()| length))
    }
}

impl hyper::rt::Write for StdIo {
    /// Ready once the bytes are written, with that write's error if it failed. While it is pending the caller
    /// retries with the same bytes, as `write_all` does.
    fn poll_write(mut self: Pin<&mut Self>, cx: &mut Context<'_>, buf: &[u8]) -> Poll<io::Result<usize>> {
        if self.writing.is_none() {
            let (output, shared, bytes) = (self.output.clone(), self.shared.clone(), buf.to_vec());
            self.writing = Some((Box::pin(offload(move || blocking_write(&output, &shared, &bytes))), buf.len()));
        }
        self.poll_written(cx)
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        self.poll_written(cx).map_ok(|_| ())
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        self.poll_written(cx).map_ok(|_| ())
    }
}

impl desktop_mcp_core::io::Peer for StdIo {
    fn peer_address(&self) -> Option<std::net::IpAddr> {
        None
    }
}

impl Drop for StdIo {
    fn drop(&mut self) {
        self.cancel();
    }
}
