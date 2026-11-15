//! TCP through Network.framework. Every callback is delivered on the main queue, which the main run loop
//! drains, so nothing here crosses threads.
//!
//! Network.framework has no binding crate (`objc2-network` is an empty placeholder), so the C functions used
//! are declared here. Everything of libdispatch's (the main queue, `dispatch_data`) comes from `dispatch2`.
use block2::{Block, RcBlock};
use desktop_mcp_core::io::{Listener, Peer};
use dispatch2::{DispatchData, DispatchObject, DispatchQueue, DispatchRetained};
use std::cell::{Cell, RefCell};
use std::ffi::{c_char, c_void, CString};
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::future::Future;
use std::pin::Pin;
use std::ptr::NonNull;
use std::rc::Rc;
use std::sync::Arc;
use std::task::{Context, Poll, Waker};
use tokio::sync::{mpsc, oneshot};

/// A Network.framework object.
type Object = *mut c_void;

const LISTENER_READY: i32 = 2;
const LISTENER_FAILED: i32 = 3;
const LISTENER_CANCELLED: i32 = 4;
const ENDPOINT_TYPE_ADDRESS: i32 = 1;

#[link(name = "Network", kind = "framework")]
extern "C" {
    static _nw_parameters_configure_protocol_default_configuration: *mut c_void;
    static _nw_parameters_configure_protocol_disable: *mut c_void;
    static _nw_content_context_default_message: Object;
    static _nw_content_context_final_send: Object;
    fn nw_parameters_create_secure_tcp(configure_tls: *mut c_void, configure_tcp: *mut c_void) -> Object;
    fn nw_parameters_set_local_endpoint(parameters: Object, endpoint: Object);
    fn nw_parameters_set_reuse_local_address(parameters: Object, reuse: bool);
    fn nw_endpoint_create_host(hostname: *const c_char, port: *const c_char) -> Object;
    fn nw_listener_create(parameters: Object) -> Object;
    fn nw_listener_set_queue(listener: Object, queue: &DispatchQueue);
    fn nw_listener_set_state_changed_handler(listener: Object, handler: Option<&Block<dyn Fn(i32, Object)>>);
    fn nw_listener_set_new_connection_handler(listener: Object, handler: Option<&Block<dyn Fn(Object)>>);
    fn nw_listener_start(listener: Object);
    fn nw_listener_cancel(listener: Object);
    fn nw_listener_get_port(listener: Object) -> u16;
    fn nw_error_get_error_code(error: Object) -> i32;
    fn nw_connection_set_queue(connection: Object, queue: &DispatchQueue);
    fn nw_connection_start(connection: Object);
    fn nw_connection_cancel(connection: Object);
    fn nw_connection_copy_endpoint(connection: Object) -> Object;
    fn nw_endpoint_get_type(endpoint: Object) -> i32;
    fn nw_endpoint_get_address(endpoint: Object) -> *const libc::sockaddr;
    // C `bool` block arguments are declared `u8`: block2 has no encoding for `bool`.
    /// `content` is a borrowed `dispatch_data_t`, or null.
    fn nw_connection_receive(connection: Object, minimum: u32, maximum: u32, completion: &Block<dyn Fn(*mut c_void, Object, u8, Object)>);
    fn nw_connection_send(connection: Object, content: Option<&DispatchData>, context: Object, is_complete: bool, completion: &Block<dyn Fn(Object)>);
    fn nw_retain(object: Object) -> Object;
    fn nw_release(object: Object);
}

/// A listening TCP socket.
pub struct NwListener {
    listener: Object,
    port: u16,
    connections: mpsc::UnboundedReceiver<NwIo>,
}

impl NwListener {
    /// Binds `host` (a name or an address; `localhost` binds 127.0.0.1) and `port` (0 lets the system choose).
    /// Resolves once the listener is ready, or with the reason it failed.
    pub async fn bind(host: &str, port: u16) -> Result<NwListener, String> {
        let host = if host.eq_ignore_ascii_case("localhost") { "127.0.0.1" } else { host };
        let host_c = CString::new(host).map_err(|_| format!("invalid host {host:?}"))?;
        let port_c = CString::new(port.to_string()).expect("digits");
        let (ready_tx, ready_rx) = oneshot::channel::<Result<u16, String>>();
        let (connection_tx, connections) = mpsc::unbounded_channel();
        let listener = unsafe {
            let parameters = nw_parameters_create_secure_tcp(_nw_parameters_configure_protocol_disable, _nw_parameters_configure_protocol_default_configuration);
            let endpoint = nw_endpoint_create_host(host_c.as_ptr(), port_c.as_ptr());
            nw_parameters_set_local_endpoint(parameters, endpoint);
            nw_parameters_set_reuse_local_address(parameters, true);
            let listener = nw_listener_create(parameters);
            nw_release(endpoint);
            nw_release(parameters);
            if listener.is_null() {
                return Err(format!("cannot listen on {host}:{port}"));
            }
            nw_listener_set_queue(listener, DispatchQueue::main());
            let ready = Cell::new(Some(ready_tx));
            let address = listener as usize; // the handler must not retain the listener it belongs to
            nw_listener_set_state_changed_handler(
                listener,
                Some(&RcBlock::new(move |state: i32, error: Object| {
                    let outcome = match state {
                        LISTENER_READY => Ok(nw_listener_get_port(address as Object)),
                        LISTENER_FAILED => Err(format!("listen failed (error {})", if error.is_null() { 0 } else { nw_error_get_error_code(error) })),
                        LISTENER_CANCELLED => Err("the listener was cancelled".to_string()),
                        _ => return,
                    };
                    if let Some(ready) = ready.take() {
                        let _ = ready.send(outcome);
                    }
                })),
            );
            nw_listener_set_new_connection_handler(
                listener,
                Some(&RcBlock::new(move |connection: Object| {
                    nw_retain(connection);
                    nw_connection_set_queue(connection, DispatchQueue::main());
                    nw_connection_start(connection);
                    let _ = connection_tx.send(NwIo::new(connection));
                })),
            );
            nw_listener_start(listener);
            listener
        };
        // From here the value owns the listener, so an early return cancels it.
        let mut bound = NwListener { listener, port: 0, connections };
        bound.port = ready_rx.await.map_err(|_| "the listener went away".to_string())??;
        Ok(bound)
    }

    pub fn port(&self) -> u16 {
        self.port
    }
}

impl Drop for NwListener {
    fn drop(&mut self) {
        unsafe {
            nw_listener_cancel(self.listener);
            nw_release(self.listener);
        }
    }
}

impl Listener for NwListener {
    type Io = NwIo;

    fn accept(&mut self) -> impl Future<Output = Option<NwIo>> {
        self.connections.recv()
    }
}

/// When this much is handed to the system and not yet sent, writes wait.
const HIGH_WATER: usize = 256 * 1024;

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
    connection: Object,
    peer: Option<IpAddr>,
    /// `Arc`, not `Rc`: the completion blocks hold a clone each, and the system may release a block on a thread
    /// of its own, so the count has to be atomic (as a Swift or Objective-C capture's is). The state itself is
    /// only ever read or written on the main thread.
    state: Arc<RefCell<IoState>>,
}

impl NwIo {
    /// Takes over a started connection (+1 retained).
    fn new(connection: Object) -> NwIo {
        let state = IoState { queue: None, receiving: false, end_of_stream: false, read_waker: None, unsent: 0, write_failed: false, write_waker: None, closing: false, closed: false };
        #[allow(clippy::arc_with_non_send_sync)]
        let state = Arc::new(RefCell::new(state));
        NwIo { connection, peer: remote_address(connection), state }
    }
}

/// The address of a connection's remote end.
fn remote_address(connection: Object) -> Option<IpAddr> {
    unsafe {
        let endpoint = nw_connection_copy_endpoint(connection);
        if endpoint.is_null() {
            return None;
        }
        let address = if nw_endpoint_get_type(endpoint) == ENDPOINT_TYPE_ADDRESS { sockaddr_ip(nw_endpoint_get_address(endpoint)) } else { None };
        nw_release(endpoint);
        address
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
        unsafe {
            nw_connection_cancel(self.connection);
            nw_release(self.connection);
        }
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
            let received = RcBlock::new(move |content: *mut c_void, _context: Object, _is_complete: u8, error: Object| {
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
            unsafe { nw_connection_receive(self.connection, 1, 64 * 1024, &received) };
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
        let sent = RcBlock::new(move |error: Object| {
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
            unsafe { nw_connection_send(self.connection, Some(&data), _nw_content_context_default_message, false, &sent) };
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
            let closed = RcBlock::new(move |_error: Object| {
                let mut state = shared.borrow_mut();
                state.closed = true;
                if let Some(waker) = state.write_waker.take() {
                    waker.wake();
                }
            });
            unsafe { nw_connection_send(self.connection, None, _nw_content_context_final_send, true, &closed) };
        }
        Poll::Pending
    }
}
