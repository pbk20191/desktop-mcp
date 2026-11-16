//! Incoming TCP connections over a WinRT `StreamSocketListener`. Connections arrive on a pool thread and are handed
//! to the main loop through a channel, as `SocketIo` streams.
use crate::net::SocketIo;
use std::future::{Future, IntoFuture};
use tokio::sync::mpsc;
use windows::core::{Interface, HSTRING};
use windows::Foundation::{IClosable, TypedEventHandler};
use windows::Networking::HostName;
use windows::Networking::Sockets::{StreamSocket, StreamSocketListener};

pub struct SocketListener {
    listener: StreamSocketListener,
    connections: mpsc::UnboundedReceiver<StreamSocket>,
    port: u16,
}

impl SocketListener {
    /// Listens on `host` ("localhost" is 127.0.0.1) and `port` (0: any free port).
    pub async fn bind(host: &str, port: u16) -> Result<SocketListener, String> {
        let host = if host.eq_ignore_ascii_case("localhost") { "127.0.0.1" } else { host };
        let listener = StreamSocketListener::new().map_err(|error| error.message())?;
        let _ = listener.Control().and_then(|control| control.SetNoDelay(true));
        let (sender, connections) = mpsc::unbounded_channel();
        listener
            .ConnectionReceived(&TypedEventHandler::new(move |_, arguments: windows::core::Ref<windows::Networking::Sockets::StreamSocketListenerConnectionReceivedEventArgs>| {
                if let Some(arguments) = arguments.as_ref() {
                    if let Ok(socket) = arguments.Socket() {
                        let _ = sender.send(socket);
                    }
                }
                Ok(())
            }))
            .map_err(|error| error.message())?;
        let name = HostName::CreateHostName(&HSTRING::from(host.trim_start_matches('[').trim_end_matches(']'))).map_err(|error| error.message())?;
        listener.BindEndpointAsync(&name, &HSTRING::from(port.to_string())).map_err(|error| error.message())?.into_future().await.map_err(|error| error.message())?;
        let port = listener.Information().and_then(|information| information.LocalPort()).map(|port| port.to_string().parse().unwrap_or(0)).unwrap_or(0);
        Ok(SocketListener { listener, connections, port })
    }

    /// The port it listens on (the one picked for port 0).
    pub fn port(&self) -> u16 {
        self.port
    }
}

impl desktop_mcp_core::io::Listener for SocketListener {
    type Io = SocketIo;

    fn accept(&mut self) -> impl Future<Output = Option<SocketIo>> {
        async {
            let socket = self.connections.recv().await?;
            let _ = socket.Control().and_then(|control| control.SetNoDelay(true));
            Some(SocketIo::from_socket(socket))
        }
    }
}

impl Drop for SocketListener {
    fn drop(&mut self) {
        let _ = self.listener.cast::<IClosable>().and_then(|closable| closable.Close());
    }
}
