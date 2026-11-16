//! HTTPS server credentials for SChannel from a PEM certificate and key. The certificate stays in memory (no
//! certificate store); the key has to be stored for SChannel to use it (see `TemporaryKey`), only while the server runs.
use schannel::cert_context::CertContext;
use schannel::schannel_cred::{Direction, SchannelCred};
use std::ffi::c_void;
use std::path::Path;
use windows::core::{w, PCWSTR};
use windows::Win32::Security::Cryptography::{
    BCryptBuffer, BCryptBufferDesc, NCryptDeleteKey, NCryptEnumKeys, NCryptFreeBuffer, NCryptFreeObject, NCryptImportKey, NCryptKeyName, NCryptOpenKey, NCryptOpenStorageProvider,
    BCRYPTBUFFER_VERSION, CERT_KEY_SPEC, MS_KEY_STORAGE_PROVIDER, NCRYPTBUFFER_PKCS_KEY_NAME, NCRYPT_FLAGS, NCRYPT_HANDLE, NCRYPT_KEY_HANDLE, NCRYPT_OVERWRITE_KEY_FLAG, NCRYPT_PROV_HANDLE,
};

pub struct TlsIdentity {
    credentials: SchannelCred,
    _key: TemporaryKey,
}

const PROVIDER: &str = "Microsoft Software Key Storage Provider";
/// The start of every key container this server makes; the rest is `<pid>-<random>`.
const PREFIX: &str = "desktop-mcp-tls-";

/// A private key stored in this user's key storage under a name of its own, deleted when dropped. Keys a crashed
/// server left behind are deleted by the next one (`remove_leftovers`).
struct TemporaryKey {
    name: String,
}

fn open_provider() -> windows::core::Result<NCRYPT_PROV_HANDLE> {
    let mut provider = NCRYPT_PROV_HANDLE::default();
    unsafe { NCryptOpenStorageProvider(&mut provider, MS_KEY_STORAGE_PROVIDER, 0) }?;
    Ok(provider)
}

fn delete_key(provider: NCRYPT_PROV_HANDLE, name: &str) {
    let wide: Vec<u16> = name.encode_utf16().chain(Some(0)).collect();
    let mut key = NCRYPT_KEY_HANDLE::default();
    if unsafe { NCryptOpenKey(provider, &mut key, PCWSTR(wide.as_ptr()), CERT_KEY_SPEC(0), NCRYPT_FLAGS(0)) }.is_ok() {
        // Deleting frees the handle too.
        unsafe {
            let _ = NCryptDeleteKey(key, 0);
        }
    }
}

impl TemporaryKey {
    fn import(pkcs8: &[u8]) -> windows::core::Result<TemporaryKey> {
        let provider = open_provider()?;
        remove_leftovers(provider);
        let mut random = [0u8; 8];
        let _ = getrandom::fill(&mut random);
        let name = format!("{PREFIX}{}-{}", std::process::id(), random.iter().map(|byte| format!("{byte:02x}")).collect::<String>());
        let mut wide: Vec<u16> = name.encode_utf16().chain(Some(0)).collect();
        let mut buffer = BCryptBuffer { cbBuffer: (wide.len() * 2) as u32, BufferType: NCRYPTBUFFER_PKCS_KEY_NAME, pvBuffer: wide.as_mut_ptr() as *mut c_void };
        let parameters = BCryptBufferDesc { ulVersion: BCRYPTBUFFER_VERSION, cBuffers: 1, pBuffers: &mut buffer };
        let mut key = NCRYPT_KEY_HANDLE::default();
        // Named: the key is persisted (for this user only), which is what lets SChannel open it.
        let imported = unsafe { NCryptImportKey(provider, None, w!("PKCS8_PRIVATEKEY"), Some(&parameters), &mut key, pkcs8, NCRYPT_OVERWRITE_KEY_FLAG) };
        unsafe {
            if imported.is_ok() {
                let _ = NCryptFreeObject(NCRYPT_HANDLE(key.0));
            }
            let _ = NCryptFreeObject(NCRYPT_HANDLE(provider.0));
        }
        imported.map(|()| TemporaryKey { name })
    }
}

impl Drop for TemporaryKey {
    fn drop(&mut self) {
        if let Ok(provider) = open_provider() {
            delete_key(provider, &self.name);
            unsafe {
                let _ = NCryptFreeObject(NCRYPT_HANDLE(provider.0));
            }
        }
    }
}

/// Deletes the keys of servers that are no longer running.
fn remove_leftovers(provider: NCRYPT_PROV_HANDLE) {
    let mut names = Vec::new();
    let mut state: *mut c_void = std::ptr::null_mut();
    loop {
        let mut item: *mut NCryptKeyName = std::ptr::null_mut();
        if unsafe { NCryptEnumKeys(provider, PCWSTR::null(), &mut item, &mut state, NCRYPT_FLAGS(0)) }.is_err() || item.is_null() {
            break;
        }
        names.push(unsafe { (*item).pszName.to_string() }.unwrap_or_default());
        unsafe {
            let _ = NCryptFreeBuffer(item as *mut c_void);
        }
    }
    if !state.is_null() {
        unsafe {
            let _ = NCryptFreeBuffer(state);
        }
    }
    for name in names {
        let Some(pid) = name.strip_prefix(PREFIX).and_then(|rest| rest.split('-').next()).and_then(|pid| pid.parse::<u32>().ok()) else { continue };
        if !process_running(pid) {
            delete_key(provider, &name);
        }
    }
}

fn process_running(pid: u32) -> bool {
    use windows::Win32::System::Threading::{GetExitCodeProcess, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};
    const STILL_ACTIVE: u32 = 259;
    let Ok(process) = (unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) }) else { return false };
    let mut code = 0u32;
    let running = unsafe { GetExitCodeProcess(process, &mut code) }.is_ok() && code == STILL_ACTIVE;
    unsafe {
        let _ = windows::Win32::Foundation::CloseHandle(process);
    }
    running
}

/// The DER of the first PEM block whose label is one of `labels`, and that label.
fn pem_block<'a>(text: &str, labels: &[&'a str]) -> Option<(&'a str, Vec<u8>)> {
    use base64::Engine;
    for label in labels {
        let begin = format!("-----BEGIN {label}-----");
        let end = format!("-----END {label}-----");
        if let Some(start) = text.find(&begin) {
            let body = &text[start + begin.len()..];
            let body = &body[..body.find(&end)?];
            let compact: String = body.chars().filter(|c| !c.is_whitespace()).collect();
            return base64::engine::general_purpose::STANDARD.decode(compact).ok().map(|der| (*label, der));
        }
    }
    None
}

impl TlsIdentity {
    /// The certificate (PEM) and its private key (PEM: PKCS#8, PKCS#1 RSA, or SEC1 EC).
    pub fn load(certificate: &Path, private_key: &Path) -> Result<TlsIdentity, String> {
        let read = |path: &Path| std::fs::read_to_string(path).map_err(|error| format!("cannot read {}: {error}", path.display()));
        let (certificate_pem, key_pem) = (read(certificate)?, read(private_key)?);
        let (_, certificate_der) = pem_block(&certificate_pem, &["CERTIFICATE"]).ok_or_else(|| format!("no certificate in {}", certificate.display()))?;
        let (label, key_der) = pem_block(&key_pem, &["PRIVATE KEY", "RSA PRIVATE KEY", "EC PRIVATE KEY"]).ok_or_else(|| format!("no private key in {}", private_key.display()))?;
        let pkcs8 = match label {
            "PRIVATE KEY" => key_der,
            "RSA PRIVATE KEY" => crate::tls_keys::rsa_pkcs1_to_pkcs8(&key_der),
            _ => crate::tls_keys::ec_sec1_to_pkcs8(&key_der).ok_or_else(|| format!("unsupported EC key in {}", private_key.display()))?,
        };

        let certificate = CertContext::new(&certificate_der).map_err(|error| format!("cannot read the certificate in {}: {error}", certificate.display()))?;
        // SChannel signs in lsass, which cannot reach a key that lives only in this process, so the key is stored
        // under a name of its own in this user's key storage for as long as the server runs, then deleted.
        let key = TemporaryKey::import(&pkcs8).map_err(|error| format!("cannot use the private key in {}: {error}", private_key.display()))?;
        certificate.set_key_prov_info().container(&key.name).provider(PROVIDER).set().map_err(|error| format!("cannot attach the private key: {error}"))?;
        let credentials = SchannelCred::builder().cert(certificate).acquire(Direction::Inbound).map_err(|error| format!("SChannel refused the certificate: {error}"))?;
        Ok(TlsIdentity { credentials, _key: key })
    }

    /// The server credentials (shared, cheap to clone).
    pub fn credentials(&self) -> SchannelCred {
        self.credentials.clone()
    }
}
