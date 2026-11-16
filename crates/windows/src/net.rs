//! Outgoing connections for Scrape over WinRT sockets: name resolution and a `StreamSocket` byte stream (TLS by
//! the system when asked). Completions arrive through WinRT async operations, awaited on the main loop.
use std::future::{Future, IntoFuture};
use std::io;
use std::net::IpAddr;
use std::pin::Pin;
use std::task::{ready, Context, Poll};
use windows::core::{Interface, HSTRING};
use windows::Foundation::IClosable;
use windows::Networking::Sockets::{DatagramSocket, SocketProtectionLevel, StreamSocket};
use windows::Networking::{HostName, HostNameType};
use windows::Storage::Streams::{Buffer, DataReader, DataWriter, InputStreamOptions};

const READ_SIZE: u32 = 64 * 1024;

/// The addresses `host` resolves to; a numeric address is returned as it is.
pub async fn resolve(host: &str) -> Result<Vec<IpAddr>, String> {
    let bare = host.trim_start_matches('[').trim_end_matches(']');
    if let Ok(address) = bare.parse::<IpAddr>() {
        return Ok(vec![address]);
    }
    let name = HostName::CreateHostName(&HSTRING::from(host)).map_err(|error| error.message())?;
    let pairs = DatagramSocket::GetEndpointPairsAsync(&name, &HSTRING::from("0")).map_err(|error| error.message())?.into_future().await.map_err(|error| error.message())?;
    let mut addresses = Vec::new();
    for pair in pairs {
        let Ok(remote) = pair.RemoteHostName() else { continue };
        if !matches!(remote.Type(), Ok(HostNameType::Ipv4) | Ok(HostNameType::Ipv6)) {
            continue;
        }
        // An IPv6 canonical name may carry a zone ("fe80::1%3").
        if let Ok(address) = remote.CanonicalName().map(|name| name.to_string()).unwrap_or_default().split('%').next().unwrap_or_default().parse::<IpAddr>() {
            if !addresses.contains(&address) {
                addresses.push(address);
            }
        }
    }
    if addresses.is_empty() {
        return Err("no addresses".to_string());
    }
    Ok(addresses)
}

type Pending<T> = Pin<Box<dyn Future<Output = io::Result<T>>>>;

fn io_error(error: windows::core::Error) -> io::Error {
    io::Error::other(error)
}

/// A connected `StreamSocket` as a byte stream. Closing it (on drop) ends any read or write in flight.
pub struct SocketIo {
    socket: StreamSocket,
    reading: Option<Pending<Vec<u8>>>,
    buffered: Vec<u8>,
    ended: bool,
    writing: Option<Pending<()>>,
}

impl SocketIo {
    /// Connects to `host`:`port`; with `tls`, over TLS 1.2 or later validated the way the system validates.
    pub async fn connect(host: &str, port: u16, tls: bool) -> Result<SocketIo, String> {
        let socket = StreamSocket::new().map_err(|error| error.message())?;
        let _ = socket.Control().and_then(|control| control.SetNoDelay(true));
        let name = HostName::CreateHostName(&HSTRING::from(host)).map_err(|error| error.message())?;
        let protection = if tls { SocketProtectionLevel::Tls12 } else { SocketProtectionLevel::PlainSocket };
        socket
            .ConnectWithProtectionLevelAsync(&name, &HSTRING::from(port.to_string()), protection)
            .map_err(|error| error.message())?
            .into_future()
            .await
            .map_err(|error| error.message())?;
        Ok(SocketIo::from_socket(socket))
    }

    /// An already connected socket (an accepted one, for example).
    pub fn from_socket(socket: StreamSocket) -> SocketIo {
        SocketIo { socket, reading: None, buffered: Vec::new(), ended: false, writing: None }
    }

    fn start_read(&self) -> Pending<Vec<u8>> {
        let socket = self.socket.clone();
        Box::pin(async move {
            let buffer = Buffer::Create(READ_SIZE).map_err(io_error)?;
            let input = socket.InputStream().map_err(io_error)?;
            let filled = input.ReadAsync(&buffer, READ_SIZE, InputStreamOptions::Partial).map_err(io_error)?.into_future().await.map_err(io_error)?;
            let length = filled.Length().map_err(io_error)? as usize;
            let mut bytes = vec![0u8; length];
            if length > 0 {
                DataReader::FromBuffer(&filled).and_then(|reader| reader.ReadBytes(&mut bytes)).map_err(io_error)?;
            }
            Ok(bytes)
        })
    }

    fn poll_written(&mut self, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        if let Some(writing) = self.writing.as_mut() {
            let result = ready!(writing.as_mut().poll(cx));
            self.writing = None;
            result?;
        }
        Poll::Ready(Ok(()))
    }
}

impl hyper::rt::Read for SocketIo {
    fn poll_read(mut self: Pin<&mut Self>, cx: &mut Context<'_>, mut buf: hyper::rt::ReadBufCursor<'_>) -> Poll<io::Result<()>> {
        loop {
            if !self.buffered.is_empty() {
                let count = self.buffered.len().min(buf.remaining());
                buf.put_slice(&self.buffered[..count]);
                self.buffered.drain(..count);
                return Poll::Ready(Ok(()));
            }
            if self.ended {
                return Poll::Ready(Ok(()));
            }
            if self.reading.is_none() {
                self.reading = Some(self.start_read());
            }
            let bytes = ready!(self.reading.as_mut().unwrap().as_mut().poll(cx));
            self.reading = None;
            let bytes = bytes?;
            if bytes.is_empty() {
                self.ended = true;
            }
            self.buffered = bytes;
        }
    }
}

impl hyper::rt::Write for SocketIo {
    fn poll_write(mut self: Pin<&mut Self>, cx: &mut Context<'_>, buf: &[u8]) -> Poll<io::Result<usize>> {
        ready!(self.poll_written(cx))?;
        let socket = self.socket.clone();
        let bytes = buf.to_vec();
        self.writing = Some(Box::pin(async move {
            let writer = DataWriter::new().map_err(io_error)?;
            writer.WriteBytes(&bytes).map_err(io_error)?;
            let buffer = writer.DetachBuffer().map_err(io_error)?;
            socket.OutputStream().map_err(io_error)?.WriteAsync(&buffer).map_err(io_error)?.into_future().await.map_err(io_error)?;
            Ok(())
        }));
        Poll::Ready(Ok(buf.len()))
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        self.poll_written(cx)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        self.poll_written(cx)
    }
}

impl desktop_mcp_core::io::Peer for SocketIo {
    fn peer_address(&self) -> Option<IpAddr> {
        let name = self.socket.Information().ok()?.RemoteAddress().ok()?.CanonicalName().ok()?.to_string();
        name.split('%').next()?.trim_start_matches('[').trim_end_matches(']').parse().ok()
    }
}

impl Drop for SocketIo {
    fn drop(&mut self) {
        let _ = self.socket.cast::<IClosable>().and_then(|closable| closable.Close());
    }
}

