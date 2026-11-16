# The Windows port on the Rust core

Status: draft for the owner's review (2026-11-16).

## Intent

desktop-mcp gets one Rust codebase for both systems. The macOS Rust port (`crates/core`, `crates/macos`,
`crates/cli`) already keeps its core free of platform code so that a Windows port can link it unchanged
(`crates/README.md`, "Starting the Windows port"; `2026-11-14-rust-port-design.md`). This document adds that Windows
port as a new crate, `crates/windows`, and retires the C# port on the `windows-port` branch.

**Why (owner):** one language for both ports, sharing the MCP server, transports, HTTP security, OAuth and the logic
and wording of the tools that both systems have.

## Decisions taken with the owner

- **(owner)** The Windows port is Rust on the shared core. The C# port (`windows/` on `windows-port`, P0 to P5) is
  replaced, not kept beside it. It stays as a reference for Windows behaviour (UIA, input, capture, control) and its
  spec `spec/windows/tools.json` as a reference for the Windows-only tools.
- **(owner)** Tools: the ones both systems have live in `core` with one schema shape and one logic; the
  ones only one system has live in that system's crate.
- **(owner)** The shared tools take the core's (macOS port's) argument structure. **(owner, option b)** One schema
  shape per shared tool; the platform supplies the text that is system-specific: description fragments, the values
  of system-specific enums (Shell's `mode`: `shell`/`osascript` on macOS, `powershell` on Windows; Notification's
  sounds; Wait's role vocabulary, `AXButton` vs UIA control types), and error wording. macOS `tools/list` and errors
  stay exactly as they are. Compatibility with
  CursorTouch/Windows-MCP or with the C# port is not a goal: neither was ever released.
- **(owner)** Built on WinRT and the Windows thread pool: the main thread runs a message loop with a
  `DispatcherQueue`; blocking work and I/O completions run on the Windows thread pool. tokio starts no thread, as on
  macOS (strategy A of `2026-11-16-rust-port-threading.md`).
- **(owner)** No Windows App SDK. The owner first allowed assuming its runtime is installed; once notifications
  were moved to `Windows.UI.Notifications` (below), nothing needed it, so the port uses only OS WinRT and Win32.
- **(owner)** WinRT first. Win32 only where WinRT has no class: stdio, child processes, the HTTPS server's TLS,
  and the APIs that are Win32/COM by nature (UI Automation, `SendInput`, window enumeration).
- **(owner)** The HTTPS server's TLS uses the `schannel` crate (SChannel, server mode).
- **(owner, considered and rejected)** Depending on `winasync` (github.com/pbk20191/winasync-rs) or
  `windows-threadpool-sys`: the parts this port lacks (stdio, child processes, server TLS) are in neither, and what
  they would save is a few hundred lines. `winasync`'s `dq` module is the model for the main loop, read, not linked.
  tokio's own net/process/stdin with `tokio-native-tls` would be the least code but needs tokio threads and its I/O
  driver, against the core's threading design.

## Goals

1. `desktop-mcp.exe` on Windows 11 (22H2, build 22621, or later; x64 and arm64) serves the shared tools over stdio
   and over HTTP/HTTPS with the core's authentication rules, with the same arguments and wording as on macOS.
2. The Windows-only tools listed below, in `crates/windows`.
3. `core` gains a way for a platform crate to add its own tools, and the macOS-only tools move out of `core` into
   `crates/macos`, so that `core` holds only shared tools.
4. A Claude Desktop extension (`.mcpb`) for Windows, as `macos/scripts/build-mcpb.sh` does for macOS.

## Non-goals

- Keeping the C# port alive, or migrating its tests one by one. Its behaviour is a reference; its code is not ported
  line by line.
- `install`/`uninstall`/`auth` commands: not on macOS either (see `crates/README.md`, "Not ported").
- The Windows App SDK (not used at all).
- Windows 10.

## Anti-goals

- tokio owning threads or an I/O driver. If the Windows loop needs that, it is a design change for both ports and
  comes back to the owner.
- Platform code in `core`, or `cfg(windows)` branches inside `core`'s tool logic.
- An HTTP transport reachable without authentication (the core already refuses it; keep it so).
- Injecting input, installing hooks, showing windows or touching the clipboard from automated tests on the
  developer's live desktop (the rule the C# port followed: such checks are listed for the owner to run by hand).

## Constraints

- Rust stable; `windows`/`windows-core` crates for WinRT and Win32 (versions picked by the builder; `winasync`
  uses 0.62).
- rmcp `local` feature, `!Send` futures, the core's guest runtime (`crates/core/src/guest.rs`): unchanged.
- The platform boundary the core already defines: `guest::MainLoop`, `mcp::Desktop`, the byte streams
  (`hyper::rt::Read + Write`), the listener.
- Minimum Windows 11 build 22621 (22H2), as the C# port; the virtual-desktop IIDs are per build.

## Approaches considered

| | What | Trade-off | |
|---|---|---|---|
| A | `winasync` as a git dependency: `tp` for TCP, waits, timers, offload; `dq` patterns for the loop | Saves the TCP adapter and the process-exit wait; adds an unlicensed git dependency and a second runtime (`tp::Runtime`) next to the DispatcherQueue | rejected |
| B | `windows-threadpool-sys` callback primitives, own futures and sockets | Published and MIT, but the most code (futures layer, accept/connect, hyper glue) | rejected |
| C | **WinRT first** (DispatcherQueue, `Windows.System.Threading`, `Windows.Networking.Sockets`), Win32 for the gaps, `schannel` for server TLS | Fewest dependencies; same gaps to write as A | **chosen** |

## Design

### Crates

| Crate | Change |
|---|---|
| `core` | A way for a platform to add tools (below). The macOS-only tools and their `Desktop` methods leave. |
| `macos` | Receives the macOS-only tools. Otherwise unchanged. |
| `windows` (new, `desktop-mcp-windows`) | `MainLoop`, I/O, listener, TLS, the `Desktop` implementation, the Windows-only tools. |
| `cli` | `cfg(target_os = "windows")` wiring next to the macOS one; the `#[cfg(not(target_os = "macos"))]` stubs go. |
| `parity` | Runs against the Windows binary for the shared tools where the macOS Swift port is not there to compare: it checks the contract (schemas, wording), not equality with Swift. |

### Platform tools **(my call: mechanism left to the builder within these rules)**

- `core::mcp::Server` takes, besides `Rc<dyn Desktop>`, the platform's extra tools: a list of rmcp tool
  definitions plus a handler (for example a second `ToolRouter` over a platform type, or a `dyn` trait that lists and
  calls them). `--include-tools`/`--exclude-tools` and the opt-in list (`OCR`) apply to both sets alike.
- The `tools/list` snapshot test becomes one snapshot per platform.
- Shared vs platform, from the two current lists:
  - **Shared (core):** App, Click, Clipboard, Move, Notification, Scrape, Scroll, Shell, Shortcut, Snapshot, Type,
    Wait, Window, OCR (opt-in). `Shell` runs `zsh` on macOS and PowerShell on Windows; its argument and result
    shape stay the core's.
  - **macOS only (to `crates/macos`):** Desktop (Spaces/Mission Control), Menu, Open, Permissions, RunShortcut,
    Spotlight.
  - **Windows only (`crates/windows`), from the C# port's set:** MultiSelect,
    MultiEdit, Screenshot, DisplayInventory. Their arguments follow the core's naming
    style; their behaviour follows `spec/windows/tools.json` and the C# implementation.
  - Not carried over: `FileSystem` (**owner**: a file-system MCP server already covers it, macOS has none, and
    `Shell` can do it), `PowerShell` (it is `Shell`), `WaitFor` (core `Wait` already waits for windows and
    elements), `Registry` and `Process` (**owner, 2026-11-16**: `Shell` does both in one PowerShell line,
    `Get-ItemProperty`/`Set-ItemProperty` and `Get-Process`/`Stop-Process`; they came from Windows-MCP compatibility,
    which is not a goal, and a dedicated tool adds no safety over `Shell`).
- Whether a tool is shared is decided by the schema shape, not by the name or the text: a tool is shared when one
  argument and result structure fits both systems, with the platform's text plugged in. The builder may move a tool
  between the lists above when that test fails, and records it.
- **(my call)** The platform text comes through the same platform hook as the extra tools (a table of strings and
  enum values keyed by tool, read when the router is built); core keeps the macOS text as data the macOS crate
  supplies, so the macOS snapshot does not change.

### Main loop

- `guest::MainLoop` over a `DispatcherQueue` on the main thread:
  - `run(future)`: create the dispatcher queue controller on the current thread, poll the future from queued work,
    run the message loop until the future completes, then shut the queue down.
  - `kick()`: one `TryEnqueue` per batch, coalesced by an atomic flag (cleared when the queued callback runs), so a
    burst of wakes costs one callback. The waker is `Send` (completions come from pool threads).
  - `set_tick(rate)`: one `DispatcherQueueTimer`, interval 50 ms or 1 s, stopped for `Off`. The core's rules for
    when the ticks run do not change.
- `Windows.System.DispatcherQueue` (`CreateDispatcherQueueController`, `DQTYPE_THREAD_CURRENT`); `winasync`'s `dq`
  uses the App SDK's `Microsoft.UI.Dispatching` with the same lifecycle and is the model.
- Ctrl+C, Ctrl+Break, console close: `SetConsoleCtrlHandler`, forwarded to the main queue; end with status 0 as on
  macOS for SIGINT/SIGTERM.
- The main thread is STA-free for WinRT purposes: initialise WinRT as the builder finds right for the
  DispatcherQueue and UI Automation (UIA calls go to pool threads in the MTA).

### Threads and blocking work

- `offload(closure)`: `Windows.System.Threading.ThreadPool.RunAsync` (or `TrySubmitThreadpoolCallback`), result
  back through a oneshot.
- Exact timers for tools (the core's `sleep`): `ThreadPoolTimer` or a thread-pool timer, completion through a
  oneshot.
- Process exit: a thread-pool wait on the process handle (`RegisterWaitForSingleObject` or `CreateThreadpoolWait`).
- Same rules as macOS for what is offloaded: anything that crosses into another process (UIA, clipboard, window
  enumeration of other processes), grows with its input, or blocks.

### Byte streams

- **TCP listener:** `StreamSocketListener` bound to the configured host and port; `ConnectionReceived` feeds a
  channel that `Listener::accept` reads. `PeerInfo` from the socket's remote address. The `--ip-allowlist` and
  Origin rules stay in core.
- **TCP stream:** an adapter from `StreamSocket.InputStream`/`OutputStream` (`IBuffer`, async operations) to
  `hyper::rt::Read`/`Write`: one read in flight, its buffer handed to hyper; writes queued. Half-close and
  cancellation on drop.
- **Outbound (Scrape, `Desktop::connect`, `resolve`):** `StreamSocket.ConnectAsync` with `SocketProtectionLevel` TLS
  1.2+ for `tls == true`; `resolve` from `DatagramSocket.GetEndpointPairsAsync` or `GetAddrInfoExW`. The core's
  SSRF checks on resolved addresses stay in core. `Desktop::connect` takes the host name, as on macOS (TLS
  validates against it), so the connect resolves again; that DNS-rebinding gap is shared with macOS and stays a
  core-level follow-up, not a Windows one.
- **HTTPS server:** `schannel` crate server credentials from `--ssl-certfile`/`--ssl-keyfile` (PEM, loaded into
  memory, never into a certificate store; if `schannel` needs a store, an in-memory store). The TLS stream wraps the
  TCP stream adapter. HTTP/1.1 only, as on macOS.
- **stdio:** stdin read by a blocking `ReadFile` loop on a dedicated pool work item (or thread-pool I/O when the
  handle is overlapped), chunks to the main loop through a channel; stdout written the same way. Same framing and
  end-of-input behaviour as macOS (`crates/macos/src/fd.rs`).
  **(owner)** A blocking console or pipe read must be cancellable: at shutdown (Ctrl+C, end of the server, a
  transport closed) the server cancels the pending `ReadFile`/`WriteFile` with `CancelSynchronousIo` on the worker
  thread that issued it (or `CancelIoEx` on the handle for overlapped I/O), so no pool thread stays blocked on stdin
  and the process exits promptly. The worker records its thread handle while a call is in flight so the cancel
  targets that call only; `ERROR_OPERATION_ABORTED` is treated as a clean end, not an error.- **Child processes (`Shell`):** **(owner)** `CreateProcessW` with the child's stdin/stdout/stderr on named pipes
  whose server end (ours) is opened `FILE_FLAG_OVERLAPPED` and bound to thread-pool I/O (`CreateThreadpoolIo`); the
  child's ends are ordinary inheritable handles. No anonymous pipes and no blocking reads; a timeout or cancel uses
  `CancelIoEx` on our ends. A job object kills the child tree on timeout; exit by the wait above. The
  **(owner: steer to PowerShell 7)** The shell is `pwsh.exe` when found (the child's `PATH`, which includes the
  Store alias under `WindowsApps`, then `%ProgramFiles%\PowerShell\pwsh.exe`), else Windows PowerShell 5.1
  (`%SystemRoot%\System32\WindowsPowerShell1.0\powershell.exe`), as the C# port resolved it. When it falls back
  to 5.1, the `Shell` result says so once per server run and how to install 7 (`winget install Microsoft.PowerShell`);
  the Windows `Shell` description says commands run in PowerShell 7 when installed. Neither the Windows App SDK nor
  Windows 11 ships PowerShell 7.
  C# port's environment rules carry over: the server's own secrets (`DESKTOP_MCP_AUTH_KEY`,
  `DESKTOP_MCP_OAUTH_CLIENT_SECRET`) never reach children.

### `Desktop` on Windows

| Area | API |
|---|---|
| Accessibility tree, find element, element actions | UI Automation (COM `IUIAutomation`) on pool threads, with the C# port's caching, timeouts and limits (300 children per parent, unresponsive windows skipped) as reference |
| Windows, apps | `EnumWindows`, `GetWindowThreadProcessId`, `SetWindowPos`, `ShellExecuteExW`, `IApplicationActivationManager` for packaged apps |
| Input | `SendInput` |
| Screenshots | `Windows.Graphics.Capture` (a `Direct3D11CaptureFramePool` frame per call), encoded with `Windows.Graphics.Imaging.BitmapEncoder`; vision annotations drawn with Direct2D |
| OCR | `Windows.Media.Ocr` |
| Clipboard | `Windows.ApplicationModel.DataTransfer.Clipboard` |
| Notification | **(owner)** `Windows.UI.Notifications.ToastNotificationManager` with an AUMID, as the C# port's `ToastSender` does (borrowed Windows PowerShell AUMID by default, or the caller's `app_id`); nothing is registered under HKCU |
| Permissions (core's `PermissionState`) | Windows has no TCC: report UIPI limits (elevated targets) instead, or nothing; the builder decides what the shared tools say |

### Control (the C# port's P4) — dropped

**(owner, 2026-11-16)** Not ported. The macOS Rust port has no desktop control, and Windows input goes through
`SendInput` as macOS's goes through posted events, so the two would differ for no gain. Started in W5 (a call gate in
core, the state machine, low-level hooks then Raw Input, a heartbeat) and reverted: the owner judged suppressing or
preempting on the user's input not worth it ("굳이"). Input from the user and from the tools is all delivered; the
user stops the agent from the client. If tool calls need serialising or a capture flash is wanted later, each is a
small addition of its own.

### Virtual desktops

Virtual desktops are not a tool. Windows on another virtual desktop are DWM-cloaked, so the window list (which
leaves cloaked windows out) and Snapshot already keep to the current desktop; Snapshot names it ("Virtual desktop:
<name> (<n> of <count>)") from Explorer's registry entries. No undocumented interface is used (W3).

### Other transports **(my call)**

`--transport socket`/`--socket` and `connect` are refused on Windows with the same message as on macOS ("disabled
until clients are verified"). A named-pipe transport comes with client validation, not before.

### Client validation

Out of scope here, as on macOS: `2026-11-16-rust-port-client-check-design.md` and the Windows APIs in
`2026-11-16-rust-port-client-validation.md` stay the plan for both ports.

### Packaging

- One `desktop-mcp.exe` per architecture (x64, arm64), static CRT, no side files.
- `.mcpb` for Claude Desktop: a script beside `macos/scripts/build-mcpb.sh` (PowerShell), binary server type,
  `compatibility.platforms = ["win32"]`.

### Testing

- `core` tests run on both systems (they use the recording fake of `Desktop`).
- `crates/windows` tests run on the main thread where the code under test owns the loop, as `crates/macos/tests`
  do.
- No automated test injects input, installs hooks, shows windows or uses the clipboard on the developer's desktop.
  HTTP tests bind loopback port 0. Live checks go to a list for the owner, as `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`
  does for the C# port.
- **(my call)** The parity suite runs against the Windows binary: the `tools/list` and error-text checks against a
  checked-in Windows reference, and the HTTP, auth, session, TLS and stdio behaviour tests with per-OS commands where
  they use `Shell` (PowerShell equivalents). "tokio starts no thread" holds on Windows; the idle check becomes "an idle
  server queues no work on the main thread" (the thread pool owns its threads by design).

### Retiring the C# port

**(my call)** `windows-port` is never merged: this work branches from the Rust port, which has no `windows/`.
`spec/windows/tools.json` is copied into this branch as the reference for the Windows-only tools; the C# code stays
readable on `windows-port`, kept on the remote as history until the owner deletes it. When the Rust Windows binary
passes its tests and the owner's live checks, the root README lists Windows as Rust on the core.

## Left to the builder

- The order of work (suggested: tool-registration split and macOS-only moves first, then the loop and stdio, then
  HTTP and TLS, then the `Desktop` areas one by one).
- Crate versions, the exact WinRT/Win32 call for each gap above.
- Error wording for Windows-specific failures, following the core's style.
