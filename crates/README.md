# desktop-mcp in Rust

The macOS and Windows servers, one core shared by both. They replace the earlier Swift (macOS) and C# (Windows)
ports, which are kept on the branches `archive/swift` and `archive/csharp`. "The Swift port" below means that
archived port, which this one was checked against.

## What works

`desktop-mcp serve --transport streamable-http [--host localhost] [--port 8000] [--session-timeout 1800]`
serves `/mcp` (and `/mcp/`) with the tools that need no display (see "Tools" below):

- Stateless by default: `POST` with one JSON-RPC message, answered with `application/json`.
- Sessions on request: an `initialize` whose `Accept` includes `text/event-stream` gets an
  `Mcp-Session-Id`. Requests carrying it are answered as server-sent events (an empty-data event,
  then the answer); `GET` opens the one stream for server-initiated messages (409 for a second);
  `DELETE` ends the session; an unknown or ended session is 404. A session ends after
  `--session-timeout` seconds without a `POST` (a running call counts, an open `GET` does not),
  after sending 64 MiB, or to make room when 64 exist (the least recently used idle one; 503 when
  all are busy). `notifications/cancelled` and a client leaving a call cancel that call only.
The status codes follow the Swift port: 406 without `application/json` in `Accept`, 415 for a
non-JSON `Content-Type`, 400 for a batch or invalid JSON, 405 with `Allow: POST` for `GET` and
`DELETE`, 202 for a notification. A client that disconnects cancels its tool call, and `Connection: close` and HTTP/1.0 clients get
their whole answer before the connection closes. SIGINT and
SIGTERM end the server with exit status 0.

Before any of that, as in the Swift port: browser requests must come from a loopback `Origin`
(403 otherwise; `OPTIONS` answers CORS), `--ip-allowlist` limits client addresses,
`--auth-key` requires `Authorization: Bearer <key>`, and `--oauth-client-id`/`--oauth-client-secret`
turn on the built-in OAuth server (authorization code with PKCE, loopback redirects only).
`/health` and the OAuth endpoints need no key. Settings come from the command line, then the
`DESKTOP_MCP_*` environment variables, then `~/.desktop-mcp/config.toml` (or `--config`). Unlike the Swift
port, HTTP is refused without `--auth-key` or OAuth wherever it is bound, loopback included (any process of any
user can reach a local port); `--allow-insecure-remote` only draws a warning.

`--ssl-certfile`/`--ssl-keyfile` serve HTTPS (HTTP/1.1; the Swift port also offers HTTP/2 here).
The PEM certificate and key (RSA or EC; PKCS#8, PKCS#1 or SEC1) are read into memory with
Security.framework, never into a keychain, and Network.framework does the TLS.

The HTTP transport is off unless asked for (`--allow-http`, `DESKTOP_MCP_ALLOW_HTTP=1`, or `allow_http = true`
under `[server]`), and then only with authentication: whatever reaches its port could drive the desktop, and it
cannot tell which program is calling. How to admit only known clients (for example by the caller's audit token and
code signature) is still to be worked out: `docs/superpowers/specs/2026-11-16-rust-port-client-validation.md`.

`--transport stdio` (the default) serves one session on stdin/stdout, newline-delimited JSON; at end of
input it still answers the requests it has read (not those the client cancelled), then exits. Which process macOS
holds responsible for Accessibility and Screen Recording depends on the host: a host that starts desktop-mcp
directly (Claude Code in a terminal) is responsible itself, but Claude Desktop starts it through its `disclaimer`
helper, so the bundled binary holds its own grants, and any program of this user could start it the same way and
borrow them. Checking which program started desktop-mcp is designed but not built:
`docs/superpowers/specs/2026-11-16-rust-port-client-check-design.md`.

**Disabled until clients are verified** (the code stays): `--transport socket` / `--socket`, and `connect`. Any
process of this user could reach the socket and borrow the server's own Accessibility, and `connect` reads the
stored auth key for whoever runs it. Both are refused with a message saying so. What they did:
`--transport socket --socket <path>` served the stdio binding on a Unix socket (mode 0600, the peer's uid checked,
launchd's `Listener` socket when there is one), alone or beside HTTP.
`desktop-mcp connect [--socket <path> | --url <url>] [--auth-key <key>]` bridged stdio to a running server.
Over a Unix socket stdin and stdout are piped through unchanged. Over HTTP each line is posted on its own
(a slow call does not hold up the next), replies come back one line each, a `GET` listener hands on what
the server starts by itself, and a lost session (404) is replaced by sending the first `initialize` again.
Without options it finds the installed Launch Agent from
`~/Library/LaunchAgents/com.desktop-mcp.server.plist`: its socket, falling back to its HTTP address.
The HTTP client is hyper over an outbound Network.framework connection (system TLS for `https`).

### Tools

`Shell`, `Scrape`, `Clipboard`, `Open`, `Notification`, `RunShortcut`, `Spotlight`, `Permissions`, and `Wait`
(a duration, or until an app runs or quits), with the Swift port's descriptions, arguments and wording. Each tool's
logic is in `core` (`crates/core/src/<tool>.rs`); the macOS side (`crates/macos/src/`) supplies what only the system
can: child processes (posix_spawn, reaped by a dispatch process source), `NSPasteboard`, `NSWorkspace`,
`NSXMLDocument` for HTML, `MDQuery`, ImageIO, and the permission checks. Differences from the Swift port:

- Argument errors are rmcp's text (`failed to deserialize parameters: ...`), not the Swift SDK's.
- `Scrape` truncates at 20,000 Unicode scalars, not grapheme clusters, so a page with combining marks is cut a few
  characters earlier.
- `Clipboard` reports copied files' paths in composed (NFC) form.

The desktop tools are there too (`App`, `Window`, `Menu`, `Snapshot`, `Click`, `Type`, `Scroll`, `Move`,
`Shortcut`, `Desktop`, `Wait` for windows and elements). `Desktop` finds Mission Control's add button by its
identifier `mc.spaces.add` (in WindowManager on macOS 27, the Dock before) and opens Mission Control by launching
it, so it works whatever the language and the keyboard shortcut; the Swift port matches English labels in the Dock
and posts control+up, which fails on macOS 27.

`OCR` (Vision) is listed only when included (`--include-tools OCR`, `[tools] include`, `DESKTOP_MCP_INCLUDE_TOOLS`).

`Batch` (both platforms) runs up to 20 calls of the other enabled tools in order, through the server's own router,
with a pause between steps (default 0.5 s); it is a Rust-port addition, not in `spec/tools.json` or the Swift port.

Not ported: the `install`/`uninstall` (launchd agent) and `auth` commands. An agent holds its own grants, which is
the borrowing problem above, and a key stored in `config.toml` can be read by any program of this user; pass the
HTTP key with `--auth-key` or `DESKTOP_MCP_AUTH_KEY` instead (for example `openssl rand -hex 32`). To use
desktop-mcp, register `desktop-mcp serve --transport stdio` in the host's MCP settings, or build a Claude Desktop
extension with `scripts/build-mcpb.sh [binary]` (default `target/release/desktop-mcp`; output `dist/desktop-mcp-macos.mcpb`).

Asking for permissions: `Permissions` with `request=true` asks macOS for missing Accessibility and Screen Recording
(the system prompt, which also lists the binary on the settings page, then that page) and says what it did;
Automation is only reported. `desktop-mcp permissions` does the same from a terminal and prints the report. macOS
asks for the process it holds responsible, so the command helps hosts started from that terminal, and a server
started by Claude Desktop must use the tool. A tool refused for a missing permission says to call
`Permissions(request=true)`.

## How it is built

- **No thread of its own.** Network.framework delivers on the main queue, the whole server is one
  future polled on the main run loop (`crates/macos/src/main_loop.rs`), and blocking work goes to a
  libdispatch queue (`offload`).
- **MCP by [rmcp](https://crates.io/crates/rmcp)**, with its tool macros, which also write the tool
  schemas: `tools/list` is not compared with `spec/tools.json`, only pinned by a snapshot. rmcp needs a tokio runtime;
  it runs as a guest of the run loop (`crates/core/src/guest.rs`): a current-thread runtime that is
  never parked, given short turns by run loop timers that are armed only while a connection or a
  request exists. An idle server is not woken at all.
- **HTTP by hyper** over `NwIo` (`crates/macos/src/nw/mod.rs`). Received bytes stay in `dispatch_data`
  until they are copied once into hyper's buffer. libdispatch is used through the `dispatch2` crate;
  Network.framework has no binding crate, so its objects and functions are declared in
  `crates/macos/src/nw/sys.rs` after `dispatch2`'s model: an opaque type per object, and
  `NWRetained<T>` for an owned reference (released on drop, retained on clone). Security.framework
  comes from `objc2-security` (`crates/macos/src/tls.rs`).
- **Stateless requests** each get a fresh rmcp service, as in rmcp's own HTTP server. A session keeps
  one service behind a channel transport (`crates/core/src/session.rs`); answers go to the `POST`
  that asked, anything else to the session's `GET` stream. While a session lives, the guest
  runtime turns once a second (its idle sweeper and rmcp's own timers need it); with no session
  and no connection, nothing runs.

| Crate | Contents |
|---|---|
| `core` | guest runtime, MCP server and tools, HTTP app. No platform code |
| `macos` | main run loop executor, Network.framework, `dispatch_io` streams and the Unix socket, TLS, signals, the tools' macOS behaviour |
| `cli` | the `desktop-mcp` binary |
| `parity` | black-box tests that drive a binary given in `DESKTOP_MCP_BIN` |

## Building and testing

```bash
cargo build -p desktop-mcp-cli                           # → target/debug/desktop-mcp
cargo test --workspace --exclude desktop-mcp-parity      # unit and integration tests
scripts/parity.sh                                        # the parity suite, against the release binary
```

Tests under `crates/macos/tests` run on the main thread with their own small runner, because the
code under test owns the main run loop. After reviewing a change to `tools/list`, refresh its
snapshot with `UPDATE_SNAPSHOTS=1 cargo test -p desktop-mcp-core --test mcp snapshot`.

## Windows (`crates/windows`)

Working (`docs/superpowers/specs/2026-11-16-rust-port-windows-design.md`). Windows 11 22H2 or later. It serves:

- `--transport stdio`, and `streamable-http` with the same rules as macOS (refused without a key or OAuth, off
  unless allowed); HTTPS through SChannel. SChannel cannot use a key that lives only in this process, so the key is
  stored in the user's key storage under `desktop-mcp-tls-<pid>-<random>` while the server runs and deleted after;
  a server that crashed leaves it until the next HTTPS start removes it. No `socket` transport and no `connect`.
- The core's tools with Windows behind them: `Snapshot` (UI Automation, with `use_vision` an annotated capture),
  `Click`, `Type`, `Scroll`, `Move`, `Shortcut` (`SendInput`; input toward an elevated window is refused), `App`
  (Start menu apps), `Window` (with `displays`), `Wait` (durations, apps, windows, elements), `Shell` (PowerShell 7
  when installed, else Windows PowerShell 5.1), `Scrape`, `Clipboard` (Win32, on the thread pool), `Notification`
  (toasts under Windows PowerShell's app id), opt-in `OCR` (Windows.Media.Ocr), `Batch` (several of these and the
  Windows-only tools' calls in one).
- Windows-only tools: `Screenshot` (Windows.Graphics.Capture, no UI tree), `MultiSelect`, `MultiEdit`.
- Not ported: desktop control (the user's input is never blocked; stop the agent from the client), `FileSystem`,
  `Registry` and `Process` (`Shell` does them in one PowerShell line).

Its `tools/list` is pinned in `crates/cli/tests/snapshots/windows-tools.json`, checked against the real binary by
`crates/cli/tests/windows_stdio.rs` (`UPDATE_SNAPSHOTS=1` rewrites it after review). For Claude Desktop,
`powershell -ExecutionPolicy Bypass -File scripts\build-mcpb.ps1` packs `target\release\desktop-mcp.exe` into
`dist\desktop-mcp-windows.mcpb` (install it by opening the file, or from Settings > Extensions); `-Arm64
path\to\arm64\desktop-mcp.exe` adds the native ARM64 build as `server\desktop-mcp-arm64.exe`. The x64 entry point,
finding itself emulated on an ARM64 machine, runs that one instead with the same arguments and standard handles, in a
kill-on-close job, and exits with its status (`DESKTOP_MCP_NO_ARCH_SWITCH=1` keeps it emulated).

How it runs: one message loop, on the main thread, with a `Windows.System.DispatcherQueue` that drives the guest
tokio runtime (`crates/windows/src/main_loop.rs`); blocking work, timers, handle waits and child-process pipes
(overlapped, thread-pool I/O) use the Windows thread pool. The main thread is a single-threaded apartment.

```powershell
cargo test --workspace --exclude desktop-mcp-macos --exclude desktop-mcp-parity   # on Windows
scripts/parity.ps1                                                                  # parity against the Windows binary
cargo check --workspace --exclude desktop-mcp-parity --target aarch64-apple-darwin  # macOS still builds
```

Checks run by hand (clipboard, toasts, a real console, Claude Desktop): `docs/superpowers/plans/2026-11-16-rust-windows-live-checks.md`.

Still open for both ports: checking which program started the server
(`docs/superpowers/specs/2026-11-16-rust-port-client-check-design.md`, with the Windows APIs in
`2026-11-16-rust-port-client-validation.md`).

## Releasing

`.github/workflows/release.yml` builds one extension per platform: `desktop-mcp-windows.mcpb` (the x64 entry point and
the native ARM64 build) and `desktop-mcp-macos.mcpb` (one universal arm64 + x86_64 binary). To cut a release, set
`version` under `[workspace.package]` in `Cargo.toml` to X.Y.Z, commit, then `git tag vX.Y.Z` and `git push origin
vX.Y.Z`. The workflow fails if the tag does not name the workspace version, and publishes a GitHub release with both
bundles and generated notes. Run by hand (workflow_dispatch), it only builds them as an artifact. The Rust build cache
is saved only by runs on master (a tag's cache would serve that tag alone) and restored by every run, so a manual run on
master after dependencies change keeps the next release build short.

Signing uses a self-signed code-signing certificate, CN "desktop-mcp (pbk20191)", made once with
`scripts/new-codesign-cert.ps1` (RSA 3072, valid 10 years, extended key usage code signing; it writes a
password-protected `.p12`, its base64 text and the public `.cer`). Repository secrets:

| Secret | Value |
|---|---|
| `CODESIGN_P12_BASE64` | the `.p12`, base64 (`desktop-mcp-codesign.p12.txt`) |
| `CODESIGN_P12_PASSWORD` | its password |
| `CODESIGN_IDENTITY` | optional: the macOS codesign identity, default the certificate in the `.p12` |

With them, the Windows executables are signed with signtool (SHA-256, timestamped by DigiCert), the macOS binary with
codesign (hardened runtime with the Apple Events entitlement, `scripts/desktop-mcp.entitlements`; no timestamp). The
`.mcpb` bundles are not signed: `mcpb sign` (2.1.2) appends its signature after the ZIP's end record without a
matching comment length, and Claude Desktop then refuses to open the bundle ("Invalid comment length"); `mcpb verify`
also reports every signed bundle as unsigned. Without the secrets everything is still built, unsigned (the macOS binary ad-hoc signed), and
the job summary says so. Each job records what was signed in its log, its summary and a report beside the bundles
(`signing-windows.txt`: SHA-256, Authenticode status, signer and thumbprint; `signing-macos.txt`: SHA-256, architectures,
`codesign --display` and `--verify`; `signing-bundles.txt`: each bundle's SHA-256), kept in the
`desktop-mcp-mcpb` artifact and attached to the release.

What the self-signed certificate gives: a stable identity, so macOS keeps the Accessibility and Screen Recording
grants across updates (an ad-hoc signature changes with every build, and the grants with it), and integrity (a
modified file no longer verifies). What it does not: macOS Gatekeeper still does not know it (no notarization), and
Windows SmartScreen still warns unless the certificate is trusted on that machine. To trust it on Windows for the
current user, import the public `.cer` into Trusted Publishers and Trusted Root Certification Authorities:

```powershell
Import-Certificate -FilePath desktop-mcp-codesign.cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher
Import-Certificate -FilePath desktop-mcp-codesign.cer -CertStoreLocation Cert:\CurrentUser\Root
```

