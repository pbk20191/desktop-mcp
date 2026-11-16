//! The server side of TLS without blocking: SChannel's stream runs over in-memory buffers that the socket fills and
//! drains, and "would block" from those buffers becomes `Pending`.
use crate::listener::SocketListener;
use crate::net::SocketIo;
use crate::tls::TlsIdentity;
use desktop_mcp_core::io::{Listener, Peer};
use hyper::rt::{Read as _, ReadBuf, Write as _};
use schannel::schannel_cred::SchannelCred;
use schannel::tls_stream::{self, HandshakeError, MidHandshakeTlsStream, TlsStream};
use std::cell::RefCell;
use std::future::Future;
use std::io;
use std::mem::MaybeUninit;
use std::pin::Pin;
use std::rc::Rc;
use std::task::{ready, Context, Poll};

/// Ciphertext in both directions. Reading an empty buffer before the end of input is `WouldBlock`.
#[derive(Default)]
struct Buffers {
    incoming: Vec<u8>,
    outgoing: Vec<u8>,
    ended: bool,
    /// Bytes received so far, to check the first one.
    received: usize,
}

/// A TLS client's first record is a handshake record. Anything else (plain HTTP, say) is refused before SChannel
/// sees it: the `schannel` crate's handshake loop can spin forever on input that is not TLS at all.
const HANDSHAKE_RECORD: u8 = 0x16;

struct Bridge(Rc<RefCell<Buffers>>);

impl io::Read for Bridge {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        let mut buffers = self.0.borrow_mut();
        if buffers.incoming.is_empty() {
            return if buffers.ended { Ok(0) } else { Err(io::ErrorKind::WouldBlock.into()) };
        }
        let count = buffers.incoming.len().min(buf.len());
        buf[..count].copy_from_slice(&buffers.incoming[..count]);
        buffers.incoming.drain(..count);
        Ok(count)
    }
}

impl io::Write for Bridge {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        self.0.borrow_mut().outgoing.extend_from_slice(buf);
        Ok(buf.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

enum State {
    Handshaking(MidHandshakeTlsStream<Bridge>),
    Ready(TlsStream<Bridge>),
    Failed,
}

/// A server-side TLS stream over a socket.
pub struct TlsIo {
    socket: SocketIo,
    buffers: Rc<RefCell<Buffers>>,
    state: State,
}

/// Above this, ciphertext waiting to go out is sent before more plaintext is taken.
const OUTGOING_LIMIT: usize = 64 * 1024;
const RECORD: usize = 16 * 1024;

impl TlsIo {
    fn accept(socket: SocketIo, credentials: SchannelCred) -> TlsIo {
        let buffers = Rc::new(RefCell::new(Buffers::default()));
        let state = match tls_stream::Builder::new().accept(credentials, Bridge(buffers.clone())) {
            Ok(stream) => State::Ready(stream),
            Err(HandshakeError::Interrupted(stream)) => State::Handshaking(stream),
            Err(HandshakeError::Failure(_)) => State::Failed,
        };
        TlsIo { socket, buffers, state }
    }

    /// Sends what SChannel wrote; ready once nothing is left.
    fn poll_send(&mut self, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        loop {
            let pending = std::mem::take(&mut self.buffers.borrow_mut().outgoing);
            if pending.is_empty() {
                return Poll::Ready(Ok(()));
            }
            let sent = Pin::new(&mut self.socket).poll_write(cx, &pending);
            let rest = match &sent {
                Poll::Ready(Ok(written)) => &pending[*written..],
                _ => &pending[..],
            };
            self.buffers.borrow_mut().outgoing.splice(0..0, rest.iter().copied());
            match sent {
                Poll::Ready(Ok(_)) => {}
                Poll::Ready(Err(error)) => return Poll::Ready(Err(error)),
                Poll::Pending => return Poll::Pending,
            }
        }
    }

    /// More ciphertext from the socket; at its end, marks the input ended.
    fn poll_receive(&mut self, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        let mut storage = [MaybeUninit::<u8>::uninit(); RECORD];
        let mut buf = ReadBuf::uninit(&mut storage);
        ready!(Pin::new(&mut self.socket).poll_read(cx, buf.unfilled()))?;
        let mut buffers = self.buffers.borrow_mut();
        if buf.filled().is_empty() {
            buffers.ended = true;
        }
        if buffers.received == 0 && buf.filled().first().is_some_and(|first| *first != HANDSHAKE_RECORD) {
            return Poll::Ready(Err(io::Error::new(io::ErrorKind::InvalidData, "not a TLS handshake")));
        }
        buffers.received += buf.filled().len();
        buffers.incoming.extend_from_slice(buf.filled());
        Poll::Ready(Ok(()))
    }

    fn poll_handshake(&mut self, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        loop {
            ready!(self.poll_send(cx))?;
            match std::mem::replace(&mut self.state, State::Failed) {
                State::Ready(stream) => {
                    self.state = State::Ready(stream);
                    return Poll::Ready(Ok(()));
                }
                State::Failed => return Poll::Ready(Err(io::Error::other("TLS handshake failed"))),
                State::Handshaking(stream) => match stream.handshake() {
                    Ok(stream) => self.state = State::Ready(stream),
                    Err(HandshakeError::Interrupted(stream)) => {
                        self.state = State::Handshaking(stream);
                        ready!(self.poll_send(cx))?;
                        if self.buffers.borrow().ended {
                            self.state = State::Failed;
                            continue;
                        }
                        ready!(self.poll_receive(cx))?;
                    }
                    Err(HandshakeError::Failure(error)) => {
                        let _ = self.poll_send(cx);
                        return Poll::Ready(Err(error));
                    }
                },
            }
        }
    }

    fn stream(&mut self) -> &mut TlsStream<Bridge> {
        match &mut self.state {
            State::Ready(stream) => stream,
            _ => unreachable!("used only after the handshake"),
        }
    }
}

impl hyper::rt::Read for TlsIo {
    fn poll_read(mut self: Pin<&mut Self>, cx: &mut Context<'_>, mut buf: hyper::rt::ReadBufCursor<'_>) -> Poll<io::Result<()>> {
        ready!(self.poll_handshake(cx))?;
        loop {
            let mut plain = vec![0u8; buf.remaining().min(RECORD)];
            match io::Read::read(self.stream(), &mut plain) {
                Ok(count) => {
                    buf.put_slice(&plain[..count]);
                    return Poll::Ready(Ok(()));
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                    ready!(self.poll_send(cx))?;
                    ready!(self.poll_receive(cx))?;
                }
                Err(error) => return Poll::Ready(Err(error)),
            }
        }
    }
}

impl hyper::rt::Write for TlsIo {
    fn poll_write(mut self: Pin<&mut Self>, cx: &mut Context<'_>, buf: &[u8]) -> Poll<io::Result<usize>> {
        ready!(self.poll_handshake(cx))?;
        if self.buffers.borrow().outgoing.len() > OUTGOING_LIMIT {
            ready!(self.poll_send(cx))?;
        }
        let written = io::Write::write(self.stream(), buf)?;
        if let Poll::Ready(Err(error)) = self.poll_send(cx) {
            return Poll::Ready(Err(error));
        }
        Poll::Ready(Ok(written))
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        ready!(self.poll_send(cx))?;
        Pin::new(&mut self.socket).poll_flush(cx)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        if let State::Ready(stream) = &mut self.state {
            let _ = stream.shutdown();
        }
        ready!(self.poll_send(cx))?;
        Pin::new(&mut self.socket).poll_shutdown(cx)
    }
}

impl Peer for TlsIo {
    fn peer_address(&self) -> Option<std::net::IpAddr> {
        self.socket.peer_address()
    }
}

/// An accepted connection: plain, or TLS when the server has an identity.
pub enum ServerIo {
    Plain(SocketIo),
    Tls(Box<TlsIo>),
}

impl hyper::rt::Read for ServerIo {
    fn poll_read(self: Pin<&mut Self>, cx: &mut Context<'_>, buf: hyper::rt::ReadBufCursor<'_>) -> Poll<io::Result<()>> {
        match self.get_mut() {
            ServerIo::Plain(io) => Pin::new(io).poll_read(cx, buf),
            ServerIo::Tls(io) => Pin::new(io.as_mut()).poll_read(cx, buf),
        }
    }
}

impl hyper::rt::Write for ServerIo {
    fn poll_write(self: Pin<&mut Self>, cx: &mut Context<'_>, buf: &[u8]) -> Poll<io::Result<usize>> {
        match self.get_mut() {
            ServerIo::Plain(io) => Pin::new(io).poll_write(cx, buf),
            ServerIo::Tls(io) => Pin::new(io.as_mut()).poll_write(cx, buf),
        }
    }

    fn poll_flush(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        match self.get_mut() {
            ServerIo::Plain(io) => Pin::new(io).poll_flush(cx),
            ServerIo::Tls(io) => Pin::new(io.as_mut()).poll_flush(cx),
        }
    }

    fn poll_shutdown(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        match self.get_mut() {
            ServerIo::Plain(io) => Pin::new(io).poll_shutdown(cx),
            ServerIo::Tls(io) => Pin::new(io.as_mut()).poll_shutdown(cx),
        }
    }
}

impl Peer for ServerIo {
    fn peer_address(&self) -> Option<std::net::IpAddr> {
        match self {
            ServerIo::Plain(io) => io.peer_address(),
            ServerIo::Tls(io) => io.peer_address(),
        }
    }
}

/// Connections from a socket listener, in TLS when there is an identity.
pub struct TlsListener {
    inner: SocketListener,
    identity: Option<Rc<TlsIdentity>>,
}

impl TlsListener {
    pub fn new(inner: SocketListener, identity: Option<Rc<TlsIdentity>>) -> TlsListener {
        TlsListener { inner, identity }
    }

    pub fn port(&self) -> u16 {
        self.inner.port()
    }
}

impl Listener for TlsListener {
    type Io = ServerIo;

    fn accept(&mut self) -> impl Future<Output = Option<ServerIo>> {
        async {
            let socket = self.inner.accept().await?;
            Some(match &self.identity {
                Some(identity) => ServerIo::Tls(Box::new(TlsIo::accept(socket, identity.credentials()))),
                None => ServerIo::Plain(socket),
            })
        }
    }
}
