mod support;
use desktop_mcp_core::io::Listener;
use desktop_mcp_macos::fd::{FdIo, UnixListener};
use desktop_mcp_macos::main_loop::run;
use std::future::poll_fn;
use std::io::{Read, Write};
use std::os::fd::IntoRawFd;
use std::os::unix::fs::PermissionsExt;
use std::os::unix::net::UnixStream;
use std::pin::Pin;
use std::time::{Duration, Instant};

async fn read_some(io: &mut FdIo) -> std::io::Result<Vec<u8>> {
    let mut storage = vec![0u8; 64 * 1024];
    let mut buffer = hyper::rt::ReadBuf::new(&mut storage);
    poll_fn(|cx| hyper::rt::Read::poll_read(Pin::new(&mut *io), cx, buffer.unfilled())).await?;
    Ok(buffer.filled().to_vec())
}
async fn write_all(io: &mut FdIo, mut bytes: &[u8]) -> std::io::Result<()> {
    while !bytes.is_empty() {
        let written = poll_fn(|cx| hyper::rt::Write::poll_write(Pin::new(&mut *io), cx, bytes)).await?;
        bytes = &bytes[written..];
    }
    Ok(())
}
async fn echo(mut io: FdIo) {
    while let Ok(bytes) = read_some(&mut io).await {
        if bytes.is_empty() || write_all(&mut io, &bytes).await.is_err() {
            break;
        }
    }
    let _ = poll_fn(|cx| hyper::rt::Write::poll_shutdown(Pin::new(&mut io), cx)).await;
}

/// A connected socket pair: an FdIo on one end, a std stream on the other.
fn pair() -> (FdIo, UnixStream) {
    let (ours, theirs) = UnixStream::pair().unwrap();
    (FdIo::socket(ours.into_raw_fd()), theirs)
}

fn socket_round_trip_of_a_megabyte() {
    let (sent, got) = run(|_| async {
        let (io, mut peer) = pair();
        tokio::task::spawn_local(echo(io));
        let (tx, rx) = tokio::sync::oneshot::channel();
        std::thread::spawn(move || {
            let payload: Vec<u8> = (0..1024 * 1024).map(|i| (i * 31 % 251) as u8).collect();
            let mut writer = peer.try_clone().unwrap();
            let to_send = payload.clone();
            let sender = std::thread::spawn(move || {
                for chunk in to_send.chunks(1000) {
                    writer.write_all(chunk).unwrap();
                }
                writer.shutdown(std::net::Shutdown::Write).unwrap();
            });
            let mut received = Vec::new();
            peer.read_to_end(&mut received).unwrap();
            sender.join().unwrap();
            let _ = tx.send((payload, received));
        });
        rx.await.unwrap()
    });
    assert!(got == sent, "{} of {} bytes came back intact", got.len(), sent.len());
}

fn read_sees_eof() {
    let saw = run(|_| async {
        let (mut io, mut peer) = pair();
        peer.write_all(b"x").unwrap();
        peer.shutdown(std::net::Shutdown::Write).unwrap();
        let first = read_some(&mut io).await.unwrap();
        let second = read_some(&mut io).await.unwrap();
        drop(peer);
        (first, second)
    });
    assert_eq!(saw, (b"x".to_vec(), Vec::new()));
}

fn write_after_the_peer_left_errors() {
    let failed = run(|main_loop| async move {
        main_loop.set_tick(desktop_mcp_core::guest::Tick::Fast); // the retry below sleeps on a tokio timer
        let (mut io, peer) = pair();
        drop(peer);
        let start = Instant::now();
        while start.elapsed() < Duration::from_secs(2) {
            if write_all(&mut io, &[0u8; 4096]).await.is_err() {
                main_loop.set_tick(desktop_mcp_core::guest::Tick::Off);
                return true;
            }
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        main_loop.set_tick(desktop_mcp_core::guest::Tick::Off);
        false
    });
    assert!(failed, "writes to a closed socket kept succeeding (and SIGPIPE must not kill the process)");
}

fn pipes_carry_the_stdio_binding() {
    let got = run(|_| async {
        let mut to_server = [0i32; 2];
        let mut from_server = [0i32; 2];
        unsafe {
            assert_eq!(libc::pipe(to_server.as_mut_ptr()), 0);
            assert_eq!(libc::pipe(from_server.as_mut_ptr()), 0);
        }
        let io = FdIo::pipes(to_server[0], from_server[1], true);
        tokio::task::spawn_local(echo(io));
        let (tx, rx) = tokio::sync::oneshot::channel();
        std::thread::spawn(move || {
            let mut input = unsafe { std::fs::File::from_raw_fd_checked(to_server[1]) };
            let mut output = unsafe { std::fs::File::from_raw_fd_checked(from_server[0]) };
            input.write_all(b"hello\n").unwrap();
            drop(input);
            let mut received = Vec::new();
            output.read_to_end(&mut received).unwrap();
            let _ = tx.send(received);
        });
        rx.await.unwrap()
    });
    assert_eq!(got, b"hello\n");
}

trait FromRawFdChecked {
    unsafe fn from_raw_fd_checked(fd: i32) -> std::fs::File;
}
impl FromRawFdChecked for std::fs::File {
    unsafe fn from_raw_fd_checked(fd: i32) -> std::fs::File {
        use std::os::fd::FromRawFd;
        assert!(fd >= 0);
        std::fs::File::from_raw_fd(fd)
    }
}

fn short_path(name: &str) -> std::path::PathBuf {
    // Socket paths are limited to 103 bytes; the usual temp directory is long.
    let directory = std::path::PathBuf::from(format!("/tmp/dmcp-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    directory.join(name)
}

fn a_socket_serves_its_own_user() {
    let path = short_path("serve.sock");
    let _ = std::fs::remove_file(&path);
    let (reply, mode) = run(|_| {
        let path = path.clone();
        async move {
            let mut listener = UnixListener::listen(&path, None).expect("listen");
            let mode = std::fs::metadata(&path).unwrap().permissions().mode() & 0o777;
            let (tx, rx) = tokio::sync::oneshot::channel();
            let client_path = path.clone();
            std::thread::spawn(move || {
                let mut stream = UnixStream::connect(client_path).unwrap();
                stream.write_all(b"ping").unwrap();
                let mut reply = [0u8; 4];
                stream.read_exact(&mut reply).unwrap();
                let _ = tx.send(reply);
            });
            let io = listener.accept().await.expect("a connection");
            tokio::task::spawn_local(echo(io));
            (rx.await.unwrap(), mode)
        }
    });
    assert_eq!(&reply, b"ping");
    assert_eq!(mode, 0o600, "only the same user may connect");
    assert!(!path.exists(), "the socket file is removed when the listener goes");
}

fn a_stale_socket_file_is_replaced() {
    let path = short_path("stale.sock");
    let _ = std::fs::remove_file(&path);
    drop(std::os::unix::net::UnixListener::bind(&path).unwrap()); // leaves the file, with no one listening
    assert!(path.exists());
    let ok = run(|_| {
        let path = path.clone();
        async move { UnixListener::listen(&path, None).is_ok() }
    });
    assert!(ok);
}

fn a_live_socket_is_not_taken_over() {
    let path = short_path("busy.sock");
    let _ = std::fs::remove_file(&path);
    let other = std::os::unix::net::UnixListener::bind(&path).unwrap();
    let error = run(|_| {
        let path = path.clone();
        async move { UnixListener::listen(&path, None).err() }
    });
    assert!(error.as_deref().unwrap_or_default().contains("another server is listening"), "{error:?}");
    drop(other);
    let _ = std::fs::remove_file(&path);
}

fn other_files_and_long_paths_are_refused() {
    let path = short_path("plain.file");
    std::fs::write(&path, "x").unwrap();
    let long = format!("/tmp/{}", "x".repeat(120));
    let (plain, too_long) = run(|_| {
        let (path, long) = (path.clone(), long.clone());
        async move { (UnixListener::listen(&path, None).err(), UnixListener::listen(std::path::Path::new(&long), None).err()) }
    });
    assert!(plain.as_deref().unwrap_or_default().contains("is not a socket"), "{plain:?}");
    assert!(too_long.as_deref().unwrap_or_default().contains("longer than"), "{too_long:?}");
    assert!(path.exists(), "a file that is not a socket is left alone");
}

fn main() {
    support::run(&[
        ("socket_round_trip_of_a_megabyte", socket_round_trip_of_a_megabyte),
        ("read_sees_eof", read_sees_eof),
        ("write_after_the_peer_left_errors", write_after_the_peer_left_errors),
        ("pipes_carry_the_stdio_binding", pipes_carry_the_stdio_binding),
        ("a_socket_serves_its_own_user", a_socket_serves_its_own_user),
        ("a_stale_socket_file_is_replaced", a_stale_socket_file_is_replaced),
        ("a_live_socket_is_not_taken_over", a_live_socket_is_not_taken_over),
        ("other_files_and_long_paths_are_refused", other_files_and_long_paths_are_refused),
    ])
}
