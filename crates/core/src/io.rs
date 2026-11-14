//! What a platform's transport gives the HTTP layer.
use std::future::Future;

/// Accepts byte streams. Every stream is delivered, read and written on the main thread.
pub trait Listener: 'static {
    type Io: hyper::rt::Read + hyper::rt::Write + Unpin + 'static;

    /// The next connection; `None` once the listener has ended.
    fn accept(&mut self) -> impl Future<Output = Option<Self::Io>>;
}
