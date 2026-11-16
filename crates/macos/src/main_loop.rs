//! The executor: one future on the main thread, served by the main run loop.
//!
//! The future is polled inside a `CFRunLoopSource` callback and its waker signals that source, so wakes from any
//! thread land on the main thread. The guest tokio runtime gets its turns from run loop timers. Nothing here
//! starts a thread; blocking work goes to a libdispatch queue with [`offload`].
use desktop_mcp_core::guest::{Guest, MainLoop, Tick};
use dispatch2::{DispatchQoS, DispatchQueue, GlobalQueueIdentifier};
use objc2_core_foundation::{
    kCFRunLoopCommonModes, kCFRunLoopDefaultMode, CFAbsoluteTimeGetCurrent, CFRetained, CFRunLoop, CFRunLoopActivity, CFRunLoopObserver,
    CFRunLoopObserverContext, CFRunLoopSource, CFRunLoopSourceContext, CFRunLoopTimer, CFRunLoopTimerContext,
};
use std::any::Any;
use std::cell::{Cell, RefCell};
use std::ffi::c_void;
use std::future::Future;
use std::panic::{catch_unwind, resume_unwind, AssertUnwindSafe};
use std::rc::Rc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, OnceLock};
use std::task::{Context, Poll, Wake, Waker};

/// A fire date that never comes: how a timer is parked.
const NEVER: f64 = 1e10;
use crate::tuning::{FAST_TICK as FAST_INTERVAL, SLOW_TICK as SLOW_INTERVAL};

static TURNS: AtomicUsize = AtomicUsize::new(0);

/// How many turns the guest runtime has been given in this process. For tests and diagnostics.
pub fn turns() -> usize {
    TURNS.load(Ordering::Relaxed)
}

/// Signals the source that polls the root future. `CFRunLoopSourceSignal` and `CFRunLoopWakeUp` may be called
/// from any thread.
struct SourceWaker {
    source: OnceLock<CFRetained<CFRunLoopSource>>,
    run_loop: CFRetained<CFRunLoop>,
}

unsafe impl Send for SourceWaker {}
unsafe impl Sync for SourceWaker {}

impl Wake for SourceWaker {
    fn wake(self: Arc<Self>) {
        self.wake_by_ref()
    }

    fn wake_by_ref(self: &Arc<Self>) {
        if let Some(source) = self.source.get() {
            source.signal();
            self.run_loop.wake_up();
        }
    }
}

/// What the run loop callbacks share. Everything here runs on the main thread.
struct Driver<'a> {
    /// Polls the root future; true once it is done.
    poll: RefCell<&'a mut dyn FnMut() -> bool>,
    done: Cell<bool>,
    /// Default-mode run loop activations on the stack, counted since `run` began.
    depth: Cell<usize>,
    /// The depth at which `run`'s own activation executes.
    own_depth: Cell<usize>,
    waker: &'a Arc<SourceWaker>,
    guest: &'a Guest,
    turning: Cell<bool>,
}

impl Driver<'_> {
    /// `CFRunLoopStop` ends the innermost run, so it is issued only when that run is ours. A nested run (a
    /// callback running the loop itself, like a modal session) is left to finish.
    fn stop_if_ours(&self) {
        if self.done.get() && self.depth.get() == self.own_depth.get() {
            self.waker.run_loop.stop();
        }
    }
}

unsafe extern "C-unwind" fn source_fired(info: *mut c_void) {
    let driver = &*(info as *const Driver);
    if !driver.done.get() {
        // Already borrowed means a poll is on the stack (a nested run loop inside it): it will see the wake.
        if let Ok(mut poll) = driver.poll.try_borrow_mut() {
            driver.done.set(poll());
        }
    }
    driver.stop_if_ours();
}

/// Tracks nesting. When a nested run ends after the future finished, the source fires again so that the loop
/// is stopped at our own level.
unsafe extern "C-unwind" fn activity_observed(_observer: *mut CFRunLoopObserver, activity: CFRunLoopActivity, info: *mut c_void) {
    let driver = &*(info as *const Driver);
    if activity == CFRunLoopActivity::Entry {
        driver.depth.set(driver.depth.get() + 1);
    } else if activity == CFRunLoopActivity::Exit {
        driver.depth.set(driver.depth.get().saturating_sub(1));
        if driver.done.get() && driver.depth.get() == driver.own_depth.get() {
            driver.waker.wake_by_ref();
        }
    }
}

/// One turn of the guest runtime. Skipped while a turn or a poll is on the stack (a nested run loop).
unsafe extern "C-unwind" fn turn_fired(_timer: *mut CFRunLoopTimer, info: *mut c_void) {
    let driver = &*(info as *const Driver);
    if driver.done.get() || driver.turning.replace(true) {
        return;
    }
    if driver.poll.try_borrow_mut().is_ok() {
        TURNS.fetch_add(1, Ordering::Relaxed);
        driver.guest.turn();
    }
    driver.turning.set(false);
}

/// The run loop timers that turn the guest runtime.
struct Timers {
    kick: CFRetained<CFRunLoopTimer>,
    fast: CFRetained<CFRunLoopTimer>,
    slow: CFRetained<CFRunLoopTimer>,
}

impl Timers {
    fn all(&self) -> [&CFRunLoopTimer; 3] {
        [&self.kick, &self.fast, &self.slow]
    }
}

impl MainLoop for Timers {
    fn kick(&self) {
        self.kick.set_next_fire_date(CFAbsoluteTimeGetCurrent());
    }

    fn set_tick(&self, rate: Tick) {
        let now = CFAbsoluteTimeGetCurrent();
        self.fast.set_next_fire_date(if rate == Tick::Fast { now + FAST_INTERVAL } else { NEVER });
        self.slow.set_next_fire_date(if rate == Tick::Slow { now + SLOW_INTERVAL } else { NEVER });
    }
}

/// Runs the future `build` returns on the main thread until it completes, serving the main run loop.
///
/// `build` receives the handle through which the guest runtime's ticks are set. Panics off the main thread.
/// A panic in the future is resumed here.
pub fn run<F: Future + 'static>(build: impl FnOnce(Rc<dyn MainLoop>) -> F) -> F::Output {
    assert!(unsafe { libc::pthread_main_np() } == 1, "main_loop::run must be called on the main thread");
    let run_loop = CFRunLoop::main().expect("no main run loop");
    let mode = unsafe { kCFRunLoopDefaultMode };
    let guest = Guest::new();
    let entered = guest.enter();
    let waker_state = Arc::new(SourceWaker { source: OnceLock::new(), run_loop: run_loop.clone() });
    let mut outcome: Option<Result<F::Output, Box<dyn Any + Send>>> = None;
    {
        let waker = Waker::from(waker_state.clone());
        let mut cx = Context::from_waker(&waker);
        // The driver is created first so that the timers can point at it; the future is built afterwards.
        let future: RefCell<Option<std::pin::Pin<Box<dyn Future<Output = F::Output>>>>> = RefCell::new(None);
        let mut poll = || {
            let polled = catch_unwind(AssertUnwindSafe(|| {
                future.borrow_mut().as_mut().expect("the future is built before the first poll").as_mut().poll(&mut cx)
            }));
            match polled {
                Ok(Poll::Pending) => false,
                Ok(Poll::Ready(output)) => {
                    outcome = Some(Ok(output));
                    true
                }
                Err(payload) => {
                    outcome = Some(Err(payload));
                    true
                }
            }
        };
        let driver = Driver {
            poll: RefCell::new(&mut poll),
            done: Cell::new(false),
            depth: Cell::new(0),
            own_depth: Cell::new(1),
            waker: &waker_state,
            guest: &guest,
            turning: Cell::new(false),
        };
        let info = &driver as *const Driver as *mut c_void;

        let mut timer_context = CFRunLoopTimerContext { version: 0, info, retain: None, release: None, copyDescription: None };
        let mut timer = |interval: f64| {
            let timer = unsafe { CFRunLoopTimer::new(None, NEVER, interval, 0, 0, Some(turn_fired), &mut timer_context) }.expect("CFRunLoopTimerCreate failed");
            run_loop.add_timer(Some(&timer), unsafe { kCFRunLoopCommonModes });
            timer
        };
        let timers = Rc::new(Timers { kick: timer(NEVER), fast: timer(FAST_INTERVAL), slow: timer(SLOW_INTERVAL) });
        *future.borrow_mut() = Some(Box::pin(guest.root(timers.clone(), build(timers.clone()))));

        let mut source_context = CFRunLoopSourceContext {
            version: 0,
            info,
            retain: None,
            release: None,
            copyDescription: None,
            equal: None,
            hash: None,
            schedule: None,
            cancel: None,
            perform: Some(source_fired),
        };
        let source = unsafe { CFRunLoopSource::new(None, 0, &mut source_context) }.expect("CFRunLoopSourceCreate failed");
        let mut observer_context = CFRunLoopObserverContext { version: 0, info, retain: None, release: None, copyDescription: None };
        let activities = (CFRunLoopActivity::Entry | CFRunLoopActivity::Exit).0;
        let observer = unsafe { CFRunLoopObserver::new(None, activities, true, 0, Some(activity_observed), &mut observer_context) }
            .expect("CFRunLoopObserverCreate failed");
        run_loop.add_observer(Some(&observer), mode);
        run_loop.add_source(Some(&source), mode);
        let _ = waker_state.source.set(source);

        waker_state.wake_by_ref(); // the first poll
        while !driver.done.get() {
            driver.own_depth.set(driver.depth.get() + 1);
            CFRunLoop::run_in_mode(mode, NEVER, false);
        }
        // Invalidate before the driver goes away: no callback may run again.
        observer.invalidate();
        waker_state.source.get().expect("the source was set").invalidate();
        for timer in timers.all() {
            timer.invalidate();
        }
    }
    drop(entered);
    match outcome.expect("the run loop ended with an outcome") {
        Ok(output) => output,
        Err(payload) => resume_unwind(payload),
    }
}

/// Runs `work` on a libdispatch global queue and resolves on the awaiting task. A panic in `work` is resumed
/// in the awaiting task.
pub fn offload<T: Send + 'static>(work: impl FnOnce() -> T + Send + 'static) -> impl Future<Output = T> {
    let (tx, rx) = tokio::sync::oneshot::channel();
    DispatchQueue::global_queue(GlobalQueueIdentifier::QualityOfService(DispatchQoS::Default)).exec_async(move || {
        let _ = tx.send(catch_unwind(AssertUnwindSafe(work)));
    });
    async move {
        match rx.await.expect("libdispatch runs every block it accepts") {
            Ok(value) => value,
            Err(payload) => resume_unwind(payload),
        }
    }
}

/// `work` over `items` on libdispatch's workers (`dispatch_apply_f`), results in order. Synchronous, so `work`
/// may borrow from the caller. Blocks until every item is done: call it off the main thread.
pub fn concurrent_map<T: Send, U: Send, F: Fn(T) -> U + Sync>(items: Vec<T>, work: F) -> Vec<U> {
    use std::sync::Mutex;
    /// What each iteration finds through the context pointer. Every index is touched by one iteration only.
    struct Job<'a, T, U, F> {
        inputs: Vec<Mutex<Option<T>>>,
        outputs: Vec<Mutex<Option<std::thread::Result<U>>>>,
        work: &'a F,
    }
    unsafe extern "C-unwind" fn iteration<T, U, F: Fn(T) -> U>(context: *mut std::ffi::c_void, index: usize) {
        let job = unsafe { &*(context as *const Job<'_, T, U, F>) };
        if let Some(item) = job.inputs[index].lock().unwrap().take() {
            // A panic must not unwind through libdispatch: it is carried out and raised again after the apply.
            *job.outputs[index].lock().unwrap() = Some(catch_unwind(AssertUnwindSafe(|| (job.work)(item))));
        }
    }
    let count = items.len();
    let job = Job { inputs: items.into_iter().map(|item| Mutex::new(Some(item))).collect(), outputs: (0..count).map(|_| Mutex::new(None)).collect(), work: &work };
    // DISPATCH_APPLY_AUTO (no queue): libdispatch picks the root queue for the caller's QoS.
    unsafe { DispatchQueue::apply_f(count, None, &job as *const Job<'_, T, U, F> as *mut std::ffi::c_void, iteration::<T, U, F>) };
    job.outputs
        .into_iter()
        .map(|slot| match slot.into_inner().unwrap().expect("dispatch_apply ran every index") {
            Ok(value) => value,
            Err(payload) => resume_unwind(payload),
        })
        .collect()
}

/// The number of threads in this process.
pub fn thread_count() -> u32 {
    extern "C" {
        static mach_task_self_: u32;
        fn task_threads(task: u32, threads: *mut *mut u32, count: *mut u32) -> i32;
        fn vm_deallocate(task: u32, address: usize, size: usize) -> i32;
        fn mach_port_deallocate(task: u32, name: u32) -> i32;
    }
    let (mut threads, mut count) = (std::ptr::null_mut::<u32>(), 0u32);
    unsafe {
        if task_threads(mach_task_self_, &mut threads, &mut count) != 0 {
            return 0;
        }
        for i in 0..count as usize {
            mach_port_deallocate(mach_task_self_, *threads.add(i));
        }
        vm_deallocate(mach_task_self_, threads as usize, count as usize * std::mem::size_of::<u32>());
    }
    count
}
