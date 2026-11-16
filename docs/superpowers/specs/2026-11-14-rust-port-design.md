# A Rust port of desktop-mcp for macOS, shaped so Windows can share its core

Date: 2026-11-14
Status: approved direction (owner, 2026-11-14), revised after a builder check. Nothing is built yet beyond the spikes named below.
Evidence: spikes in the sibling repo `async-swift-rs`, branch `feat/runtime`,
`docs/superpowers/specs/spikes/tokio-guest/` (`nothread`, `rmcp_guest`, `nw_mcp`, `nw_hyper`,
`schema`, `stateless`).

Lines marked **(my call)** are choices I made that the owner has not confirmed.
Lines marked **(owner)** record what the owner said.

## Intent

Rewrite the macOS server in Rust so that the protocol, session, HTTP and configuration code is
written once and reused by the Windows port the root README already plans. The macOS binary keeps
its present character: one file, no runtime to install, system frameworks for everything the
system can do, and no thread pool of its own.

## What the owner decided

- **(owner)** Threads stay managed by libdispatch and the Swift Concurrency runtime, as today.
- **(owner)** The transport is Network.framework.
- **(owner)** tokio must not manage any thread.
- **(owner)** The Windows version will also be Rust.
- **(owner)** Prefer existing crates over a hand-written HTTP layer; `hyper` was chosen over the
  hand-written HTTP/1.1 of the first spike.
- **(owner)** rmcp's tool macros are used, schemas included: platforms cannot mean exactly the
  same thing anyway, so each port's `tools/list` is what its own code generates.
- **(owner)** No dependency on `async-swift-rs` for now: the port needs only the run loop and
  libdispatch, so it carries its own small main-loop executor.
- **(owner)** The `dispatch` backend only in the first version; macOS 13 stays the floor.
- **(owner)** libdispatch is used through the `dispatch2` crate (data, queues, sources), not through
  hand-written declarations.
- **(owner)** Received bytes stay in `dispatch_data` (concat, apply, subrange) rather than being
  copied into an intermediate buffer.

## Goals

1. The same tools as the Swift port, ported one by one. Their schemas are whatever rmcp's macros
   generate; agreement with `spec/tools.json` is not checked.
2. Same CLI, `config.toml`, Launch Agent, Unix socket, `connect` bridge and HTTP behaviour as the
   Swift port's README describes, except where "Differences" below says otherwise.
3. A core crate with no macOS code in it, which the Windows port links unchanged.
4. No thread created by anything but the system (main thread, dispatch workers, Swift's pool).

## Non-goals

- Changing tool behaviour. Every tool is ported, not redesigned.
- Cross-language LTO, Swift source, or the Swift Concurrency runtime. The port contains no Swift
  and does not depend on `async-swift-rs`.
- Linux.
- The Windows port itself. This document only fixes the boundary it will plug into.

## Anti-goals

- A second scheduler. If a piece of the design needs a thread that tokio, hyper or rmcp starts,
  the design is wrong.
- A fork of rmcp or tokio that has to be maintained.
- Parity "by eye". A transport or protocol behaviour counts as ported when a parity test that
  passes against the Swift binary passes against the Rust binary. The only allowed exceptions are
  the items under "Differences", each with its own per-binary expectation in the suite.

## Architecture

```
Network.framework ──► NwIo ──► hyper (HTTP/1.1, SSE) ──► http app ──► session ──► rmcp service ──► tools
 (main queue)      dispatch_data                         (auth, routes)  (one Transport each)       │
                   queue, 1 copy                                                                    ├─ main thread: AppKit, short AX calls
stdio / Unix socket ──► FdIo (dispatch_io) ──► newline-delimited JSON ──► session                   └─ dispatch queues: blocking AX, shell, capture
```

Everything in the top row after `NwIo` runs on the main thread, inside one `LocalSet` polled by
the port's own run loop executor (`MainLoop::run`). That root future is the "one root task per process" of the README;
the process exits by returning from it.

### The tokio guest

rmcp needs a tokio runtime context (`spawn`, `time`). The runtime is `current_thread`, entered on
the main thread and never given a thread to block on:

- Tasks live in a `LocalSet`; the root future is polled inside a `CFRunLoopSource` callback and
  wakes signal that source (the structure `async-swift-rs`'s `run_main` proved).
- Three run loop timers give the runtime non-blocking turns (`rt.block_on(yield_now())`):
  one right after each poll of the `LocalSet` (rmcp sends responses from plain `tokio::spawn`),
  one every 50 ms while a request is in flight, one every second while any session is alive.
- The slow tick runs while the server has anything that can own a timer: an open connection, a
  session, or a stdio or socket peer. With only the listener open, no timer runs and the process
  does not wake.
- Consequence: tokio timers are coarse. In the `nothread` spike a `sleep` in a `spawn_local` task
  was up to one fast tick late (100 ms asked, 116 ms measured) and one in a plain `tokio::spawn`
  task up to two (10 ms asked, 116 ms measured). They are used for timeouts only.

This is the `nothread` design the owner accepted (spiked with `async-swift-rs`'s `run_main` as
the executor; the port's own executor has the same shape). It lives in the core crate behind a small
trait, because Windows needs the same thing driven by its own loop:

```rust
/// What the platform's main loop does for the guest runtime.
pub trait MainLoop {
    /// Ask for one runtime turn soon (coalesced).
    fn kick(&self);
    /// Turn the runtime repeatedly while `rate` is not `Off`.
    fn set_tick(&self, rate: Tick);   // Off | Slow (1 s) | Fast (50 ms)
    /// Run `future` on the main thread until it completes, serving the platform's event loop.
    fn run<F: Future + 'static>(&self, future: F) -> F::Output;
}
```

macOS implements it with a `CFRunLoopSource`, `CFRunLoopTimer`s and `CFRunLoop::run_in_mode`
(`objc2-core-foundation`), about a hundred lines. **(my call)** Windows will implement
it over its message loop; whether tokio may own threads there is the owner's to say later.

**(owner, 2026-11-14)** No `async-swift-rs`. Blocking work is handed to a libdispatch global
queue through `dispatch2` (`offload(|| …) -> impl Future`, a closure plus a oneshot channel), and
exact timers for tools come from dispatch timer sources. macOS 13 stays the floor.

### Crates **(my call)**

A Cargo workspace at the repository root, next to `Package.swift`:

| Crate | Contents | Platform code |
|---|---|---|
| `crates/core` (`desktop-mcp-core`) | tool contract (`spec/tools.json` embedded), tool routing, sessions, HTTP app (routes, CORS/Origin, allowlist, bearer auth, OAuth), `config.toml`, option merging, the stdio and HTTP bridges of `connect`, the guest runtime, the traits below | none |
| `crates/macos` (`desktop-mcp-macos`) | `MainLoop`, `NwIo`/listener, `FdIo`, the `Desktop` implementation, Launch Agent install/uninstall, permissions | all of it |
| `crates/cli` (binary `desktop-mcp`) | argument parsing, wiring | `cfg(target_os)` selects the platform crate |

The Swift port stays in `macos/` and keeps shipping until the Rust binary passes the parity suite.

This changes two statements of the root README, which is updated in step 1: Windows is listed as
"to be decided (C# NativeAOT or Rust)" with its own workspace under `windows/` (the owner has now
said Rust, and the workspace is shared at the root), and the shared principles name "the official
MCP Swift SDK" (it becomes "the official MCP SDK of the port's language").

### Platform boundary

```rust
/// A byte stream the platform delivers on the main thread. hyper and the line framer sit on it.
pub trait Io: hyper::rt::Read + hyper::rt::Write + Unpin + 'static {}

pub trait Listener {
    type Io: Io;
    async fn accept(&mut self) -> Option<(Self::Io, PeerInfo)>;   // PeerInfo: remote IP, or peer uid for a socket
    fn local_port(&self) -> Option<u16>;
}

/// The tools' behaviour. One method per tool; arguments and results are the core's types.
pub trait Desktop: 'static {
    async fn snapshot(&self, args: SnapshotArgs) -> ToolResult;
    async fn click(&self, args: ClickArgs) -> ToolResult;
    // … one per entry of spec/tools.json
}

/// Outbound HTTP for `connect --url` (the stdio-to-HTTP bridge), streaming responses included.
pub trait HttpClient { /* request(method, url, headers, body) -> status, headers, byte stream */ }
```

**(my call)** `HttpClient` is a platform trait, not a hyper client: macOS implements it with
`NSURLSession` (as the Swift bridge does, which also gives it https and the system proxy
settings), Windows with WinHTTP. **Not yet spiked.**

`Desktop` is not `Send`: the implementation may hold main-thread objects. Work that blocks
(Accessibility calls into another process, screen capture, a child process) is handed to a
dispatch queue with `offload` and awaited, exactly as the Swift port offloads to GCD today.

### MCP protocol: rmcp with the `local` feature

- `default-features = false, features = ["server", "macros", "local"]`.
- **(owner)** Tools are declared with rmcp's macros (`#[tool_router]`, `#[tool]`,
  `#[tool_handler]`), and the macros generate the schemas (`schemars`). A spike on `Click` and
  `Scroll` showed the result: annotations equal the contract; input schemas are equivalent in
  meaning and different in form (`$schema`, `$defs`/`$ref` for enums, `"format": "int64"`,
  `"type": ["array", "null"]` instead of `anyOf`).
- **(owner, 2026-11-14)** The Rust port's `tools/list` is not compared with `spec/tools.json` at
  all, not by form, not by meaning, not by name: making them the same is not a requirement. The
  port pins its own `tools/list` with a snapshot test so that an rmcp or schemars upgrade that
  changes it is noticed.
- Opt-in tools that are not in the contract (`OCR` today) are listed only when included, as in
  the Swift port.
- A session is one rmcp service with one `Transport`. The transport is a pair of channels;
  answers are routed to the waiting HTTP request by JSON-RPC id, server-initiated messages go to
  the session's `GET` stream. The spikes hold a single global session; several sessions,
  re-attaching a re-opened `GET`, idle eviction and the byte budget are **not yet spiked**.
- In-session `POST`s are answered as `text/event-stream` with the leading empty-data event, as
  the README specifies (the spikes answer `application/json`; that is spike shorthand, not a
  difference).
- `MCP-Protocol-Version` is not validated today, and 2026-07-28 clients are served statelessly.
  That stays a requirement: if rmcp would reject such a request, the HTTP app handles it before
  rmcp sees it. How rmcp behaves here is checked in step 1.
- Stateless `POST /mcp` (the default mode) creates a service per request with
  `rmcp::service::serve_directly`, which skips the initialize handshake, and drops it after the
  answer. Spiked (`stateless`): `initialize`, `tools/list`, `tools/call` and `ping` are each
  answered by a fresh service behind a one-message transport, and the service ends at once.
- Cancellation: a client disconnect or `notifications/cancelled` cancels that request's
  `CancellationToken`; tools observe it and kill child processes as today. **Not yet spiked.**

### HTTP: hyper

- `hyper` with `http1` + `server`, served per connection with `http1::Builder::serve_connection`.
- `NwIo` (spiked): reads queue `dispatch_data` with `dispatch_data_create_concat`, fill hyper's
  buffer from `dispatch_data_apply`, drop the consumed prefix with
  `dispatch_data_create_subrange`; writes copy once into `dispatch_data_create` and return
  `Pending` above 256 KiB unsent. The read path is exercised by the spike (including a 300 KB
  request body); the write back-pressure path exists but no test has sent a large response.
- hyper gets a timer (implemented on tokio's, so it rides the tick) so that its header read
  timeout works on non-loopback binds. **Not yet spiked**: `nw_hyper` gave hyper no timer.
- **(owner, 2026-11-15)** TLS is done by Network.framework, so hyper never sees ciphertext. The
  identity comes from the PEM files without a keychain: `SecItemImport` with no keychain reads the
  certificate and key, `SecIdentityCreate` (public since macOS 10.12) pairs them, `sec_identity_create`
  wraps them for `sec_protocol_options_set_local_identity`; all through `objc2-security`. An EC key in
  PKCS#8 is rewritten as SEC1 first, which `SecItemImport` reads. Built and tested.

### stdio and the Unix socket

- `FdIo` wraps a file descriptor with `dispatch_io` (dispatch owns the threads): stdin/stdout for
  `--transport stdio`, and each accepted connection of the Launch Agent's socket. **Not yet
  spiked.**
- The socket listener: **(my call)** try `nw_listener_create_with_launchd_key` first, since it
  keeps one transport stack; fall back to `launch_activate_socket` + a dispatch read source for
  accept if Network.framework cannot give the peer's uid. The Swift port checks `getpeereid`
  against `geteuid`, and that check is kept; if Network.framework cannot expose the peer, the
  fallback is the only path. **Not yet spiked.**

### CLI and config **(my call)**

`clap` for arguments and `toml` for `config.toml`. Both are build-time dependencies, which the
project's "no runtime dependencies" rule allows. The Swift port's hand-written TOML subset parser
is not ported.

### Framework bindings

The `objc2` family: `objc2-app-kit`, `objc2-application-services` (Accessibility),
`objc2-core-graphics`, `objc2-screen-capture-kit`, `objc2-vision`, `objc2-core-services`,
`objc2-foundation` (`NSXMLDocument` for `Scrape`, as today), `block2`, `dispatch2`.
Network.framework has no binding crate (`objc2-network` is an empty placeholder), so the port
declares the C functions it uses itself, as the spikes do.

## Differences from the Swift port

1. **HTTP/2 over TLS is dropped in the first version. (my call; the owner accepted the
   recommendations of the draft on 2026-11-14)** hyper's
   HTTP/2 needs an executor and adds the `h2` state machine to the guest runtime; nothing in the
   current clients requires it. HTTPS stays, as HTTP/1.1.
2. **JSON-RPC error texts change.** The Swift SDK's prefixes (`Invalid params: Unknown tool: …`)
   become rmcp's. The README already calls these SDK-specific; tests that pin them are rewritten.
3. **`data:` URL arguments reach tools unchanged.** The rebuilt-by-the-SDK behaviour was a Swift
   SDK artifact.
4. Timeouts measured by tokio timers are coarse (see "The tokio guest"). `Wait` and `Shell`
   timeouts use dispatch timer sources and stay exact.

## Parity suite

The Swift tests (about 390 cases) run in-process against injected fakes; none starts the binary.
So the parity suite is new work, and it has two halves **(my call)**:

- **Black-box, binary-agnostic**: a Rust test crate that starts a server binary (path from an
  environment variable) and drives it from outside. It covers what a client can observe without a
  display: `tools/list` (that it answers and names the ported tools); stateless `POST /mcp` including the 406, 415, 400
  and 405 answers; sessions (id issue, SSE answers, `GET` stream, 404 on an unknown id, `DELETE`,
  idle eviction); cancellation by disconnect and by `notifications/cancelled`; bearer auth,
  allowlist, Origin check, OAuth endpoints; stdio; the Unix socket; `connect` over both; and the
  core tools that need no display (`Shell`, `Wait` by time, `Scrape` against a local server
  where the address rules allow a test). It must pass on the Swift binary first. Step 1 is done
  when the `tools/list` and stateless cases pass on both binaries.
- **Ported unit tests**: tool logic is tested as today, in-process against fakes of the system
  sources (`FakeWindowSource` and the like become Rust trait fakes), ported with the code they
  cover. The same holds for pure logic (config merging, allowlist, OAuth, HTML to Markdown).

Tests that need a display or permissions keep their current skip rules.

## Order of work

1. Workspace, `MainLoop` for macOS, guest runtime, `NwIo`, hyper, stateless `POST /mcp` with
   `tools/list` from the contract and one trivial tool. Parity suite skeleton against Swift.
2. Sessions (`GET` stream, re-attach, idle eviction, byte budget), cancellation, auth, allowlist,
   Origin check, OAuth, TLS.
3. stdio, Unix socket, `connect`, Launch Agent install/uninstall.
4. Core tools (no display): `Shell`, `Scrape`, `Wait` (time and app forms), `Clipboard`, `Open`,
   `Notification`, `Spotlight`, `RunShortcut`, `Permissions`.
5. Desktop tools: `App`, `Window`, `Menu`, `Snapshot` (tree traversal), `Click`, `Type`,
   `Scroll`, `Move`, `Shortcut`, `Desktop`, `Wait` (window and element forms), `OCR`.
6. Release script (universal binary, signing), README, switch the default port.

## Risks

- **A forgotten tick hangs timers.** Seen once in the spike (shutdown). The always-on slow tick
  bounds the damage to one second; the parity suite must include idle-then-timeout cases.
- **rmcp moves fast** (3.10 today, weekly releases). Pin the version; the surface used is
  `Transport`, `ServerHandler`, `serve`/`serve_directly`, the model types.
- **`Transport: Send` under `local`.** The spike used `unsafe impl Send`. The real transport must
  hold only `Send` state (channels), with main-thread state kept outside it.
- **The main-loop executor is new code** in this repository (small, and a copy of a structure
  that was reviewed and tested in `async-swift-rs`, including the nested-run-loop case). A
  long-running agent needs a soak test before it replaces the Swift port.
- **Accessibility through `objc2`** is the bulk of the work and the least explored part: nothing
  in the spikes touches it.

## Deferred

- Whether the Rust port replaces `macos/` at parity or both ship: decided when parity is near.
- Moving onto `async-swift-rs` (Swift Tasks instead of the port's own executor): only once that
  crate is published and tested on older systems.
