mod support;
use desktop_mcp_core::io::Listener;
use desktop_mcp_macos::main_loop::run;
use desktop_mcp_macos::nw::{NwIo, NwListener};
use std::future::poll_fn;
use std::io::{Read, Write};
use std::net::{Shutdown, TcpStream};
use std::pin::Pin;
use std::time::{Duration, Instant};

/// Reads whatever is available (at most 64 KiB); empty at end of stream.
async fn read_some(io: &mut NwIo) -> std::io::Result<Vec<u8>> {
    let mut storage = vec![0u8; 64 * 1024];
    let mut buffer = hyper::rt::ReadBuf::new(&mut storage);
    poll_fn(|cx| hyper::rt::Read::poll_read(Pin::new(&mut *io), cx, buffer.unfilled())).await?;
    Ok(buffer.filled().to_vec())
}
async fn write_all(io: &mut NwIo, mut bytes: &[u8]) -> std::io::Result<()> {
    while !bytes.is_empty() {
        let written = poll_fn(|cx| hyper::rt::Write::poll_write(Pin::new(&mut *io), cx, bytes)).await?;
        bytes = &bytes[written..];
    }
    Ok(())
}
/// Echoes until the peer stops sending.
async fn echo(mut io: NwIo) {
    loop {
        match read_some(&mut io).await {
            Ok(bytes) if !bytes.is_empty() => {
                if write_all(&mut io, &bytes).await.is_err() {
                    return;
                }
            }
            _ => return,
        }
    }
}
fn connect(port: u16) -> TcpStream {
    let stream = TcpStream::connect(("127.0.0.1", port)).expect("connect");
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    stream
}
/// Runs `server` with a listener on a fresh port while `client` runs on another thread; returns the client's result.
fn with_listener<T: Send + 'static>(client: impl FnOnce(u16) -> T + Send + 'static, serve_connections: usize) -> T {
    run(move |_| async move {
        let mut listener = NwListener::bind("127.0.0.1", 0).await.expect("bind");
        let port = listener.port();
        let (tx, rx) = tokio::sync::oneshot::channel();
        std::thread::spawn(move || tx.send(client(port)));
        for _ in 0..serve_connections {
            let io = listener.accept().await.expect("a connection");
            tokio::task::spawn_local(echo(io));
        }
        rx.await.expect("the client finished")
    })
}

fn binds_a_system_port() {
    let port = with_listener(|port| { connect(port); port }, 1);
    assert_ne!(port, 0);
}

fn bind_fails_on_a_port_in_use() {
    let second = run(|_| async {
        let first = NwListener::bind("127.0.0.1", 0).await.expect("bind");
        let second = NwListener::bind("127.0.0.1", first.port()).await;
        second.map(|listener| listener.port())
    });
    assert!(second.is_err(), "bound the same port twice: {second:?}");
}

fn localhost_binds_loopback() {
    let ok = run(|_| async {
        let listener = NwListener::bind("localhost", 0).await.expect("bind");
        let port = listener.port();
        let (tx, rx) = tokio::sync::oneshot::channel();
        std::thread::spawn(move || tx.send(TcpStream::connect(("127.0.0.1", port)).is_ok()));
        rx.await.unwrap()
    });
    assert!(ok);
}

fn echoes_small() {
    let got = with_listener(|port| {
        let mut stream = connect(port);
        stream.write_all(b"hello").unwrap();
        let mut reply = [0u8; 5];
        stream.read_exact(&mut reply).unwrap();
        reply
    }, 1);
    assert_eq!(&got, b"hello");
}

fn echoes_one_megabyte() {
    let (sent, got) = with_listener(|port| {
        let stream = connect(port);
        let payload: Vec<u8> = (0..1024 * 1024).map(|i| (i * 31 % 251) as u8).collect();
        let mut writer = stream.try_clone().unwrap();
        let to_send = payload.clone();
        // Writes in small pieces while reading: many regions arrive, and the echo must apply back-pressure.
        let sender = std::thread::spawn(move || {
            for chunk in to_send.chunks(1024) {
                writer.write_all(chunk).unwrap();
            }
            writer.shutdown(Shutdown::Write).unwrap();
        });
        let mut reader = stream;
        let mut reply = Vec::new();
        reader.read_to_end(&mut reply).unwrap();
        sender.join().unwrap();
        (payload, reply)
    }, 1);
    assert_eq!(got.len(), sent.len());
    assert!(got == sent, "the echoed megabyte differs");
}

fn read_sees_eof() {
    let saw_eof = run(|_| async {
        let mut listener = NwListener::bind("127.0.0.1", 0).await.expect("bind");
        let port = listener.port();
        std::thread::spawn(move || {
            let mut stream = connect(port);
            stream.write_all(b"x").unwrap();
            stream.shutdown(Shutdown::Write).unwrap();
            std::thread::sleep(Duration::from_millis(500)); // keep the read side open meanwhile
        });
        let mut io = listener.accept().await.expect("a connection");
        let first = read_some(&mut io).await.unwrap();
        let second = read_some(&mut io).await.unwrap();
        first == b"x" && second.is_empty()
    });
    assert!(saw_eof);
}

fn write_after_peer_closed_errors() {
    let failed = run(|_| async {
        let mut listener = NwListener::bind("127.0.0.1", 0).await.expect("bind");
        let port = listener.port();
        std::thread::spawn(move || drop(connect(port)));
        let mut io = listener.accept().await.expect("a connection");
        let _ = read_some(&mut io).await; // the close
        let start = Instant::now();
        while start.elapsed() < Duration::from_secs(1) {
            if write_all(&mut io, &[0u8; 4096]).await.is_err() {
                return true;
            }
            tokio::task::yield_now().await;
        }
        false
    });
    assert!(failed, "writes to a closed connection kept succeeding");
}

fn large_write_waits_for_the_peer() {
    const SIZE: usize = 8 * 1024 * 1024;
    let (write_took, received) = run(|_| async {
        let mut listener = NwListener::bind("127.0.0.1", 0).await.expect("bind");
        let port = listener.port();
        let (tx, rx) = tokio::sync::oneshot::channel();
        std::thread::spawn(move || {
            let mut stream = connect(port);
            std::thread::sleep(Duration::from_millis(400)); // not reading yet: the server must wait, not buffer it all
            let mut received = vec![0u8; SIZE];
            stream.read_exact(&mut received).unwrap();
            let _ = tx.send(received.iter().enumerate().all(|(i, b)| *b == (i % 251) as u8));
        });
        let mut io = listener.accept().await.expect("a connection");
        let payload: Vec<u8> = (0..SIZE).map(|i| (i % 251) as u8).collect();
        let start = Instant::now();
        // In 64 KiB pieces, as hyper writes: each piece is accepted or made to wait.
        for chunk in payload.chunks(64 * 1024) {
            write_all(&mut io, chunk).await.unwrap();
        }
        (start.elapsed(), rx.await.unwrap())
    });
    assert!(received, "the peer received different bytes");
    assert!(write_took >= Duration::from_millis(300), "8 MiB were accepted in {write_took:?} while the peer was not reading");
}

fn shutdown_after_a_large_write_delivers_everything() {
    const SIZE: usize = 4 * 1024 * 1024;
    let received = run(|_| async {
        let mut listener = NwListener::bind("127.0.0.1", 0).await.expect("bind");
        let port = listener.port();
        let (tx, rx) = tokio::sync::oneshot::channel();
        std::thread::spawn(move || {
            let mut stream = connect(port);
            let mut received = Vec::new();
            let _ = stream.read_to_end(&mut received);
            let _ = tx.send(received.len());
        });
        let mut io = listener.accept().await.expect("a connection");
        // What hyper does for `Connection: close`: write the response, shut down, drop.
        write_all(&mut io, &vec![7u8; SIZE]).await.unwrap();
        poll_fn(|cx| hyper::rt::Write::poll_shutdown(Pin::new(&mut io), cx)).await.unwrap();
        drop(io);
        rx.await.unwrap()
    });
    assert_eq!(received, SIZE, "the peer got {received} of {SIZE} bytes before the connection closed");
}

fn main() {
    support::run(&[
        ("binds_a_system_port", binds_a_system_port),
        ("bind_fails_on_a_port_in_use", bind_fails_on_a_port_in_use),
        ("localhost_binds_loopback", localhost_binds_loopback),
        ("echoes_small", echoes_small),
        ("echoes_one_megabyte", echoes_one_megabyte),
        ("read_sees_eof", read_sees_eof),
        ("write_after_peer_closed_errors", write_after_peer_closed_errors),
        ("large_write_waits_for_the_peer", large_write_waits_for_the_peer),
        ("shutdown_after_a_large_write_delivers_everything", shutdown_after_a_large_write_delivers_everything),
    ])
}
