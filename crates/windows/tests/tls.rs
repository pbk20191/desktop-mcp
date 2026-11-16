//! SChannel server credentials from a PEM certificate and key held in memory.
#[cfg(windows)]
fn main() {
    use desktop_mcp_windows::tls::TlsIdentity;
    use std::io::{Read, Write};

    for kind in ["rsa", "ec"] {
        let directory = std::env::temp_dir().join(format!("desktop-mcp-tls-{}-{kind}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        if kind == "ec" {
            let key = rcgen::KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256).unwrap();
            let certificate = rcgen::CertificateParams::new(vec!["localhost".to_string()]).unwrap().self_signed(&key).unwrap();
            std::fs::write(directory.join("cert.pem"), certificate.pem()).unwrap();
            std::fs::write(directory.join("key.pem"), key.serialize_pem()).unwrap();
        } else {
            // rcgen cannot make RSA keys; .NET can, in memory (no certificate store), as PKCS#1.
            let script = "$k=[Security.Cryptography.RSA]::Create(2048); $r=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost',$k,'SHA256',[Security.Cryptography.RSASignaturePadding]::Pkcs1); $c=$r.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1),[DateTimeOffset]::Now.AddDays(2)); Set-Content cert.pem $c.ExportCertificatePem(); Set-Content key.pem $k.ExportRSAPrivateKeyPem()";
            let status = std::process::Command::new("pwsh").args(["-NoProfile", "-Command", script]).current_dir(&directory).status().unwrap();
            assert!(status.success());
        }
        let identity = TlsIdentity::load(&directory.join("cert.pem"), &directory.join("key.pem")).unwrap_or_else(|error| panic!("{kind}: {error}"));

        // A synchronous handshake: the server on a thread, a schannel client that skips validation.
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let credentials = identity.credentials();
        let server = std::thread::spawn(move || {
            let (stream, _) = listener.accept().unwrap();
            let mut tls = schannel::tls_stream::Builder::new().accept(credentials, stream).map_err(|error| format!("{error:?}")).unwrap();
            tls.write_all(b"hello").unwrap();
            tls.flush().unwrap();
        });
        let client_credentials = schannel::schannel_cred::SchannelCred::builder().acquire(schannel::schannel_cred::Direction::Outbound).unwrap();
        let stream = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let mut client = schannel::tls_stream::Builder::new().domain("localhost").verify_callback(|_| Ok(())).connect(client_credentials, stream).map_err(|error| format!("{error:?}")).unwrap();
        let mut greeting = [0u8; 5];
        client.read_exact(&mut greeting).unwrap();
        assert_eq!(&greeting, b"hello", "{kind}");
        server.join().unwrap();

        // Through the core's HTTP app: a plain-text client first (its handshake fails alone), then a TLS one.
        let identity = std::rc::Rc::new(identity);
        desktop_mcp_windows::main_loop::run(|main_loop| async move {
            use desktop_mcp_core::http::{self, App, Security};
            let listener = desktop_mcp_windows::listener::SocketListener::bind("127.0.0.1", 0).await.unwrap();
            let port = listener.port();
            let listener = desktop_mcp_windows::tls_io::TlsListener::new(listener, Some(identity));
            let desktop = std::rc::Rc::new(desktop_mcp_windows::desktop::WindowsDesktop::new());
            let app = std::rc::Rc::new(App {
                make_server: Box::new(move || desktop_mcp_core::mcp::Server::new(desktop.clone(), &["Wait"])),
                activity: desktop_mcp_core::guest::Activity::new(main_loop),
                sessions: desktop_mcp_core::session::Sessions::new(Default::default()),
                security: Security { auth_key: Some("k".into()), ..Security::default() },
            });
            let (done, finished) = tokio::sync::oneshot::channel();
            std::thread::spawn(move || {
                let mut plain = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
                plain.write_all(b"GET / HTTP/1.1\r\nHost: x\r\n\r\n").unwrap();
                // Then end the sending side: the server sees the end of a handshake that never was.
                plain.shutdown(std::net::Shutdown::Write).unwrap();
                plain.set_read_timeout(Some(std::time::Duration::from_secs(5))).unwrap();
                let _ = plain.read(&mut [0u8; 64]);
                drop(plain);
                let credentials = schannel::schannel_cred::SchannelCred::builder().acquire(schannel::schannel_cred::Direction::Outbound).unwrap();
                let stream = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
                let mut client = schannel::tls_stream::Builder::new().domain("localhost").verify_callback(|_| Ok(())).connect(credentials, stream).unwrap();
                client.write_all(b"GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n").unwrap();
                let mut answer = String::new();
                let _ = client.read_to_string(&mut answer);
                let _ = done.send(answer);
            });
            let answer = tokio::select! {
                _ = http::serve(listener, app) => panic!("the listener ended"),
                answer = finished => answer.unwrap(),
            };
            assert!(answer.starts_with("HTTP/1.0 404"), "{answer}");
        });
        let _ = std::fs::remove_dir_all(&directory);
    }
    println!("tls: ok");
}

#[cfg(not(windows))]
fn main() {}
