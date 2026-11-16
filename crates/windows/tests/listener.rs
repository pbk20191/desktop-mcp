//! HTTP over the WinRT listener: an HTTP/1.0 client gets the whole answer, then the connection closes.
#[cfg(windows)]
fn main() {
    use desktop_mcp_core::guest::Activity;
    use desktop_mcp_core::http::{self, App, Security};
    use desktop_mcp_core::mcp::Server;
    use desktop_mcp_core::session::{SessionSettings, Sessions};
    use desktop_mcp_windows::desktop::WindowsDesktop;
    use desktop_mcp_windows::listener::SocketListener;
    use desktop_mcp_windows::main_loop::run;
    use std::io::{Read, Write};
    use std::rc::Rc;
    use std::time::{Duration, Instant};

    run(|main_loop| async move {
        let listener = SocketListener::bind("localhost", 0).await.unwrap();
        let port = listener.port();
        assert_ne!(port, 0);
        let desktop = Rc::new(WindowsDesktop::new());
        let app = Rc::new(App {
            make_server: Box::new(move || Server::new(desktop.clone(), &["Wait"])),
            activity: Activity::new(main_loop),
            sessions: Sessions::new(SessionSettings::default()),
            security: Security { auth_key: Some("k".into()), ..Security::default() },
        });
        let (done, finished) = tokio::sync::oneshot::channel();
        std::thread::spawn(move || {
            let started = Instant::now();
            let mut stream = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
            stream.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
            stream.write_all(b"GET /health HTTP/1.0\r\nHost: 127.0.0.1\r\n\r\n").unwrap();
            let mut answer = String::new();
            stream.read_to_string(&mut answer).unwrap();
            let _ = done.send((answer, started.elapsed()));
        });
        let (answer, took) = tokio::select! {
            _ = http::serve(listener, app) => panic!("the listener ended"),
            result = finished => result.unwrap(),
        };
        // The core has no /health body (it only needs no key), so this is a 404: whole, then end of stream.
        assert!(answer.starts_with("HTTP/1.0 404") && answer.ends_with("\r\n\r\n"), "{answer}");
        assert!(took < Duration::from_secs(2), "took {took:?}");
    });
    println!("listener: ok");
}

#[cfg(not(windows))]
fn main() {}
