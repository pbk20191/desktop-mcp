//! Dispatch source handlers as plain functions (the `_f` variants) rather than blocks: the Rust closures are the
//! source's context, called through one function each, and dropped by the source's finalizer. libdispatch runs a
//! finalizer on the object's target queue; every source made here targets the main queue, so the closures are
//! made, called and dropped on the main thread.
use dispatch2::{dispatch_source_type_t, DispatchObject, DispatchQueue, DispatchRetained, DispatchSource};
use std::ffi::c_void;

struct Handlers {
    event: Box<dyn Fn()>,
    cancel: Option<Box<dyn Fn()>>,
}

extern "C" fn on_event(context: *mut c_void) {
    let handlers = unsafe { &*(context as *const Handlers) };
    (handlers.event)();
}

extern "C" fn on_cancel(context: *mut c_void) {
    let handlers = unsafe { &*(context as *const Handlers) };
    if let Some(cancel) = &handlers.cancel {
        cancel();
    }
}

extern "C" fn finalize(context: *mut c_void) {
    drop(unsafe { Box::from_raw(context as *mut Handlers) });
}

/// A suspended source on the main queue whose events call `event` (and whose cancellation calls `cancel`).
/// Resume it to start. The closures live as long as the source: a closure that holds the source itself must let
/// go of it once done, or neither is ever freed.
pub fn on_main(kind: *const dispatch2::dispatch_source_type_s, handle: usize, mask: usize, event: impl Fn() + 'static, cancel: Option<Box<dyn Fn()>>) -> DispatchRetained<DispatchSource> {
    let source = unsafe { DispatchSource::new(kind as dispatch_source_type_t, handle, mask, Some(DispatchQueue::main())) };
    let handlers = Box::into_raw(Box::new(Handlers { event: Box::new(event), cancel }));
    unsafe {
        source.set_context(handlers.cast());
        source.set_finalizer_f(finalize);
    }
    source.set_event_handler_f(on_event);
    source.set_cancel_handler_f(on_cancel);
    source
}
