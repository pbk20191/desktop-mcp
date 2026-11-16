//! Scrape's network side on Windows: resolving, a WinRT socket, and HTML parsing.
#[cfg(windows)]
fn main() {
    use desktop_mcp_core::html::Node;
    use desktop_mcp_windows::main_loop::run;
    use desktop_mcp_windows::net::{resolve, SocketIo};
    use hyper::rt::{Read, ReadBuf, Write};
    use std::future::poll_fn;
    use std::io::{Read as _, Write as _};
    use std::mem::MaybeUninit;
    use std::pin::Pin;

    // A local echo server on a plain thread.
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    std::thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        let mut buffer = vec![0; 100_000];
        stream.read_exact(&mut buffer).unwrap();
        stream.write_all(&buffer).unwrap();
    });

    run(|_| async move {
        let addresses = resolve("localhost").await.unwrap();
        assert!(addresses.iter().any(|address| address.is_loopback()), "{addresses:?}");

        let mut io = SocketIo::connect("127.0.0.1", port, false).await.unwrap();
        let sent: Vec<u8> = (0..100_000u32).map(|i| i as u8).collect();
        let mut rest = &sent[..];
        while !rest.is_empty() {
            let written = poll_fn(|cx| Pin::new(&mut io).poll_write(cx, rest)).await.unwrap();
            rest = &rest[written..];
        }
        poll_fn(|cx| Pin::new(&mut io).poll_flush(cx)).await.unwrap();
        let mut received = Vec::new();
        loop {
            let mut storage = [MaybeUninit::<u8>::uninit(); 8192];
            let mut buf = ReadBuf::uninit(&mut storage);
            poll_fn(|cx| Pin::new(&mut io).poll_read(cx, buf.unfilled())).await.unwrap();
            if buf.filled().is_empty() {
                break;
            }
            received.extend_from_slice(buf.filled());
        }
        assert_eq!(received.len(), sent.len());
        assert!(received == sent);
    });

    let html = desktop_mcp_windows::html::parse(b"<p>a<b>c</b></p>", None).unwrap();
    assert_eq!(html.name, "html");
    let body = html.children.iter().find_map(|node| match node { Node::Element(e) if e.name == "body" => Some(e), _ => None }).unwrap();
    let Node::Element(p) = &body.children[0] else { panic!("p") };
    assert_eq!(p.children[0], Node::Text("a".into()));
    // EUC-KR bytes for 한글.
    let korean = desktop_mcp_windows::html::parse(&[b'<', b'p', b'>', 0xC7, 0xD1, 0xB1, 0xDB], Some("euc-kr")).unwrap();
    assert!(format!("{korean:?}").contains("한글"));
    println!("net: ok");
}

#[cfg(not(windows))]
fn main() {}
