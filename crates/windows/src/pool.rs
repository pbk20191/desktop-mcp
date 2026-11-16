//! The Windows thread pool as futures: work, exact timers and handle waits. Callbacks run on pool threads and
//! complete a oneshot; the awaiting side runs on the main loop.
use std::ffi::c_void;
use std::future::Future;
use std::panic::{catch_unwind, resume_unwind, AssertUnwindSafe};
use std::pin::Pin;
use std::task::{Context, Poll};
use std::time::Duration;
use tokio::sync::oneshot;
use windows::Win32::Foundation::{FILETIME, HANDLE};
use windows::Win32::System::Threading::{
    CloseThreadpoolTimer, CloseThreadpoolWait, CreateThreadpoolTimer, CreateThreadpoolWait, SetThreadpoolTimer, SetThreadpoolWait, TrySubmitThreadpoolCallback,
    WaitForThreadpoolTimerCallbacks, WaitForThreadpoolWaitCallbacks, PTP_CALLBACK_INSTANCE, PTP_TIMER, PTP_WAIT,
};

type Work = Box<dyn FnOnce() + Send>;

unsafe extern "system" fn run_work(_instance: PTP_CALLBACK_INSTANCE, context: *mut c_void) {
    let work = unsafe { Box::from_raw(context as *mut Work) };
    work();
}

/// Runs `work` on a pool thread. A panic in `work` resurfaces where the result is awaited.
pub fn offload<T: Send + 'static>(work: impl FnOnce() -> T + Send + 'static) -> impl Future<Output = T> {
    let (sender, receiver) = oneshot::channel();
    let job: Work = Box::new(move || {
        let _ = sender.send(catch_unwind(AssertUnwindSafe(work)));
    });
    let context = Box::into_raw(Box::new(job));
    if unsafe { TrySubmitThreadpoolCallback(Some(run_work), Some(context as *mut c_void), None) }.is_err() {
        // The pool refused (out of memory): run it here rather than lose it.
        unsafe { run_work(PTP_CALLBACK_INSTANCE::default(), context as *mut c_void) };
    }
    async move {
        match receiver.await.expect("the pool runs every submitted callback") {
            Ok(value) => value,
            Err(panic) => resume_unwind(panic),
        }
    }
}

/// A pool object's callback context: the sender of its one completion.
struct Signal(std::sync::Mutex<Option<oneshot::Sender<()>>>);

impl Signal {
    fn fire(&self) {
        if let Some(sender) = self.0.lock().unwrap().take() {
            let _ = sender.send(());
        }
    }
}

unsafe extern "system" fn timer_fired(_instance: PTP_CALLBACK_INSTANCE, context: *mut c_void, _timer: PTP_TIMER) {
    unsafe { &*(context as *const Signal) }.fire();
}

unsafe extern "system" fn wait_fired(_instance: PTP_CALLBACK_INSTANCE, context: *mut c_void, _wait: PTP_WAIT, _result: u32) {
    unsafe { &*(context as *const Signal) }.fire();
}

/// Completes after `duration`, measured by a pool timer with no coalescing window. Dropping it cancels the timer.
pub fn sleep(duration: Duration) -> Sleep {
    let (sender, receiver) = oneshot::channel();
    let signal = Box::into_raw(Box::new(Signal(std::sync::Mutex::new(Some(sender)))));
    let timer = unsafe { CreateThreadpoolTimer(Some(timer_fired), Some(signal as *mut c_void), None) }.expect("CreateThreadpoolTimer failed");
    // Negative: relative, in 100 ns units (clamped: a cast past i64 would wrap and fire at once).
    let due = -((duration.as_nanos() / 100).min(i64::MAX as u128) as i64).max(1);
    let due = FILETIME { dwLowDateTime: due as u32, dwHighDateTime: (due >> 32) as u32 };
    unsafe { SetThreadpoolTimer(timer, Some(&due), 0, None) };
    Sleep { timer, signal, receiver }
}

pub struct Sleep {
    timer: PTP_TIMER,
    signal: *mut Signal,
    receiver: oneshot::Receiver<()>,
}

impl Future for Sleep {
    type Output = ();
    fn poll(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<()> {
        Pin::new(&mut self.receiver).poll(cx).map(|_| ())
    }
}

impl Drop for Sleep {
    fn drop(&mut self) {
        unsafe {
            SetThreadpoolTimer(self.timer, None, 0, None);
            WaitForThreadpoolTimerCallbacks(self.timer, true);
            CloseThreadpoolTimer(self.timer);
            drop(Box::from_raw(self.signal));
        }
    }
}

/// Completes when `handle` is signalled. Dropping it unregisters the wait. The handle must stay open meanwhile.
pub fn wait_for(handle: HANDLE) -> HandleWait {
    let (sender, receiver) = oneshot::channel();
    let signal = Box::into_raw(Box::new(Signal(std::sync::Mutex::new(Some(sender)))));
    let wait = unsafe { CreateThreadpoolWait(Some(wait_fired), Some(signal as *mut c_void), None) }.expect("CreateThreadpoolWait failed");
    unsafe { SetThreadpoolWait(wait, Some(handle), None) };
    HandleWait { wait, signal, receiver }
}

pub struct HandleWait {
    wait: PTP_WAIT,
    signal: *mut Signal,
    receiver: oneshot::Receiver<()>,
}

impl Future for HandleWait {
    type Output = ();
    fn poll(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<()> {
        Pin::new(&mut self.receiver).poll(cx).map(|_| ())
    }
}

impl Drop for HandleWait {
    fn drop(&mut self) {
        unsafe {
            SetThreadpoolWait(self.wait, None, None);
            WaitForThreadpoolWaitCallbacks(self.wait, true);
            CloseThreadpoolWait(self.wait);
            drop(Box::from_raw(self.signal));
        }
    }
}
