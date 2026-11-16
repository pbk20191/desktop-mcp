# Rust port, step 1: a stateless MCP server on the main run loop — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Rust `desktop-mcp serve --transport streamable-http` that answers stateless `POST /mcp` (`initialize`, `tools/list`, `tools/call` of `Wait` with a duration) with no thread of its own, plus a parity suite that passes against both the Swift and the Rust binary.

**Architecture:** Network.framework delivers bytes on the main queue; `NwIo` feeds hyper from a `dispatch_data` queue; each request gets a fresh rmcp service (`serve_directly`); rmcp's tokio runtime is a guest of the main `CFRunLoop` (a `LocalSet` polled in a run loop source, run loop timers turning the runtime). Platform code sits behind traits in a core crate with no macOS code.

**Tech Stack:** Rust (stable), `rmcp =3.10.0` (`server`, `macros`, `local`), `tokio 1` (`rt`, `time`, `sync`, `macros`), `hyper 1` (`http1`, `server`), `http-body 1`, `http-body-util 0.1`, `bytes 1`, `serde`/`serde_json 1`, `clap 4` (`derive`), `block2 0.6`, `dispatch2 0.3`, `objc2-core-foundation 0.3`, `libtest-mimic 0.8` (tests that must own the main thread).

**Spec:** `docs/superpowers/specs/2026-11-14-rust-port-design.md`. Working code for every unfamiliar piece is in the sibling repo `../async-swift-rs/docs/superpowers/specs/spikes/tokio-guest/src/bin/` (`nothread.rs`, `nw_hyper.rs`, `stateless.rs`, `schema.rs`); read the named spike before each task. Do not add a dependency on `async-swift-rs`.

## Global Constraints

- No thread is created by this code, tokio, hyper or rmcp. Blocking work goes to a libdispatch global queue. A test asserts the process thread count.
- `crates/core` contains no `cfg(target_os)` code and no Apple crate.
- macOS 13 is the deployment floor (`MACOSX_DEPLOYMENT_TARGET=13.0` in the build).
- The Swift port under `macos/` and `Package.swift` are not modified.
- Work on the branch `pbk/rust-port`. The owner allowed local commits there (2026-11-14) and wants them squashed: commit per task while working, then squash the step into one commit at the end. Never push.
- Tests that touch launchd must not run in this step (none are planned).
- Match the Swift port's observable behaviour described in `macos/README.md` § Endpoints for the stateless mode: `POST /mcp` and `/mcp/`; answers are `application/json`; `Accept` without `application/json` (or missing) → 406; non-JSON `Content-Type` → 415; a JSON-RPC batch (top-level array) → 400; `GET` or `DELETE` without a session id → 405 with `Allow: POST`; a notification → 202 with an empty body.
- `Wait` with only `duration` answers the text `Waited for <duration> seconds.` after sleeping that long.

## Review Focus

- A client that disconnects mid-request: the per-request service must end and nothing may leak (the suite cannot see leaks; the reviewer reads `OneShot` and the connection task for it).
- An idle server: with only the listener open, no run loop timer may be armed (reviewer checks every `set_tick`/`kick` call site against that rule).
- A response larger than 256 KiB: write back-pressure in `NwIo` (Task 4's large-echo test covers the mechanism; the reviewer checks that the HTTP path cannot bypass it).
- A request body split across many TCP segments, and two requests on one keep-alive connection (Task 4 and Task 8 tests).
- A tool that sleeps via a tokio timer under the slow tick only: timers must still fire (Task 3 test `tokio_sleep_fires_under_slow_tick`).

---

### Task 1: Workspace skeleton

**Files:**
- Create: `Cargo.toml` (workspace: `crates/core`, `crates/macos`, `crates/cli`, `crates/parity`; `resolver = "2"`; `[workspace.dependencies]` with the versions of Tech Stack)
- Create: `crates/core/Cargo.toml` (package `desktop-mcp-core`), `crates/core/src/lib.rs`
- Create: `crates/macos/Cargo.toml` (package `desktop-mcp-macos`), `crates/macos/src/lib.rs`
- Create: `crates/cli/Cargo.toml` (package `desktop-mcp-cli`, `[[bin]] name = "desktop-mcp"`), `crates/cli/src/main.rs`
- Create: `crates/parity/Cargo.toml` (package `desktop-mcp-parity`, `publish = false`), `crates/parity/src/lib.rs`
- Create: `.cargo/config.toml` with `[env] MACOSX_DEPLOYMENT_TARGET = "13.0"`
- Modify: `.gitignore` (add `/target/`)

**Interfaces:**
- Produces: the four packages by those names; `desktop-mcp-core` has modules `guest`, `contract`, `mcp`, `http` (empty for now).

- [ ] **Step 1:** Create the files; `crates/cli/src/main.rs` is `fn main() {}`.
- [ ] **Step 2:** Run `cargo build --workspace`. Expected: builds with no warnings.
- [ ] **Step 3:** Run `swift build` from the repository root. Expected: still builds (the Swift port is untouched).
- [ ] **Step 4:** Commit (`Rust port: workspace skeleton`).

### Task 2: The guest runtime (core)

**Files:**
- Create: `crates/core/src/guest.rs`
- Test: `crates/core/tests/guest.rs`

**Interfaces:**
- Produces:
```rust
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Tick { Off, Slow, Fast }            // Slow = 1 s, Fast = 50 ms
/// What the platform's main loop does for the guest runtime. Main thread only.
pub trait MainLoop: 'static {
    fn kick(&self);                          // one runtime turn soon; calls coalesce
    fn set_tick(&self, rate: Tick);          // repeated turns
}
pub struct Guest { /* tokio current_thread runtime with time enabled */ }
impl Guest {
    pub fn new() -> Guest;
    pub fn enter(&self) -> tokio::runtime::EnterGuard<'_>;
    /// One non-blocking turn: runs what `tokio::spawn` queued and fires due timers.
    pub fn turn(&self);                      // rt.block_on(tokio::task::yield_now())
    /// Wraps `future` in a LocalSet; every poll is followed by `main_loop.kick()`.
    pub fn root<F: Future + 'static>(&self, main_loop: Rc<dyn MainLoop>, future: F) -> impl Future<Output = F::Output> + 'static;
}
/// Counts what can own a timer (connections, requests in flight) and sets the tick from it:
/// requests > 0 → Fast; else connections > 0 → Slow; else Off.
pub struct Activity { /* Rc<dyn MainLoop>, two Cell<usize> */ }
impl Activity {
    pub fn new(main_loop: Rc<dyn MainLoop>) -> Rc<Activity>;
    pub fn connection(self: &Rc<Self>) -> ActivityGuard;   // decrements on drop
    pub fn request(self: &Rc<Self>) -> ActivityGuard;
}
```

- [ ] **Step 1: Write the failing tests** in `crates/core/tests/guest.rs`, using a `FakeLoop` that records `kick` count and the last `Tick` (no platform code; the test pumps by hand):
  - `spawn_local_task_runs_when_root_is_polled`: a `spawn_local` task inside `root` sets a flag; polling the root once with a noop waker sets it.
  - `every_poll_of_root_kicks`: after two polls `FakeLoop.kicks == 2`.
  - `tokio_spawn_runs_on_turn_not_on_poll`: a `tokio::spawn` task's flag is false after polling root, true after `guest.turn()`.
  - `timer_fires_on_a_turn_after_its_deadline`: `tokio::time::sleep(20 ms)` in a `spawn_local` task; poll, `std::thread::sleep(30 ms)`, `turn()`, poll → done.
  - `activity_sets_tick`: none → `Off`; one connection → `Slow`; plus one request → `Fast`; drop request → `Slow`; drop connection → `Off`.
- [ ] **Step 2:** Run `cargo test -p desktop-mcp-core --test guest`. Expected: FAIL to compile (`guest` items missing).
- [ ] **Step 3:** Implement `guest.rs` (see `nothread.rs` and `KickAfterPoll` in `nw_hyper.rs`).
- [ ] **Step 4:** Run the same command. Expected: 5 passed.
- [ ] **Step 5:** Commit (`Rust port: guest runtime`).

### Task 3: The main-loop executor (macOS)

**Files:**
- Create: `crates/macos/src/main_loop.rs`
- Test: `crates/macos/tests/main_loop.rs` (`harness = false`, `libtest-mimic`, so tests run on the main thread, one after another)

**Interfaces:**
- Consumes: `Guest`, `MainLoop`, `Tick` from Task 2.
- Produces:
```rust
/// Runs the future `build` returns on the main thread until it completes, serving the main run loop.
/// The future is polled inside a CFRunLoopSource callback; wakes signal the source from any thread.
/// Panics off the main thread. A panic in the future is resumed here.
pub fn run<F: Future + 'static>(build: impl FnOnce(Rc<dyn MainLoop>) -> F) -> F::Output;
/// Runs `work` on a libdispatch global queue (default QoS) and resolves on the caller's task.
pub fn offload<T: Send + 'static>(work: impl FnOnce() -> T + Send + 'static) -> impl Future<Output = T>;
pub fn thread_count() -> u32;    // mach task_threads; used by tests and later by diagnostics
```
`run` creates the `Guest`, holds its `EnterGuard`, installs three `CFRunLoopTimer`s in common modes (kick: one-shot re-armed by `kick()`; fast 50 ms; slow 1 s; `set_tick` arms one of the last two and parks the other at a far date), wraps the future with `Guest::root`, and loops `CFRunLoop::run_in_mode(default, 1e10, false)` until the future is done; it then invalidates the source and timers. Nested run loops: track run loop entry depth with a `CFRunLoopObserver` and stop only the loop `run` itself started (copy the structure of `../async-swift-rs/src/runloop.rs`).

- [ ] **Step 1: Write the failing tests:**
  - `run_returns_the_output`: `run(|_| async { 7 }) == 7`.
  - `wake_from_another_thread`: a `tokio::sync::oneshot` sent from a `std::thread` after 30 ms completes the future in under 200 ms.
  - `no_turns_while_idle`: with tick `Off`, awaiting a oneshot sent after 300 ms causes at most 2 runtime turns (expose a test-only turn counter).
  - `tokio_sleep_fires_under_slow_tick`: `set_tick(Slow)`, `tokio::time::sleep(100 ms)` completes within 1.3 s.
  - `tokio_sleep_fires_under_fast_tick`: `set_tick(Fast)`, `sleep(100 ms)` completes within 220 ms.
  - `offload_runs_off_main_and_returns`: `offload(|| pthread_main_np() == 1).await == false`, and the awaiting code is back on the main thread.
  - `no_threads_are_kept`: `thread_count()` after `run` of a future that does spawn_local + sleep + channel work equals the count before, plus at most 1 (an idle dispatch worker).
  - `nested_run_loop_is_not_stopped`: a run loop timer callback that runs the loop for 50 ms while the root future completes does not hang and returns the output.
  - `panic_is_resumed`: `catch_unwind(|| run(|_| async { panic!("x") }))` is `Err`.
- [ ] **Step 2:** Run `cargo test -p desktop-mcp-macos --test main_loop`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement `main_loop.rs`.
- [ ] **Step 4:** Run the same command 10 times in a row (`for i in $(seq 10); do cargo test -q -p desktop-mcp-macos --test main_loop || break; done`). Expected: 9 passed each time.
- [ ] **Step 5:** Commit (`Rust port: main run loop executor`).

### Task 4: Network.framework listener and `NwIo`

**Files:**
- Create: `crates/macos/src/nw.rs` (the `extern "C"` declarations, `NwListener`, `NwIo`)
- Create: `crates/core/src/io.rs` (the `Listener` trait)
- Test: `crates/macos/tests/nw.rs` (`harness = false`)

**Interfaces:**
- Produces (core):
```rust
pub trait Listener: 'static {
    type Io: hyper::rt::Read + hyper::rt::Write + Unpin + 'static;
    fn accept(&mut self) -> impl Future<Output = Option<Self::Io>>;
}
```
- Produces (macos):
```rust
pub struct NwListener { /* … */ }
impl NwListener {
    /// Binds `host` (a name or address; "localhost" binds 127.0.0.1) and `port` (0 = system-assigned).
    /// Resolves when the listener is ready, with the error text when it fails.
    pub async fn bind(host: &str, port: u16) -> Result<NwListener, String>;
    pub fn port(&self) -> u16;
}
impl Listener for NwListener { type Io = NwIo; /* … */ }
pub struct NwIo { /* connection + Rc<RefCell<state>> */ }   // hyper::rt::Read + Write
```
All Network.framework callbacks are delivered on the main queue. `NwIo` is the one in `nw_hyper.rs`: reads queue `dispatch_data` (`dispatch_data_create_concat`, fill from `dispatch_data_apply`, `dispatch_data_create_subrange`); writes copy with `dispatch_data_create` and return `Pending` while 256 KiB or more are unsent. Dropping `NwIo` cancels the connection; dropping `NwListener` cancels the listener (`nw_listener_cancel`).

- [ ] **Step 1: Write the failing tests** (each inside `main_loop::run`; the client is a `std::net::TcpStream` on a `std::thread`; an echo task reads with `poll_fn` over `hyper::rt::Read` and writes back):
  - `binds_a_system_port`: `bind("127.0.0.1", 0)` gives `port() != 0`, and a `TcpStream::connect` to it succeeds.
  - `bind_fails_on_a_port_in_use`: binding the same port twice yields `Err` for the second.
  - `echoes_small`: 5 bytes round trip.
  - `echoes_one_megabyte`: 1 MiB sent in 1 KiB writes comes back identical (many regions in, back-pressure out).
  - `read_sees_eof`: after the client shuts down its write side, a read returns 0 bytes.
  - `write_after_peer_closed_errors`: after the client closes, a later write returns `Err` within 1 s of retries.
- [ ] **Step 2:** Run `cargo test -p desktop-mcp-macos --test nw`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement `io.rs` and `nw.rs`.
- [ ] **Step 4:** Run the same command. Expected: 6 passed.
- [ ] **Step 5:** Commit (`Rust port: Network.framework listener and hyper I/O`).

### Task 5: The contract and the MCP server (core)

**Files:**
- Create: `crates/core/src/contract.rs`, `crates/core/src/mcp.rs`
- Test: `crates/core/tests/mcp.rs`, `crates/core/tests/snapshots/tools.json`

**Interfaces:**
- Produces:
```rust
// contract.rs
pub const SPEC: &str = include_str!("../../../spec/tools.json");
/// A tool reduced to what must agree between ports.
#[derive(Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct ToolMeaning { pub name: String, pub description: String, pub annotations: String /* canonical JSON */, pub properties: Vec<PropertyMeaning> }
#[derive(Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct PropertyMeaning { pub name: String, pub types: Vec<String> /* sorted JSON types, "null" included */, pub item_type: Option<String>, pub enum_values: Option<Vec<String>> /* canonical JSON, nulls dropped */, pub default: Option<String> /* canonical JSON */, pub required: bool }
/// Reduces one `tools/list` entry (either form: the spec's or schemars') to its meaning.
/// Resolves `$ref` into `$defs`, reads `anyOf` and `type` arrays alike, ignores `$schema`, `format`, `title`.
pub fn meaning(tool: &serde_json::Value) -> ToolMeaning;

// mcp.rs
/// The tools' behaviour, implemented per platform. Not Send.
pub trait Desktop: 'static {
    fn sleep(&self, duration: std::time::Duration) -> Pin<Box<dyn Future<Output = ()>>>;
}
pub struct Server { /* tool_router, Rc<dyn Desktop>, HashSet of enabled tool names */ }
impl Server {
    /// `tools`: the names to serve (step 1 passes ["Wait"]; later steps pass the configured set).
    pub fn new(desktop: Rc<dyn Desktop>, tools: &[&str]) -> Server;
}
// #[tool_router] impl Server { #[tool(name = "Wait", description = <the spec's text>, annotations(...))] async fn wait(&self, Parameters(WaitArgs)) -> Result<CallToolResult, ErrorData> }
// #[tool_handler] impl ServerHandler for Server — server name "desktop-mcp", version env!("CARGO_PKG_VERSION"), tools capability.
/// Answers one JSON-RPC message with a fresh service: `rmcp::transport::OneshotTransport` +
/// `serve_directly_with_ct`, which is how rmcp's own Streamable HTTP server implements stateless mode
/// (`transport/streamable_http_server/tower.rs`). If `OneshotTransport` is not available without the HTTP
/// features, use the equivalent `OneShot` of `stateless.rs`.
/// `None` for notifications and responses. Dropping the future ends the service.
pub async fn answer_stateless(server: Server, message: rmcp::model::ClientJsonRpcMessage) -> Option<rmcp::model::ServerJsonRpcMessage>;
```
`WaitArgs` declares every property of the spec's `Wait` (`duration`, `until`, `timeout`, `app`, `title`, `role`) with the spec's types and defaults; in this step any `until` other than null answers an error result with the text `Wait until '<value>' is not available in this build.`

- [ ] **Step 1: Write the failing tests** (tokio `current_thread` + `LocalSet`, a `FakeDesktop` whose `sleep` records the duration and returns at once):
  - `meaning_reads_both_forms`: `meaning` of the spec's `Scroll` entry equals `meaning` of the schemars form of `Scroll` printed by `schema.rs` (paste both JSON values into the test).
  - `wait_matches_the_contract`: `meaning` of the server's `tools/list` entry for `Wait` equals `meaning` of the spec's `Wait`.
  - `tools_list_snapshot`: the server's `tools/list` result equals `tests/snapshots/tools.json` (write the file from the first run, review it, keep it).
  - `initialize_is_answered`: result has `serverInfo.name == "desktop-mcp"` and the requested `protocolVersion` `2025-11-25`.
  - `wait_sleeps_and_reports`: `tools/call` `Wait` `{"duration": 3}` → text `Waited for 3 seconds.`, and `FakeDesktop` saw 3 s.
  - `wait_without_duration_is_an_error_result`: `{}` → `isError: true`, text `Missing required argument 'duration'`.
  - `unknown_tool_is_a_jsonrpc_error`: code `-32602`.
  - `notification_gets_no_answer`: `notifications/initialized` → `None`.
- [ ] **Step 2:** Run `cargo test -p desktop-mcp-core --test mcp`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement `contract.rs` and `mcp.rs`.
- [ ] **Step 4:** Run the same command. Expected: 8 passed.
- [ ] **Step 5:** Commit (`Rust port: contract comparison and the MCP server with Wait`).

### Task 6: The HTTP app (core)

**Files:**
- Create: `crates/core/src/http.rs`
- Test: `crates/core/tests/http.rs`

**Interfaces:**
- Consumes: `Listener` (Task 4), `Server`, `answer_stateless` (Task 5), `Activity` (Task 2).
- Produces:
```rust
pub struct App { pub make_server: Box<dyn Fn() -> Server>, pub activity: Rc<Activity> }
/// One request. Pure routing over `http` types, so it is testable without a socket.
pub async fn handle<B>(app: &App, request: http::Request<B>) -> http::Response<http_body_util::Full<bytes::Bytes>>
where B: http_body::Body, B::Error: std::fmt::Debug;
/// Accepts connections until the listener ends; one `spawn_local` task per connection, hyper HTTP/1.1.
pub async fn serve<L: Listener>(listener: L, app: Rc<App>);
```
`handle` holds an `activity.request()` guard while a JSON-RPC request is answered; `serve` holds an `activity.connection()` guard per connection. JSON-RPC parse errors answer 400 with a JSON-RPC error body (`-32700`).

- [ ] **Step 1: Write the failing tests** for `handle` (requests built with `http::Request::builder()` and `Full` bodies):
  - `post_tools_list_is_json_200` (`Content-Type: application/json` on the answer; path `/mcp` and `/mcp/` both).
  - `missing_accept_is_406`, `accept_without_json_is_406`, `accept_with_json_among_others_is_ok` (`application/json, text/event-stream`).
  - `non_json_content_type_is_415`.
  - `batch_is_400`, `invalid_json_is_400`.
  - `get_is_405_with_allow_post`, `delete_is_405_with_allow_post`.
  - `notification_is_202_with_empty_body`.
  - `other_path_is_404`.
  - `request_guard_is_held_only_during_the_call`: `FakeLoop` sees `Fast` while a `Wait` whose `FakeDesktop::sleep` is pending is in flight, and not after.
- [ ] **Step 2:** Run `cargo test -p desktop-mcp-core --test http`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement `http.rs`.
- [ ] **Step 4:** Run the same command. Expected: 12 passed.
- [ ] **Step 5:** Commit (`Rust port: stateless HTTP app on hyper`).

### Task 7: The binary

**Files:**
- Modify: `crates/cli/src/main.rs`
- Create: `crates/macos/src/desktop.rs` (the macOS `Desktop`: `sleep` on a dispatch timer source, exact)
- Test: `crates/macos/tests/desktop.rs` (`harness = false`)

**Interfaces:**
- Produces: `desktop_mcp_macos::MacDesktop` (`impl Desktop`), and the command line
  `desktop-mcp [serve] --transport streamable-http [--host <h>] [--port <p>]` (defaults `localhost`, `8000`; `--transport stdio` exits 2 with `stdio is not available in this build yet`). On start it prints one line to stderr: `desktop-mcp (rust) listening on http://<host>:<port>/mcp`. SIGINT and SIGTERM (dispatch signal sources) end the root future; the process exits 0 by returning from `main`.

- [ ] **Step 1: Write the failing test** `sleep_is_exact`: `MacDesktop.sleep(120 ms)` under tick `Off` completes in 120–160 ms.
- [ ] **Step 2:** Run `cargo test -p desktop-mcp-macos --test desktop`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement `desktop.rs`, then `main.rs` (clap; `main_loop::run(|main_loop| async { NwListener::bind → http::serve, raced with the signal future })`).
- [ ] **Step 4:** Run the test. Expected: 1 passed. Then run `cargo run -q -p desktop-mcp-cli -- serve --transport streamable-http --port 18431 &`, `curl -s -X POST localhost:18431/mcp -H 'Accept: application/json' -H 'Content-Type: application/json' -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'`, then `kill -TERM %1; wait %1; echo $?`. Expected: a JSON result naming `Wait`, then `0`.
- [ ] **Step 5:** Commit (`Rust port: serve command`).

### Task 8: The parity suite

**Files:**
- Create: `crates/parity/src/lib.rs` (harness), `crates/parity/tests/stateless.rs`
- Create: `scripts/parity.sh`

**Interfaces:**
- Produces: `struct ServerUnderTest` — `start()` reads `DESKTOP_MCP_BIN` (required; the test fails with a clear message when unset), picks a free port by binding and dropping a `TcpListener`, runs `<bin> serve --transport streamable-http --host 127.0.0.1 --port <p>` with `HOME` set to a fresh temp directory (the scratch-HOME rule of this repository), waits until the port accepts, and kills the child on drop. `post(json) -> (status, headers, body)`, `request(method, path, headers, body)`. The client is `std::net::TcpStream` with hand-written HTTP/1.1 (no HTTP crate, so the suite does not share code with the server).
- `scripts/parity.sh` builds both binaries (`swift build -c release`, `cargo build --release -p desktop-mcp-cli`) and runs `cargo test -p desktop-mcp-parity` once per binary, printing which binary each run used.

- [ ] **Step 1: Write the tests** (they are the specification; each must hold for both binaries unless marked):
  - `initialize_answers_with_the_requested_version` (`2025-11-25`).
  - `tools_list_contains_wait_with_the_contract_meaning`: the `Wait` entry's `contract::meaning` equals the spec's (the Swift binary lists all tools; only `Wait` is compared in this step).
  - `wait_one_second`: answers `Waited for 1 seconds.` after 0.9–1.6 s.
  - `two_requests_on_one_connection`: keep-alive, both answered.
  - `concurrent_requests_are_independent`: a `Wait` of 1 s and a `tools/list` sent together on two connections; `tools/list` answers in under 300 ms.
  - `large_body_in_many_segments`: a 200 KiB `tools/list` request (padding in an ignored `_pad` param) written in 1 KiB chunks is answered.
  - `status_codes`: 406 (no `Accept`), 415, 400 (batch), 405 + `Allow: POST` (GET and DELETE), 202 (notification), 404 (`/nope`).
  - `server_exits_zero_on_sigterm`.
  - `idle_server_does_not_spin` (Rust binary only, by checking the stderr banner contains `(rust)`): CPU time of the child (`proc_pid_rusage` or `ps -o cputime`) grows by less than 20 ms over 3 idle seconds.
- [ ] **Step 2:** Run `DESKTOP_MCP_BIN=$PWD/.build/release/desktop-mcp cargo test -p desktop-mcp-parity`. Expected: all pass against the Swift binary (the Rust-only test reports itself skipped). A failure here is a wrong test: fix the test, not the Swift port.
- [ ] **Step 3:** Run `DESKTOP_MCP_BIN=$PWD/target/release/desktop-mcp cargo test -p desktop-mcp-parity`. Expected: all pass. Fix the Rust port where they do not.
- [ ] **Step 4:** Run `scripts/parity.sh`, then `cargo test --workspace` and `swift test`. Expected: all green; report any failure by name.
- [ ] **Step 5:** Commit (`Rust port: parity suite for the stateless transport`).

### Task 9: Documentation

**Files:**
- Modify: `README.md` (table: a row for the Rust macOS port, "in progress"; Windows "planned, Rust"; Layout: the workspace and `crates/`; "The tool contract": ports agree on names, parameters, defaults and descriptions, compared by meaning, schema form is each SDK's; Principles: "the official MCP SDK of the port's language")
- Create: `crates/README.md` (what step 1 serves, how to run it and `scripts/parity.sh`, what is not there yet)

- [ ] **Step 1:** Make the edits. State only what exists after Task 8.
- [ ] **Step 2:** Run `cargo build --workspace 2>&1 | grep -c warning`. Expected: `0`.
- [ ] **Step 3:** Commit (`Rust port: README for step 1`).
