//! The stdio binding: one MCP session over a byte stream of newline-delimited JSON (stdin/stdout, or a Unix
//! socket connection).
//!
//! Two things the plain binding leaves open are settled as the Swift port settles them. At end of input the
//! requests already read are still answered before the session ends, as a client that closes stdin after its
//! last request expects; a request the client cancelled is not waited for. And a write that fails (the client
//! closed its end) ends the session, which cancels the calls in flight.
use crate::mcp::Server;
use rmcp::model::{ClientJsonRpcMessage, JsonRpcMessage, ServerJsonRpcMessage};
use rmcp::{RoleServer, ServiceExt};
use serde::Deserialize;
use std::cell::RefCell;
use std::collections::HashSet;
use std::future::{poll_fn, Future};
use std::pin::Pin;
use std::rc::Rc;
use tokio::sync::mpsc;

/// A line the writer is to send, and the request it answers (to forget it once written).
struct Outgoing {
    line: String,
    answers: Option<String>,
}

struct LineTransport {
    incoming: mpsc::UnboundedReceiver<ClientJsonRpcMessage>,
    outgoing: mpsc::UnboundedSender<Outgoing>,
}

impl rmcp::transport::Transport<RoleServer> for LineTransport {
    type Error = std::io::Error;

    fn send(&mut self, item: ServerJsonRpcMessage) -> impl Future<Output = Result<(), Self::Error>> + Send + 'static {
        let answers = crate::session::answered(&item);
        let line = serde_json::to_string(&item).expect("rmcp messages serialize");
        let sent = self.outgoing.send(Outgoing { line, answers }).map_err(|_| std::io::Error::from(std::io::ErrorKind::BrokenPipe));
        std::future::ready(sent)
    }

    fn receive(&mut self) -> impl Future<Output = Option<ClientJsonRpcMessage>> + Send {
        self.incoming.recv()
    }

    async fn close(&mut self) -> Result<(), Self::Error> {
        Ok(())
    }
}

/// When the session's input may end: the reader and the writer share it.
#[derive(Default)]
struct Ledger {
    /// Requests read and not yet answered (or cancelled), by id as JSON text.
    unanswered: HashSet<String>,
    input_ended: bool,
    /// The service's input; dropped once input has ended and everything is answered, or the output is gone.
    to_server: Option<mpsc::UnboundedSender<ClientJsonRpcMessage>>,
}

impl Ledger {
    fn finish_if_drained(&mut self) {
        if self.input_ended && self.unanswered.is_empty() {
            self.to_server = None;
        }
    }
}

fn error_line(code: i64, message: &str) -> String {
    serde_json::json!({"jsonrpc": "2.0", "id": null, "error": {"code": code, "message": message}}).to_string()
}

/// Serves one session on `io` until its input has ended and every request is answered, or its output fails.
/// Must run inside the guest runtime's `LocalSet`.
pub async fn serve_stream<IO>(io: IO, server: Server)
where
    IO: hyper::rt::Read + hyper::rt::Write + Unpin + 'static,
{
    let io = Rc::new(RefCell::new(io));
    let (to_server, incoming) = mpsc::unbounded_channel();
    let (outgoing, mut to_write) = mpsc::unbounded_channel::<Outgoing>();
    let ledger = Rc::new(RefCell::new(Ledger { to_server: Some(to_server), ..Ledger::default() }));

    let reader = tokio::task::spawn_local(read_lines(io.clone(), ledger.clone(), outgoing.clone()));
    let (output_gone, gone) = tokio::sync::oneshot::channel::<()>();
    let writer = {
        let (io, ledger) = (io.clone(), ledger.clone());
        tokio::task::spawn_local(async move {
            while let Some(Outgoing { line, answers }) = to_write.recv().await {
                if write_all(&io, format!("{line}\n").as_bytes()).await.is_err() {
                    // The client has gone: nothing more can reach it, so the session ends now.
                    let _ = output_gone.send(());
                    return;
                }
                let mut ledger = ledger.borrow_mut();
                if let Some(id) = answers {
                    ledger.unanswered.remove(&id);
                }
                ledger.finish_if_drained();
            }
        })
    };
    let session = async {
        if let Ok(running) = server.serve(LineTransport { incoming, outgoing }).await {
            let _ = running.waiting().await;
        }
    };
    // When the output is gone, dropping the running service cancels its calls at once; ending its input
    // instead would let it wait for them (rmcp allows them five seconds), and their answers could not be sent.
    tokio::select! {
        _ = session => {}
        _ = gone => {}
    }
    reader.abort();
    // The service is gone, and with it the last sender: the writer finishes what is queued, then stops.
    let _ = writer.await;
    let _ = poll_fn(|cx| hyper::rt::Write::poll_shutdown(Pin::new(&mut *io.borrow_mut()), cx)).await;
}

async fn read_lines<IO>(io: Rc<RefCell<IO>>, ledger: Rc<RefCell<Ledger>>, outgoing: mpsc::UnboundedSender<Outgoing>)
where
    IO: hyper::rt::Read + Unpin,
{
    let mut pending = Vec::new();
    let mut storage = vec![0u8; 64 * 1024];
    loop {
        let count = {
            let mut buffer = hyper::rt::ReadBuf::new(&mut storage);
            match poll_fn(|cx| hyper::rt::Read::poll_read(Pin::new(&mut *io.borrow_mut()), cx, buffer.unfilled())).await {
                Ok(()) => buffer.filled().len(),
                Err(_) => 0,
            }
        };
        if count == 0 {
            // An unterminated last line is dropped, as the binding requires a newline after each message.
            let mut ledger = ledger.borrow_mut();
            ledger.input_ended = true;
            ledger.finish_if_drained();
            return;
        }
        pending.extend_from_slice(&storage[..count]);
        while let Some(end) = pending.iter().position(|byte| *byte == b'\n') {
            let line: Vec<u8> = pending.drain(..=end).collect();
            handle_line(&line[..line.len() - 1], &ledger, &outgoing);
        }
    }
}

fn handle_line(line: &[u8], ledger: &Rc<RefCell<Ledger>>, outgoing: &mpsc::UnboundedSender<Outgoing>) {
    let line = line.strip_suffix(b"\r").unwrap_or(line);
    if line.iter().all(u8::is_ascii_whitespace) {
        return;
    }
    let reply = |line: String| {
        let _ = outgoing.send(Outgoing { line, answers: None });
    };
    let Ok(value) = serde_json::from_slice::<serde_json::Value>(line) else {
        return reply(error_line(-32700, "Parse error"));
    };
    if value.is_array() {
        return reply(error_line(-32600, "Invalid Request: JSON-RPC batches are not supported"));
    }
    let Ok(message) = ClientJsonRpcMessage::deserialize(&value) else {
        return reply(error_line(-32600, "Invalid Request"));
    };
    let mut ledger = ledger.borrow_mut();
    if matches!(message, JsonRpcMessage::Request(_)) {
        ledger.unanswered.insert(value["id"].to_string());
    } else if value["method"] == "notifications/cancelled" {
        // The client expects no answer to a request it cancelled.
        ledger.unanswered.remove(&value["params"]["requestId"].to_string());
    }
    if let Some(to_server) = &ledger.to_server {
        let _ = to_server.send(message);
    }
}

async fn write_all<IO: hyper::rt::Write + Unpin>(io: &Rc<RefCell<IO>>, mut bytes: &[u8]) -> std::io::Result<()> {
    while !bytes.is_empty() {
        let written = poll_fn(|cx| hyper::rt::Write::poll_write(Pin::new(&mut *io.borrow_mut()), cx, bytes)).await?;
        if written == 0 {
            return Err(std::io::ErrorKind::WriteZero.into());
        }
        bytes = &bytes[written..];
    }
    Ok(())
}
