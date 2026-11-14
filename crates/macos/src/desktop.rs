//! The tools' behaviour on macOS.
use block2::RcBlock;
use desktop_mcp_core::mcp::Desktop;
use dispatch2::{DispatchObject, DispatchQueue, DispatchRetained, DispatchSource, DispatchTime};
use std::cell::Cell;
use std::future::Future;
use std::pin::Pin;
use std::time::Duration;

/// `DISPATCH_TIME_FOREVER` as a timer interval: the timer fires once.
const NO_REPEAT: u64 = u64::MAX;

/// A one-shot libdispatch timer on the main queue. Dropping it cancels it, so an abandoned wait leaves nothing
/// armed.
struct Timer(DispatchRetained<DispatchSource>);

impl Drop for Timer {
    fn drop(&mut self) {
        self.0.cancel();
    }
}

#[derive(Default)]
pub struct MacDesktop;

impl MacDesktop {
    pub fn new() -> MacDesktop {
        MacDesktop
    }
}

impl Desktop for MacDesktop {
    /// A libdispatch timer, so it needs no tick of the guest runtime and is as exact as the system allows
    /// (a leeway of a hundredth of the delay, at most 100 ms).
    fn sleep(&self, duration: Duration) -> Pin<Box<dyn Future<Output = ()>>> {
        Box::pin(async move {
            // Delays libdispatch cannot express (centuries) never end; the request's cancellation does.
            let Ok(start) = DispatchTime::try_from(duration) else { return std::future::pending().await };
            let (fired, wait) = tokio::sync::oneshot::channel::<()>();
            let fired = Cell::new(Some(fired));
            let kind = &raw const dispatch2::_dispatch_source_type_timer;
            let source = unsafe { DispatchSource::new(kind.cast_mut(), 0, 0, Some(DispatchQueue::main())) };
            let leeway = (duration.as_nanos() / 100).min(100_000_000) as u64;
            source.set_timer(start, NO_REPEAT, leeway);
            let handler = RcBlock::new(move || {
                if let Some(fired) = fired.take() {
                    let _ = fired.send(());
                }
            });
            unsafe { source.set_event_handler_with_block(RcBlock::as_ptr(&handler)) };
            source.resume();
            let timer = Timer(source);
            let _ = wait.await;
            drop(timer);
        })
    }
}
