//! What a platform's transport gives the HTTP layer.
use std::future::Future;

/// A connection's other end.
pub trait Peer {
    /// The client's IP address, when the transport has one (a Unix socket has none).
    fn peer_address(&self) -> Option<std::net::IpAddr>;
}

/// Accepts byte streams. Every stream is delivered, read and written on the main thread.
pub trait Listener: 'static {
    type Io: hyper::rt::Read + hyper::rt::Write + Peer + Unpin + 'static;

    /// The next connection; `None` once the listener has ended.
    fn accept(&mut self) -> impl Future<Output = Option<Self::Io>>;
}
