//! The Scrape tool against fake web servers: URL rules, address blocking, redirects, errors and caps.
mod support;
use desktop_mcp_core::html::{self, Element, Node};
use desktop_mcp_core::io::Stream;
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_core::scrape::{is_blocked, parse, run, MAX_CHARS, MAX_DOWNLOAD_BYTES};
use desktop_mcp_core::shell::{Run, ShellMode};
use http::{Request, Response};
use http_body_util::Full;
use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::future::Future;
use std::net::IpAddr;
use std::pin::Pin;
use std::rc::Rc;
use std::time::Duration;
use support::{block_on, duplex};

type Page = Rc<dyn Fn(&str, &Request<hyper::body::Incoming>) -> Response<Full<bytes::Bytes>>>;

/// Hosts with addresses, one web server answering for all of them, and a parser that keeps the body as text.
struct FakeWeb {
    addresses: HashMap<&'static str, Vec<IpAddr>>,
    page: Page,
    /// (host, port, tls, request target, Host header, User-Agent) per request.
    requests: Rc<RefCell<Vec<(String, u16, bool, String, String, String)>>>,
    charsets: RefCell<Vec<Option<String>>>,
    parsed_bytes: Cell<usize>,
    /// Whether `sleep` ends at once (every fetch then times out) or never.
    instant_sleep: bool,
    /// Whether the server never answers.
    silent: bool,
}

impl FakeWeb {
    fn new(page: impl Fn(&str, &Request<hyper::body::Incoming>) -> Response<Full<bytes::Bytes>> + 'static) -> FakeWeb {
        let addresses = HashMap::from([
            ("example.com", vec!["93.184.216.34".parse().unwrap()]),
            ("other.example", vec!["8.8.8.8".parse().unwrap()]),
            ("localhost", vec!["127.0.0.1".parse().unwrap()]),
            ("intranet.example", vec!["8.8.4.4".parse().unwrap(), "10.0.0.5".parse().unwrap()]),
            ("1.1.1.1", vec!["1.1.1.1".parse().unwrap()]),
            ("127.0.0.1", vec!["127.0.0.1".parse().unwrap()]),
            ("::1", vec!["::1".parse().unwrap()]),
        ]);
        FakeWeb {
            addresses,
            page: Rc::new(page),
            requests: Rc::default(),
            charsets: RefCell::default(),
            parsed_bytes: Cell::new(0),
            instant_sleep: false,
            silent: false,
        }
    }
}

fn html(status: u16, body: &str) -> Response<Full<bytes::Bytes>> {
    Response::builder().status(status).header("content-type", "text/html").body(Full::new(bytes::Bytes::from(body.to_string()))).unwrap()
}
fn redirect(status: u16, location: &str) -> Response<Full<bytes::Bytes>> {
    Response::builder().status(status).header("location", location).body(Full::new(bytes::Bytes::from("moved"))).unwrap()
}

impl Desktop for FakeWeb {
    fn read_clipboard(&self, _image_max_side: u32) -> Pin<Box<dyn Future<Output = desktop_mcp_core::clipboard::Clipboard>>> {
        unreachable!()
    }
    fn write_clipboard(&self, _contents: desktop_mcp_core::clipboard::ClipboardWrite) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        unreachable!()
    }
    fn sleep(&self, _duration: Duration) -> Pin<Box<dyn Future<Output = ()>>> {
        if self.instant_sleep { Box::pin(async {}) } else { Box::pin(std::future::pending()) }
    }
    fn shell(&self, _command: &str, _mode: ShellMode, _timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>> {
        unreachable!()
    }
    fn resolve(&self, host: &str) -> Pin<Box<dyn Future<Output = Result<Vec<IpAddr>, String>>>> {
        let answer = self.addresses.get(host).cloned().ok_or_else(|| "nodename nor servname provided, or not known".to_string());
        Box::pin(async move { answer })
    }
    fn connect(&self, host: &str, port: u16, tls: bool) -> Pin<Box<dyn Future<Output = Result<Box<dyn Stream>, String>>>> {
        let (client, server) = duplex();
        let (page, requests, host, silent) = (self.page.clone(), self.requests.clone(), host.to_string(), self.silent);
        tokio::task::spawn_local(async move {
            if silent {
                let _server = server;
                return std::future::pending().await;
            }
            let service = hyper::service::service_fn(move |request: Request<hyper::body::Incoming>| {
                let header = |name: &str| request.headers().get(name).map(|v| v.to_str().unwrap().to_string()).unwrap_or_default();
                requests.borrow_mut().push((host.clone(), port, tls, request.uri().to_string(), header("host"), header("user-agent")));
                let response = page(&host, &request);
                async move { Ok::<_, std::convert::Infallible>(response) }
            });
            let _ = hyper::server::conn::http1::Builder::new().serve_connection(server, service).await;
        });
        Box::pin(async move { Ok(Box::new(client) as Box<dyn Stream>) })
    }
    fn parse_html(&self, body: &[u8], charset: Option<&str>) -> Option<Element> {
        self.charsets.borrow_mut().push(charset.map(str::to_string));
        self.parsed_bytes.set(body.len());
        Some(Element { name: "html".into(), attributes: vec![], children: vec![Node::Text(String::from_utf8_lossy(body).into_owned())] })
    }
}

fn scrape(web: &FakeWeb, url: &str) -> Result<String, String> {
    block_on(run(web, url))
}

#[test]
fn blocked_addresses() {
    for address in [
        "10.1.2.3", "127.0.0.1", "169.254.169.254", "172.16.0.1", "192.168.1.1", "0.0.0.0", "224.0.0.1", "255.255.255.255", "::1", "::",
        "fe80::1", "fd00::1", "ff02::1", "::ffff:127.0.0.1", "2001:db8::1",
        // Reserved ranges the Python ipaddress module also treats as non-global.
        "::10.0.0.1", "::8.8.8.8", "64:ff9b::808:808", "2001::1", "2001:10::1", "100::1", "198.18.0.1", "203.0.113.9",
    ] {
        assert!(is_blocked(address.parse().unwrap()), "{address}");
    }
    for address in ["8.8.8.8", "1.1.1.1", "172.32.0.1", "2606:4700:4700::1111", "::ffff:8.8.8.8", "100:0:0:1::1"] {
        assert!(!is_blocked(address.parse().unwrap()), "{address}");
    }
}

#[test]
fn urls_are_parsed() {
    let target = parse("https://Example.com:8443/a/b?c=1#frag").unwrap();
    assert_eq!((target.tls, target.host.as_str(), target.port, target.authority.as_str(), target.path.as_str()), (true, "example.com", 8443, "Example.com:8443", "/a/b?c=1"));
    let target = parse("http://[::1]").unwrap();
    assert_eq!((target.host.as_str(), target.port, target.path.as_str()), ("::1", 80, "/"));
}

#[test]
fn unsafe_urls_are_refused() {
    let web = FakeWeb::new(|_, _| html(200, "no"));
    for (url, expected) in [
        ("ftp://example.com", "URL scheme 'ftp' is not allowed; use http or https."),
        ("file:///etc/passwd", "URL scheme 'file' is not allowed; use http or https."),
        ("http://user:pass@example.com", "URLs with embedded credentials are not allowed."),
        ("http://127.0.0.1:8080/", "Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1"),
        ("http://localhost/", "Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1"),
        ("http://[::1]/", "Private, loopback, link-local, multicast, and reserved addresses are blocked: ::1"),
        ("http://intranet.example/", "Private, loopback, link-local, multicast, and reserved addresses are blocked: 10.0.0.5"),
        ("http://nowhere.invalid/", "Could not resolve hostname 'nowhere.invalid': nodename nor servname provided, or not known"),
        ("http:/example.com", "URL has no hostname: http:/example.com"),
        ("http://", "URL has no hostname: http://"),
        ("example.com", "Invalid URL: example.com"),
    ] {
        assert_eq!(scrape(&web, url), Err(expected.to_string()), "{url}");
    }
    assert!(web.requests.borrow().is_empty(), "nothing was fetched");
}

#[test]
fn a_page_is_fetched_and_converted() {
    let web = FakeWeb::new(|_, _| html(200, "Hello"));
    assert_eq!(scrape(&web, "http://example.com/page?q=1"), Ok("URL:http://example.com/page?q=1\nContent:\nHello".to_string()));
    let requests = web.requests.borrow();
    let (host, port, tls, target, host_header, agent) = &requests[0];
    assert_eq!((host.as_str(), *port, *tls, target.as_str(), host_header.as_str()), ("example.com", 80, false, "/page?q=1", "example.com"));
    assert!(agent.starts_with("Mozilla/5.0"), "{agent}");
}

#[test]
fn https_and_an_explicit_port_are_used() {
    let web = FakeWeb::new(|_, _| html(200, "ok"));
    scrape(&web, "https://example.com:8443/").unwrap();
    let requests = web.requests.borrow();
    assert_eq!((requests[0].1, requests[0].2, requests[0].4.as_str()), (8443, true, "example.com:8443"));
}

#[test]
fn the_charset_reaches_the_parser() {
    let web = FakeWeb::new(|_, _| Response::builder().header("content-type", "text/html; charset=\"EUC-KR\"").body(Full::new(bytes::Bytes::from("x"))).unwrap());
    scrape(&web, "http://example.com/").unwrap();
    assert_eq!(*web.charsets.borrow(), [Some("EUC-KR".to_string())]);
}

#[test]
fn http_errors_are_reported_as_content() {
    let web = FakeWeb::new(|_, request| if request.uri().path() == "/gone" { html(404, "nope") } else { html(503, "busy") });
    assert_eq!(scrape(&web, "http://example.com/gone"), Ok("URL:http://example.com/gone\nContent:\n404 Client Error: Not Found for url: http://example.com/gone".to_string()));
    assert_eq!(scrape(&web, "http://example.com/x"), Ok("URL:http://example.com/x\nContent:\n503 Server Error: Service Unavailable for url: http://example.com/x".to_string()));
}

#[test]
fn redirects_are_followed_to_public_hosts() {
    let web = FakeWeb::new(|host, request| match (host, request.uri().path()) {
        ("example.com", "/old") => redirect(301, "http://other.example/new"),
        ("other.example", "/new") => redirect(302, "/final?x=1"),
        ("other.example", "/final") => html(200, "arrived"),
        _ => html(404, ""),
    });
    assert_eq!(scrape(&web, "http://example.com/old"), Ok("URL:http://example.com/old\nContent:\narrived".to_string()));
    let targets: Vec<String> = web.requests.borrow().iter().map(|r| format!("{}{}", r.0, r.3)).collect();
    assert_eq!(targets, ["example.com/old", "other.example/new", "other.example/final?x=1"]);
}

#[test]
fn relative_redirects_resolve_against_the_page() {
    let web = FakeWeb::new(|_, request| match request.uri().path() {
        "/a/b" => redirect(302, "c"),
        "/a/c" => redirect(302, "//other.example/d"),
        "/d" => html(200, "done"),
        _ => html(404, ""),
    });
    assert_eq!(scrape(&web, "http://example.com/a/b"), Ok("URL:http://example.com/a/b\nContent:\ndone".to_string()));
}

#[test]
fn a_redirect_to_a_private_address_is_not_followed() {
    let web = FakeWeb::new(|_, _| redirect(302, "http://127.0.0.1/admin"));
    assert_eq!(scrape(&web, "http://example.com/"), Ok("URL:http://example.com/\nContent:\nmoved".to_string()));
    assert_eq!(web.requests.borrow().len(), 1);
}

#[test]
fn endless_redirects_stop() {
    let web = FakeWeb::new(|_, _| redirect(302, "/again"));
    assert_eq!(scrape(&web, "http://example.com/"), Ok("URL:http://example.com/\nContent:\ntoo many HTTP redirects".to_string()));
    assert_eq!(web.requests.borrow().len(), 17);
}

#[test]
fn long_pages_are_truncated() {
    let web = FakeWeb::new(|_, _| html(200, &"é".repeat(MAX_CHARS + 5)));
    let page = scrape(&web, "http://example.com/").unwrap();
    let expected = format!("URL:http://example.com/\nContent:\n{}\n\n...[truncated — 5 chars omitted. Scroll the page and scrape again to read more.]", "é".repeat(MAX_CHARS));
    assert!(page == expected, "{}", &page[page.len() - 120..]);
}

#[test]
fn downloads_are_capped() {
    let web = FakeWeb::new(|_, _| html(200, &"a".repeat(MAX_DOWNLOAD_BYTES + 1_000_000)));
    scrape(&web, "http://example.com/").unwrap();
    assert_eq!(web.parsed_bytes.get(), MAX_DOWNLOAD_BYTES);
}

#[test]
fn a_silent_server_times_out() {
    let mut web = FakeWeb::new(|_, _| html(200, "late"));
    web.silent = true;
    web.instant_sleep = true;
    assert_eq!(scrape(&web, "http://example.com/"), Ok("URL:http://example.com/\nContent:\nThe request timed out.".to_string()));
}

#[test]
fn markdown_walks_the_tree() {
    fn element(name: &str, attributes: &[(&str, &str)], children: Vec<Node>) -> Node {
        Node::Element(Element { name: name.into(), attributes: attributes.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect(), children })
    }
    let text = |t: &str| Node::Text(t.to_string());
    let Node::Element(root) = element("html", &[], vec![
        element("head", &[], vec![element("title", &[], vec![text(" Page ")]), element("style", &[], vec![text("x{}")])]),
        element("body", &[], vec![
            element("p", &[], vec![text("Some "), element("b", &[], vec![text("bold")]), text(" and "), element("a", &[("href", "/x")], vec![text("a\nlink")])]),
            element("ol", &[], vec![element("li", &[], vec![text("one")]), element("li", &[], vec![text("two")])]),
            element("script", &[], vec![text("no")]),
            element("pre", &[], vec![text("\n  a\n  b\n")]),
        ]),
    ]) else { unreachable!() };
    assert_eq!(html::to_markdown(&root), "Page\n\nSome **bold** and [a link](/x)\n\n1. one\n2. two\n\n```\n  a\n  b\n```");
}
