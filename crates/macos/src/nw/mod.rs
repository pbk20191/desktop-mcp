//! TCP through Network.framework. Every callback is delivered on the main queue, which the main run loop
//! drains, so nothing here crosses threads. libdispatch comes from `dispatch2`, Network.framework from [`sys`].
pub mod sys;

use block2::RcBlock;
use desktop_mcp_core::io::{Listener, Peer};
use dispatch2::{DispatchData, DispatchObject, DispatchQueue, DispatchRetained};
use std::cell::{Cell, RefCell};
use std::ffi::{c_void, CString};
use std::future::Future;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::pin::Pin;
use std::ptr::NonNull;
use std::rc::Rc;
use std::sync::Arc;
use std::task::{Context, Poll, Waker};
use crate::tls::TlsIdentity;
use sys::*;
use tokio::sync::{mpsc, oneshot};

/// A listening TCP socket.
pub struct NwListener {
    listener: NWRetained<NWListener>,
    /// Kept for as long as the listener may use it.
    _tls: Option<Rc<TlsIdentity>>,
    port: u16,
    connections: mpsc::UnboundedReceiver<NwIo>,
}

impl NwListener {
    /// Binds `host` (a name or an address; `localhost` binds 127.0.0.1) and `port` (0 lets the system choose).
    /// Resolves once the listener is ready, or with the reason it failed.
    pub async fn bind(host: &str, port: u16) -> Result<NwListener, String> {
        Self::bind_with(host, port, None).await
    }

    /// As [`bind`](Self::bind), serving TLS with `tls` when given (ALPN `http/1.1`).
    pub async fn bind_with(host: &str, port: u16, tls: Option<Rc<TlsIdentity>>) -> Result<NwListener, String> {
        let host = if host.eq_ignore_ascii_case("localhost") { "127.0.0.1" } else { host };
        let host_c = CString::new(host).map_err(|_| format!("invalid host {host:?}"))?;
        let port_c = CString::new(port.to_string()).expect("digits");
        let (ready_tx, ready_rx) = oneshot::channel::<Result<(), String>>();
        let (connection_tx, connections) = mpsc::unbounded_channel();
        let listener = unsafe {
            let configure_tls = tls.clone().map(|identity| {
                RcBlock::new(move |options: NonNull<NWProtocolOptions>| {
                    let security = nw_tls_copy_sec_protocol_options(options.as_ref());
                    if !security.is_null() {
                        crate::tls::configure(security, &identity);
                        crate::tls::release(security.cast());
                    }
                })
            });
            let tls_block: &ConfigureProtocol = match &configure_tls {
                Some(block) => block,
                None => _nw_parameters_configure_protocol_disable,
            };
            let parameters = nw_parameters_create_secure_tcp(tls_block, _nw_parameters_configure_protocol_default_configuration).ok_or("cannot create TCP parameters")?;
            let endpoint = nw_endpoint_create_host(host_c.as_ptr(), port_c.as_ptr()).ok_or_else(|| format!("invalid address {host}:{port}"))?;
            nw_parameters_set_local_endpoint(&parameters, Some(&endpoint));
            nw_parameters_set_reuse_local_address(&parameters, true);
            nw_listener_create(&parameters).ok_or_else(|| format!("cannot listen on {host}:{port}"))?
        };
        unsafe {
            nw_listener_set_queue(&listener, DispatchQueue::main());
            let ready = Cell::new(Some(ready_tx));
            nw_listener_set_state_changed_handler(
                &listener,
                Some(&RcBlock::new(move |state: i32, error: *mut NWError| {
                    let outcome = match state {
                        LISTENER_READY => Ok(()),
                        LISTENER_FAILED => Err(format!("listen failed (error {})", error.as_ref().map_or(0, |error| nw_error_get_error_code(error)))),
                        LISTENER_CANCELLED => Err("the listener was cancelled".to_string()),
                        _ => return,
                    };
                    if let Some(ready) = ready.take() {
                        let _ = ready.send(outcome);
                    }
                })),
            );
            nw_listener_set_new_connection_handler(
                &listener,
                Some(&RcBlock::new(move |connection: NonNull<NWConnection>| {
                    // Handed over borrowed: retained for as long as the NwIo lives.
                    let connection = NWRetained::retain(connection);
                    nw_connection_set_queue(&connection, DispatchQueue::main());
                    nw_connection_start(&connection);
                    let _ = connection_tx.send(NwIo::new(connection));
                })),
            );
            nw_listener_start(&listener);
        }
        // From here the value owns the listener, so an early return cancels it.
        let mut bound = NwListener { listener, _tls: tls, port: 0, connections };
        ready_rx.await.map_err(|_| "the listener went away".to_string())??;
        bound.port = unsafe { nw_listener_get_port(&bound.listener) };
        Ok(bound)
    }

    pub fn port(&self) -> u16 {
        self.port
    }
}

impl Drop for NwListener {
    fn drop(&mut self) {
        unsafe { nw_listener_cancel(&self.listener) }
    }
}

impl Listener for NwListener {
    type Io = NwIo;

    fn accept(&mut self) -> impl Future<Output = Option<NwIo>> {
        self.connections.recv()
    }
}

/// When this much is handed to the system and not yet sent, writes wait.
use crate::tuning::WRITE_HIGH_WATER as HIGH_WATER;

/// What a connection's callbacks and hyper's polls share.
struct IoState {
    /// Received and not yet consumed: one `dispatch_data` made of the chunks Network.framework handed over.
    queue: Option<DispatchRetained<DispatchData>>,
    receiving: bool,
    end_of_stream: bool,
    read_waker: Option<Waker>,
    /// Bytes given to `nw_connection_send` whose completion has not arrived.
    unsent: usize,
    write_failed: bool,
    write_waker: Option<Waker>,
    /// The write side's close: asked for, and then acknowledged by the system.
    closing: bool,
    closed: bool,
}

/// hyper's view of a Network.framework connection.
///
/// Received data is never copied into a buffer of ours: chunks are joined (`DispatchData::concat`), copied
/// straight into hyper's buffer from the addresses `DispatchData::apply` hands out, and the consumed prefix is
/// dropped (`DispatchData::subrange`). Writes copy once (hyper only lends its bytes).
pub struct NwIo {
    connection: NWRetained<NWConnection>,
    peer: Option<IpAddr>,
    /// `Arc`, not `Rc`: the completion blocks hold a clone each, and the system may release a block on a thread
    /// of its own, so the count has to be atomic (as a Swift or Objective-C capture's is). The state itself is
    /// only ever read or written on the main thread.
    state: Arc<RefCell<IoState>>,
}

impl NwIo {
    /// Takes over a started connection.
    fn new(connection: NWRetained<NWConnection>) -> NwIo {
        let state = IoState { queue: None, receiving: false, end_of_stream: false, read_waker: None, unsent: 0, write_failed: false, write_waker: None, closing: false, closed: false };
        #[allow(clippy::arc_with_non_send_sync)]
        let state = Arc::new(RefCell::new(state));
        NwIo { peer: remote_address(&connection), connection, state }
    }
}

/// Opens connections to one server, as `connect` needs them: TCP, with the system's TLS (and trust) for HTTPS.
pub struct NwConnector {
    pub host: String,
    pub port: u16,
    pub tls: bool,
}

impl desktop_mcp_core::http_bridge::Connect for NwConnector {
    type Io = NwIo;

    fn connect(&self) -> Pin<Box<dyn Future<Output = Result<NwIo, String>>>> {
        let (host, port, tls) = (self.host.clone(), self.port, self.tls);
        Box::pin(async move { NwIo::connect(&host, port, tls).await })
    }
}

impl NwIo {
    /// Connects to `host`:`port`; with `tls`, over TLS that trusts what the system trusts.
    pub async fn connect(host: &str, port: u16, tls: bool) -> Result<NwIo, String> {
        let host_c = CString::new(host).map_err(|_| format!("invalid host {host:?}"))?;
        let port_c = CString::new(port.to_string()).expect("digits");
        let (ready_tx, ready_rx) = oneshot::channel::<Result<(), String>>();
        let connection = unsafe {
            let tls_block: &ConfigureProtocol = if tls { _nw_parameters_configure_protocol_default_configuration } else { _nw_parameters_configure_protocol_disable };
            let parameters = nw_parameters_create_secure_tcp(tls_block, _nw_parameters_configure_protocol_default_configuration).ok_or("cannot create TCP parameters")?;
            let endpoint = nw_endpoint_create_host(host_c.as_ptr(), port_c.as_ptr()).ok_or_else(|| format!("invalid address {host}:{port}"))?;
            nw_connection_create(&endpoint, &parameters).ok_or_else(|| format!("cannot connect to {host}:{port}"))?
        };
        unsafe {
            nw_connection_set_queue(&connection, DispatchQueue::main());
            let ready = Cell::new(Some(ready_tx));
            nw_connection_set_state_changed_handler(
                &connection,
                Some(&RcBlock::new(move |state: i32, error: *mut NWError| {
                    let outcome = match state {
                        CONNECTION_READY => Ok(()),
                        // Waiting means the path or the peer refused (connection refused, say): no use retrying here.
                        CONNECTION_WAITING | CONNECTION_FAILED => Err(describe(error)),
                        CONNECTION_CANCELLED => Err("the connection was cancelled".to_string()),
                        _ => return,
                    };
                    if let Some(ready) = ready.take() {
                        let _ = ready.send(outcome);
                    }
                })),
            );
            nw_connection_start(&connection);
        }
        let outcome = ready_rx.await.unwrap_or_else(|_| Err("the connection went away".into()));
        match outcome {
            Ok(()) => Ok(NwIo::new(connection)),
            Err(reason) => {
                unsafe { nw_connection_cancel(&connection) };
                Err(reason)
            }
        }
    }
}

/// An error as the system describes it (`Connection refused`), from its POSIX code.
unsafe fn describe(error: *mut NWError) -> String {
    match error.as_ref().map(|error| nw_error_get_error_code(error)) {
        Some(code) if code != 0 => std::io::Error::from_raw_os_error(code).to_string().split(" (os error").next().unwrap_or_default().to_string(),
        _ => "the connection failed".to_string(),
    }
}

/// The address of a connection's remote end.
fn remote_address(connection: &NWConnection) -> Option<IpAddr> {
    unsafe {
        let endpoint = nw_connection_copy_endpoint(connection)?;
        if nw_endpoint_get_type(&endpoint) != ENDPOINT_TYPE_ADDRESS {
            return None;
        }
        sockaddr_ip(nw_endpoint_get_address(&endpoint))
    }
}

/// The IP address in a `sockaddr_in` or `sockaddr_in6`.
unsafe fn sockaddr_ip(address: *const libc::sockaddr) -> Option<IpAddr> {
    if address.is_null() {
        return None;
    }
    match i32::from((*address).sa_family) {
        libc::AF_INET => {
            let v4 = &*(address as *const libc::sockaddr_in);
            Some(IpAddr::V4(Ipv4Addr::from(u32::from_be(v4.sin_addr.s_addr))))
        }
        libc::AF_INET6 => {
            let v6 = &*(address as *const libc::sockaddr_in6);
            Some(IpAddr::V6(Ipv6Addr::from(v6.sin6_addr.s6_addr)))
        }
        _ => None,
    }
}

impl Peer for NwIo {
    fn peer_address(&self) -> Option<IpAddr> {
        self.peer
    }
}

impl Drop for NwIo {
    fn drop(&mut self) {
        // The reference is released afterwards, with the field.
        unsafe { nw_connection_cancel(&self.connection) }
    }
}

impl hyper::rt::Read for NwIo {
    fn poll_read(self: Pin<&mut Self>, cx: &mut Context<'_>, mut buffer: hyper::rt::ReadBufCursor<'_>) -> Poll<std::io::Result<()>> {
        let mut state = self.state.borrow_mut();
        if let Some(queue) = state.queue.take() {
            let total = queue.size();
            // The cursor is used only during the synchronous apply below.
            let cursor = &mut buffer as *mut hyper::rt::ReadBufCursor<'_> as usize;
            let copied = Rc::new(Cell::new(0usize));
            let counter = copied.clone();
            let fill = RcBlock::new(move |_region: *mut c_void, _offset: usize, address: *const u8, size: usize| -> u8 {
                let buffer = unsafe { &mut *(cursor as *mut hyper::rt::ReadBufCursor<'_>) };
                let count = size.min(buffer.remaining());
                buffer.put_slice(unsafe { std::slice::from_raw_parts(address, count) });
                counter.set(counter.get() + count);
                (buffer.remaining() > 0) as u8
            });
            // The block is declared with plain pointers and a `u8` result where dispatch2 says `NonNull` and
            // `bool` (block2 cannot encode those in a closure's signature); the ABI is the same.
            unsafe { queue.apply(RcBlock::as_ptr(&fill).cast()) };
            let count = copied.get();
            if count < total {
                state.queue = Some(unsafe { queue.subrange(count, total - count) });
            }
            return Poll::Ready(Ok(()));
        }
        if state.end_of_stream {
            return Poll::Ready(Ok(()));
        }
        state.read_waker = Some(cx.waker().clone());
        if !state.receiving {
            state.receiving = true;
            let shared = self.state.clone();
            let received = RcBlock::new(move |content: *mut c_void, _context: *mut NWContentContext, _is_complete: u8, error: *mut NWError| {
                let mut state = shared.borrow_mut();
                state.receiving = false;
                match NonNull::new(content.cast::<DispatchData>()).filter(|_| error.is_null()) {
                    Some(content) => {
                        let content = unsafe { content.as_ref() };
                        state.queue = Some(match state.queue.take() {
                            Some(queue) => queue.concat(content),
                            None => content.retain(),
                        });
                    }
                    None => state.end_of_stream = true,
                }
                if let Some(waker) = state.read_waker.take() {
                    waker.wake();
                }
            });
            unsafe { nw_connection_receive(&self.connection, 1, 64 * 1024, &received) };
        }
        Poll::Pending
    }
}

impl hyper::rt::Write for NwIo {
    fn poll_write(self: Pin<&mut Self>, cx: &mut Context<'_>, bytes: &[u8]) -> Poll<std::io::Result<usize>> {
        let mut state = self.state.borrow_mut();
        if state.write_failed {
            return Poll::Ready(Err(std::io::ErrorKind::BrokenPipe.into()));
        }
        if state.unsent >= HIGH_WATER {
            state.write_waker = Some(cx.waker().clone());
            return Poll::Pending;
        }
        state.unsent += bytes.len();
        let (shared, length) = (self.state.clone(), bytes.len());
        let sent = RcBlock::new(move |error: *mut NWError| {
            let mut state = shared.borrow_mut();
            state.unsent -= length;
            if !error.is_null() {
                state.write_failed = true;
            }
            if let Some(waker) = state.write_waker.take() {
                waker.wake();
            }
        });
        if !bytes.is_empty() {
            let data = DispatchData::from_bytes(bytes); // copies
            unsafe { nw_connection_send(&self.connection, Some(&data), _nw_content_context_default_message, false, &sent) };
        }
        Poll::Ready(Ok(bytes.len()))
    }

    /// Nothing is buffered here: what `poll_write` accepted is already with the system.
    fn poll_flush(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Poll::Ready(Ok(()))
    }

    /// Closes the write side once everything is sent, and is ready when the system has done so. Only then is
    /// it safe to drop the connection: a send's completion alone does not mean the bytes have left, and
    /// cancelling at that point was seen to lose a whole response.
    fn poll_shutdown(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        let mut state = self.state.borrow_mut();
        if state.closed || state.write_failed {
            return Poll::Ready(Ok(()));
        }
        state.write_waker = Some(cx.waker().clone());
        if state.unsent == 0 && !state.closing {
            state.closing = true;
            let shared = self.state.clone();
            let closed = RcBlock::new(move |_error: *mut NWError| {
                let mut state = shared.borrow_mut();
                state.closed = true;
                if let Some(waker) = state.write_waker.take() {
                    waker.wake();
                }
            });
            unsafe { nw_connection_send(&self.connection, None, _nw_content_context_final_send, true, &closed) };
        }
        Poll::Pending
    }
}
