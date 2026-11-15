mod support;
use desktop_mcp_core::io::Listener;
use desktop_mcp_macos::main_loop::run;
use desktop_mcp_macos::nw::NwListener;
use desktop_mcp_macos::tls::TlsIdentity;
use std::future::poll_fn;
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::process::Command;
use std::rc::Rc;

/// Runs /usr/bin/openssl in `directory`.
fn openssl(directory: &Path, arguments: &[&str]) {
    let status = Command::new("/usr/bin/openssl").args(arguments).current_dir(directory).stderr(std::process::Stdio::null()).status().unwrap();
    assert!(status.success(), "openssl {arguments:?}");
}

/// A self-signed certificate for localhost and its key, in the form `kind` names. EC keys use a named curve
/// (P-256), as mkcert, OpenSSL 3 and Let's Encrypt write them. /usr/bin/openssl is LibreSSL.
fn pair(directory: &Path, name: &str, kind: &str) -> (PathBuf, PathBuf) {
    let (cert, key) = (format!("{name}.cert.pem"), format!("{name}.key.pem"));
    if kind.starts_with("ec") {
        openssl(directory, &["ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", &key]);
    } else {
        openssl(directory, &["genrsa", "-out", &key, "2048"]);
    }
    let converted = format!("{key}.converted");
    match kind {
        "rsa-pkcs8" | "ec-pkcs8" => openssl(directory, &["pkcs8", "-topk8", "-nocrypt", "-in", &key, "-out", &converted]),
        "rsa-pkcs1" => openssl(directory, &["rsa", "-in", &key, "-out", &converted]),
        _ => {}
    }
    if directory.join(&converted).exists() {
        std::fs::rename(directory.join(&converted), directory.join(&key)).unwrap();
    }
    openssl(directory, &["req", "-x509", "-new", "-key", &key, "-out", &cert, "-days", "2", "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1"]);
    let first_line = std::fs::read_to_string(directory.join(&key)).unwrap().lines().next().unwrap().to_string();
    let expected = match kind {
        "rsa-pkcs1" => "-----BEGIN RSA PRIVATE KEY-----",
        "ec-sec1" => "-----BEGIN EC PRIVATE KEY-----",
        _ => "-----BEGIN PRIVATE KEY-----",
    };
    assert_eq!(first_line, expected, "{kind}");
    (directory.join(cert), directory.join(key))
}

fn loads_every_common_key_form() {
    let directory = tempfile::tempdir().unwrap();
    for kind in ["rsa-pkcs8", "rsa-pkcs1", "ec-sec1", "ec-pkcs8"] {
        let (cert, key) = pair(directory.path(), kind, kind);
        if let Err(error) = TlsIdentity::load(&cert, &key) {
            panic!("{kind}: {error}");
        }
    }
}

fn a_key_of_another_certificate_is_refused() {
    let directory = tempfile::tempdir().unwrap();
    let (cert, _) = pair(directory.path(), "one", "rsa-pkcs8");
    let (_, other_key) = pair(directory.path(), "two", "rsa-pkcs8");
    let error = TlsIdentity::load(&cert, &other_key).err().expect("a mismatched pair is refused");
    assert!(error.contains("does not match"), "{error}");
}

fn unreadable_files_are_named() {
    let directory = tempfile::tempdir().unwrap();
    let (cert, key) = pair(directory.path(), "one", "rsa-pkcs8");
    let missing = directory.path().join("missing.pem");
    let error = TlsIdentity::load(&missing, &key).err().unwrap();
    assert!(error.contains("missing.pem"), "{error}");
    let garbage = directory.path().join("garbage.pem");
    std::fs::write(&garbage, "not a key").unwrap();
    let error = TlsIdentity::load(&cert, &garbage).err().unwrap();
    assert!(error.contains("garbage.pem"), "{error}");
    let error = TlsIdentity::load(&garbage, &key).err().unwrap();
    assert!(error.contains("garbage.pem"), "{error}");
}

/// Answers one HTTP request with "ok" and closes.
async fn answer_once(listener: &mut NwListener) {
    let mut io = listener.accept().await.expect("a connection");
    let mut storage = vec![0u8; 4096];
    let mut buffer = hyper::rt::ReadBuf::new(&mut storage);
    poll_fn(|cx| hyper::rt::Read::poll_read(Pin::new(&mut io), cx, buffer.unfilled())).await.unwrap();
    let reply = b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
    poll_fn(|cx| hyper::rt::Write::poll_write(Pin::new(&mut io), cx, reply)).await.unwrap();
    poll_fn(|cx| hyper::rt::Write::poll_shutdown(Pin::new(&mut io), cx)).await.unwrap();
}

fn curl(arguments: Vec<String>) -> (i32, String) {
    let output = Command::new("/usr/bin/curl").args(arguments).output().unwrap();
    (output.status.code().unwrap_or(-1), String::from_utf8_lossy(&output.stdout).into_owned())
}

fn serves_tls_that_a_client_verifies() {
    let directory = tempfile::tempdir().unwrap();
    for kind in ["rsa-pkcs8", "ec-pkcs8"] {
        let (cert, key) = pair(directory.path(), kind, kind);
        let identity = Rc::new(TlsIdentity::load(&cert, &key).unwrap());
        let (verified, untrusted) = run(move |_| async move {
            let mut listener = NwListener::bind_with("127.0.0.1", 0, Some(identity)).await.expect("bind");
            let url = format!("https://localhost:{}/", listener.port());
            let (tx, rx) = tokio::sync::oneshot::channel();
            let cert = cert.clone();
            std::thread::spawn(move || {
                let verified = curl(vec!["-s".into(), "--max-time".into(), "5".into(), "--cacert".into(), cert.to_string_lossy().into(), url.clone()]);
                let untrusted = curl(vec!["-s".into(), "--max-time".into(), "5".into(), url]);
                let _ = tx.send((verified, untrusted));
            });
            answer_once(&mut listener).await;
            // The untrusted attempt fails during the handshake, after the connection was accepted.
            let accept_and_drop = async {
                let _ = listener.accept().await;
            };
            tokio::select! {
                result = rx => result.unwrap(),
                _ = async { accept_and_drop.await; std::future::pending::<()>().await } => unreachable!(),
            }
        });
        assert_eq!(verified, (0, "ok".to_string()), "{kind}: curl trusting the certificate");
        assert_eq!(untrusted.0, 60, "{kind}: curl without the certificate must fail verification (exit 60), so TLS is really on");
    }
}

fn main() {
    support::run(&[
        ("loads_every_common_key_form", loads_every_common_key_form),
        ("a_key_of_another_certificate_is_refused", a_key_of_another_certificate_is_refused),
        ("unreadable_files_are_named", unreadable_files_are_named),
        ("serves_tls_that_a_client_verifies", serves_tls_that_a_client_verifies),
    ])
}
