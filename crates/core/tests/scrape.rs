//! The Scrape tool against fake web servers: URL rules, address blocking, redirects, errors and caps.
mod support;
use desktop_mcp_core::html::{Element, Node};
use desktop_mcp_core::io::Stream;
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_core::scrape::{is_blocked, run};
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
    fn create_space(&self, _open_delay: f64, _close_after: bool) -> Pin<Box<dyn Future<Output = String> + '_>> {
        unreachable!()
    }
    fn snapshot(&self, _use_vision: bool) -> Pin<Box<dyn Future<Output = desktop_mcp_core::snapshot::State>>> {
        unreachable!()
    }
    fn find_element(&self, _app: Option<&str>, _title: Option<&str>, _role: Option<&str>, _deadline: std::time::Instant) -> Pin<Box<dyn Future<Output = Option<desktop_mcp_core::element::FoundElement>>>> {
        unreachable!()
    }
    fn menu(&self, _app: Option<String>, _request: desktop_mcp_core::menu::Request) -> Pin<Box<dyn Future<Output = Result<String, String>>>> {
        unreachable!()
    }
    fn window_source(&self) -> std::rc::Rc<dyn desktop_mcp_core::window::WindowSource> {
        unreachable!()
    }
    fn launch_app(&self, _name: &str) -> Pin<Box<dyn Future<Output = bool>>> {
        unreachable!()
    }
    fn activate_app(&self, _name: &str) -> Pin<Box<dyn Future<Output = bool>>> {
        unreachable!()
    }
    fn change_front_window(&self, _change: Option<desktop_mcp_core::app::FrameChange>) -> Pin<Box<dyn Future<Output = bool>>> {
        unreachable!()
    }
    fn terminate(&self, _pids: &[i32], _force: bool) -> Pin<Box<dyn Future<Output = bool>>> {
        unreachable!()
    }
    fn accessibility_trusted(&self) -> bool {
        unreachable!()
    }
    fn post(&self, _event: desktop_mcp_core::input::Event) {
        unreachable!()
    }
    fn cursor_position(&self) -> (f64, f64) {
        unreachable!()
    }
    fn secure_input_at(&self, _x: i64, _y: i64) -> Pin<Box<dyn Future<Output = bool>>> {
        unreachable!()
    }
    fn select_typing_layout(&self) -> Option<Box<dyn FnOnce()>> {
        unreachable!()
    }
    fn running_apps(&self) -> Pin<Box<dyn Future<Output = Vec<desktop_mcp_core::wait::RunningApp>>>> {
        unreachable!()
    }
    fn permissions(&self) -> Pin<Box<dyn Future<Output = desktop_mcp_core::permissions::PermissionState>>> {
        unreachable!()
    }
    fn search_files(&self, _search: desktop_mcp_core::spotlight::Search) -> Pin<Box<dyn Future<Output = Result<desktop_mcp_core::spotlight::Found, String>>>> {
        unreachable!()
    }
    fn list_shortcuts(&self, _folder: Option<&str>, _timeout: Duration) -> Pin<Box<dyn Future<Output = Result<Run, String>>>> {
        unreachable!()
    }
    fn run_shortcut(&self, _name: &str, _input: Option<&str>, _timeout: Duration) -> Pin<Box<dyn Future<Output = Result<desktop_mcp_core::shortcuts::ShortcutRun, String>>>> {
        unreachable!()
    }
    fn notify(&self, _message: &str, _title: &str, _subtitle: Option<&str>, _sound: Option<&str>) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        unreachable!()
    }
    fn open(&self, _target: &desktop_mcp_core::open::Target, _app: Option<&std::path::Path>) -> Pin<Box<dyn Future<Output = Result<(), String>>>> {
        unreachable!()
    }
    fn reveal(&self, _path: &std::path::Path) -> Pin<Box<dyn Future<Output = ()>>> {
        unreachable!()
    }
    fn find_application(&self, _name: &str) -> Pin<Box<dyn Future<Output = Option<desktop_mcp_core::open::Application>>>> {
        unreachable!()
    }
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
    fn parse_html(&self, body: Vec<u8>, charset: Option<String>) -> Pin<Box<dyn Future<Output = Option<Element>>>> {
        self.charsets.borrow_mut().push(charset);
        self.parsed_bytes.set(body.len());
        let root = Element { name: "html".into(), attributes: vec![], children: vec![Node::Text(String::from_utf8_lossy(&body).into_owned())] };
        Box::pin(async move { Some(root) })
    }
    fn background(&self, work: Box<dyn FnOnce() + Send>) -> Pin<Box<dyn Future<Output = ()>>> {
        // On a real thread, as the platform would: the work must not need this thread.
        let worker = std::thread::spawn(work);
        Box::pin(async move { worker.join().unwrap() })
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
fn http_errors_are_reported_as_content() {
    let web = FakeWeb::new(|_, request| if request.uri().path() == "/gone" { html(404, "nope") } else { html(503, "busy") });
    assert_eq!(scrape(&web, "http://example.com/gone"), Ok("URL:http://example.com/gone\nContent:\n404 Client Error: Not Found for url: http://example.com/gone".to_string()));
    assert_eq!(scrape(&web, "http://example.com/x"), Ok("URL:http://example.com/x\nContent:\n503 Server Error: Service Unavailable for url: http://example.com/x".to_string()));
}

#[test]
fn a_redirect_to_a_private_address_is_not_followed() {
    let web = FakeWeb::new(|_, _| redirect(302, "http://127.0.0.1/admin"));
    assert_eq!(scrape(&web, "http://example.com/"), Ok("URL:http://example.com/\nContent:\nmoved".to_string()));
    assert_eq!(web.requests.borrow().len(), 1);
}

