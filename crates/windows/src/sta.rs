//! A thread of its own in a single-threaded apartment, with a message loop: for the calls that need an STA that
//! pumps (the OLE-backed WinRT clipboard) and may block, so they never hold up the main loop.
//!
//! The thread is a DispatcherQueue's dedicated thread. Work arrives as a closure that makes a future; the future is
//! polled there from queued handlers, and its result goes back to the caller through a oneshot.
use std::cell::RefCell;
use std::collections::HashMap;
use std::future::Future;
use std::pin::Pin;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::task::{Context, Wake, Waker};
use tokio::sync::oneshot;
use windows::System::{DispatcherQueue, DispatcherQueueController, DispatcherQueueHandler};
use windows::Win32::System::WinRT::{CreateDispatcherQueueController, DispatcherQueueOptions, DQTAT_COM_STA, DQTYPE_THREAD_DEDICATED};

pub struct StaThread {
    queue: DispatcherQueue,
    // Kept for the process: dropping the controller would not end the thread, and nothing ever shuts it down.
    _controller: DispatcherQueueController,
}

type Task = Pin<Box<dyn Future<Output = ()>>>;

thread_local! {
    /// The futures being run on this (the STA) thread, by id.
    static TASKS: RefCell<HashMap<u64, Task>> = RefCell::new(HashMap::new());
}

static NEXT_TASK: AtomicU64 = AtomicU64::new(0);

/// Polls a task again on the STA thread.
struct TaskWaker {
    queue: DispatcherQueue,
    id: u64,
}

impl Wake for TaskWaker {
    fn wake(self: Arc<Self>) {
        self.wake_by_ref()
    }

    fn wake_by_ref(self: &Arc<Self>) {
        let this = self.clone();
        let _ = self.queue.TryEnqueue(&DispatcherQueueHandler::new(move || {
            poll_task(&this);
            Ok(())
        }));
    }
}

fn poll_task(waker: &Arc<TaskWaker>) {
    let Some(mut task) = TASKS.with(|tasks| tasks.borrow_mut().remove(&waker.id)) else { return };
    let waker_handle = Waker::from(waker.clone());
    if task.as_mut().poll(&mut Context::from_waker(&waker_handle)).is_pending() {
        TASKS.with(|tasks| tasks.borrow_mut().insert(waker.id, task));
    }
}

impl StaThread {
    /// The process's STA thread, started on first use.
    pub fn shared() -> &'static StaThread {
        static SHARED: OnceLock<StaThread> = OnceLock::new();
        SHARED.get_or_init(|| {
            let options = DispatcherQueueOptions { dwSize: size_of::<DispatcherQueueOptions>() as u32, threadType: DQTYPE_THREAD_DEDICATED, apartmentType: DQTAT_COM_STA };
            let controller = unsafe { CreateDispatcherQueueController(options) }.expect("CreateDispatcherQueueController (dedicated STA) failed");
            let queue = controller.DispatcherQueue().expect("the controller has a queue");
            StaThread { queue, _controller: controller }
        })
    }

    /// Runs the future `work` makes on the STA thread; resolves with its output wherever it is awaited.
    pub fn run<T: Send + 'static, F: Future<Output = T> + 'static>(&self, work: impl FnOnce() -> F + Send + 'static) -> impl Future<Output = T> {
        let (sender, receiver) = oneshot::channel();
        let id = NEXT_TASK.fetch_add(1, Ordering::Relaxed);
        let waker = Arc::new(TaskWaker { queue: self.queue.clone(), id });
        let start = Mutex::new(Some((work, sender)));
        let queued = self.queue.TryEnqueue(&DispatcherQueueHandler::new(move || {
            if let Some((work, sender)) = start.lock().unwrap().take() {
                let future = work();
                let task: Task = Box::pin(async move {
                    let _ = sender.send(future.await);
                });
                TASKS.with(|tasks| tasks.borrow_mut().insert(id, task));
                poll_task(&waker);
            }
            Ok(())
        }));
        async move {
            assert!(queued.unwrap_or(false), "the STA thread's queue takes work for the life of the process");
            receiver.await.expect("work queued on the STA thread runs to completion")
        }
    }
}
