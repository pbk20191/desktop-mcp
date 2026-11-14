//! The guest runtime, pumped by hand: no platform code is involved.
use desktop_mcp_core::guest::{Activity, Guest, MainLoop, Tick};
use std::cell::Cell;
use std::future::Future;
use std::pin::Pin;
use std::rc::Rc;
use std::task::{Context, Poll, Waker};
use std::time::Duration;

#[derive(Default)]
struct FakeLoop { kicks: Cell<usize>, tick: Cell<Option<Tick>> }
impl MainLoop for FakeLoop {
    fn kick(&self) { self.kicks.set(self.kicks.get() + 1) }
    fn set_tick(&self, rate: Tick) { self.tick.set(Some(rate)) }
}
fn poll_once<F: Future>(future: &mut Pin<Box<F>>) -> Poll<F::Output> {
    future.as_mut().poll(&mut Context::from_waker(Waker::noop()))
}

#[test]
fn spawn_local_task_runs_when_root_is_polled() {
    let (guest, fake) = (Guest::new(), Rc::new(FakeLoop::default()));
    let _entered = guest.enter();
    let flag = Rc::new(Cell::new(false));
    let seen = flag.clone();
    let mut root = Box::pin(guest.root(fake, async move {
        tokio::task::spawn_local(async move { seen.set(true) });
        std::future::pending::<()>().await
    }));
    assert!(poll_once(&mut root).is_pending());
    assert!(flag.get());
}

#[test]
fn every_poll_of_root_kicks() {
    let (guest, fake) = (Guest::new(), Rc::new(FakeLoop::default()));
    let _entered = guest.enter();
    let mut root = Box::pin(guest.root(fake.clone(), std::future::pending::<()>()));
    let _ = poll_once(&mut root);
    let _ = poll_once(&mut root);
    assert_eq!(fake.kicks.get(), 2);
}

#[test]
fn tokio_spawn_runs_on_turn_not_on_poll() {
    let (guest, fake) = (Guest::new(), Rc::new(FakeLoop::default()));
    let _entered = guest.enter();
    let flag = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
    let seen = flag.clone();
    let mut root = Box::pin(guest.root(fake, async move {
        tokio::spawn(async move { seen.store(true, std::sync::atomic::Ordering::SeqCst) });
        std::future::pending::<()>().await
    }));
    let _ = poll_once(&mut root);
    assert!(!flag.load(std::sync::atomic::Ordering::SeqCst));
    guest.turn();
    assert!(flag.load(std::sync::atomic::Ordering::SeqCst));
}

#[test]
fn timer_fires_on_a_turn_after_its_deadline() {
    let (guest, fake) = (Guest::new(), Rc::new(FakeLoop::default()));
    let _entered = guest.enter();
    let mut root = Box::pin(guest.root(fake, async move {
        tokio::task::spawn_local(tokio::time::sleep(Duration::from_millis(20))).await.unwrap();
        7
    }));
    assert!(poll_once(&mut root).is_pending());
    std::thread::sleep(Duration::from_millis(30));
    guest.turn();
    // The fired timer wakes the task; the task's completion wakes the root. A real loop follows the wakes;
    // here a few polls stand in for them.
    let done = (0..3).any(|_| poll_once(&mut root) == Poll::Ready(7));
    assert!(done);
}

#[test]
fn activity_sets_tick() {
    let fake = Rc::new(FakeLoop::default());
    let activity = Activity::new(fake.clone());
    assert_eq!(fake.tick.get(), None, "nothing is armed before there is activity");
    let connection = activity.connection();
    assert_eq!(fake.tick.get(), Some(Tick::Slow));
    let request = activity.request();
    assert_eq!(fake.tick.get(), Some(Tick::Fast));
    drop(request);
    assert_eq!(fake.tick.get(), Some(Tick::Slow));
    drop(connection);
    assert_eq!(fake.tick.get(), Some(Tick::Off));
}
