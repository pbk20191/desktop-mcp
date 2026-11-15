//! Small security helpers: IP networks for the allowlist, loopback hosts, tokens, constant-time comparison.
use base64::Engine;
use std::fmt;
use std::net::IpAddr;
use std::str::FromStr;

/// An IPv4 or IPv6 network: an address with a prefix length. Host bits are cleared, as Python's
/// `ip_network(strict=False)` does.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct IpNetwork {
    address: IpAddr,
    prefix: u8,
}

impl IpNetwork {
    pub fn contains(&self, address: IpAddr) -> bool {
        // An IPv4 client seen through a dual-stack listener arrives as ::ffff:a.b.c.d.
        let address = match address {
            IpAddr::V6(v6) => v6.to_ipv4_mapped().map(IpAddr::V4).unwrap_or(address),
            v4 => v4,
        };
        match (self.address, address) {
            (IpAddr::V4(_), IpAddr::V4(_)) | (IpAddr::V6(_), IpAddr::V6(_)) => mask(address, self.prefix) == self.address,
            _ => false,
        }
    }

    /// Parses every entry, reporting all the bad ones at once. Blank entries are skipped.
    pub fn parse_list<S: AsRef<str>>(entries: &[S]) -> Result<Vec<IpNetwork>, String> {
        let mut networks = Vec::new();
        let mut errors = Vec::new();
        for entry in entries {
            let value = entry.as_ref().trim();
            if value.is_empty() {
                continue;
            }
            match value.parse() {
                Ok(network) => networks.push(network),
                Err(error) => errors.push(format!("'{}': {error}", entry.as_ref())),
            }
        }
        if errors.is_empty() {
            Ok(networks)
        } else {
            Err(format!("Invalid IP allowlist entries: {}", errors.join("; ")))
        }
    }
}

fn mask(address: IpAddr, prefix: u8) -> IpAddr {
    match address {
        IpAddr::V4(v4) => {
            let bits = u32::from(v4);
            IpAddr::V4((if prefix == 0 { 0 } else { bits & (u32::MAX << (32 - u32::from(prefix))) }).into())
        }
        IpAddr::V6(v6) => {
            let bits = u128::from(v6);
            IpAddr::V6((if prefix == 0 { 0 } else { bits & (u128::MAX << (128 - u32::from(prefix))) }).into())
        }
    }
}

/// `"10.0.0.0/8"`, `"192.168.1.5"` (a /32), `"2001:db8::/32"` or `"::1"` (a /128).
fn parse_address(text: &str) -> Option<IpAddr> {
    // A zone id (`fe80::1%en0`) does not take part in matching.
    text.split('%').next()?.trim_start_matches('[').trim_end_matches(']').parse().ok()
}

impl FromStr for IpNetwork {
    type Err = String;

    fn from_str(text: &str) -> Result<IpNetwork, String> {
        let (address, prefix) = match text.split_once('/') {
            Some((address, prefix)) => (address, Some(prefix)),
            None => (text, None),
        };
        let address = parse_address(address).ok_or_else(|| format!("'{text}' does not appear to be an IPv4 or IPv6 address or network"))?;
        let max = if address.is_ipv4() { 32 } else { 128 };
        let prefix = match prefix {
            None => max,
            Some(prefix) => prefix.parse::<u8>().ok().filter(|p| *p <= max).ok_or_else(|| format!("'{text}' has an invalid prefix length"))?,
        };
        Ok(IpNetwork { address: mask(address, prefix), prefix })
    }
}

impl fmt::Display for IpNetwork {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}/{}", self.address, self.prefix)
    }
}

/// True when `host` names only a loopback interface.
pub fn is_loopback_host(host: &str) -> bool {
    host.eq_ignore_ascii_case("localhost") || parse_address(host).is_some_and(|address| address.is_loopback())
}

/// Python's `secrets.token_urlsafe(n)`: `n` random bytes, base64url without padding.
pub fn token_url_safe(byte_count: usize) -> String {
    let mut bytes = vec![0u8; byte_count];
    getrandom::fill(&mut bytes).expect("the system's random source is available");
    base64_url(&bytes)
}

pub fn base64_url(bytes: &[u8]) -> String {
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(bytes)
}

/// A comparison whose running time does not depend on where the inputs differ.
pub fn constant_time_eq(a: &str, b: &str) -> bool {
    let (x, y) = (a.as_bytes(), b.as_bytes());
    let mut difference = u8::from(x.len() != y.len());
    for i in 0..x.len().max(y.len()) {
        difference |= x.get(i).copied().unwrap_or(0) ^ y.get(i).copied().unwrap_or(0);
    }
    difference == 0
}
