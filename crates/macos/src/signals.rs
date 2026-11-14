//! SIGINT and SIGTERM as a future, through libdispatch signal sources on the main queue.
use block2::RcBlock;
use dispatch2::{DispatchObject, DispatchQueue, DispatchRetained, DispatchSource};
use std::cell::RefCell;
use std::future::Future;
use std::sync::Arc;

/// Resolves with the signal's number when SIGINT or SIGTERM arrives. While the future lives, those signals no
/// longer end the process by themselves; dropping it removes the sources (the signals stay ignored).
pub fn termination() -> impl Future<Output = i32> {
    let (tx, rx) = tokio::sync::oneshot::channel();
    // `Arc`: libdispatch may release a handler block on a thread of its own; the cell is only used on the main
    // queue, where the handlers run.
    #[allow(clippy::arc_with_non_send_sync)]
    let tx = Arc::new(RefCell::new(Some(tx)));
    let sources: Vec<Source> = [libc::SIGINT, libc::SIGTERM]
        .into_iter()
        .map(|signal| {
            // The default action would kill the process before the source sees the signal.
            unsafe { libc::signal(signal, libc::SIG_IGN) };
            let kind = &raw const dispatch2::_dispatch_source_type_signal;
            let source = unsafe { DispatchSource::new(kind.cast_mut(), signal as usize, 0, Some(DispatchQueue::main())) };
            let tx = tx.clone();
            let handler = RcBlock::new(move || {
                if let Some(tx) = tx.borrow_mut().take() {
                    let _ = tx.send(signal);
                }
            });
            unsafe { source.set_event_handler_with_block(RcBlock::as_ptr(&handler)) };
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
