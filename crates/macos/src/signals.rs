//! SIGINT and SIGTERM as a future, through libdispatch signal sources on the main queue.
use dispatch2::{DispatchObject, DispatchRetained, DispatchSource};
use std::cell::RefCell;
use std::future::Future;
use std::rc::Rc;

/// Resolves with the signal's number when SIGINT or SIGTERM arrives. While the future lives, those signals no
/// longer end the process by themselves; dropping it removes the sources (the signals stay ignored).
pub fn termination() -> impl Future<Output = i32> {
    let (tx, rx) = tokio::sync::oneshot::channel();
    // Shared by the sources' handlers, which run (and are dropped) on the main queue.
    let tx = Rc::new(RefCell::new(Some(tx)));
    let sources: Vec<Source> = [libc::SIGINT, libc::SIGTERM]
        .into_iter()
        .map(|signal| {
            // The default action would kill the process before the source sees the signal.
            unsafe { libc::signal(signal, libc::SIG_IGN) };
            let tx = tx.clone();
            let source = crate::source::on_main(&raw const dispatch2::_dispatch_source_type_signal, signal as usize, 0, move || {
                if let Some(tx) = tx.borrow_mut().take() {
                    let _ = tx.send(signal);
                }
            }, None);
            source.resume();
            Source(source)
        })
        .collect();
    async move {
        let signal = rx.await.unwrap_or(libc::SIGTERM);
        drop(sources);
        signal
    }
}

/// A dispatch source that is cancelled when dropped.
struct Source(DispatchRetained<DispatchSource>);

impl Drop for Source {
    fn drop(&mut self) {
        self.0.cancel();
    }
}
