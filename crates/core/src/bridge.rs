//! `connect`: a stdio MCP endpoint for clients that can only spawn stdio servers, bridging to the running
//! server (the Launch Agent, which holds the permissions).
use std::cell::RefCell;
use std::future::poll_fn;
use std::pin::Pin;
use std::rc::Rc;

/// How a relay over the agent's socket ended.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Relayed {
    /// The input ended, and the server, having answered, closed its end.
    Finished,
    /// Writing to stdout failed: nobody reads the answers any more.
    OutputClosed,
    /// The server closed the connection while input was still open.
    ServerClosed,
}

/// Pipes `stdio` and `socket` into each other unchanged: what the client writes reaches the server, what the
/// server writes reaches the client. At the end of input the socket's write side is shut down, so the server
/// answers what it has read and then closes.
pub async fn relay<S, C>(stdio: S, socket: C) -> Relayed
where
    S: hyper::rt::Read + hyper::rt::Write + Unpin + 'static,
    C: hyper::rt::Read + hyper::rt::Write + Unpin + 'static,
{
    let (stdio, socket) = (Rc::new(RefCell::new(stdio)), Rc::new(RefCell::new(socket)));
    let input_ended = Rc::new(std::cell::Cell::new(false));
    let upstream = {
        let (stdio, socket, input_ended) = (stdio.clone(), socket.clone(), input_ended.clone());
        async move {
            while let Some(chunk) = read_some(&stdio).await {
                if write_all(&socket, &chunk).await.is_err() {
                    // The server has gone; the other direction reports it.
                    return std::future::pending::<Relayed>().await;
                }
            }
            input_ended.set(true);
            let _ = poll_fn(|cx| hyper::rt::Write::poll_shutdown(Pin::new(&mut *socket.borrow_mut()), cx)).await;
            std::future::pending::<Relayed>().await
        }
    };
    let downstream = async move {
        while let Some(chunk) = read_some(&socket).await {
            if write_all(&stdio, &chunk).await.is_err() {
                return Relayed::OutputClosed;
            }
        }
        if input_ended.get() {
            Relayed::Finished
        } else {
            Relayed::ServerClosed
        }
    };
    tokio::select! {
        outcome = upstream => outcome,
        outcome = downstream => outcome,
    }
}

/// The next bytes, or `None` at the end of the stream (or on an error, which ends it as well).
async fn read_some<IO: hyper::rt::Read + Unpin>(io: &Rc<RefCell<IO>>) -> Option<Vec<u8>> {
    let mut storage = vec![0u8; 64 * 1024];
    let mut buffer = hyper::rt::ReadBuf::new(&mut storage);
    poll_fn(|cx| hyper::rt::Read::poll_read(Pin::new(&mut *io.borrow_mut()), cx, buffer.unfilled())).await.ok()?;
    let filled = buffer.filled();
    (!filled.is_empty()).then(|| filled.to_vec())
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
