//! Dispatch sources whose handlers need nothing of the main thread (signals, timers, process exits, socket
//! accepts) run on one serial queue of their own, so they neither wait for nor occupy the main run loop. Their
//! handlers only do their work and hand the outcome on through a channel; what touches main-thread state stays on
//! the main queue elsewhere. Handlers are blocks: released when the source is cancelled.
use block2::RcBlock;
use dispatch2::{DispatchQueue, DispatchRetained, DispatchSource};
use std::sync::OnceLock;

/// The serial queue the sources made here deliver on.
pub fn queue() -> &'static DispatchQueue {
    static QUEUE: OnceLock<DispatchRetained<DispatchQueue>> = OnceLock::new();
    QUEUE.get_or_init(|| DispatchQueue::new("desktop-mcp.sources", None))
}

/// A suspended source on [`queue`] whose events call `event` (and whose cancellation calls `cancel`). Resume it
/// to start.
pub fn background(kind: *const dispatch2::dispatch_source_type_s, handle: usize, mask: usize, event: impl Fn() + Send + 'static, cancel: Option<Box<dyn Fn() + Send>>) -> DispatchRetained<DispatchSource> {
    let source = unsafe { DispatchSource::new(kind as dispatch2::dispatch_source_type_t, handle, mask, Some(queue())) };
    let event = RcBlock::new(event);
    unsafe { source.set_event_handler_with_block(RcBlock::as_ptr(&event)) };
    if let Some(cancel) = cancel {
        let cancel = RcBlock::new(cancel);
        unsafe { source.set_cancel_handler_with_block(RcBlock::as_ptr(&cancel)) };
    }
    source
}
