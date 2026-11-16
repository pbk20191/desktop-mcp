//! HTTPS from PEM files (`--ssl-certfile`/`--ssl-keyfile`). Holds for both ports; the client is curl.
use desktop_mcp_parity::{run_to_exit, ServerUnderTest, DEFAULT_KEY};
use std::path::{Path, PathBuf};
use std::process::Command;

/// A self-signed certificate for localhost and its PKCS#8 key (what mkcert and the `auth --with-tls` fallback write).
fn certificate(name: &str, kind: &str) -> (PathBuf, PathBuf) {
    let directory = std::env::temp_dir().join(format!("desktop-mcp-parity-tls-{}-{name}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    #[cfg(windows)]
    {
        // .NET makes the pair in memory (no certificate store); the key is written as PKCS#8.
        let algorithm = if kind == "ec" { "$k=[Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256); $r=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost',$k,'SHA256')" } else { "$k=[Security.Cryptography.RSA]::Create(2048); $r=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost',$k,'SHA256',[Security.Cryptography.RSASignaturePadding]::Pkcs1)" };
        let script = format!("{algorithm}; $s=[Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new(); $s.AddDnsName('localhost'); $s.AddIpAddress([Net.IPAddress]::Loopback); $r.CertificateExtensions.Add($s.Build()); $c=$r.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1),[DateTimeOffset]::Now.AddDays(2)); Set-Content cert.pem $c.ExportCertificatePem(); Set-Content key.pem $k.ExportPkcs8PrivateKeyPem()");
        assert!(Command::new("pwsh").args(["-NoProfile", "-Command", &script]).current_dir(&directory).status().unwrap().success(), "pwsh certificate");
        return (directory.join("cert.pem"), directory.join("key.pem"));
    }
    #[allow(unreachable_code)]
    let openssl = |arguments: &[&str]| assert!(Command::new("/usr/bin/openssl").args(arguments).current_dir(&directory).stderr(std::process::Stdio::null()).status().unwrap().success(), "openssl {arguments:?}");
    if kind == "ec" {
        openssl(&["ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", "raw.pem"]);
    } else {
        openssl(&["genrsa", "-out", "raw.pem", "2048"]);
    }
    openssl(&["pkcs8", "-topk8", "-nocrypt", "-in", "raw.pem", "-out", "key.pem"]);
    openssl(&["req", "-x509", "-new", "-key", "key.pem", "-out", "cert.pem", "-days", "2", "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1"]);
    (directory.join("cert.pem"), directory.join("key.pem"))
}

fn curl(arguments: &[&str]) -> (i32, String) {
    // On Windows, the system curl (SChannel); a self-signed certificate has no revocation list to check.
    let (program, extra): (&str, &[&str]) = if cfg!(windows) { (r"C:\Windows\System32\curl.exe", &["--ssl-no-revoke"]) } else { ("/usr/bin/curl", &[]) };
    let output = Command::new(program).args(["-s", "--max-time", "10"]).args(extra).args(arguments).output().unwrap();
    (output.status.code().unwrap_or(-1), String::from_utf8_lossy(&output.stdout).into_owned())
}

fn serves(cert: &Path, key: &Path) {
    let server = ServerUnderTest::start_with(&["--ssl-certfile", cert.to_str().unwrap(), "--ssl-keyfile", key.to_str().unwrap()]);
    let url = format!("https://localhost:{}/mcp", server.port);
    let authorization = format!("Authorization: Bearer {DEFAULT_KEY}");
    let request = ["-X", "POST", "-H", "Accept: application/json", "-H", "Content-Type: application/json", "-H", &authorization, "-d", r#"{"jsonrpc":"2.0","id":1,"method":"tools/list"}"#];
    let mut trusted = vec!["--cacert", cert.to_str().unwrap()];
    trusted.extend(request);
    trusted.push(&url);
    let (status, body) = curl(&trusted);
    assert_eq!(status, 0, "curl trusting the certificate: {body}");
    let answer: serde_json::Value = serde_json::from_str(&body).unwrap_or_else(|_| panic!("not JSON: {body:?}"));
    assert!(answer["result"]["tools"].as_array().unwrap().iter().any(|tool| tool["name"] == "Wait"));
    let mut untrusted = request.to_vec();
    untrusted.push(&url);
    assert_eq!(curl(&untrusted).0, 60, "an unknown certificate fails verification");
    let plain = format!("http://localhost:{}/mcp", server.port);
    let mut cleartext = request.to_vec();
    cleartext.push(&plain);
    assert_ne!(curl(&cleartext).0, 0, "plain HTTP is not served on the HTTPS port");
}

#[test]
fn https_with_an_rsa_key() {
    let (cert, key) = certificate("rsa", "rsa");
    serves(&cert, &key);
}

#[test]
fn https_with_an_ec_key() {
    let (cert, key) = certificate("ec", "ec");
    serves(&cert, &key);
}

#[test]
fn a_missing_key_file_is_refused() {
    let (cert, _) = certificate("missing", "rsa");
    let (status, stderr) = run_to_exit(&["serve", "--transport", "streamable-http", "--port", "0", "--auth-key", "k", "--ssl-certfile", cert.to_str().unwrap(), "--ssl-keyfile", "/nonexistent/key.pem"]);
    assert!(!status.success());
    assert!(stderr.contains("key.pem"), "{stderr}");
}
