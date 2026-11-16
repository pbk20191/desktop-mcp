# Rust Windows W2: HTTP and HTTPS — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `desktop-mcp.exe serve --transport streamable-http` on Windows serves the core's HTTP app (sessions, auth,
OAuth, Origin/allowlist rules, `/health`) over a WinRT `StreamSocketListener`, and over HTTPS with the `schannel`
crate, with the same refusals as macOS (no HTTP without authentication, off unless allowed).

**Architecture:** `crates/windows/src/listener.rs` implements `core::io::Listener` over `StreamSocketListener`
(connections arrive on a pool thread and are handed to the main loop through a channel) yielding W1's `SocketIo`.
`crates/windows/src/tls.rs` loads the PEM certificate and key into memory (never a certificate store or a persisted
key) and wraps each accepted `SocketIo` in a server-side TLS stream. The cli's Windows `serve` gains the HTTP branch
the macOS one has. The parity suite learns to run against the Windows binary.

**Tech Stack:** `windows` 0.62 (`Networking_Sockets`, `Win32_Security_Cryptography`, `Win32_Security_Authentication_Identity`),
`schannel` 0.1, hyper 1 (through core), `rcgen` (tests only, certificates).

**Spec:** `docs/superpowers/specs/2026-11-16-rust-port-windows-design.md` (Byte streams; Other transports; Testing)

## Global Constraints

- Everything from W1's Global Constraints still holds (no tokio threads, WinRT first, minimal tests per the owner).
- HTTP rules are the core's and do not change: refused without `--auth-key` or OAuth wherever bound; off unless
  `--allow-http` / `DESKTOP_MCP_ALLOW_HTTP=1` / `allow_http = true`; `--allow-insecure-remote` only warns.
- `localhost` binds `127.0.0.1`, as macOS's `NwListener::bind_with` does.
- TLS: HTTP/1.1 only; certificate and key from `--ssl-certfile`/`--ssl-keyfile` (PEM; RSA or EC; PKCS#8, PKCS#1 or
  SEC1) kept in memory: **no certificate store, no persisted or named key container**.
- `--transport socket`, `--socket` and `connect` stay refused on Windows.
- Tests bind loopback port 0 only; no outbound connections; no certificate store.

## Review Focus

1. **A client that disconnects mid-response** — the call is cancelled, the connection's resources are released, the
   server keeps serving (Task 3 test `client_gone_cancels`).
2. **`Connection: close` and HTTP/1.0 clients** — get the whole answer, then the connection closes (needs
   `SocketIo::poll_shutdown` to finish sending; W1 review M10) (Task 1 test `connection_close_gets_whole_answer`).
3. **Plain HTTP sent to the HTTPS port, or a broken handshake** — that connection fails alone; the listener keeps
   accepting (Task 2 test `bad_handshake_does_not_stop_the_listener`).
4. **Port already in use** — `Error: cannot serve on 127.0.0.1:<port>: <reason>` and exit 1, as macOS (Task 3).
5. **IPv6 loopback host `::1`** — binds and serves; the URL is bracketed (Task 3 test `ipv6_loopback`).

---

### Task 1: the TCP listener

**Files:**
- Create: `crates/windows/src/listener.rs`
- Modify: `crates/windows/src/net.rs` (`poll_shutdown` finishes the write side), `crates/windows/src/lib.rs`
- Test: `crates/windows/tests/listener.rs` (`harness = false`)

**Interfaces:**
- Produces:
  ```rust
  pub struct SocketListener { /* StreamSocketListener + receiver */ }
  impl SocketListener {
      /// Binds `host` ("localhost" → 127.0.0.1) and `port` (0: any free port).
      pub async fn bind(host: &str, port: u16) -> Result<SocketListener, String>;
      pub fn port(&self) -> u16;               // Information().LocalPort()
  }
  impl desktop_mcp_core::io::Listener for SocketListener { type Io = crate::net::SocketIo; /* accept: next connection, None after drop */ }
  ```
  `ConnectionReceived` runs on a pool thread: it sends the `StreamSocket` through a `tokio::sync::mpsc::unbounded`
  channel (the receiver is polled on the main loop). `Control().SetNoDelay(true)` on accepted sockets.
  `SocketIo::poll_shutdown`: after the pending write completes, close the output stream so the peer sees end of
  stream (verify on this host that `IOutputStream.Close`/`StreamSocket` disposal sends FIN only after queued data;
  if `IClosable::Close` on the output stream closes the whole socket, that is acceptable after the last write).

- [ ] **Step 1: Test** `connection_close_gets_whole_answer`: bind `127.0.0.1:0`; serve with `desktop_mcp_core::http::serve` and a minimal `App` (copy the shape from `crates/core/tests/support/mod.rs`, auth key `k`); a `std::net::TcpStream` on a test thread sends `GET /health HTTP/1.0` and reads to end: status line `HTTP/1.0 200` (or 1.1) and the body arrive, then EOF within 2 s.
- [ ] **Step 2:** Run — FAIL (module missing). **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Accept HTTP connections on a WinRT StreamSocketListener`.

### Task 2: TLS with SChannel, keys in memory

**Files:**
- Create: `crates/windows/src/tls.rs`
- Modify: root `Cargo.toml` (`schannel = "0.1"`, `rcgen` dev-dep; `windows` features `Win32_Security_Cryptography`), `crates/windows/Cargo.toml`
- Test: `crates/windows/tests/tls.rs` (`harness = false`)

**Interfaces:**
- Produces:
  ```rust
  pub struct TlsIdentity { /* schannel credentials (inbound) */ }
  impl TlsIdentity { pub fn load(certificate: &Path, private_key: &Path) -> Result<TlsIdentity, String>; }
  pub enum ServerIo { Plain(SocketIo), Tls(TlsIo) }      // hyper Read + Write + Peer
  pub struct TlsListener { inner: SocketListener, identity: Rc<TlsIdentity> } // Listener<Io = ServerIo>; Plain when no identity
  ```
- Key loading (decided): PEM → DER with a small PEM reader; certificate via `CertCreateCertificateContext`; key via
  `NCryptOpenStorageProvider(MS_KEY_STORAGE_PROVIDER)` + `NCryptImportKey` **without a key name** (ephemeral, not
  persisted) from `NCRYPT_PKCS8_PRIVATE_KEY_BLOB`; PKCS#1 (`RSA PRIVATE KEY`) and SEC1 (`EC PRIVATE KEY`) are
  rewrapped as PKCS#8 first (fixed DER prefix for RSA; for EC, the curve OID from the SEC1 structure); the key handle
  is attached with `CertSetCertificateContextProperty(CERT_NCRYPT_KEY_HANDLE_PROP_ID)`.
- **Step 0 (spike, ≤ 1 hour):** confirm `schannel` 0.1 can build inbound credentials from that `CertContext`
  (wrapping the raw `PCCERT_CONTEXT`). If it cannot without a named key container, ledger a ruling and implement
  the server side directly with SSPI through `windows` (`AcquireCredentialsHandleW` with `SCH_CREDENTIALS`,
  `AcceptSecurityContext` loop, `EncryptMessage`/`DecryptMessage`) behind the same `TlsIo` interface.
- `TlsIo` drives the TLS state machine over `SocketIo` without blocking: the handshake and records read from an
  internal buffer that is filled by polling `SocketIo`; `WouldBlock` from the inner adapter maps to `Pending`.
- The handshake runs on the main loop as part of the connection's first reads; a failed handshake ends only that
  connection.

- [ ] **Step 1: Tests** (certificates from `rcgen`, written to a temp dir): `https_with_rsa_and_ec_keys` (for each:
  `TlsIdentity::load`, serve `/health` over `TlsListener`, then a test-thread client using `schannel`'s client
  stream with certificate validation off fetches `200`); `bad_handshake_does_not_stop_the_listener` (send
  `GET / HTTP/1.1` in plain text first; that connection fails; the next TLS client still gets `200`);
  `pkcs1_and_sec1_keys_load`.
- [ ] **Step 2:** Run — FAIL. **Step 3:** Spike (Step 0), then implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Serve HTTPS with SChannel and in-memory keys`.

### Task 3: the cli serves HTTP and HTTPS on Windows

**Files:**
- Modify: `crates/cli/src/main.rs` (`#[cfg(windows)] fn serve`)
- Test: `crates/cli/tests/windows_http.rs` (`#![cfg(windows)]`)

**Interfaces:**
- Consumes: Task 1 `SocketListener`, Task 2 `TlsIdentity`/`TlsListener`.
- The Windows `serve` mirrors the macOS HTTP branch: `TlsIdentity::load` errors → `Error: <reason>`, exit 1; bind
  failure → `Error: cannot serve on {host}:{port}: {reason}`, exit 1; log `Serving streamable-http on
  {scheme}://{host}:{port}/mcp (rust)`; `Security { auth_key, ip_allowlist, oauth }`; `Sessions` with
  `plan.session_timeout`; `tokio::select!` with `signals::termination()`. `socket` → the core refusal (unchanged).

- [ ] **Step 1: Tests** (spawn the binary with `DESKTOP_MCP_ALLOW_HTTP=1`, `--auth-key k`, `--port 0` is not
  supported by the plan → pick a free port by binding `127.0.0.1:0` in the test and releasing it):
  `http_initialize_and_call` (POST `initialize` with `Authorization: Bearer k` and `Accept: application/json` → 200
  JSON; without the header → 401); `client_gone_cancels` (start `Wait` 30 s, drop the connection, then `GET /health`
  answers within 1 s); `port_in_use_fails` (bind the port in the test first → exit 1 and stderr starts with
  `Error: cannot serve on`); `ipv6_loopback` (`--host ::1` → `/health` 200 over `[::1]`).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Serve streamable HTTP and HTTPS on Windows`.

### Task 4: the parity suite runs against the Windows binary

**Files:**
- Modify: `crates/parity/src/lib.rs`, `crates/parity/tests/{guard,sessions,stateless,stdio,tls,tools}.rs`, `crates/parity/Cargo.toml`
- Create: `scripts/parity.ps1`

**Interfaces:**
- `libc` usage (`proc_pid_rusage`, `proc_pidinfo`, `kill`) becomes `cfg(target_os = "macos")`. Windows
  counterparts: graceful stop = spawn with `CREATE_NEW_PROCESS_GROUP` and `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid)`;
  the idle check = process CPU time (`GetProcessTimes`) grows by < 20 ms over 3 s idle; thread-count checks are
  macOS-only (the thread pool owns threads by design, spec Testing).
- `tls.rs`: certificates from `rcgen` instead of `/usr/bin/openssl`; the client is `%SystemRoot%\System32\curl.exe`
  with `-k` (SChannel curl) on Windows, `/usr/bin/curl` on macOS.
- Shell commands in tool tests come from a per-OS helper (`sh` syntax on macOS, PowerShell on Windows), e.g. the
  stderr+exit case is `[Console]::Error.WriteLine('e'); exit 3`.
- `tools.rs`: on Windows the `tools/list` names and texts are checked against W1's Windows list, not the Swift
  port's; macOS-only tools are skipped there.
- `scripts/parity.ps1`: builds the cli and runs `cargo test -p desktop-mcp-parity` with `DESKTOP_MCP_BIN` set.

- [ ] **Step 1:** Run `scripts/parity.ps1` — Expected: FAIL to build (macOS-only APIs).
- [ ] **Step 2:** Port as above, test by test; each test either runs on Windows or carries a one-line `cfg` reason.
- [ ] **Step 3:** Run `scripts/parity.ps1` — Expected: all Windows-applicable tests PASS. `cargo check -p desktop-mcp-parity --target aarch64-apple-darwin` still compiles.
- [ ] **Step 4:** Commit `Run the parity suite against the Windows binary`.

### Task 5: live checks and docs

**Files:**
- Modify: `docs/superpowers/plans/2026-11-16-rust-windows-live-checks.md` (W2 section), `crates/README.md` (Windows status line)

- [ ] **Step 1:** Add W2 live checks: Claude Desktop/Code connected over `http://127.0.0.1:<port>/mcp` with a bearer
  key; HTTPS with a real certificate (a CA-signed or `mkcert` one) validated by the client; OAuth sign-in flow in a
  browser; Windows Firewall prompt behaviour when binding a non-loopback host.
- [ ] **Step 2:** `crates/README.md`: Windows builds `stdio` and `streamable-http` (HTTPS via SChannel); no
  `socket`/`connect`; desktop tools still to come.
- [ ] **Step 3:** Commit `Note the Windows HTTP checks and status`.
