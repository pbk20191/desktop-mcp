//! Network.framework's C API, the part desktop-mcp uses. There is no binding crate (`objc2-network` is an empty
//! placeholder), so the objects and functions are declared here, after `dispatch2`'s model: an opaque type per
//! object, used behind `&T`, and [`NWRetained`] for an owned reference.
use dispatch2::{DispatchData, DispatchQueue};
use objc2::encode::{Encoding, RefEncode};
use std::ffi::{c_char, c_void};
use std::fmt;
use std::marker::{PhantomData, PhantomPinned};
use std::ops::Deref;
use std::ptr::NonNull;

/// A Network.framework object: reference counted with `nw_retain` / `nw_release`.
///
/// # Safety
///
/// Only for the opaque types declared with `nw_object!`.
pub unsafe trait NWObject {}

macro_rules! nw_object {
    ($(#[$m:meta])* $name:ident) => {
        $(#[$m])*
        #[repr(C)]
        pub struct $name {
            _inner: [u8; 0],
            _marker: PhantomData<(*mut u8, PhantomPinned)>,
        }

        // SAFETY: an opaque Network.framework object.
        unsafe impl NWObject for $name {}

        // Network.framework objects are OS objects marked sendable: their reference counts are atomic, and the
        // framework serializes their callbacks on the queue each one is given.
        unsafe impl Send for $name {}
        unsafe impl Sync for $name {}

        // An OS object is an Objective-C object, which is how blocks describe the pointer.
        unsafe impl RefEncode for $name {
            const ENCODING_REF: Encoding = Encoding::Object;
        }

        impl fmt::Debug for $name {
            fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                write!(f, "{}({:p})", stringify!($name), self)
            }
        }
    };
}

nw_object!(
    /// `nw_parameters_t`
    NWParameters
);
nw_object!(
    /// `nw_endpoint_t`
    NWEndpoint
);
nw_object!(
    /// `nw_listener_t`
    NWListener
);
nw_object!(
    /// `nw_connection_t`
    NWConnection
);
nw_object!(
    /// `nw_error_t`
    NWError
);
nw_object!(
    /// `nw_content_context_t`
    NWContentContext
);
nw_object!(
    /// `nw_protocol_options_t`
    NWProtocolOptions
);

/// An owned reference to a Network.framework object: released when dropped, retained when cloned. The
/// counterpart of `dispatch2::DispatchRetained`.
///
/// Same size as a pointer; `Option<NWRetained<T>>` is a nullable pointer, so a function that returns a +1
/// object can be declared to return it directly.
#[repr(transparent)]
pub struct NWRetained<T: NWObject> {
    ptr: NonNull<T>,
    item: PhantomData<T>,
}

impl<T: NWObject> NWRetained<T> {
    /// Takes over a pointer that already holds a +1 reference (the create rule).
    ///
    /// # Safety
    ///
    /// `ptr` must be a valid object of type `T` with a reference the caller owns.
    pub unsafe fn from_raw(ptr: NonNull<T>) -> Self {
        NWRetained { ptr, item: PhantomData }
    }

    /// Retains a borrowed object, such as one a callback hands over.
    ///
    /// # Safety
    ///
    /// `ptr` must be a valid object of type `T`.
    pub unsafe fn retain(ptr: NonNull<T>) -> Self {
        nw_retain(ptr.as_ptr().cast());
        Self::from_raw(ptr)
    }

    pub fn as_ptr(this: &Self) -> NonNull<T> {
        this.ptr
    }
}

// The pointer moves freely; the object itself never does.
impl<T: NWObject> Unpin for NWRetained<T> {}

// As for `Arc`: sharing an owned reference is as safe as sharing the object.
unsafe impl<T: NWObject + Send + Sync> Send for NWRetained<T> {}
unsafe impl<T: NWObject + Send + Sync> Sync for NWRetained<T> {}

impl<T: NWObject> Drop for NWRetained<T> {
    fn drop(&mut self) {
        // SAFETY: the pointer holds a reference this value owns.
        unsafe { nw_release(self.ptr.as_ptr().cast()) }
    }
}

impl<T: NWObject> Clone for NWRetained<T> {
    fn clone(&self) -> Self {
        // SAFETY: the pointer is valid while this value lives.
        unsafe { Self::retain(self.ptr) }
    }
}

impl<T: NWObject> Deref for NWRetained<T> {
    type Target = T;

    fn deref(&self) -> &T {
        // SAFETY: the pointer is valid while this value lives.
        unsafe { self.ptr.as_ref() }
    }
}

impl<T: NWObject + fmt::Debug> fmt::Debug for NWRetained<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        (**self).fmt(f)
    }
}

pub const LISTENER_READY: i32 = 2;
pub const LISTENER_FAILED: i32 = 3;
pub const LISTENER_CANCELLED: i32 = 4;
pub const ENDPOINT_TYPE_ADDRESS: i32 = 1;
pub const CONNECTION_WAITING: i32 = 1;
pub const CONNECTION_READY: i32 = 3;
pub const CONNECTION_FAILED: i32 = 4;
pub const CONNECTION_CANCELLED: i32 = 5;

/// `nw_parameters_configure_protocol_block_t`: configures one protocol of new parameters. Network.framework
/// also offers two ready ones, `_nw_parameters_configure_protocol_default_configuration` and `…_disable`.
pub type ConfigureProtocol = block2::Block<dyn Fn(NonNull<NWProtocolOptions>)>;

#[link(name = "Network", kind = "framework")]
extern "C" {
    pub static _nw_parameters_configure_protocol_default_configuration: &'static ConfigureProtocol;
    pub static _nw_parameters_configure_protocol_disable: &'static ConfigureProtocol;
    pub static _nw_content_context_default_message: &'static NWContentContext;
    pub static _nw_content_context_final_send: &'static NWContentContext;

    fn nw_retain(object: *mut c_void) -> *mut c_void;
    fn nw_release(object: *mut c_void);

    pub fn nw_parameters_create_secure_tcp(configure_tls: &ConfigureProtocol, configure_tcp: &ConfigureProtocol) -> Option<NWRetained<NWParameters>>;
    pub fn nw_parameters_set_local_endpoint(parameters: &NWParameters, endpoint: Option<&NWEndpoint>);
    pub fn nw_parameters_set_reuse_local_address(parameters: &NWParameters, reuse: bool);
    pub fn nw_endpoint_create_host(hostname: *const c_char, port: *const c_char) -> Option<NWRetained<NWEndpoint>>;
    pub fn nw_endpoint_get_type(endpoint: &NWEndpoint) -> i32;
    pub fn nw_endpoint_get_address(endpoint: &NWEndpoint) -> *const libc::sockaddr;
    pub fn nw_error_get_error_code(error: &NWError) -> i32;
    /// A +1 `sec_protocol_options_t`, released with `sec_release`.
    pub fn nw_tls_copy_sec_protocol_options(options: &NWProtocolOptions) -> objc2_security::sec_protocol_options_t;

    pub fn nw_listener_create(parameters: &NWParameters) -> Option<NWRetained<NWListener>>;
    pub fn nw_listener_set_queue(listener: &NWListener, queue: &DispatchQueue);
    pub fn nw_listener_set_state_changed_handler(listener: &NWListener, handler: Option<&block2::Block<dyn Fn(i32, *mut NWError)>>);
    pub fn nw_listener_set_new_connection_handler(listener: &NWListener, handler: Option<&block2::Block<dyn Fn(NonNull<NWConnection>)>>);
    pub fn nw_listener_start(listener: &NWListener);
    pub fn nw_listener_cancel(listener: &NWListener);
    pub fn nw_listener_get_port(listener: &NWListener) -> u16;

    pub fn nw_connection_create(endpoint: &NWEndpoint, parameters: &NWParameters) -> Option<NWRetained<NWConnection>>;
    pub fn nw_connection_set_state_changed_handler(connection: &NWConnection, handler: Option<&block2::Block<dyn Fn(i32, *mut NWError)>>);
    pub fn nw_connection_set_queue(connection: &NWConnection, queue: &DispatchQueue);
    pub fn nw_connection_start(connection: &NWConnection);
    pub fn nw_connection_cancel(connection: &NWConnection);
    pub fn nw_connection_copy_endpoint(connection: &NWConnection) -> Option<NWRetained<NWEndpoint>>;
    /// `content` is borrowed and may be null. C `bool` block arguments are declared `u8`: block2 cannot encode
    /// `bool` in a closure's signature; the ABI is the same.
    pub fn nw_connection_receive(connection: &NWConnection, minimum: u32, maximum: u32, completion: &block2::Block<dyn Fn(*mut c_void, *mut NWContentContext, u8, *mut NWError)>);
    pub fn nw_connection_send(connection: &NWConnection, content: Option<&DispatchData>, context: &NWContentContext, is_complete: bool, completion: &block2::Block<dyn Fn(*mut NWError)>);
}
