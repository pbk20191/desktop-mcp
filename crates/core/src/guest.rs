//! The tokio runtime as a guest of the platform's main loop.
//!
//! rmcp needs a tokio runtime context (`spawn`, timers), and tokio must not own a thread. So the runtime is
//! `current_thread`, entered on the main thread and never parked: tasks live in a `LocalSet` that the platform
//! polls, and the platform gives the runtime short non-blocking turns for everything else.
use std::cell::Cell;
use std::future::Future;
use std::pin::Pin;
use std::rc::Rc;
use std::task::{Context, Poll};

/// How often the main loop turns the runtime on its own.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Tick {
    Off,
    /// Every second: nothing is in flight, but something that can own a timer exists.
    Slow,
    /// Every 50 ms: a request is in flight.
    Fast,
}

/// What the platform's main loop does for the guest runtime. Main thread only.
pub trait MainLoop: 'static {
    /// One runtime turn soon. Calls coalesce.
    fn kick(&self);
    /// Repeated turns at `rate`.
    fn set_tick(&self, rate: Tick);
}

pub struct Guest {
    runtime: tokio::runtime::Runtime,
}

impl Default for Guest {
    fn default() -> Self {
        Self::new()
    }
}

impl Guest {
    pub fn new() -> Guest {
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_time()
            .build()
            .expect("a current-thread runtime needs nothing from the system");
        Guest { runtime }
    }

    /// Makes this runtime the context of the calling thread (for `tokio::spawn` and timers).
    pub fn enter(&self) -> tokio::runtime::EnterGuard<'_> {
        self.runtime.enter()
    }

    /// One non-blocking turn: runs what `tokio::spawn` queued and fires due timers.
    pub fn turn(&self) {
        self.runtime.block_on(tokio::task::yield_now());
    }

    /// Wraps `future` in a `LocalSet`. Every poll is followed by a kick: rmcp sends its responses from plain
    /// `tokio::spawn` tasks, which only a turn runs.
    pub fn root<F: Future + 'static>(&self, main_loop: Rc<dyn MainLoop>, future: F) -> impl Future<Output = F::Output> + 'static {
        let local = tokio::task::LocalSet::new();
        KickAfterPoll { inner: Box::pin(async move { local.run_until(future).await }), main_loop }
    }
}

struct KickAfterPoll<T> {
    inner: Pin<Box<dyn Future<Output = T>>>,
    main_loop: Rc<dyn MainLoop>,
}

impl<T> Future for KickAfterPoll<T> {
    type Output = T;
    fn poll(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<T> {
        let result = self.inner.as_mut().poll(cx);
        self.main_loop.kick();
        result
    }
}

/// Counts what can own a timer and sets the tick from it: a request in flight means `Fast`, otherwise an open
/// connection means `Slow`, otherwise `Off`, so an idle server never wakes.
pub struct Activity {
    main_loop: Rc<dyn MainLoop>,
    connections: Cell<usize>,
    requests: Cell<usize>,
}

impl Activity {
    pub fn new(main_loop: Rc<dyn MainLoop>) -> Rc<Activity> {
        Rc::new(Activity { main_loop, connections: Cell::new(0), requests: Cell::new(0) })
    }

    pub fn connection(self: &Rc<Self>) -> ActivityGuard {
        self.connections.set(self.connections.get() + 1);
        self.update();
        ActivityGuard { activity: self.clone(), request: false }
    }

    pub fn request(self: &Rc<Self>) -> ActivityGuard {
        self.requests.set(self.requests.get() + 1);
        self.update();
        ActivityGuard { activity: self.clone(), request: true }
    }

    fn update(&self) {
        self.main_loop.set_tick(if self.requests.get() > 0 {
            Tick::Fast
        } else if self.connections.get() > 0 {
            Tick::Slow
        } else {
            Tick::Off
        });
    }
}

/// Ends the connection or request it stands for when dropped.
pub struct ActivityGuard {
    activity: Rc<Activity>,
    request: bool,
}

impl Drop for ActivityGuard {
    fn drop(&mut self) {
        let count = if self.request { &self.activity.requests } else { &self.activity.connections };
        count.set(count.get() - 1);
        self.activity.update();
    }
}
