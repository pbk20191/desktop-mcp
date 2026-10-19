# desktop-mcp for Windows: design

Date: 2026-10-19
Status: approved in brainstorming, pending written-spec review
Upstream baseline: [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) commit
`128f2960c79b74bd2630f4dbc9d4f664f4ce89b3` (package 0.8.8, FastMCP 4.1.0)

## 1. Goal

Ship `windows/`, a port of CursorTouch/Windows-MCP that reaches full parity with upstream: all 21
tools, the `serve`/`install`/`uninstall`/`auth` CLI, the `config.toml` format, the stdio,
Streamable HTTP and legacy HTTP+SSE transports, bearer auth, IP allowlist, CORS, TLS, OAuth 2.0 +
PKCE, the desktop-control feature and telemetry. It ships as one NativeAOT `desktop-mcp.exe` per
architecture (win-x64, win-arm64) that needs no runtime, Python, PowerShell 5.1 or external tools.

Parity means the tool **contract** (names, parameters, schemas, descriptions, annotations) matches
upstream exactly. Tool **output text** and **behaviour** match upstream except where this document
lists a deliberate difference (§10): upstream bugs are fixed and the output text is cleaned up.

Naming follows the macOS port: command `desktop-mcp`, config directory `~/.desktop-mcp`,
environment variables `DESKTOP_MCP_*` in place of `WINDOWS_MCP_*`. `ANONYMIZED_TELEMETRY`,
`POSTHOG_API_KEY` and `POSTHOG_HOST` keep their upstream names.

## 2. Decisions

| Topic | Decision | Why |
|---|---|---|
| Language | C# on .NET 10 (LTS), NativeAOT | Schannel TLS and IOCP sockets come from the platform; probes showed every needed area works under AOT with zero trim/AOT warnings. |
| Win32/COM bindings | CsWin32 with `comInterop.useComSourceGenerators` (`[GeneratedComInterface]`) | AOT-safe; generates every UIA interface we need. `Interop.UIAutomationClient` relies on built-in COM interop, which NativeAOT does not support. |
| WinRT | CsWinRT through the `net10.0-windows10.0.19041.0` TFM | Toasts, `DispatcherQueue`, `Windows.Graphics.Capture`. |
| MCP protocol | Official MCP C# SDK (`ModelContextProtocol`, `.AspNetCore`) | Protocol versions, sampling and cancellation come from the SDK. Accepted cost: the wire serialisation is the SDK's (field order, escaping) and the legacy SSE message path is `/message`. |
| HTTP | Kestrel via `WebApplication.CreateSlimBuilder` | AOT-supported; HTTP/1.1 + HTTP/2 over Schannel with ALPN. HttpListener is ruled out (urlacl and `netsh http add sslcert` need admin). |
| Tool contract | Per-port: `spec/macos/tools.json`, `spec/windows/tools.json` | The upstream servers expose different tool sets. |
| Abstraction level | Prefer WinRT and libraries over raw Win32 where an AOT-friendly option exists | Screen capture uses Windows.Graphics.Capture; imaging and drawing use ImageSharp. UIA, input injection, hooks, display enumeration and the clipboard stay on thin CsWin32 wrappers because no AOT-friendly higher-level option exists. |
| Threads | One dedicated thread, driven by a system `DispatcherQueue` | See §5. |
| Architectures | win-x64 and win-arm64 | NativeAOT cross-compiles both from an x64 host. |
| Minimum OS | Windows 10 2004 (build 19041) | Needed for `WDA_EXCLUDEFROMCAPTURE`; matches the TFM. |

## 3. Repository layout

```
desktop-mcp/
  spec/
    macos/tools.json          # moved from spec/tools.json; macOS ContractTests path updated
    windows/tools.json        # upstream's 21 tools, see §4
  windows/
    global.json               # pins the .NET 10 SDK
    Directory.Build.props     # TFM, PublishAot, Nullable, TreatWarningsAsErrors (IL2xxx/IL3xxx included)
    DesktopMcp.slnx
    src/
      DesktopMcp/             # the exe: CLI, composition root, app.manifest (PerMonitorV2 DPI)
      DesktopMcp.Core/        # platform-light logic: Config, ServePlan, Security, OAuth, Analytics,
                              # Scrape + HtmlToMarkdown, PowerShellRunner, FileSystem, Registry,
                              # Processes, text formatting (tables, tree renderer)
      DesktopMcp.Desktop/     # the only project that references CsWin32: Uia, Tree, Input, Capture,
                              # Imaging, Displays, VirtualDesktops, Control, Overlay, Toast,
                              # Clipboard, Apps, Dispatcher
      DesktopMcp.Server/      # MCP SDK wiring: 21 McpServerTool subclasses, control gate filter,
                              # session registry, Kestrel pipeline, transports
    tests/
      DesktopMcp.Core.Tests/
      DesktopMcp.Server.Tests/
      DesktopMcp.Desktop.Tests/   # Category=Desktop, needs an interactive session, excluded by default
    scripts/build-release.ps1     # -> windows/dist/win-x64/desktop-mcp.exe, windows/dist/win-arm64/desktop-mcp.exe
    README.md
```

Boundary rule: BSTR, VARIANT, HRESULT, HWND and COM pointers never leave `DesktopMcp.Desktop`.
Its public surface is plain C# records (`UiNode`, `WindowInfo`, `DisplayInfo`, `Rect`, `Point`) and
interfaces (`IUiAutomation`, `IScreenCapture`, `IInputInjector`, `IDesktopDispatcher`, ...), so
`Core` and `Server` can be tested without a desktop.

Release tags are `windows-vX.Y.Z`. The root README's port table, layout block and "single
contract" paragraph are updated to describe per-port contracts.

## 4. Tool contract

`spec/windows/tools.json` holds the 21 tools exactly as upstream declares them: names, `title`,
`description` strings verbatim, `inputSchema`, `outputSchema` and `annotations`. It is generated
once from the upstream source at the baseline commit and then checked against a real upstream
`tools/list` response. Two FastMCP artefacts are dropped: `_meta.fastmcp` on each tool and the
`x-fastmcp-wrap-result` key in output schemas.

The spec file is embedded in `DesktopMcp.Server` as a resource. Each tool class looks up its own
entry by name and returns it as `ProtocolTool`, so the spec file is the only source of schemas and
descriptions. `ContractTests` compares `tools/list` with the spec file as JSON values (not bytes,
since the SDK owns serialisation).

Tools: App, Click, Clipboard, ControlStatus, DisplayInventory, FileSystem, Move, MultiEdit,
MultiSelect, Notification, PowerShell, Process, Registry, Scrape, Screenshot, Scroll, Shortcut,
Snapshot, Type, Wait, WaitFor.

Result shapes:
- Text tools return one `TextContent` plus `structuredContent: {"result": <text>}`, matching their
  `outputSchema`.
- ControlStatus returns its JSON object as text and as `structuredContent`; DisplayInventory does
  the same with its array wrapped as `{"result": [...]}`.
- Snapshot returns `[TextContent, ImageContent(png)]` when it has an image and a single
  `TextContent` otherwise. Screenshot always includes the image.
- An unexpected exception becomes `isError: true` with text `Error calling tool '<Name>': <message>`.
- Parameter coercion follows upstream: `bool | "true"/"false"` strings (strict for most tools,
  loose for Snapshot/Screenshot `use_*` and Scrape `use_dom`/`use_sampling`), and list parameters
  (`loc`, `region`, `locs`, `labels`, `args`) also accept a JSON string. Validation messages are
  upstream's strings.

## 5. Threads and concurrency

The process has one root: `Main` builds a host (Generic Host for stdio, `WebApplication` for HTTP),
runs it and returns. Shutdown comes from the host lifetime (Ctrl+C, stdin EOF, SIGTERM). At
startup the process puts itself in a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so
PowerShell children cannot outlive it.

### The dispatcher thread

There is exactly one dedicated thread, created by
`CreateDispatcherQueueController(DQTYPE_THREAD_DEDICATED, DQTAT_COM_NONE)`. The system owns the
thread and its message loop; we post work with `DispatcherQueue.TryEnqueue`, and a small
`DispatcherQueueSynchronizationContext` makes `await` continuations started there resume there.
On shutdown, `ShutdownQueueAsync()` drains the queue after hooks are removed and windows destroyed.

It hosts everything that needs a message loop:
- the `WH_KEYBOARD_LL` and `WH_MOUSE_LL` hooks and a message-only window that receives Raw Input
  (`RIDEV_INPUTSINK`),
- the overlay windows (control indicator, capture flash),
- `DispatcherQueueTimer`s for the idle timeout (10 s), hook reinstall (5 s), the capture flash
  (3.5 s) and the missed-key-up check (0.1 s x 5).

Rule: work on this thread is short and never blocks. UIA, capture, file, process and network work
are forbidden there, because a slow hook callback delays all input on the machine and a callback
that exceeds `LowLevelHooksTimeout` makes Windows silently remove the hook.

`IDesktopDispatcher` hides the mechanism. If the P0 probe shows that `DispatcherQueue` cannot be
created or projected under NativeAOT, or that LL hook callbacks are not delivered on its thread, a
`MsgWaitForMultipleObjectsEx` loop with its own work queue implements the same interface.

### Everything else runs on the thread pool

- **UIA** calls run on thread-pool threads (MTA, the AOT default), serialised by a `SemaphoreSlim`.
  They must not run on the dispatcher thread: a UIA client calling from a thread that owns windows
  can deadlock on its own windows. `IUIAutomation2` connection and transaction timeouts bound how
  long a hung target app can block a call.
- Input injection, capture, PowerShell, HTTP and the MCP SDK run on the thread pool.

### Tool-call serialisation

Gated tools (per the control policy, §7) wait on one global desktop lock (`SemaphoreSlim(1)`),
re-check the control state every 0.1 s while waiting, and keep a 15 s lease after the call ends.
Ungated tools run concurrently without the lock. With `--no-control`, every call takes the lock.

### Cancellation

The SDK's `CancellationToken` is passed through every call. `Wait` and `WaitFor` use
`Task.Delay(token)`. Input sequences (typing, drag steps, MultiSelect, MultiEdit) check the token
and the control checkpoint between steps. PowerShell sends Ctrl+Break, waits 2 s, then kills the
process tree.

### Shared state

`DesktopStateStore` holds the last snapshot (label to element map, windows, displays). Every
`get_state` replaces it atomically; labels always resolve against the latest snapshot, as upstream.

Without an interactive session (service session, lock screen, UAC desktop), desktop tools return
an error result; the server still starts.

## 6. Desktop layer

### UI Automation (`Desktop/Uia`)

`CUIAutomation8` queried for `IUIAutomation6`. One cache request carries upstream's property set:
Name, AutomationId, LocalizedControlType, AcceleratorKey, ClassName, ControlType, RuntimeId,
IsContentElement, FrameworkId, ClickablePoint, IsEnabled, IsOffscreen, IsControlElement,
HasKeyboardFocus, IsKeyboardFocusable, IsPassword, BoundingRectangle, HelpText; LegacyIAccessible
Role, Value, DefaultAction, State; Scroll horizontal/vertical scrollable and percent;
ExpandCollapseState; Selection CanSelectMultiple, IsSelectionRequired; SelectionItem IsSelected;
Window IsModal; Toggle state; RangeValue Value, Minimum, Maximum; plus the LegacyIAccessible,
Scroll and Window patterns. LegacyIAccessible property IDs must be in the cache request; without
them `get_Cached*` fails with `E_INVALIDARG`. Children are fetched one level at a time with
`BuildUpdatedCache` at `TreeScope_Children` under the control view.

The wrapper frees every BSTR (`SysFreeString`) and disposes every `ComVariant` before returning, and
exposes immutable `UiNode` records.

### Tree and Snapshot (`Desktop/Tree`)

- **Windows walked**: `EnumWindows` filtered to visible windows on the current virtual desktop
  (documented `IVirtualDesktopManager`) that our process does not own, plus `Progman`,
  `Shell_TrayWnd` and `Shell_SecondaryTrayWnd`; processes named in
  `DESKTOP_MCP_EXCLUDE_PROCESSES` are skipped before any UIA call. As upstream, the walk covers the
  active window plus non-app windows, and other app windows only when a `region` overlaps them.
- **Classification**: upstream's `tree/config.py` constants and rules are ported into
  `TreeConfig.cs` (interactive, scrollable and informative types, roles, browser special cases,
  Word nodes from TextPattern, modal-dialog reset, window-name fixes such as Progman to "Desktop").
- **Limits**: `DESKTOP_MCP_MAX_TREE_ELEMENTS` (default 500) with upstream's truncation note; a
  RuntimeId cycle guard; three retries per window with 0.5/1/2 s backoff.
- **Virtual desktop names** use the undocumented `IVirtualDesktopManagerInternal`, with interface
  IDs keyed by build (19041, 22621, 26100) declared by hand, then the registry name, then
  `Desktop N`. Failure only affects the report.
- **Browser DOM**: Chrome and Edge enter DOM mode below the UIA element with
  `AutomationId == "RootWebArea"`, including upstream's DOM corrections. Firefox falls back to MSAA
  `IAccessible` from `AccessibleObjectFromWindow(OBJID_CLIENT)`; the few IAccessible2 interfaces we
  need are declared by hand with `[GeneratedComInterface]` because they are not in the Windows SDK
  metadata.
- **Region and display selection**, validation and error strings follow upstream.

### Snapshot text

The renderer in `Core` turns a snapshot model into text, so it is unit-tested without a desktop.
Section order and content follow upstream (cursor position, screenshot size and scale lines,
displays, desktops, focused window, opened windows, UI tree). Cleanups:
- no stray four-space indentation; every section header starts at column 0;
- tables use a deterministic implementation of tabulate's `simple` format;
- each interactive and scrollable line starts with its label: `[12] (x,y) button "OK"  [action: click]`;
- without an image, the result is one plain `TextContent`, not a JSON-encoded array of strings.

### Input (`Desktop/Input`)

All injection goes through `SendInput`: absolute mouse coordinates normalised over the virtual
desktop (`MOUSEEVENTF_VIRTUALDESK`), wheel events, virtual-key events with scan codes, and text as
`KEYEVENTF_UNICODE` with surrogate pairs. Every injected event carries a fixed `dwExtraInfo`
signature so the input monitor can ignore it. Timings, step counts, drag durations, the paste path
(20 or more characters without `\n`, `\t`, `{`, `}`: save clipboard, set, Ctrl+V, restore if
unchanged) and Type's `clear`/`caret_position`/`press_enter` follow upstream. Shortcut parsing
ports upstream's `SpecialKeyNames` table and aliases. `InputLedger` records keys and buttons held
by the AI and releases them on preemption, cancellation or shutdown.

### Capture and imaging (`Desktop/Capture`, `Desktop/Imaging`)

- **Capture**: Windows.Graphics.Capture. One `GraphicsCaptureItem` per monitor
  (`IGraphicsCaptureItemInterop.CreateForMonitor`), a free-threaded frame pool on a D3D11 device,
  one frame converted to a `SoftwareBitmap`. A full virtual-desktop capture composites the monitors
  it covers. `IsBorderRequired = false` where the OS allows it (Windows 11); on Windows 10 a capture
  border may flash. Windows marked `WDA_EXCLUDEFROMCAPTURE`, including our overlays, are excluded by
  the system.
- **Imaging**: SixLabors ImageSharp, ImageSharp.Drawing and Fonts. Scaling
  (`scale = min(DESKTOP_MCP_SCREENSHOT_SCALE clamped to [0.1, 1], 1920/w, 1080/h)`, Lanczos),
  annotation (2 px boxes and filled index labels in Arial 12 loaded from the system font folder,
  the cursor marker, reference grid lines) and PNG encoding. Box colours come from a fixed palette
  indexed by label, not random. ImageSharp's Six Labors Split License grants Apache 2.0 terms to
  open-source projects, which covers this MIT project.
- `DESKTOP_MCP_SCREENSHOT_BACKEND` is accepted and ignored; the README says so.
- `IScreenCapture` and `IImageRenderer` hide both choices. If either fails the P0 probe (AOT
  compatibility or warnings), GDI capture or Direct2D/DirectWrite drawing implements the same
  interface.

### Displays

DisplayInventory and display selection use `EnumDisplayMonitors`, `GetMonitorInfo`,
`GetDpiForMonitor` (falling back to the system DPI) and `EnumDisplaySettings`, producing upstream's
fields (`index`, `device`, `primary`, `bounds`, `work_area`, `resolution`, `orientation`,
`effective_dpi`, `scale`). The process is PerMonitorV2 DPI-aware through its manifest, so UIA,
input and capture share physical virtual-desktop coordinates.

## 7. Desktop control

Desktop control keeps the AI from fighting the user for the mouse and keyboard. Behaviour follows
upstream; the implementation is split so the policy logic is pure and testable.

| Component | Runs on | Responsibility |
|---|---|---|
| `InputMonitor` | dispatcher thread | LL hooks and Raw Input. Reports physical input only (ignores `LLMHF_INJECTED`/`LLKHF_INJECTED` and our `dwExtraInfo` signature). Swallows input while suppression is active. |
| `ControlStateMachine` | pure C#, `TimeProvider` injected | Turns events into state and effects. States: `disabled`, `unavailable`, `user`, `ready`, `ai`. |
| `ControlGate` | SDK call-tool filter | Policy classification, the desktop lock, the 15 s lease, checkpoints, rejection results. |
| `ControlOverlay` | dispatcher thread | Indicator: blue border on every monitor, the notice ("AI is controlling this computer" / "Move the mouse a significant distance" / "or press Ctrl + Alt + Shift + Backspace to take over"), and a ring that follows the cursor. Click-through, `WDA_EXCLUDEFROMCAPTURE`. Drawn with ImageSharp into a BGRA buffer and shown with `UpdateLayeredWindow`. Also shows the #FF4500 capture flash for 3.5 s (`DESKTOP_MCP_DISABLE_FLASH` turns it off). |
| `ControlNotifier` | thread pool | Sends `notifications/message` (level `notice`, logger `desktop-mcp.control`, data `{"event": "USER_CONTROL" or "AI_CONTROL_AVAILABLE", "status": {...}}`) to live sessions. |

Rules:
- **Policies**: `relaxed` gates the direct tools (Click, Type, Scroll, Move, Shortcut,
  MultiSelect, MultiEdit, App `resize`/`switch`); `balanced` adds the possible ones (PowerShell,
  Notification, App `launch`/`launch_executable`, Process `kill`, Clipboard `set`); `strict` gates
  everything except ControlStatus. Unknown tools or modes count as direct.
- **User first**: physical input in `ready` or `user` moves to `user`; gated calls are then rejected
  at once with `USER_CONTROL`.
- **During `ai`**: physical input is swallowed. The user takes over by moving the mouse (Raw Input
  relative path length reaching `mouse_takeover_units`, default 500, range 40 to 2000, reset after
  a 0.5 s pause) or by pressing Ctrl+Alt+Shift+Backspace (the chord is swallowed until released).
  The running call stops at its next checkpoint with `CONTROL_PREEMPTED` and `executed_steps`, and
  `InputLedger` releases held keys.
- **Back to `ready`** after `idle_timeout_seconds` (default 10) without physical input and with no
  keys or buttons held. A missed key-up is cleared after five all-up `GetAsyncKeyState` samples
  0.1 s apart.
- **`unavailable`** when there is no active WTS session, the input desktop is not `Default`, a hook
  or the overlay fails, or the event queue (4096) overflows; gated calls get `CONTROL_UNAVAILABLE`.
- **Rejections** are `isError` results whose text is `{"code": C, "status": {...}}` with
  C in `USER_CONTROL`, `CONTROL_UNAVAILABLE`, `CONTROL_PREEMPTED`, `PHYSICAL_INPUT_HELD`.
- **ControlStatus** returns `{state, generation, wait_seconds, enabled, policy, idle_timeout_seconds}`
  and is never gated.

**Input-lock safety.** The hook swallows input only while a suppression deadline (1 s) is in the
future. The deadline is renewed by a heartbeat from the running tool call on the thread pool, not
by a dispatcher timer, so a stuck or crashed call releases the user's input within a second. If
the dispatcher thread itself stalls, Windows removes the hook after `LowLevelHooksTimeout`. No path
leaves input locked indefinitely; both cases are integration tests.

**Sessions.** The gate filter records each SDK session (`McpServer` instance) with a last-seen time.
Notifications go to every session seen within the TTL: unlimited for stdio, 60 s for HTTP, none
for stateless Streamable HTTP (as upstream).

## 8. Server layer

### CLI and configuration

- `System.CommandLine` 2.0 (AOT-compatible). Commands: `serve` (default), `install`, `uninstall`,
  `auth`. Flags as upstream: `--transport stdio|sse|streamable-http`, `--host` (localhost),
  `--port` (8000), `--debug`, `--config`, `--auth-key`, `--allow-insecure-remote`,
  `--ip-allowlist`, `--tools`, `--exclude-tools`, `--cors-origins`, `--ssl-certfile`,
  `--ssl-keyfile`, `--oauth-client-id`, `--oauth-client-secret`, `--stateless-http`,
  `--control/--no-control`, `--control-idle-timeout`, `--control-policy`, each with its
  `DESKTOP_MCP_*` variable. Serve flags given before a subcommand produce upstream's usage error.
- Precedence: CLI or environment, then `config.toml`, then defaults.
- `~/.desktop-mcp/config.toml` (or `--config`) is parsed with Tomlyn's source generator, with
  upstream's sections (`[server]`, `[security]`, `[tools]`, `[control]`) and strict types (a TOML
  bool where an int is expected is an error). Certificate paths resolve relative to the config
  file's folder.
- `ServePlan` is the validated, immutable result: SSL files both or neither, OAuth id and secret
  both or neither, a non-loopback HTTP bind requires an auth key, OAuth or
  `--allow-insecure-remote` (upstream's "Refusing to bind HTTP transport..." message), port range.
- `--tools` / `--exclude-tools` filter the registered tools; ControlStatus is always kept.

### Transports

| Transport | Setup |
|---|---|
| stdio | Generic Host + `WithStdioServerTransport()`. Logs go to stderr only. |
| streamable-http | `MapMcp("/mcp")`, stateful by default to match upstream (`Stateless = false`); `--stateless-http` switches to stateless. |
| sse | `MapMcp` with `EnableLegacySse = true`: `GET /sse` and the SDK's `/message` endpoint, announced by the `endpoint` event. |

Only the selected HTTP transport is mapped. Server name `desktop-mcp`; the `initialize`
instructions are upstream's text without its leading and trailing newlines.

### HTTP pipeline

Kestrel middleware in upstream's order:
1. OPTIONS: every OPTIONS request gets 200; CORS headers only for allowed origins.
2. Trusted host: `localhost`, `127.0.0.1`, `[::1]` for loopback binds; the host itself for a
   specific IP; no check for `0.0.0.0`/`::` or with `--allow-insecure-remote`.
3. CORS: methods GET, POST, OPTIONS; headers `Content-Type`, `Authorization`, `Mcp-Session-Id`;
   no credentials.
4. IP allowlist: 403 `{"error":"Forbidden: <ip> is not in the IP allowlist"}`.
5. Auth: bearer key and OAuth access tokens; 401 with upstream's JSON bodies and
   `WWW-Authenticate: Bearer`. Public paths: `/health`, `/.well-known/oauth-authorization-server`,
   `/oauth/register`, `/oauth/authorize`, `/oauth/token`.
6. MCP endpoints, plus `GET /health` returning `{"status":"ok"}`.

**TLS**: the certificate and key are read with `X509Certificate2.CreateFromPemFile` and reloaded
through PKCS#12 (`X509CertificateLoader.LoadPkcs12(cert.Export(Pkcs12), null)`), because Schannel
cannot use an ephemeral key loaded straight from PEM. ALPN offers `h2` and `http/1.1`.

**OAuth 2.0 + PKCE** (in memory): metadata with issuer `{scheme}://{host}:{port}`, code flow, S256
only, `client_secret_post`; `/oauth/register` returns 403 `registration_disabled`;
`/oauth/authorize` requires `response_type=code`, an S256 `code_challenge`, a loopback http(s)
`redirect_uri` without fragment and the configured `client_id`, and redirects with a code valid
for 300 s; `/oauth/token` (form or JSON) validates grant, code, client, redirect URI, expiry, PKCE
verifier and secret and returns `{access_token, token_type: "Bearer", expires_in: 3600}`. All JSON
uses System.Text.Json source generation.

### Core tools

- **PowerShell**: `pwsh` on PATH, else `powershell`, else the System32 path; `-NoProfile`,
  `-OutputFormat Text` for 5.1, `-EncodedCommand` (UTF-16LE base64) of the command prefixed with
  the UTF-8 output-encoding preamble; stdin closed, `CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW`,
  working directory `~`, environment rebuilt from HKLM/HKCU with PATH merged, `NO_COLOR=1`. Output
  is stdout (or stderr when stdout is empty) as `Response: {output}\nStatus Code: {rc}`; timeout
  (default 30 s) gives `Command execution timed out`; the elevation hint is appended on "Access is
  denied" when not elevated.
- **FileSystem**: upstream's modes, limits (10 MB, 500 results), relative-path base (the user's
  Desktop folder), output formats and `Error: ...` texts.
- **Registry**: `Microsoft.Win32.Registry` instead of PowerShell cmdlets; same modes, value types
  and output strings.
- **Process**: `System.Diagnostics.Process`; CPU% from two samples a short interval apart (upstream
  always reports about 0); fuzzy name filter (partial ratio above 60) implemented in `Core`; same
  table and kill messages.
- **Clipboard**: Win32 clipboard with `CF_UNICODETEXT` on a thread-pool thread; same messages.
- **Notification**: CsWinRT `ToastNotificationManager.CreateToastNotifier(app_id).Show()` with
  ToastGeneric XML; never reads `notifier.Setting` (throws for unpackaged apps).
- **Scrape**: `HttpClient` over `SocketsHttpHandler` whose `ConnectCallback` connects only to the
  address that passed validation; http/https only, no URL credentials, private, loopback,
  link-local, multicast, reserved and unspecified addresses blocked (with the Fake-IP DNS message
  for 198.18.0.0/15 and 2001:2::/48); up to 5 redirects followed manually and re-validated; 10 s
  timeout. HTML is converted by a C# port of the macOS port's `HTMLToMarkdown.swift`. `use_dom`
  reads DOM text nodes from the last snapshot. `use_sampling` calls the SDK's `SampleAsync` with
  upstream's system prompt and `max_tokens` 2048, returning the raw content when the client
  cannot sample or sampling fails.
- **App**:
  - `launch` enumerates the Shell `AppsFolder` namespace (`IShellItem`, covering Win32 shortcuts
    and packaged apps with their localised names) instead of `Get-StartApps`, picks the best fuzzy
    match (cutoff 70), and launches packaged apps with `IApplicationActivationManager` and paths
    with `ShellExecuteEx`. It then waits up to 10 s for a window of the new process, and up to
    10 s more for a title match.
  - `launch_executable`, `switch` (restore, `AllowSetForegroundWindow`, `AttachThreadInput`,
    `SetForegroundWindow`, `BringWindowToTop`, 1 s verification) and `resize` (`MoveWindow`) keep
    upstream's semantics and strings.
- **WaitFor**: polls snapshots with upstream's conditions, aliases, limits and messages;
  `element_enabled` checks `IsEnabled`.
- **Wait**, **MultiSelect**, **MultiEdit**: upstream semantics on top of Input and Tree.

### install, uninstall, auth

- `install` writes `~/.desktop-mcp/start-server.cmd` (runs `desktop-mcp.exe serve ...` with output
  appended to `server.log` / `server.error.log`) and registers the scheduled task
  `desktop-mcp-server` (at logon, current user, limited run level, runs on battery) through the
  Task Scheduler COM API (`ITaskService`), then starts it. Flags as upstream (`--transport`
  sse|streamable-http, `--host` 127.0.0.1, `--port` 8000, `--force`).
- `uninstall` ends and deletes the task and removes the .cmd file.
- `auth` writes `config.toml` with a fresh key (32 random bytes, URL-safe base64) and only
  non-default values, and prints a client config snippet. `--with-tls` creates a self-signed
  certificate with `CertificateRequest` (SAN localhost, 127.0.0.1, ::1) and writes `cert.pem` and
  `key.pem`; it needs neither mkcert nor openssl.

### Telemetry

PostHog over `HttpClient`, fire-and-forget, failures ignored. Off when
`ANONYMIZED_TELEMETRY=false`; `POSTHOG_API_KEY` (default: upstream's project key) and
`POSTHOG_HOST` (default `https://us.i.posthog.com`). `distinct_id` is a UUIDv7 stored in
`~/.desktop-mcp/.desktop-mcp-user-id`; `session_id` is `mcp_{ms}_{pid}`. Events `tool_executed`
and `exception` with upstream's properties (`tool_name`, `session_id`, `process_person_profile`,
`duration_ms`, `success`, `client_name`, `client_version`, and for exceptions `exception` and
`traceback`). Unlike upstream, the events are actually sent.

### Diagnostics

`--debug` / `DESKTOP_MCP_DEBUG` enables debug logging to stderr. `DESKTOP_MCP_PROFILE_SNAPSHOT`
logs per-stage snapshot timings. `DESKTOP_MCP_WATCHDOG` registers a UIA focus-changed handler that
logs focus changes at debug level.

## 9. Testing

| Layer | Covers | Runs |
|---|---|---|
| Core unit | config precedence and strict TOML types; ServePlan validation messages; SSRF rules; HTML to Markdown; OAuth/PKCE flows; PowerShell runner against real pwsh (encoding, timeout, tree kill); FileSystem formats; Shortcut parsing; table formatter; Snapshot text rendering from synthetic `UiNode`s; fuzzy matching; control state machine with a fake `TimeProvider` (takeover, idle return, lease, policy classification) | always |
| Server | ContractTests (`tools/list` equals `spec/windows/tools.json` as JSON); in-memory Kestrel tests of middleware order, 401/403 bodies, CORS, OPTIONS, trusted host, `/health`; stdio initialize, tools/list and call | always |
| Desktop integration | Notepad: Snapshot finds the edit control, Type, Shortcut, App switch and resize; capture and annotation; hooks, overlay, takeover; input-lock safety (heartbeat stops, input returns within 1 s) | `Category=Desktop`, run by hand in an interactive session |
| AOT smoke | `build-release.ps1` publishes win-x64, launches the exe and runs initialize, tools/list and a Wait call over stdio; any IL2xxx/IL3xxx warning fails the build | every release build |

xUnit runs under the JIT, so AOT-only failures are caught by the smoke step.

## 10. Differences from upstream (for the Windows README)

- **Naming**: `desktop-mcp`, `~/.desktop-mcp`, `DESKTOP_MCP_*`. No Python, uv, PowerShell 5.1,
  mkcert or openssl needed.
- **Output cleanups**: no stray indentation in Snapshot/Screenshot text; labels shown in the UI
  tree text; a plain text result instead of a JSON-encoded array when there is no image; Scroll
  without `loc` reports `Scrolled ... at current cursor position (x,y).` instead of an empty
  string; Click with `clicks` of 3 or more reports `Clicked N times` instead of `None`;
  annotation colours are fixed per label; the `initialize` instructions have no leading or
  trailing newline.
- **Bug fixes**: scrollable elements report their box centre (upstream picks a random point);
  WaitFor `element_enabled` checks the enabled state; telemetry events are sent; `/health` exists;
  Process CPU% is measured; the unreachable `takeover_pending` state is gone.
- **Protocol**: the legacy SSE message endpoint is `/message` (clients follow the `endpoint`
  event); no FastMCP `_meta` on tools; JSON serialisation details are the MCP C# SDK's.
- **Implementation**: capture uses Windows.Graphics.Capture, so `DESKTOP_MCP_SCREENSHOT_BACKEND`
  is ignored; Registry, Notification, the app list and `install` do not use PowerShell;
  `auth --with-tls` generates its own self-signed certificate.

## 11. Phases

One implementation plan, executed phase by phase with a review checkpoint after each. Riskiest
work first.

| Phase | Scope | Outcome |
|---|---|---|
| P0 Foundations and probes | Move `spec/tools.json` to `spec/macos/` and fix the macOS test path; write `spec/windows/tools.json`; solution skeleton and build settings; AOT probes for `DispatcherQueue` + LL hooks, Windows.Graphics.Capture, ImageSharp; spec-driven `McpServerTool` base class and ContractTests | An empty server answers `tools/list` |
| P1 Server core and headless tools | CLI, config, ServePlan, stdio; Wait, PowerShell, FileSystem, Clipboard, Process, Registry, Notification, Scrape without sampling | A useful server without desktop access |
| P2 Desktop read | Dispatcher, UIA wrapper, Tree, Snapshot without DOM, capture, imaging, Screenshot, DisplayInventory, virtual desktops | The agent can see the screen |
| P3 Desktop act | Input, InputLedger, Click, Type, Scroll, Move, Shortcut, MultiSelect, MultiEdit, App, WaitFor | Upstream's core workflows |
| P4 Desktop control | InputMonitor, state machine, overlay, capture flash, gate, notifier, ControlStatus | Protection against fighting the user |
| P5 HTTP | Kestrel, Streamable HTTP and SSE, middleware, TLS, OAuth, `/health`, Scrape sampling, telemetry | Remote use |
| P6 Finish | Browser DOM (Chrome, Edge, Firefox), install, uninstall, auth, release script for x64 and arm64, Windows README, root README update | `windows-v0.1.0` |

## 12. Risks

| Risk | Mitigation |
|---|---|
| `DispatcherQueue` creation/projection or LL hook delivery fails under AOT | P0 probe; `IDesktopDispatcher` fallback to a `MsgWaitForMultipleObjectsEx` loop |
| Windows.Graphics.Capture needs D3D11/WinRT interop that misbehaves under AOT, or its border cannot be hidden on Windows 10 | P0 probe; GDI fallback behind `IScreenCapture` |
| ImageSharp produces trim/AOT warnings or adds too much size | P0 probe; Direct2D/DirectWrite fallback behind `IImageRenderer` |
| The undocumented virtual-desktop interface changes in a new Windows build | Only desktop names depend on it; fall back to registry names, then `Desktop N` |
| The MCP SDK's serialisation or legacy SSE support changes | Pin the SDK version; ContractTests and HTTP tests catch drift |
| LL hooks swallow input and lock the user out | Heartbeat-renewed suppression deadline, `LowLevelHooksTimeout`, integration tests |
| UIA calls into hung apps | `IUIAutomation2` connection and transaction timeouts, serialised UIA access |
