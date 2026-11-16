//! The executor: one future on this thread, served by its DispatcherQueue and message loop.
//!
//! The root future is polled from work queued on the thread's `Windows.System.DispatcherQueue`; its waker queues
//! that work (once per batch of wakes), so wakes from thread-pool callbacks land on this thread. The guest tokio
//! runtime gets its turns from queued work (`kick`) and from a `DispatcherQueueTimer` (`set_tick`). Nothing here
//! starts a thread.
use crate::tuning::{FAST_TICK, SLOW_TICK};
use desktop_mcp_core::guest::{Guest, MainLoop, Tick};
use std::cell::{Cell, RefCell};
use std::future::Future;
use std::panic::{catch_unwind, resume_unwind, AssertUnwindSafe};
use std::pin::Pin;
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Arc;
use std::task::{Context, Poll, Wake, Waker};
use windows::Foundation::{TimeSpan, TypedEventHandler};
use windows::System::{DispatcherQueue, DispatcherQueueController, DispatcherQueueHandler, DispatcherQueueTimer};
use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::System::WinRT::{CreateDispatcherQueueController, DispatcherQueueOptions, DQTAT_COM_NONE, DQTYPE_THREAD_CURRENT};
use windows::Win32::UI::WindowsAndMessaging::{DispatchMessageW, GetMessageW, PostThreadMessageW, TranslateMessage, MSG, WM_QUIT};
use windows::Win32::Foundation::{LPARAM, WPARAM};

static TURNS: AtomicUsize = AtomicUsize::new(0);
static QUEUED_POLLS: AtomicUsize = AtomicUsize::new(0);

/// How many turns the guest runtime has been given in this process. For tests and diagnostics.
pub fn turns() -> usize {
    TURNS.load(Ordering::Relaxed)
}

/// How many polls of the root future were queued: one per batch of wakes. For tests.
pub fn queued_polls() -> usize {
    QUEUED_POLLS.load(Ordering::Relaxed)
}

/// What the queued work reaches on this thread.
struct Driver {
    poll: RefCell<Box<dyn FnMut() -> bool>>,
    guest: Guest,
    done: Cell<bool>,
    turning: Cell<bool>,
    kick_pending: Cell<bool>,
}

thread_local! {
    static DRIVER: RefCell<Option<Rc<Driver>>> = const { RefCell::new(None) };
}

fn driver() -> Option<Rc<Driver>> {
    DRIVER.with(|driver| driver.borrow().clone())
}

/// Polls the root future. False when it is being polled already (a nested message loop, such as a COM call's
/// modal wait, ran this from inside a poll), so the caller asks again.
fn poll_root() -> bool {
    let Some(driver) = driver() else { return true };
    if driver.done.get() {
        return true;
    }
    let Ok(mut poll) = driver.poll.try_borrow_mut() else { return false };
    // Polls run inside the message loop's dispatch, which checks `done` right after.
    if poll() {
        driver.done.set(true);
    }
    true
}

/// One guest-runtime turn, unless one is running or the root is being polled.
fn turn() {
    let Some(driver) = driver() else { return };
    driver.kick_pending.set(false);
    if driver.done.get() || driver.turning.replace(true) {
        return;
    }
    if driver.poll.try_borrow_mut().is_ok() {
        TURNS.fetch_add(1, Ordering::Relaxed);
        driver.guest.turn();
    }
    driver.turning.set(false);
}

/// Queues a poll of the root on the main thread. `Send`: thread-pool callbacks wake it.
struct QueueWaker {
    queue: DispatcherQueue,
    pending: AtomicBool,
}

impl Wake for QueueWaker {
    fn wake(self: Arc<Self>) {
        self.wake_by_ref()
    }

    fn wake_by_ref(self: &Arc<Self>) {
        if self.pending.swap(true, Ordering::AcqRel) {
            return;
        }
        QUEUED_POLLS.fetch_add(1, Ordering::Relaxed);
        let this = self.clone();
        let handler = DispatcherQueueHandler::new(move || {
            this.pending.store(false, Ordering::Release);
            if !poll_root() {
                this.wake_by_ref();
            }
            Ok(())
        });
        // False only while the queue shuts down, when nothing is left to poll.
        let _ = self.queue.TryEnqueue(&handler);
    }
}

struct Ticks {
    queue: DispatcherQueue,
    timer: DispatcherQueueTimer,
}

impl MainLoop for Ticks {
    fn kick(&self) {
        let Some(driver) = driver() else { return };
        if driver.kick_pending.replace(true) {
            return;
        }
        let _ = self.queue.TryEnqueue(&DispatcherQueueHandler::new(|| {
            turn();
            Ok(())
        }));
    }

    fn set_tick(&self, rate: Tick) {
        let interval = match rate {
            Tick::Off => {
                let _ = self.timer.Stop();
                return;
            }
            Tick::Slow => SLOW_TICK,
            Tick::Fast => FAST_TICK,
        };
        let interval = TimeSpan { Duration: (interval.as_nanos() / 100) as i64 };
        if self.timer.Interval().map(|current| current.Duration) != Ok(interval.Duration) || !self.timer.IsRunning().unwrap_or(false) {
            let _ = self.timer.SetInterval(interval);
            let _ = self.timer.Start();
        }
    }
}

/// Runs the future `build` makes on this thread until it completes, serving the thread's DispatcherQueue and
/// message loop meanwhile. One at a time per thread.
pub fn run<F: Future + 'static>(build: impl FnOnce(Rc<dyn MainLoop>) -> F) -> F::Output {
    // A single-threaded apartment, as a UI thread has: the WinRT clipboard needs one, and this thread pumps
    // messages. (The queue's own apartment option applies only to threads it creates.)
    let _ = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
    let options = DispatcherQueueOptions { dwSize: size_of::<DispatcherQueueOptions>() as u32, threadType: DQTYPE_THREAD_CURRENT, apartmentType: DQTAT_COM_NONE };
    let controller: DispatcherQueueController = unsafe { CreateDispatcherQueueController(options) }.expect("CreateDispatcherQueueController failed");
    let queue = controller.DispatcherQueue().expect("the controller has a queue");
    let timer = queue.CreateTimer().expect("DispatcherQueue.CreateTimer failed");
    timer.SetIsRepeating(true).expect("DispatcherQueueTimer.IsRepeating");
    timer
        .Tick(&TypedEventHandler::new(|_, _| {
            turn();
            Ok(())
        }))
        .expect("DispatcherQueueTimer.Tick");
    let ticks = Rc::new(Ticks { queue: queue.clone(), timer: timer.clone() });

    let guest = Guest::new();
    let _entered = guest.enter();
    let root = guest.root(ticks.clone(), build(ticks.clone()));
    let output: Rc<RefCell<Option<std::thread::Result<F::Output>>>> = Rc::new(RefCell::new(None));
    let waker_state = Arc::new(QueueWaker { queue: queue.clone(), pending: AtomicBool::new(false) });
    let waker = Waker::from(waker_state.clone());
    let mut root = Box::pin(root) as Pin<Box<dyn Future<Output = F::Output>>>;
    let slot = output.clone();
    let poll = Box::new(move || {
        let mut cx = Context::from_waker(&waker);
        match catch_unwind(AssertUnwindSafe(|| root.as_mut().poll(&mut cx))) {
            Ok(Poll::Pending) => false,
            Ok(Poll::Ready(value)) => {
                *slot.borrow_mut() = Some(Ok(value));
                true
            }
            Err(panic) => {
                *slot.borrow_mut() = Some(Err(panic));
                true
            }
        }
    });
    drop(_entered);
    let driver = Rc::new(Driver { poll: RefCell::new(poll), guest, done: Cell::new(false), turning: Cell::new(false), kick_pending: Cell::new(false) });
    DRIVER.with(|slot| *slot.borrow_mut() = Some(driver.clone()));
    let entered = driver.guest.enter();
    waker_state.wake_by_ref(); // the first poll

    let mut message = MSG::default();
    while !driver.done.get() {
        let got = unsafe { GetMessageW(&mut message, None, 0, 0) };
        if got.0 <= 0 {
            break;
        }
        unsafe {
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    let _ = timer.Stop();
    drop(entered);
    DRIVER.with(|slot| slot.borrow_mut().take());
    // Shutting the queue down runs what is still queued (whose handlers now find no driver) and needs the
    // message loop to finish.
    if let Ok(shutdown) = controller.ShutdownQueueAsync() {
        let thread = unsafe { GetCurrentThreadId() };
        let _ = shutdown.SetCompleted(&windows_future::AsyncActionCompletedHandler::new(move |_, _| {
            unsafe {
                let _ = PostThreadMessageW(thread, WM_QUIT, WPARAM(0), LPARAM(0));
            }
            Ok(())
        }));
        loop {
            let got = unsafe { GetMessageW(&mut message, None, 0, 0) };
            if got.0 <= 0 {
                break;
            }
            unsafe {
                let _ = TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
    }
    drop(driver);
    // The apartment stays for the thread's life: completions of operations the root started (on sockets it
    // closed, say) can still arrive after this, and must not find COM torn down.
    let result = output.borrow_mut().take().expect("the root completed");
    match result {
        Ok(value) => value,
        Err(panic) => resume_unwind(panic),
    }
}
