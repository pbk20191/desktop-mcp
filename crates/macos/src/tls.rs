//! A TLS identity from PEM files, for Network.framework, without the keychain.
//!
//! `SecItemImport` (no keychain given) reads the certificate and the key into memory, `SecIdentityCreate` pairs
//! them, and `sec_identity_create` wraps the pair for `sec_protocol_options_set_local_identity`. All public
//! Security.framework API (`SecIdentityCreate` since macOS 10.12), through `objc2-security`.
use base64::Engine;
use objc2_core_foundation::{CFArray, CFData, CFRetained, CFType};
use objc2_security::{
    sec_identity, sec_identity_t, sec_protocol_options, sec_protocol_options_t, sec_release, SecCertificate, SecExternalFormat, SecExternalItemType, SecIdentity,
    SecItemImport, SecItemImportExportFlags, SecKey,
};
use std::path::Path;
use std::ptr::NonNull;

/// A certificate and its private key, ready for Network.framework's TLS (`sec_identity_t`).
pub struct TlsIdentity {
    identity: NonNull<sec_identity>,
}

impl Drop for TlsIdentity {
    fn drop(&mut self) {
        unsafe { sec_release(self.identity.as_ptr().cast()) }
    }
}

impl TlsIdentity {
    /// Reads a PEM certificate and its PEM private key (PKCS#8, PKCS#1 or SEC1; RSA or EC).
    pub fn load(certificate: &Path, private_key: &Path) -> Result<TlsIdentity, String> {
        let read = |path: &Path| std::fs::read(path).map_err(|error| format!("cannot read {}: {error}", path.display()));
        let certificate_pem = read(certificate)?;
        let key_pem = read(private_key)?;
        let certificate_item: CFRetained<SecCertificate> = import(&certificate_pem).ok_or_else(|| format!("{} holds no PEM certificate", certificate.display()))?;
        // An EC key in PKCS#8 is not understood by SecItemImport; the same key in SEC1 form is.
        let key_item: CFRetained<SecKey> = import(&key_pem)
            .or_else(|| ec_pkcs8_to_sec1(&key_pem).and_then(|sec1| import(&sec1)))
            .ok_or_else(|| format!("{} holds no PEM private key that macOS can read (RSA or EC, unencrypted)", private_key.display()))?;
        if !same_public_key(&certificate_item, &key_item) {
            return Err(format!("the private key in {} does not match the certificate in {}", private_key.display(), certificate.display()));
        }
        let identity = unsafe { SecIdentity::new(None, &certificate_item, &key_item) }.ok_or("SecIdentityCreate refused the certificate and key")?;
        let identity = NonNull::new(unsafe { sec_identity::create(&identity) }).ok_or("sec_identity_create failed")?;
        Ok(TlsIdentity { identity })
    }

    fn as_ptr(&self) -> sec_identity_t {
        self.identity.as_ptr()
    }
}

/// The first item of type `T` that `SecItemImport` finds in `pem`, kept in memory (no keychain is given).
fn import<T: objc2_core_foundation::ConcreteType>(pem: &[u8]) -> Option<CFRetained<T>> {
    let data = CFData::from_bytes(pem);
    let (mut format, mut item_type, mut items) = (SecExternalFormat::FormatUnknown, SecExternalItemType::ItemTypeUnknown, std::ptr::null());
    let status = unsafe { SecItemImport(&data, None, &mut format, &mut item_type, SecItemImportExportFlags(0), std::ptr::null(), None, &mut items) };
    let items: CFRetained<CFArray> = unsafe { CFRetained::from_raw(NonNull::new(items.cast_mut())?) };
    if status != 0 {
        return None;
    }
    (0..items.count()).find_map(|index| {
        let item = unsafe { items.value_at_index(index) } as *const CFType;
        unsafe { item.as_ref() }?.downcast_ref::<T>().map(|item| item.retain())
    })
}

/// Whether the certificate carries the public half of `key`.
fn same_public_key(certificate: &SecCertificate, key: &SecKey) -> bool {
    let external = |key: Option<CFRetained<SecKey>>| key.and_then(|key| unsafe { key.external_representation(std::ptr::null_mut()) });
    match (external(unsafe { certificate.key() }), external(unsafe { key.public_key() })) {
        (Some(a), Some(b)) => a == b,
        _ => false,
    }
}

/// `id-ecPublicKey`, 1.2.840.10045.2.1, as a DER object identifier.
const EC_PUBLIC_KEY: &[u8] = &[0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01];

/// An EC key in PKCS#8 (`BEGIN PRIVATE KEY`) rewritten as SEC1 (`BEGIN EC PRIVATE KEY`). PKCS#8 names the
/// curve in its algorithm identifier and leaves it out of the key inside, so the curve is moved in.
fn ec_pkcs8_to_sec1(pem: &[u8]) -> Option<Vec<u8>> {
    let text = std::str::from_utf8(pem).ok()?;
    let body = text.split("-----BEGIN PRIVATE KEY-----").nth(1)?.split("-----END PRIVATE KEY-----").next()?;
    let der = base64::engine::general_purpose::STANDARD.decode(body.split_whitespace().collect::<String>()).ok()?;
    // PrivateKeyInfo ::= SEQUENCE { version INTEGER, algorithm SEQUENCE { OID, parameters }, privateKey OCTET STRING }
    let (_, info) = tlv(&der, 0x30)?;
    let (rest, _version) = tlv(info, 0x02)?;
    let (rest, algorithm) = tlv(rest, 0x30)?;
    let (_, inner) = tlv(rest, 0x04)?;
    let curve = algorithm.strip_prefix(EC_PUBLIC_KEY)?;
    // ECPrivateKey ::= SEQUENCE { version, privateKey OCTET STRING, [0] parameters OPTIONAL, [1] publicKey OPTIONAL }
    let (_, key) = tlv(inner, 0x30)?;
    let (after_version, _) = tlv(key, 0x02)?;
    let (after_private, _) = tlv(after_version, 0x04)?;
    let mut fields = key[..key.len() - after_private.len()].to_vec();
    if after_private.first() != Some(&0xa0) {
        fields.extend(encode(0xa0, curve));
    }
    fields.extend_from_slice(after_private);
    let sec1 = encode(0x30, &fields);
    let base64 = base64::engine::general_purpose::STANDARD.encode(sec1);
    let lines: Vec<&str> = base64.as_bytes().chunks(64).map(|line| std::str::from_utf8(line).expect("base64 is ASCII")).collect();
    Some(format!("-----BEGIN EC PRIVATE KEY-----\n{}\n-----END EC PRIVATE KEY-----\n", lines.join("\n")).into_bytes())
}

/// Reads one DER element of type `tag`: (what follows it, its contents).
fn tlv(input: &[u8], tag: u8) -> Option<(&[u8], &[u8])> {
    let (&found, rest) = input.split_first()?;
    if found != tag {
        return None;
    }
    let (&first, rest) = rest.split_first()?;
    let (length, rest) = if first < 0x80 {
        (usize::from(first), rest)
    } else {
        let count = usize::from(first & 0x7f);
        if count == 0 || count > 4 || rest.len() < count {
            return None;
        }
        (rest[..count].iter().fold(0usize, |length, byte| length << 8 | usize::from(*byte)), &rest[count..])
    };
    (rest.len() >= length).then(|| (&rest[length..], &rest[..length]))
}

fn encode(tag: u8, contents: &[u8]) -> Vec<u8> {
    let mut out = vec![tag];
    let length = contents.len();
    if length < 0x80 {
        out.push(length as u8);
    } else {
        let bytes: Vec<u8> = length.to_be_bytes().into_iter().skip_while(|byte| *byte == 0).collect();
        out.push(0x80 | bytes.len() as u8);
        out.extend(bytes);
    }
    out.extend_from_slice(contents);
    out
}

/// What Network.framework's TLS options get: the identity and ALPN `http/1.1`.
pub(crate) fn configure(options: sec_protocol_options_t, identity: &TlsIdentity) {
    unsafe {
        sec_protocol_options::set_local_identity(options, identity.as_ptr());
        sec_protocol_options::add_tls_application_protocol(options, NonNull::from(c"http/1.1").cast());
    }
}

/// Releases a `sec_*` object this code owns.
pub(crate) fn release(object: *mut std::ffi::c_void) {
    unsafe { sec_release(object) }
}
