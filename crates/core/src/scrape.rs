//! The Scrape tool: fetch a page over the platform's connections and return it as Markdown. URLs and every
//! redirect are checked so a page cannot point the fetch at this machine or its network.
use crate::html;
use crate::mcp::Desktop;
use bytes::Bytes;
use http_body_util::{BodyExt, Empty};
use hyper::{Request, Response, StatusCode};
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::time::Duration;

pub const MAX_CHARS: usize = 20_000;
/// Download cap. Pages are truncated to [`MAX_CHARS`] of Markdown anyway, so a response that keeps streaming
/// cannot fill memory.
pub const MAX_DOWNLOAD_BYTES: usize = 8 * 1024 * 1024;
const USER_AGENT: &str = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
const TIMEOUT: Duration = Duration::from_secs(10);
const MAX_REDIRECTS: usize = 16;

/// Where a URL points.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Target {
    pub tls: bool,
    /// Without IPv6 brackets.
    pub host: String,
    pub port: u16,
    /// The `Host` header: host and any explicit port.
    pub authority: String,
    /// Path and query, at least `/`.
    pub path: String,
}

/// The page at `url` as the tool reports it. `Err` (a tool error) for a URL that is refused; a page that cannot
/// be fetched is reported as the content, as the original did.
pub async fn run(desktop: &dyn Desktop, url: &str) -> Result<String, String> {
    let target = validate(desktop, url).await?;
    let content = match fetch(desktop, url, target).await {
        Ok(content) | Err(content) => content,
    };
    Ok(format!("URL:{url}\nContent:\n{}", truncate(content)))
}

fn truncate(content: String) -> String {
    let count = content.chars().count();
    if count <= MAX_CHARS {
        return content;
    }
    let kept: String = content.chars().take(MAX_CHARS).collect();
    format!("{kept}\n\n...[truncated — {} chars omitted. Scroll the page and scrape again to read more.]", count - MAX_CHARS)
}

/// Rejects non-http(s) schemes, embedded credentials, and hosts resolving to private, loopback, link-local,
/// multicast, reserved or unspecified addresses.
pub async fn validate(desktop: &dyn Desktop, url: &str) -> Result<Target, String> {
    let target = parse(url)?;
    let addresses = desktop.resolve(&target.host).await.map_err(|reason| format!("Could not resolve hostname '{}': {reason}", target.host))?;
    if let Some(address) = addresses.into_iter().find(|address| is_blocked(*address)) {
        return Err(format!("Private, loopback, link-local, multicast, and reserved addresses are blocked: {address}"));
    }
    Ok(target)
}

/// The URL's parts, or why it is refused. Resolves nothing.
pub fn parse(url: &str) -> Result<Target, String> {
    let invalid = || format!("Invalid URL: {url}");
    let (scheme, _) = url.split_once(':').ok_or_else(invalid)?;
    if scheme.is_empty() || !scheme.starts_with(|c: char| c.is_ascii_alphabetic()) || !scheme.chars().all(|c| c.is_ascii_alphanumeric() || "+-.".contains(c)) {
        return Err(invalid());
    }
    let scheme = scheme.to_ascii_lowercase();
    if scheme != "http" && scheme != "https" {
        return Err(format!("URL scheme '{scheme}' is not allowed; use http or https."));
    }
    let without_fragment = url.split('#').next().unwrap_or(url);
    let rest = without_fragment[scheme.len() + 1..].strip_prefix("//").ok_or_else(|| format!("URL has no hostname: {url}"))?;
    let authority_end = rest.find(['/', '?']).unwrap_or(rest.len());
    let (authority, path) = rest.split_at(authority_end);
    if authority.is_empty() {
        return Err(format!("URL has no hostname: {url}"));
    }
    if authority.contains('@') {
        return Err("URLs with embedded credentials are not allowed.".to_string());
    }
    let uri: hyper::Uri = format!("{scheme}://{authority}{path}").parse().map_err(|_| invalid())?;
    let host = uri.host().unwrap_or_default().trim_start_matches('[').trim_end_matches(']').to_ascii_lowercase();
    let tls = scheme == "https";
    Ok(Target {
        tls,
        port: uri.port_u16().unwrap_or(if tls { 443 } else { 80 }),
        authority: uri.authority().map(|a| a.as_str().to_string()).unwrap_or_default(),
        path: uri.path_and_query().map(|p| p.as_str().to_string()).filter(|p| !p.is_empty()).unwrap_or_else(|| "/".to_string()),
        host,
    })
}

/// Python's `is_private or is_loopback or is_link_local or is_multicast or is_reserved or is_unspecified`.
pub fn is_blocked(address: IpAddr) -> bool {
    match address {
        IpAddr::V4(v4) => is_blocked_v4(v4),
        IpAddr::V6(v6) => is_blocked_v6(v6),
    }
}

fn is_blocked_v4(address: Ipv4Addr) -> bool {
    let ip = u32::from(address);
    const BLOCKED: &[([u8; 4], u32)] = &[
        ([0, 0, 0, 0], 8),       // "this" network / unspecified
        ([10, 0, 0, 0], 8),      // private
        ([127, 0, 0, 0], 8),     // loopback
        ([169, 254, 0, 0], 16),  // link-local
        ([172, 16, 0, 0], 12),   // private
        ([192, 0, 0, 0], 24),    // IETF protocol assignments
        ([192, 0, 2, 0], 24),    // documentation
        ([192, 168, 0, 0], 16),  // private
        ([198, 18, 0, 0], 15),   // benchmarking
        ([198, 51, 100, 0], 24), // documentation
        ([203, 0, 113, 0], 24),  // documentation
        ([224, 0, 0, 0], 4),     // multicast
        ([240, 0, 0, 0], 4),     // reserved, includes broadcast
    ];
    BLOCKED.iter().any(|(network, bits)| {
        let mask = if *bits == 0 { 0 } else { u32::MAX << (32 - bits) };
        ip & mask == u32::from_be_bytes(*network) & mask
    })
}

fn is_blocked_v6(address: Ipv6Addr) -> bool {
    let b = address.octets();
    let zero = |range: std::ops::Range<usize>| b[range].iter().all(|byte| *byte == 0);
    if zero(0..16) || (zero(0..15) && b[15] == 1) {
        return true; // :: and ::1
    }
    if zero(0..10) && b[10] == 0xff && b[11] == 0xff {
        return is_blocked_v4(Ipv4Addr::new(b[12], b[13], b[14], b[15])); // IPv4-mapped
    }
    zero(0..12)                                                        // ::/96 IPv4-compatible (reserved)
        || (b[..4] == [0x00, 0x64, 0xff, 0x9b] && zero(4..12))         // 64:ff9b::/96 NAT64
        || b[0] == 0xff                                                // multicast
        || (b[0] == 0xfe && b[1] & 0xc0 == 0x80)                       // fe80::/10 link-local
        || b[0] & 0xfe == 0xfc                                         // fc00::/7 unique local
        || (b[0] == 0x20 && b[1] == 0x01 && b[2] & 0xfe == 0x00)       // 2001::/23 IETF (Teredo, ORCHID, ...)
        || b[..4] == [0x20, 0x01, 0x0d, 0xb8]                          // 2001:db8::/32 documentation
        || (b[0] == 0x01 && zero(1..8))                                // 100::/64 discard
}

/// The page as Markdown, or the failure to report in its place.
async fn fetch(desktop: &dyn Desktop, url: &str, target: Target) -> Result<String, String> {
    tokio::select! {
        biased;
        page = fetch_following_redirects(desktop, url, target) => page,
        () = desktop.sleep(TIMEOUT) => Err("The request timed out.".to_string()),
    }
}

async fn fetch_following_redirects(desktop: &dyn Desktop, url: &str, mut target: Target) -> Result<String, String> {
    let mut redirects = 0;
    loop {
        let response = get(desktop, &target).await?;
        let location = response.headers().get(hyper::header::LOCATION).and_then(|value| value.to_str().ok()).map(str::to_string);
        if let (true, Some(location)) = (response.status().is_redirection(), location) {
            if redirects == MAX_REDIRECTS {
                return Err("too many HTTP redirects".to_string());
            }
            let next = join(&target, &location);
            // A public page must not be able to bounce the fetch to a private address; a refused redirect ends the
            // fetch with the 3xx response itself.
            match validate(desktop, &next).await {
                Ok(next_target) => {
                    redirects += 1;
                    target = next_target;
                    continue;
                }
                Err(reason) => eprintln!("[desktop-mcp] Scrape: refused redirect to {next}: {reason}"),
            }
        }
        return page(desktop, url, response).await;
    }
}

async fn get(desktop: &dyn Desktop, target: &Target) -> Result<Response<hyper::body::Incoming>, String> {
    let io = desktop.connect(&target.host, target.port, target.tls).await?;
    let (mut sender, connection) = hyper::client::conn::http1::handshake::<_, Empty<Bytes>>(io).await.map_err(|error| error.to_string())?;
    tokio::task::spawn_local(async move {
        let _ = connection.await;
    });
    let request = Request::get(target.path.as_str())
        .header(hyper::header::HOST, target.authority.as_str())
        .header(hyper::header::USER_AGENT, USER_AGENT)
        .header(hyper::header::ACCEPT, "*/*")
        .body(Empty::new())
        .map_err(|error| error.to_string())?;
    sender.send_request(request).await.map_err(|error| error.to_string())
}

/// The response's body (at most [`MAX_DOWNLOAD_BYTES`]) as Markdown, or its HTTP error.
async fn page(desktop: &dyn Desktop, url: &str, response: Response<hyper::body::Incoming>) -> Result<String, String> {
    let status = response.status();
    let charset = charset(&response);
    let mut body = response.into_body();
    let mut data = Vec::new();
    while data.len() < MAX_DOWNLOAD_BYTES {
        let Some(frame) = body.frame().await else { break };
        if let Ok(chunk) = frame.map_err(|error| error.to_string())?.into_data() {
            data.extend_from_slice(&chunk);
        }
    }
    data.truncate(MAX_DOWNLOAD_BYTES);
    if status.as_u16() >= 400 {
        return Err(http_error(status, url));
    }
    Ok(desktop.parse_html(&data, charset.as_deref()).map(|root| html::to_markdown(&root)).unwrap_or_default())
}

fn http_error(status: StatusCode, url: &str) -> String {
    let kind = if status.as_u16() < 500 { "Client" } else { "Server" };
    let reason = status.canonical_reason().unwrap_or("Unknown");
    let reason: Vec<String> = reason.split(' ').map(|word| word[..1].to_uppercase() + &word[1..].to_lowercase()).collect();
    format!("{} {kind} Error: {} for url: {url}", status.as_u16(), reason.join(" "))
}

/// The `charset` parameter of the response's `Content-Type`.
fn charset<B>(response: &Response<B>) -> Option<String> {
    let content_type = response.headers().get(hyper::header::CONTENT_TYPE)?.to_str().ok()?;
    content_type.split(';').skip(1).find_map(|parameter| {
        let (name, value) = parameter.split_once('=')?;
        name.trim().eq_ignore_ascii_case("charset").then(|| value.trim().trim_matches('"').to_string())
    })
}

/// `location` resolved against the page at `target`.
fn join(target: &Target, location: &str) -> String {
    let scheme = if target.tls { "https" } else { "http" };
    if location.split_once(':').is_some_and(|(scheme, _)| !scheme.is_empty() && scheme.chars().all(|c| c.is_ascii_alphanumeric() || "+-.".contains(c))) {
        return location.to_string();
    }
    if location.starts_with("//") {
        return format!("{scheme}:{location}");
    }
    let origin = format!("{scheme}://{}", target.authority);
    if location.starts_with('/') {
        return format!("{origin}{location}");
    }
    let path = target.path.split('?').next().unwrap_or("/");
    if location.starts_with('?') {
        return format!("{origin}{path}{location}");
    }
    let directory = &path[..path.rfind('/').map(|i| i + 1).unwrap_or(0)];
    format!("{origin}{}{location}", if directory.is_empty() { "/" } else { directory })
}
