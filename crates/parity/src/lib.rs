//! Black-box tests that drive a desktop-mcp binary, whichever port built it.
//!
//! The binary comes from `DESKTOP_MCP_BIN`. The HTTP client here is written by hand over `TcpStream`, so the
//! suite shares no HTTP code with the server it tests.
use serde_json::Value;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::PathBuf;
use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{Duration, Instant};

pub struct ServerUnderTest {
    child: Option<Child>,
    pub port: u16,
    home: PathBuf,
    /// The key the harness gave a server started without authentication of its own, sent with every request.
    key: Option<&'static str>,
}

/// HTTP is served only with authentication (the Rust port refuses it otherwise), so a test that sets none gets this.
pub const DEFAULT_KEY: &str = "parity-key";

pub struct Reply {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl Reply {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers.iter().find(|(key, _)| key.eq_ignore_ascii_case(name)).map(|(_, value)| value.as_str())
    }

    pub fn json(&self) -> Value {
        serde_json::from_slice(&self.body).unwrap_or_else(|_| panic!("not JSON (status {}): {:?}", self.status, String::from_utf8_lossy(&self.body)))
    }
}

/// The headers a JSON-RPC POST carries. `Accept` names JSON only: with `text/event-stream` in it, an
/// `initialize` would open a session on the Swift port.
pub const JSON_HEADERS: &[(&str, &str)] = &[("Accept", "application/json"), ("Content-Type", "application/json")];

static SERVERS: AtomicUsize = AtomicUsize::new(0);

/// How to start the server under test.
#[derive(Default)]
pub struct Launch<'a> {
    /// More `serve` options.
    pub options: &'a [&'a str],
    /// More environment variables.
    pub env: &'a [(&'a str, &'a str)],
    /// The contents of `~/.desktop-mcp/config.toml` in the scratch HOME.
    pub config: Option<&'a str>,
}

/// A fresh directory to serve as HOME, with `config` as `~/.desktop-mcp/config.toml` when given.
fn scratch_home(config: Option<&str>) -> PathBuf {
    let home = std::env::temp_dir().join(format!("desktop-mcp-parity-{}-{}", std::process::id(), SERVERS.fetch_add(1, Ordering::SeqCst)));
    std::fs::create_dir_all(home.join(".desktop-mcp")).expect("a scratch HOME");
    if let Some(config) = config {
        std::fs::write(home.join(".desktop-mcp/config.toml"), config).expect("a config file");
    }
    home
}

/// Runs `$DESKTOP_MCP_BIN <arguments>` with `input` as its stdin, closed after the last line: its exit
/// status and the JSON lines it wrote to stdout.
pub fn run_stdio(arguments: &[&str], input: &[Value]) -> (ExitStatus, Vec<Value>) {
    run_stdio_with(arguments, input, &[])
}

/// As [`run_stdio`], with `files` (paths relative to HOME, and their contents) in the scratch HOME.
pub fn run_stdio_with(arguments: &[&str], input: &[Value], files: &[(&str, &str)]) -> (ExitStatus, Vec<Value>) {
    let binary = std::env::var_os("DESKTOP_MCP_BIN").expect("set DESKTOP_MCP_BIN to the desktop-mcp binary under test");
    let home = scratch_home(None);
    for (path, contents) in files {
        let path = home.join(path);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, contents).unwrap();
    }
    let mut child = Command::new(&binary).args(arguments).env("HOME", &home).env("DESKTOP_MCP_ALLOW_HTTP", "1").stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null()).spawn().expect("start");
    let mut stdin = child.stdin.take().unwrap();
    for message in input {
        stdin.write_all(format!("{message}\n").as_bytes()).unwrap();
    }
    drop(stdin);
    let mut stdout = child.stdout.take().unwrap();
    let reader = std::thread::spawn(move || {
        let mut text = String::new();
        let _ = stdout.read_to_string(&mut text);
        text
    });
    let deadline = Instant::now() + Duration::from_secs(20);
    let status = loop {
        if let Some(status) = child.try_wait().expect("wait") {
            break status;
        }
        if Instant::now() > deadline {
            let _ = child.kill();
            panic!("{arguments:?} did not exit within 20 s of its input ending");
        }
        std::thread::sleep(Duration::from_millis(20));
    };
    let text = reader.join().unwrap();
    let _ = std::fs::remove_dir_all(&home);
    (status, text.lines().filter(|line| !line.trim().is_empty()).map(|line| serde_json::from_str(line).unwrap_or_else(|_| panic!("not a JSON line: {line:?}"))).collect())
}

/// A short socket path for this test run (a Unix socket path is limited to 103 bytes).
pub fn socket_path(name: &str) -> PathBuf {
    let directory = PathBuf::from(format!("/tmp/dmcp-parity-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    let path = directory.join(format!("{name}-{}.sock", SERVERS.fetch_add(1, Ordering::SeqCst)));
    let _ = std::fs::remove_file(&path);
    path
}

/// Runs `$DESKTOP_MCP_BIN <arguments>` in a scratch HOME to its end: the exit status and what it wrote to stderr.
/// For invocations that are expected to stop by themselves (a refused configuration).
pub fn run_to_exit(arguments: &[&str]) -> (ExitStatus, String) {
    let binary = std::env::var_os("DESKTOP_MCP_BIN").expect("set DESKTOP_MCP_BIN to the desktop-mcp binary under test");
    let home = scratch_home(None);
    let mut child = Command::new(&binary).args(arguments).env("HOME", &home).env("DESKTOP_MCP_ALLOW_HTTP", "1").stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::piped()).spawn().expect("start");
    let deadline = Instant::now() + Duration::from_secs(10);
    let status = loop {
        if let Some(status) = child.try_wait().expect("wait") {
            break status;
        }
        if Instant::now() > deadline {
            let _ = child.kill();
            panic!("{arguments:?} did not exit within 10 s");
        }
        std::thread::sleep(Duration::from_millis(20));
    };
    let mut stderr = String::new();
    let _ = child.stderr.take().unwrap().read_to_string(&mut stderr);
    let _ = std::fs::remove_dir_all(&home);
    (status, stderr)
}

impl ServerUnderTest {
    /// Starts `$DESKTOP_MCP_BIN serve --transport streamable-http` on a free loopback port, with `HOME` pointing
    /// at a fresh directory so no user configuration is read or written.
    pub fn start() -> ServerUnderTest {
        Self::start_with(&[])
    }

    /// As [`start`](Self::start), with more `serve` options.
    pub fn start_with(options: &[&str]) -> ServerUnderTest {
        Self::launch(&Launch { options, ..Launch::default() })
    }

    /// Starts the server as `launch` describes and waits until it listens.
    pub fn launch(launch: &Launch) -> ServerUnderTest {
        let binary = std::env::var_os("DESKTOP_MCP_BIN").expect("set DESKTOP_MCP_BIN to the desktop-mcp binary under test");
        // The port is free when chosen but not reserved: a test starting at the same moment can take it first. A
        // server that cannot listen for that reason is started again on another port.
        for _attempt in 0..5 {
            let port = TcpListener::bind("127.0.0.1:0").expect("a free port").local_addr().unwrap().port();
            let home = scratch_home(launch.config);
            let stderr = std::fs::File::create(home.join("stderr.log")).expect("a log file");
            let own_key = launch.options.iter().any(|option| matches!(*option, "--auth-key" | "--oauth-client-id"))
                || launch.env.iter().any(|(name, _)| *name == "DESKTOP_MCP_AUTH_KEY")
                || launch.config.is_some_and(|config| config.contains("auth_key") || config.contains("oauth_client_id"));
            let key = (!own_key).then_some(DEFAULT_KEY);
            let child = Command::new(&binary)
                .args(["serve", "--transport", "streamable-http", "--host", "127.0.0.1", "--port", &port.to_string()])
                .args(launch.options)
                .args(key.map(|key| ["--auth-key", key]).iter().flatten())
                .env("HOME", &home)
                // The Rust port serves HTTP only on request; the Swift port ignores this.
                .env("DESKTOP_MCP_ALLOW_HTTP", "1")
                .envs(launch.env.iter().copied())
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(stderr)
                .spawn()
                .unwrap_or_else(|error| panic!("cannot start {binary:?}: {error}"));
            let mut server = ServerUnderTest { child: Some(child), port, home, key };
            let deadline = Instant::now() + Duration::from_secs(15);
            loop {
                // A connection alone could reach another test's server that took this port first: the server must
                // also have said it listens here (only one process can).
                if server.log().contains(&format!(":{port}/")) && TcpStream::connect(("127.0.0.1", port)).is_ok() {
                    return server;
                }
                if let Some(status) = server.child.as_mut().unwrap().try_wait().expect("the server's status") {
                    let log = server.log();
                    if log.contains("Address already in use") || log.contains("error 48") {
                        break;
                    }
                    panic!("the server exited ({status}) before listening:\n{log}");
                }
                assert!(Instant::now() < deadline, "the server did not listen within 15 s:\n{}", server.log());
                std::thread::sleep(Duration::from_millis(20));
            }
        }
        panic!("no free port after 5 attempts");
    }

    /// What the server has written to stderr.
    pub fn log(&self) -> String {
        std::fs::read_to_string(self.home.join("stderr.log")).unwrap_or_default()
    }

    /// Whether the binary is the Rust port (it says so when it starts).
    pub fn is_rust(&self) -> bool {
        self.log().contains("(rust)")
    }

    pub fn connect(&self) -> Connection {
        let stream = TcpStream::connect(("127.0.0.1", self.port)).expect("connect");
        stream.set_read_timeout(Some(Duration::from_secs(20))).unwrap();
        Connection { stream, key: self.key }
    }

    /// One request on its own connection.
    pub fn request(&self, method: &str, path: &str, headers: &[(&str, &str)], body: &[u8]) -> Reply {
        self.connect().request(method, path, headers, body)
    }

    pub fn post(&self, message: &Value) -> Reply {
        self.request("POST", "/mcp", JSON_HEADERS, message.to_string().as_bytes())
    }

    pub fn pid(&self) -> u32 {
        self.child.as_ref().expect("running").id()
    }

    /// How often the kernel has woken the server process from idle (timers and interrupts) since it started.
    pub fn wakeups(&self) -> u64 {
        let mut usage: libc::rusage_info_v2 = unsafe { std::mem::zeroed() };
        let status = unsafe { libc::proc_pid_rusage(self.pid() as i32, libc::RUSAGE_INFO_V2, &mut usage as *mut _ as *mut libc::rusage_info_t) };
        assert_eq!(status, 0, "proc_pid_rusage failed");
        usage.ri_pkg_idle_wkups + usage.ri_interrupt_wkups
    }

    /// The number of threads in the server process.
    pub fn threads(&self) -> i32 {
        let mut info: libc::proc_taskinfo = unsafe { std::mem::zeroed() };
        let size = std::mem::size_of::<libc::proc_taskinfo>() as i32;
        let written = unsafe { libc::proc_pidinfo(self.pid() as i32, libc::PROC_PIDTASKINFO, 0, &mut info as *mut _ as *mut libc::c_void, size) };
        assert_eq!(written, size, "proc_pidinfo failed");
        info.pti_threadnum
    }

    /// Sends SIGTERM and waits for the exit status.
    pub fn terminate(mut self) -> ExitStatus {
        let mut child = self.child.take().expect("running");
        unsafe { libc::kill(child.id() as i32, libc::SIGTERM) };
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            if let Some(status) = child.try_wait().expect("wait") {
                return status;
            }
            if Instant::now() > deadline {
                let _ = child.kill();
                panic!("the server did not exit within 10 s of SIGTERM");
            }
            std::thread::sleep(Duration::from_millis(20));
        }
    }
}

impl Drop for ServerUnderTest {
    fn drop(&mut self) {
        if let Some(mut child) = self.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
        let _ = std::fs::remove_dir_all(&self.home);
    }
}

/// One TCP connection, for several requests in a row.
pub struct Connection {
    stream: TcpStream,
    key: Option<&'static str>,
}

impl Connection {
    pub fn request(&mut self, method: &str, path: &str, headers: &[(&str, &str)], body: &[u8]) -> Reply {
        self.send(method, path, headers, body, usize::MAX);
        self.read_reply()
    }

    /// Writes a request, the body in pieces of `chunk` bytes with a short pause between them.
    pub fn send(&mut self, method: &str, path: &str, headers: &[(&str, &str)], body: &[u8], chunk: usize) {
        let mut head = format!("{method} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: {}\r\n", body.len());
        for (name, value) in headers {
            head += &format!("{name}: {value}\r\n");
        }
        if let Some(key) = self.key.filter(|_| !headers.iter().any(|(name, _)| name.eq_ignore_ascii_case("authorization"))) {
            head += &format!("Authorization: Bearer {key}\r\n");
        }
        head += "\r\n";
        self.stream.write_all(head.as_bytes()).expect("write the head");
        for piece in body.chunks(chunk.max(1)) {
            self.stream.write_all(piece).expect("write the body");
            if chunk != usize::MAX {
                self.stream.flush().unwrap();
                std::thread::sleep(Duration::from_micros(200));
            }
        }
    }

    /// Everything the server sends until it closes the connection.
    pub fn read_to_close(&mut self) -> Vec<u8> {
        let mut received = Vec::new();
        let _ = self.stream.read_to_end(&mut received);
        received
    }

    /// The status line and headers only, for a response that streams on (the session's `GET`).
    pub fn read_head(&mut self) -> Reply {
        let mut buffer = Vec::new();
        let mut byte = [0u8; 1];
        while !buffer.ends_with(b"\r\n\r\n") {
            let count = self.stream.read(&mut byte).expect("read the reply head");
            assert!(count > 0, "the connection closed before a reply: {:?}", String::from_utf8_lossy(&buffer));
            buffer.push(byte[0]);
        }
        let (status, headers) = parse_head(&String::from_utf8_lossy(&buffer[..buffer.len() - 4]));
        Reply { status, headers, body: Vec::new() }
    }

    pub fn read_reply(&mut self) -> Reply {
        let mut buffer = Vec::new();
        let head_end = loop {
            if let Some(position) = find(&buffer, b"\r\n\r\n") {
                break position;
            }
            let mut chunk = [0u8; 4096];
            let count = self.stream.read(&mut chunk).expect("read the reply head");
            assert!(count > 0, "the connection closed before a reply: {:?}", String::from_utf8_lossy(&buffer));
            buffer.extend_from_slice(&chunk[..count]);
        };
        let (status, headers) = parse_head(&String::from_utf8_lossy(&buffer[..head_end]));
        let mut rest = buffer[head_end + 4..].to_vec();
        let header = |name: &str| headers.iter().find(|(key, _)| key.eq_ignore_ascii_case(name)).map(|(_, value)| value.clone());
        let body = if header("transfer-encoding").is_some_and(|value| value.eq_ignore_ascii_case("chunked")) {
            self.read_chunked(&mut rest)
        } else {
            let length: usize = header("content-length").and_then(|value| value.parse().ok()).unwrap_or(0);
            while rest.len() < length {
                let mut chunk = [0u8; 16384];
                let count = self.stream.read(&mut chunk).expect("read the reply body");
                assert!(count > 0, "the connection closed inside the body");
                rest.extend_from_slice(&chunk[..count]);
            }
            rest.truncate(length);
            rest
        };
        Reply { status, headers, body }
    }

    fn read_chunked(&mut self, pending: &mut Vec<u8>) -> Vec<u8> {
        let mut body = Vec::new();
        loop {
            let line_end = loop {
                if let Some(position) = find(pending, b"\r\n") {
                    break position;
                }
                self.fill(pending);
            };
            let size = usize::from_str_radix(String::from_utf8_lossy(&pending[..line_end]).split(';').next().unwrap().trim(), 16).expect("a chunk size");
            pending.drain(..line_end + 2);
            while pending.len() < size + 2 {
                self.fill(pending);
            }
            body.extend_from_slice(&pending[..size]);
            pending.drain(..size + 2);
            if size == 0 {
                return body;
            }
        }
    }

    fn fill(&mut self, pending: &mut Vec<u8>) {
        let mut chunk = [0u8; 16384];
        let count = self.stream.read(&mut chunk).expect("read a chunk");
        assert!(count > 0, "the connection closed inside a chunked body");
        pending.extend_from_slice(&chunk[..count]);
    }
}

fn parse_head(head: &str) -> (u16, Vec<(String, String)>) {
    let mut lines = head.split("\r\n");
    let status = lines.next().and_then(|line| line.split(' ').nth(1)).and_then(|code| code.parse().ok()).expect("a status line");
    let headers = lines.filter_map(|line| line.split_once(':')).map(|(name, value)| (name.trim().to_string(), value.trim().to_string())).collect();
    (status, headers)
}

/// Server-sent events as (event name, data) pairs.
pub fn sse_events(body: &[u8]) -> Vec<(Option<String>, String)> {
    String::from_utf8_lossy(body)
        .replace("\r\n", "\n")
        .split("\n\n")
        .filter(|block| !block.trim().is_empty())
        .map(|block| {
            let mut event = None;
            let mut data = Vec::new();
            for line in block.lines() {
                if let Some(name) = line.strip_prefix("event:") {
                    event = Some(name.trim().to_string());
                } else if let Some(value) = line.strip_prefix("data:") {
                    data.push(value.strip_prefix(' ').unwrap_or(value).to_string());
                }
            }
            (event, data.join("\n"))
        })
        .collect()
}

/// The one JSON-RPC message of an SSE reply.
pub fn sse_message(reply: &Reply) -> Value {
    assert_eq!(reply.status, 200, "{:?}", String::from_utf8_lossy(&reply.body));
    let messages: Vec<_> = sse_events(&reply.body).into_iter().filter(|(_, data)| !data.is_empty()).collect();
    assert_eq!(messages.len(), 1, "one message expected in {:?}", String::from_utf8_lossy(&reply.body));
    serde_json::from_str(&messages[0].1).expect("JSON in the event")
}

fn find(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    haystack.windows(needle.len()).position(|window| window == needle)
}
