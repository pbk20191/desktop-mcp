//! stdin and stdout as one byte stream for `serve_stream`.
//!
//! stdin may be a console or a pipe opened without overlapped I/O, so it is read with a blocking `ReadFile` on a
//! pool thread, one read at a time. A read waiting for input that never comes would keep that thread and the
//! process alive, so cancelling (on drop, or by a [`Canceller`]) calls `CancelSynchronousIo` on the thread inside
//! the call. Writes go to stdout from a pool thread too, one at a time and in order.
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

/// What the reading thread and the canceller share.
#[derive(Default)]
struct ReadState {
    cancelled: bool,
    /// The thread inside `ReadFile`, while it is.
    thread: Option<isize>,
}

/// Ends a pending read and every later one. `Send`: callable from anywhere.
#[derive(Clone)]
pub struct Canceller(Arc<Mutex<ReadState>>);

impl Canceller {
    pub fn cancel(&self) {
        self.0.lock().unwrap().cancelled = true;
        // The cancel can land before the thread has entered ReadFile; keep at it until the thread is out.
        for _ in 0..1000 {
            let state = self.0.lock().unwrap();
            let Some(thread) = state.thread else { return };
            unsafe {
                let _ = CancelSynchronousIo(HANDLE(thread as *mut _));
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
    shared: Arc<Mutex<ReadState>>,
    reading: Option<Pending<Vec<u8>>>,
    buffered: Vec<u8>,
    ended: bool,
    writing: Option<Pending<()>>,
}

impl StdIo {
    /// This process's stdin and stdout.
    pub fn stdio() -> StdIo {
        let duplicate = |which| -> OwnedHandle {
            let handle = unsafe { GetStdHandle(which) }.expect("GetStdHandle");
            let mut copy = HANDLE::default();
            unsafe { DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), &mut copy, 0, false, DUPLICATE_SAME_ACCESS) }.expect("DuplicateHandle");
            unsafe { OwnedHandle::from_raw_handle(copy.0) }
        };
        StdIo::from_handles(duplicate(STD_INPUT_HANDLE), duplicate(STD_OUTPUT_HANDLE))
    }

    /// Any readable and writable handles.
    pub fn from_handles(input: OwnedHandle, output: OwnedHandle) -> StdIo {
        StdIo { input: Arc::new(input), output: Arc::new(output), shared: Arc::default(), reading: None, buffered: Vec::new(), ended: false, writing: None }
    }

    pub fn canceller(&self) -> Canceller {
        Canceller(self.shared.clone())
    }

    /// Ends a pending read now; later reads find the end of input.
    pub fn cancel(&self) {
        self.canceller().cancel()
    }
}

fn blocking_read(input: &OwnedHandle, shared: &Mutex<ReadState>) -> io::Result<Vec<u8>> {
    {
        let mut state = shared.lock().unwrap();
        if state.cancelled {
            return Ok(Vec::new());
        }
        let mut thread = HANDLE::default();
        unsafe { DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &mut thread, 0, false, DUPLICATE_SAME_ACCESS) }.map_err(io::Error::other)?;
        state.thread = Some(thread.0 as isize);
    }
    let mut buffer = vec![0u8; READ_SIZE];
    let mut read = 0u32;
    let result = unsafe { ReadFile(HANDLE(input.as_raw_handle()), Some(&mut buffer), Some(&mut read), None) };
    if let Some(thread) = shared.lock().unwrap().thread.take() {
        unsafe {
            let _ = CloseHandle(HANDLE(thread as *mut _));
        }
    }
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

impl StdIo {
    fn poll_written(&mut self, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        if let Some(writing) = self.writing.as_mut() {
            let result = ready!(writing.as_mut().poll(cx));
            self.writing = None;
            result?;
        }
        Poll::Ready(Ok(()))
    }
}

impl hyper::rt::Write for StdIo {
    fn poll_write(mut self: Pin<&mut Self>, cx: &mut Context<'_>, buf: &[u8]) -> Poll<io::Result<usize>> {
        ready!(self.poll_written(cx))?;
        let (output, bytes) = (self.output.clone(), buf.to_vec());
        self.writing = Some(Box::pin(offload(move || {
            let mut rest = &bytes[..];
            while !rest.is_empty() {
                let mut written = 0u32;
                unsafe { WriteFile(HANDLE(output.as_raw_handle()), Some(rest), Some(&mut written), None) }.map_err(io::Error::other)?;
                rest = &rest[written as usize..];
            }
            Ok(())
        })));
        Poll::Ready(Ok(buf.len()))
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        self.poll_written(cx)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        self.poll_written(cx)
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
