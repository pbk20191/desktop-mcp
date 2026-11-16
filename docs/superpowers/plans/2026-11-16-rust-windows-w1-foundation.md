# Rust Windows W1: the core on Windows, the main loop, stdio and the headless tools — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `desktop-mcp.exe serve` (stdio) runs on Windows 11 on the shared core and answers `Shell`, `Scrape`,
`Clipboard`, `Notification` and `Wait` (duration), with Windows wording, over a DispatcherQueue main loop and the
Windows thread pool.

**Architecture:** a new crate `crates/windows` implements the core's `guest::MainLoop` over
`Windows.System.DispatcherQueue`, a cancellable stdio byte stream, child processes on overlapped named pipes, and
the headless part of `mcp::Desktop`. `core` gains a platform hook (`mcp::Platform`) through which a platform
replaces system-specific tool text and adds its own tools; macOS keeps the core's current text by default, so its
`tools/list` does not change.

**Tech Stack:** Rust 1.99 stable, `windows` 0.62 (WinRT + Win32), rmcp 3.10 (`local`), tokio current-thread guest,
hyper 1, `html5ever` for HTML.

**Spec:** `docs/superpowers/specs/2026-11-16-rust-port-windows-design.md`

## Roadmap (one plan each, written when the previous one lands)

| Phase | Contents |
|---|---|
| **W1 (this plan)** | core green on Windows; platform hook; main loop; offload/timers/waits; stdio; Shell; Scrape; Clipboard; Notification; Wait(duration); cli wiring for stdio |
| W2 | HTTP over `StreamSocketListener`, HTTPS with `schannel`, parity transport tests on Windows |
| W3 | Desktop read: UIA tree, Snapshot (+ virtual desktops), Screenshot, OCR, Window list, DisplayInventory, Wait(window/element/app) |
| W4 | Desktop act: Click, Type, Scroll, Move, Shortcut, App, Window actions, MultiSelect, MultiEdit |
| W5 | Control: input monitor, state machine, overlay, capture flash, gate (core dispatch hook), notifier, ControlStatus |
| W6 | Registry, Process; `.mcpb` script; parity reference for Windows; README; retire notes |
| M (needs a Mac) | Move the macOS-only tools and their `Desktop` methods into `crates/macos`; macOS supplies its text through the hook instead of core defaults |

**Deviation from the spec, recorded:** the spec moves the macOS-only tools out of `core` first. That code cannot be
built or tested on this Windows host, so it becomes phase M. Until then `core` keeps them, and each platform's tool
list (in `crates/cli`) decides what is offered — as `cli`'s `TOOLS` already does.

## Global Constraints

- Windows 11 build 22621 or later; x64 and arm64.
- tokio starts no thread: `current_thread`, timer driver only, no I/O driver, no `spawn_blocking`.
- No Windows App SDK.
- WinRT first; Win32 only for stdio, child processes, server TLS, and APIs that are Win32/COM by nature.
- Child-process pipes: named pipes, our end `FILE_FLAG_OVERLAPPED` + `CreateThreadpoolIo`; no anonymous pipes.
- Blocking console reads are cancellable with `CancelSynchronousIo`; `ERROR_OPERATION_ABORTED` is a clean end.
- `DESKTOP_MCP_AUTH_KEY` and `DESKTOP_MCP_OAUTH_CLIENT_SECRET` never reach a child process.
- Shell prefers `pwsh.exe` (PATH incl. `WindowsApps` alias, then `%ProgramFiles%\PowerShell\7\pwsh.exe`), else
  `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`; on fallback the result says so once per run with
  `winget install Microsoft.PowerShell`.
- Notifications: `Windows.UI.Notifications.ToastNotificationManager`, borrowed Windows PowerShell AUMID
  (`{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe`); nothing written to HKCU.
- macOS `tools/list` output stays byte-identical (`crates/core/tests/snapshots/tools.json`).
- Automated tests never inject input, install hooks, show windows or touch the clipboard of the live desktop;
  clipboard and toast are verified by the owner by hand (listed in Task 9).
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **stdin closed while a request runs** — answers already-read requests, then exits 0 (Task 5 test
   `eof_mid_request_still_answers`).
2. **Ctrl+C while blocked on a console read** — process exits within 1 s, no thread left in `ReadFile` (Task 5 test
   `cancel_unblocks_pending_read`).
3. **A Shell command that starts a grandchild and times out** — the whole tree is killed and the call returns the
   partial output (Task 6 test `timeout_kills_the_tree`).
4. **Non-UTF-8 / UTF-16 console output from PowerShell 5.1** — output decodes correctly (Task 6 test
   `non_ascii_output_round_trips`, forcing UTF-8 with `[Console]::OutputEncoding`).
5. **A burst of wakes from pool threads** — one queued callback per burst, no unbounded `TryEnqueue` growth (Task 3
   test `kicks_coalesce`).

---

### Task 1: core's tests pass on Windows

Four core tests fail on Windows today (`cargo test -p desktop-mcp-core --no-fail-fast`):
`config::load_validates_and_resolves_relative_tls_paths`,
`launch_agent::the_plist_has_the_originals_keys_and_escapes_its_text`, `mcp::tools_list_snapshot`,
`open::a_file_url_is_a_path`.

**Files:**
- Modify: whichever of `crates/core/src/{config,launch_agent,open}.rs` and `crates/core/tests/{config,launch_agent,mcp,open}.rs` the cause lives in
- Create: `.gitattributes` entry `crates/core/tests/snapshots/*.json -text` if the snapshot fails on CRLF

- [ ] **Step 1:** Run the four tests one by one; record each cause (path separators, drive letters, CRLF checkout, `file://` URL form).
- [ ] **Step 2:** Fix each at its cause. Product code changes only where the Windows behaviour is wrong (e.g. `file:///C:/x` must give `C:\x`); test-only assumptions (POSIX absolute paths) become `cfg`-specific expectations. No change to macOS behaviour.
- [ ] **Step 3:** Run `cargo test -p desktop-mcp-core --no-fail-fast` — Expected: all pass.
- [ ] **Step 4:** `cargo check -p desktop-mcp-core --target aarch64-apple-darwin` (after `rustup target add aarch64-apple-darwin`) — Expected: compiles (macOS untouched).
- [ ] **Step 5:** Commit `Make the core's tests pass on Windows`.

### Task 2: the platform hook in core

**Files:**
- Create: `crates/core/src/platform.rs`
- Modify: `crates/core/src/mcp.rs` (`Server::new`), `crates/core/src/lib.rs`, `crates/core/src/shell.rs` (`ShellMode::Powershell`)
- Modify: every `Server::new` call site (`crates/cli/src/main.rs`, `crates/core/tests/*`)
- Test: `crates/core/tests/platform.rs`

**Interfaces:**
- Produces:
  ```rust
  // crates/core/src/platform.rs
  pub struct ToolText {
      pub description: Option<&'static str>,
      /// JSON-pointer-like path into the input schema ("/properties/mode") → replacement schema fragment.
      pub schema_patches: Vec<(&'static str, serde_json::Value)>,
  }
  pub type PlatformToolCall = Box<dyn Fn(serde_json::Map<String, serde_json::Value>, tokio_util::sync::CancellationToken)
      -> std::pin::Pin<Box<dyn std::future::Future<Output = rmcp::model::CallToolResult>>>>;
  pub struct PlatformTool { pub tool: rmcp::model::Tool, pub call: PlatformToolCall }
  pub trait Platform: 'static {
      /// Text replacing the core's (macOS) text for a shared tool; `None` keeps the core's.
      fn text(&self, tool: &str) -> Option<ToolText> { None }
      /// Tools only this platform has.
      fn tools(&self) -> Vec<PlatformTool> { Vec::new() }
  }
  /// The core's own text everywhere and no extra tools: what macOS uses until phase M.
  pub struct CoreText;
  impl Platform for CoreText {}
  ```
  `Server::new(desktop: Rc<dyn Desktop>, platform: Rc<dyn Platform>, tools: &[&str]) -> Server`
  `ShellMode::Powershell` (serde `"powershell"`), hidden from the core's schema (`#[schemars(skip)]`) so macOS's schema is unchanged.
- Platform tools are added to the router with `ToolRoute::new_dyn`; text is applied by editing `router.map[name].attr` (`description`, `input_schema`). The tool-name filter applies to both kinds.

- [ ] **Step 1: Write the failing tests** in `crates/core/tests/platform.rs` (use `tests/support`'s fake `Desktop`):
  - `core_text_keeps_the_snapshot`: `Server::new(fake, Rc::new(CoreText), ALL)` lists exactly `tests/snapshots/tools.json`.
  - `text_replaces_description_and_schema`: a platform returning `ToolText { description: Some("Runs PowerShell."), schema_patches: vec![("/properties/mode", json!({"type":"string","enum":["powershell"],"default":"powershell"}))] }` for `"Shell"` → `tools/list` shows that description and `mode.enum == ["powershell"]`.
  - `platform_tool_is_listed_and_called`: a platform tool `"Echo"` (input `{text: string}`) is listed and `tools/call` returns its text.
  - `filter_applies_to_platform_tools`: `Echo` absent when `tools` omits it.
  - `powershell_mode_deserializes`: `{"command":"x","mode":"powershell"}` reaches `Desktop::shell` with `ShellMode::Powershell`.
- [ ] **Step 2:** `cargo test -p desktop-mcp-core --test platform` — Expected: FAIL (module missing).
- [ ] **Step 3:** Implement `platform.rs` and the `Server::new` change; update call sites to pass `Rc::new(CoreText)`.
- [ ] **Step 4:** `cargo test -p desktop-mcp-core --no-fail-fast` — Expected: all pass, `tools_list_snapshot` unchanged.
- [ ] **Step 5:** `cargo check -p desktop-mcp-core -p desktop-mcp-macos --target aarch64-apple-darwin` — Expected: compiles.
- [ ] **Step 6:** Commit `Let a platform replace tool text and add its own tools`.

### Task 3: `crates/windows` and the main loop

**Files:**
- Create: `crates/windows/Cargo.toml` (`desktop-mcp-windows`; `[target.'cfg(windows)'.dependencies]` only, so the workspace still builds on macOS), `crates/windows/src/lib.rs`, `crates/windows/src/main_loop.rs`, `crates/windows/src/tuning.rs`
- Modify: root `Cargo.toml` (`members`, `windows` in `[workspace.dependencies]`)
- Test: `crates/windows/tests/main_loop.rs` (`harness = false`, runs on the main thread like `crates/macos/tests`)

**Interfaces:**
- Produces:
  ```rust
  // crates/windows/src/main_loop.rs
  /// Runs `body`'s future on this thread's DispatcherQueue until it completes; tokio is a guest.
  pub fn run<F, Fut>(body: F) -> Fut::Output where F: FnOnce(Rc<dyn MainLoop>) -> Fut, Fut: Future + 'static;
  /// How many guest-runtime turns ran in this process (tests, diagnostics).
  pub fn turns() -> usize;
  /// How many root-future polls were queued (tests): one per coalesced batch of wakes.
  pub fn queued_polls() -> usize;
  // tuning.rs
  pub const FAST_TICK: Duration = Duration::from_millis(50);
  pub const SLOW_TICK: Duration = Duration::from_secs(1);
  ```
- Mechanism: `CreateDispatcherQueueController(DQTYPE_THREAD_CURRENT, DQTAT_COM_NONE)`; root future polled from a
  `TryEnqueue`d handler; the waker is `Send`, sets an `AtomicBool` and enqueues only on false→true; ticks from one
  `DispatcherQueueTimer` (`Interval` = tick, stopped for `Off`) calling `Guest::turn`; message loop
  `GetMessageW`/`DispatchMessageW` until the root completes, then `ShutdownQueueAsync` drained and the loop exits.
  Model: `winasync` `src/dq/mod.rs` (read, not linked).

- [ ] **Step 1: Write the failing tests:**
  - `run_returns_the_output`: `run(|_| async { 7 }) == 7`.
  - `kicks_coalesce`: 1000 wakes from 8 test threads (`std::thread::scope`; only tokio is barred from owning threads) before the main loop runs → `queued_polls()` grows by ≤ 2.
  - `ticks_follow_activity`: with `Activity::request()` held for 300 ms, `turns()` grows by ≥ 4; after dropping it and an idle 1.2 s with no connection, `turns()` grows by 0.
  - `tokio_sleep_completes`: `tokio::time::sleep(100ms)` inside the root (with a request held) completes within 250 ms.
  - `no_tokio_thread`: thread count of the process before and after `run` of a root that uses tokio timers differs by no thread named `tokio-*`.
- [ ] **Step 2:** `cargo test -p desktop-mcp-windows --test main_loop` — Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run tests — Expected: PASS. `cargo check --workspace --target aarch64-apple-darwin` still compiles.
- [ ] **Step 5:** Commit `Drive the guest runtime from a DispatcherQueue on Windows`.

### Task 4: offload, exact sleep and handle waits on the thread pool

**Files:**
- Create: `crates/windows/src/pool.rs`
- Test: `crates/windows/tests/pool.rs`

**Interfaces:**
- Produces:
  ```rust
  /// Runs `work` on the Windows thread pool (`TrySubmitThreadpoolCallback`); resolves on the main thread.
  pub fn offload<T: Send + 'static>(work: impl FnOnce() -> T + Send + 'static) -> impl Future<Output = T>;
  /// Completes after `duration` (`CreateThreadpoolTimer`, window 0); dropping it cancels the timer.
  pub fn sleep(duration: Duration) -> impl Future<Output = ()>;
  /// Completes when `handle` is signalled (`CreateThreadpoolWait`); dropping it unregisters the wait.
  pub fn wait_for(handle: HANDLE) -> impl Future<Output = ()>;
  ```
  Dropping any of them must wait for an in-flight callback (`WaitForThreadpool*Callbacks(.., TRUE)`) before freeing.

- [ ] **Step 1: Tests:** `offload_returns_value`; `offload_panics_are_reported` (a panic in `work` resurfaces on the awaiting side, not an abort); `sleep_is_exact` (100 ms sleeps 95–150 ms); `dropped_sleep_never_fires`; `wait_for_event` (`CreateEventW`, set from a pool callback after 50 ms); `dropped_wait_unregisters` (drop, then set the event; no callback runs — counter stays 0).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Offload, sleep and wait on the Windows thread pool`.

### Task 5: stdio as a cancellable byte stream

**Files:**
- Create: `crates/windows/src/stdio.rs`
- Test: `crates/windows/tests/stdio.rs`

**Interfaces:**
- Produces:
  ```rust
  /// stdin/stdout as one stream for `core::stream::serve_stream`. Reads run as blocking `ReadFile` on one pool
  /// worker at a time; dropping the stream (or `cancel`) calls `CancelSynchronousIo` on that worker.
  pub struct StdIo { /* … */ }
  impl StdIo {
      pub fn stdio() -> StdIo;
      /// For tests: any readable and writable handles (an anonymous pipe pair stands in for a console).
      pub fn from_handles(input: OwnedHandle, output: OwnedHandle) -> StdIo;
      /// Ends a pending read now; later reads return EOF.
      pub fn cancel(&self);
  }
  impl hyper::rt::Read for StdIo; impl hyper::rt::Write for StdIo; impl desktop_mcp_core::io::Peer for StdIo; // peer_address None
  ```
  The worker records its thread handle (`DuplicateHandle` of `GetCurrentThread`) under a mutex while inside
  `ReadFile`; `cancel` calls `CancelSynchronousIo` on it and retries until the worker reports out (a cancel can race
  the call's start). `ERROR_OPERATION_ABORTED`, `ERROR_BROKEN_PIPE` and 0 bytes are EOF. Writes go through
  `WriteFile` on a pool worker, in order. (Anonymous pipes appear here only as test doubles for a console, not for
  child processes.)

- [ ] **Step 1: Tests:** `reads_and_writes` (pipe pair, `serve_stream` with a fake server echoes one JSON-RPC line);
  `eof_mid_request_still_answers` (write a request then close the writer: the answer arrives, then the stream ends);
  `cancel_unblocks_pending_read` (no data written; `cancel()`; the read future returns EOF within 1 s and the worker
  thread count returns to its baseline); `drop_cancels` (same through `drop`).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Serve stdio with cancellable blocking reads`.

### Task 6: Shell — PowerShell on overlapped named pipes

**Files:**
- Create: `crates/windows/src/process.rs` (spawn, pipes, job), `crates/windows/src/shell.rs` (resolution, environment, Windows text)
- Test: `crates/windows/tests/process.rs`, `crates/windows/tests/shell.rs`

**Interfaces:**
- Produces:
  ```rust
  // process.rs
  pub struct Spawn<'a> { pub program: &'a Path, pub args: &'a [OsString], pub env: Vec<(OsString, OsString)> }
  /// Starts the program in a new kill-on-close job with stdout/stderr on overlapped named pipes (thread-pool I/O),
  /// stdin NUL; collects both until exit or `timeout`; on timeout or drop terminates the job and cancels the reads
  /// (`CancelIoEx`).
  pub async fn run(spawn: Spawn<'_>, timeout: Duration) -> Result<desktop_mcp_core::shell::Run, String>;
  // shell.rs
  pub enum Interpreter { Pwsh(PathBuf), WindowsPowerShell(PathBuf) }
  /// `path_var` and `program_files` are injected for tests.
  pub fn resolve_interpreter(path_var: Option<&OsStr>, program_files: Option<&Path>, system_root: &Path, exists: &dyn Fn(&Path) -> bool) -> Interpreter;
  /// The child's environment: this process's, less `DESKTOP_MCP_AUTH_KEY` and `DESKTOP_MCP_OAUTH_CLIENT_SECRET`, plus `NO_COLOR=1`.
  pub fn child_environment() -> Vec<(OsString, OsString)>;
  pub fn text() -> desktop_mcp_core::platform::ToolText; // Windows Shell description + mode enum ["powershell"]
  pub const FALLBACK_NOTE: &str = "PowerShell 7 was not found, so this ran in Windows PowerShell 5.1. Install PowerShell 7 with: winget install Microsoft.PowerShell";
  ```
  Pipe names `\\.\pipe\desktop-mcp-<pid>-<counter>`, created `PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED |
  FILE_FLAG_FIRST_PIPE_INSTANCE`, `PIPE_REJECT_REMOTE_CLIENTS`, default DACL; child end opened with `CreateFileW`,
  inheritable, passed via `STARTUPINFOEXW` + `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` (only these handles inherited).
  Command line: `-NoLogo -NoProfile -NonInteractive -OutputFormat Text -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; <command>"`
  (the command quoted per `CommandLineToArgvW` rules).
  Exit status = process exit code; timed out → `timed_out: true`.
  Windows Shell description (exact): `"Execute commands on Windows in PowerShell (PowerShell 7 when installed, else Windows PowerShell 5.1). Use for file system, process management, system operations, and automation scripts. SECURITY WARNING: Commands are executed with the same permissions as the terminal/application running this server. Review and understand all actions before execution, especially those involving file operations, system modifications, or external processes."`

- [ ] **Step 1: Tests (process.rs):** `captures_stdout_stderr_and_status` (`cmd.exe /c "echo out & echo err 1>&2 & exit /b 3"` → `out`, `err`, 3); `timeout_kills_the_tree` (`cmd /c "start /b ping -n 30 127.0.0.1 & ping -n 30 127.0.0.1"`, timeout 1 s → returns ≤ 2 s, `timed_out`, and no `ping.exe` child of the job remains); `drop_kills` (drop the future after 200 ms; the job's process count goes to 0).
- [ ] **Step 2: Tests (shell.rs):** `prefers_pwsh_on_path`, `falls_back_to_program_files_pwsh`, `falls_back_to_windows_powershell` (fake `exists`); `secrets_removed` (`child_environment` lacks both names when set in this process); `non_ascii_output_round_trips` (real interpreter: `Write-Output '한글 ü'` → exactly that); `fallback_note_once` (two runs with a forced 5.1 interpreter: note in the first result only).
- [ ] **Step 3:** Run — FAIL. **Step 4:** Implement. **Step 5:** Run — PASS.
- [ ] **Step 6:** Commit `Run Shell in PowerShell over overlapped named pipes`.

### Task 7: Scrape — resolve, connect, HTML

**Files:**
- Create: `crates/windows/src/net.rs` (resolve, `SocketIo` over `StreamSocket`), `crates/windows/src/html.rs`
- Modify: root `Cargo.toml` (`html5ever`, `markup5ever_rcdom` workspace deps)
- Test: `crates/windows/tests/net.rs`, `crates/windows/tests/html.rs`

**Interfaces:**
- Produces:
  ```rust
  pub async fn resolve(host: &str) -> Result<Vec<IpAddr>, String>;            // DatagramSocket::GetEndpointPairsAsync, numeric hosts parsed directly
  pub struct SocketIo { /* StreamSocket + one read in flight */ }
  impl SocketIo { pub async fn connect(host: &str, port: u16, tls: bool) -> Result<SocketIo, String>; } // SocketProtectionLevel::Tls12 when tls
  impl hyper::rt::Read for SocketIo; impl hyper::rt::Write for SocketIo; impl desktop_mcp_core::io::Peer for SocketIo;
  pub fn parse(body: &[u8], charset: Option<&str>) -> Option<desktop_mcp_core::html::Element>; // html5ever; decode with encoding_rs, UTF-8 default
  ```
  `SocketIo` keeps one `ReadAsync` (64 KiB `Buffer`) in flight and completes it on the main thread through the
  operation's `Completed` handler + waker; writes `WriteAsync` in order; `shutdown` closes the output stream; drop
  cancels pending operations (`IAsyncInfo::Cancel`). W2 reuses `SocketIo` for accepted sockets.

- [ ] **Step 1: Tests:** `resolves_localhost` (contains `127.0.0.1` or `::1`); `numeric_host_is_not_looked_up`; `connects_to_a_local_listener` (std `TcpListener` on `127.0.0.1:0` on a pool thread echoes; `SocketIo` round-trips 100 KiB); `peer_closes_is_eof`; `parse_builds_the_tree` (`<p>a<b>c</b></p>` → html/body/p with text and b); `parse_decodes_charset` (`euc-kr` bytes for 한글).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Resolve, connect and parse HTML for Scrape on Windows`.

### Task 8: `WindowsDesktop` (headless), Clipboard and Notification

**Files:**
- Create: `crates/windows/src/desktop.rs`, `crates/windows/src/clipboard.rs`, `crates/windows/src/notification.rs`, `crates/windows/src/platform.rs`
- Test: `crates/windows/tests/desktop.rs`

**Interfaces:**
- Consumes: Tasks 3–7.
- Produces:
  ```rust
  pub struct WindowsDesktop;                 // impl desktop_mcp_core::mcp::Desktop
  pub struct WindowsPlatform;                // impl desktop_mcp_core::platform::Platform: text for Shell, Notification, Clipboard
  pub const TOOLS: &[&str] = &["Clipboard", "Notification", "Scrape", "Shell", "Wait"]; // W1's list
  ```
  `Desktop` methods: `sleep` → `pool::sleep`; `shell` → `shell` + `process::run` (mode must be `Powershell` or the
  default `Shell`, both PowerShell; `Osascript` → `Err("osascript is not available on Windows")`); `resolve`,
  `connect`, `parse_html` (offloaded), `background` → `offload`; `read_clipboard`/`write_clipboard` →
  `Windows.ApplicationModel.DataTransfer.Clipboard` on the main thread (text, `StorageItems` paths, bitmap → PNG
  via `BitmapEncoder` scaled to `image_max_side`); `notify` → toast XML `ToastGeneric` with title, message,
  subtitle as a second text line, sound `ms-winsoundevent:Notification.<Default|IM|Mail|Reminder|SMS>`. Every other
  method returns the empty/false value or `Err("not available on Windows yet")` (W3–W5 fill them).
  Notification Windows text: description `"Sends a Windows toast notification with a message, title, optional subtitle, and optional sound."`, `sound` enum `["Default","IM","Mail","Reminder","SMS"]`.

- [ ] **Step 1: Tests:** `osascript_mode_is_refused`; `notification_xml` (pure function `toast_xml(message, title, subtitle, sound) -> String`: escaping of `<&"`, subtitle line present only when given, sound URI); `clipboard_png_scaling` (pure function scaling a 3000×1000 BGRA buffer to 1568×523); `platform_text_applies` (a `Server` with `WindowsPlatform` lists Shell's Windows description and `mode` enum `["powershell"]`).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Implement the headless Desktop on Windows with toasts and the WinRT clipboard`.

### Task 9: cli wiring for Windows (stdio)

**Files:**
- Modify: `crates/cli/src/main.rs` (`#[cfg(windows)] fn serve`, Ctrl handler, per-platform `TOOLS`), `crates/cli/Cargo.toml`
- Create: `crates/windows/src/signals.rs`
- Create: `docs/superpowers/plans/2026-11-16-rust-windows-live-checks.md` (the owner's by-hand list)
- Test: `crates/cli/tests/windows_stdio.rs` (`#![cfg(windows)]`)

**Interfaces:**
- Produces: `signals::termination() -> impl Future<Output = ()>` (`SetConsoleCtrlHandler` for CTRL_C/BREAK/CLOSE → main queue). On Windows: `--transport stdio` serves; `streamable-http` → `Error: HTTP on Windows comes in a later build`, exit 1; `socket`/`connect` → the macOS refusal text.

- [ ] **Step 1: Tests** (spawn the built binary with piped stdio): `initialize_and_list` (`tools/list` names == `TOOLS`, Shell description is the Windows one); `shell_round_trip` (`Write-Output hi` → `Shell Response: hi` and `Status Code: 0`); `eof_exits_zero` (close stdin after one request: answer arrives, exit code 0 within 2 s); `ctrl_break_exits_zero` (spawned with `CREATE_NEW_PROCESS_GROUP`, `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT)` while idle on a blocked read → exit 0 within 1 s); `http_is_refused`.
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS; also `cargo test --workspace --no-fail-fast` on Windows and `cargo check --workspace --target aarch64-apple-darwin`.
- [ ] **Step 5:** Write the live-checks list: Clipboard read/write text, files and image; Notification with each sound; Shell under Claude Desktop with PowerShell 7 and with only 5.1.
- [ ] **Step 6:** Commit `Serve stdio on Windows from the cli`.
