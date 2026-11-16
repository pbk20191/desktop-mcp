//! Rewrapping PKCS#1 (RSA) and SEC1 (EC) private keys as PKCS#8, the one form CNG imports for both.

/// A DER length.
fn length(len: usize) -> Vec<u8> {
    if len < 0x80 {
        return vec![len as u8];
    }
    let bytes: Vec<u8> = len.to_be_bytes().into_iter().skip_while(|byte| *byte == 0).collect();
    let mut out = vec![0x80 | bytes.len() as u8];
    out.extend(bytes);
    out
}

fn tlv(tag: u8, content: &[u8]) -> Vec<u8> {
    let mut out = vec![tag];
    out.extend(length(content.len()));
    out.extend_from_slice(content);
    out
}

/// PrivateKeyInfo { version 0, algorithm, OCTET STRING key }.
fn private_key_info(algorithm: &[u8], key: &[u8]) -> Vec<u8> {
    let mut body = vec![0x02, 0x01, 0x00];
    body.extend(tlv(0x30, algorithm));
    body.extend(tlv(0x04, key));
    tlv(0x30, &body)
}

/// RSAPrivateKey → PKCS#8 (rsaEncryption, NULL parameters).
pub fn rsa_pkcs1_to_pkcs8(pkcs1: &[u8]) -> Vec<u8> {
    const RSA_ENCRYPTION: [u8; 13] = [0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00];
    private_key_info(&RSA_ENCRYPTION, pkcs1)
}

/// One TLV at the start of `der`: (tag, content, rest).
fn read_tlv(der: &[u8]) -> Option<(u8, &[u8], &[u8])> {
    let (&tag, rest) = der.split_first()?;
    let (&first, rest) = rest.split_first()?;
    let (len, rest) = if first < 0x80 {
        (first as usize, rest)
    } else {
        let count = (first & 0x7F) as usize;
        if count == 0 || count > 4 || rest.len() < count {
            return None;
        }
        (rest[..count].iter().fold(0usize, |len, byte| len << 8 | *byte as usize), &rest[count..])
    };
    (rest.len() >= len).then(|| (tag, &rest[..len], &rest[len..]))
}

/// ECPrivateKey (with its curve in the `[0]` parameters) → PKCS#8 (id-ecPublicKey, that curve).
pub fn ec_sec1_to_pkcs8(sec1: &[u8]) -> Option<Vec<u8>> {
    let (0x30, mut fields, _) = read_tlv(sec1)? else { return None };
    let mut curve = None;
    while !fields.is_empty() {
        let (tag, content, rest) = read_tlv(fields)?;
        if tag == 0xA0 {
            let (0x06, _, _) = read_tlv(content)? else { return None };
            curve = Some(content.to_vec());
        }
        fields = rest;
    }
    const EC_PUBLIC_KEY: [u8; 9] = [0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01];
    let mut algorithm = EC_PUBLIC_KEY.to_vec();
    algorithm.extend(curve?);
    Some(private_key_info(&algorithm, sec1))
}
