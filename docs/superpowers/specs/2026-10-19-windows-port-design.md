# desktop-mcp for Windows: design

Date: 2026-10-19
Status: approved in brainstorming, pending written-spec review
Upstream baseline: [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) commit
`128f2960c79b74bd2630f4dbc9d4f664f4ce89b3` (package 0.8.8, FastMCP 4.1.0)

## 1. Goal

Ship `windows/`, a port of CursorTouch/Windows-MCP that reaches full parity with upstream: all 21
tools, the `serve`/`install`/`uninstall`/`auth` CLI, the `config.toml` format, the stdio,
Streamable HTTP and legacy HTTP+SSE transports, bearer auth, IP allowlist, CORS, TLS, OAuth 2.0 +
PKCE, the desktop-control feature and telemetry. It ships as a NativeAOT `desktop-mcp.exe` per
architecture (win-x64, win-arm64). Each ships as one file: the exe runs alone in an empty folder. It
needs Windows 11 22H2 or later and the system Windows App SDK 2.5 runtime, but no Python, PowerShell
5.1 or other external tools. The graphics DLLs that the static Win2D imports (d2d1, d3d11, dwrite,
d3dcompiler_47 and the shcore WinRT stream API set) are delay-loaded, so headless runs never load them.

Parity means the tool **contract** (names, parameters, schemas, descriptions, annotations) matches
upstream exactly. Tool **output text** and **behaviour** match upstream except where this document
lists a deliberate difference (§10): upstream bugs are fixed and the output text is cleaned up.

Naming follows the macOS port: command `desktop-mcp`, config directory `~/.desktop-mcp`,
environment variables `DESKTOP_MCP_*` in place of `WINDOWS_MCP_*`. `ANONYMIZED_TELEMETRY`,
`POSTHOG_API_KEY` and `POSTHOG_HOST` keep their upstream names.

## 2. Decisions

| Topic | Decision | Why |
|---|---|---|
| Language | C# on .NET 10 (LTS), NativeAOT | Schannel TLS and IOCP sockets come from the platform; probes showed every needed area works under AOT with zero trim/AOT warnings. The Release `desktop-mcp.exe` is 10,399,744 bytes (win-x64) and 11,318,784 bytes (win-arm64), with Win2D linked in statically. |
| Win32/COM bindings | CsWin32 (`Microsoft.Windows.CsWin32` 0.3.347, `allowMarshaling: false`, `public: false`, `PrivateAssets=all`) in both `DesktopMcp.Core` and `DesktopMcp.Desktop` (user decision, 2026-10-19); since P2, DesktopMcp.Desktop sets `allowMarshaling: true`, `comInterop.useComSourceGenerators` and `CsWin32RunAsBuildTask`, so UIA and the shell interfaces are `[GeneratedComInterface]` interfaces; DesktopMcp.Core keeps `allowMarshaling: false` (P2 plan, Decisions 1) | AOT-safe; generates every Win32 entry point and UIA interface we need. `Interop.UIAutomationClient` relies on built-in COM interop, which NativeAOT does not support. |
| WinRT | CsWinRT through the `net10.0-windows10.0.22621.0` TFM | Toasts, `Windows.Graphics.Capture`, and the Windows App SDK's `Microsoft.UI.Dispatching` and `Microsoft.UI.Windowing`. |
| MCP protocol | Official MCP C# SDK (`ModelContextProtocol`, `.AspNetCore`) | Protocol versions, sampling and cancellation come from the SDK. Accepted cost: the wire serialisation is the SDK's (field order, escaping) and the legacy SSE message path is `/message`. |
| HTTP | Kestrel via `WebApplication.CreateSlimBuilder` | AOT-supported; HTTP/1.1 + HTTP/2 over Schannel with ALPN. HttpListener is ruled out (urlacl and `netsh http add sslcert` need admin). |
| Tool contract | Per-port: `spec/macos/tools.json`, `spec/windows/tools.json` | The upstream servers expose different tool sets. |
| Abstraction level | Prefer WinRT and libraries over raw Win32 where an AOT-friendly option exists | Screen capture uses Windows.Graphics.Capture; imaging and drawing use Win2D (`Microsoft.Graphics.Win2D` 1.4.0, linked statically; see the Win2D row). ImageSharp was dropped: 4.x refuses a Release build without a Six Labors license key, and the key-free 3.1.x / 2.1.x line fails restore with known-vulnerability errors (NU1902/NU1903). Direct2D was the fallback for imaging and was not needed. UIA, input injection, hooks and the other Win32 areas listed in the WinRT first row stay on thin CsWin32 or hand-declared wrappers because neither WinRT nor the Windows App SDK has an API for them. |
| WinRT first | Wherever WinRT or the Windows App SDK has the API, use it; raw Win32/COM stays only where neither has one | The Windows App SDK removes hand-written interop and its failure modes. Raw Win32/COM remains for the UIA client, `SendInput`, low-level hooks and Raw Input, foreign-window control, virtual desktops, Shell app listing, Job Objects and Task Scheduler. Done: the dispatcher thread is `DispatcherQueueController.CreateOnDedicatedThread()`, and capture gets each monitor's display id from the Windows App SDK's `Win32Interop.GetDisplayIdFromMonitor`. Done in P1: Clipboard through WinRT `Clipboard` first, with a bounded Win32 fallback (§8). Evaluated in P2: `DisplayArea` agrees with `GetMonitorInfo`, but DisplayInventory and display selection stay on Win32 because device names and indices need it and DisplayInventory needs no runtime (user decision; P2 plan, Decisions 15 and Task 8). Planned: P4 overlays evaluate `AppWindow` with WinUI 3 or Composition plus Win2D first. Notifications stay on WinRT `ToastNotificationManager`: upstream's `app_id` parameter needs arbitrary AUMIDs, and `AppNotificationManager` cannot send under another app's identity. |
| Windows App SDK runtime | Framework-dependent and unpackaged; the user's machine must have the system Windows App SDK 2.5 runtime installed. The build references the component packages `Microsoft.WindowsAppSDK.Runtime` 2.5.1 and `Microsoft.WindowsAppSDK.InteractiveExperiences` 2.1.9, not the `Microsoft.WindowsAppSDK` metapackage | The dispatcher, capture's `Win32Interop.GetDisplayIdFromMonitor` and Win2D need it (user decision, 2026-10-19). `AppRuntime.EnsureInitialized()` bootstraps it lazily through kernelbase `TryCreatePackageDependency` / `AddPackageDependency`, the way windows-rs `windows-reactor` does: family `Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe`, minimum version 2.5.1.0, process lifetime. There is no `Microsoft.WindowsAppRuntime.Bootstrap.dll` and no SDK auto-initializer. If the runtime is missing, it throws with an install hint. |
| Win2D | `Microsoft.Graphics.Win2D` 1.4.0, linked statically into the NativeAOT exe from the user's fork | One exe with no companion DLL. Win2D-WinUI 1.4.0 (source SHA 25680382) is built as static libraries on the public fork https://github.com/pbk20191/Win2D, branch `static-lib`, by a GitHub Actions workflow, and published as release `win2d-static-v1.4.0-1` (x64 and ARM64 zips with `winrt.lib`, the `win2dmod.lib` shim and their PDBs, plus `SHA256SUMS.txt`). NativeAOT publishes download the zip for the RID, verify the SHA-256 pinned in the repo and link the libs with `/WHOLEARCHIVE`; CsWinRT's `ActivationFactory.ActivationHandler` routes `Microsoft.Graphics.Canvas.*` to the linked `Win2DStatic_DllGetActivationFactory`. JIT builds and tests use the package's normal DLL. This is unofficial and unsupported: we own the build. |
| Threads | One dedicated thread, driven by a Windows App SDK `DispatcherQueue` | See §5. |
| Architectures | win-x64 and win-arm64 | NativeAOT cross-compiles both from an x64 host. |
| Minimum OS | Windows 11 22H2 (build 22621) | The OS Dynamic Dependencies API that `AppRuntime` uses needs 22H2. Windows 10 is dropped: its support ended in October 2025. The TFM is `net10.0-windows10.0.22621.0` with `SupportedOSPlatformVersion` 10.0.22621.0, so capture relies on `GraphicsCaptureItem.TryCreateFromDisplayId` and `IsBorderRequired` without fallbacks. |

## 3. Repository layout

```
desktop-mcp/
  spec/
    macos/tools.json          # moved from spec/tools.json; macOS ContractTests path updated
    windows/tools.json        # upstream's 21 tools, see §4
  windows/
    global.json               # pins the .NET 10 SDK
    Directory.Build.props     # TFM, default RuntimeIdentifier (win-<arch>), Windows App SDK properties,
                              # Nullable, TreatWarningsAsErrors (IL2xxx/IL3xxx included), the exclusion
                              # block for Win2D's WinUI dependencies (PublishAot is set in the two exe
                              # csprojs, src/DesktopMcp and probes/DesktopMcp.Probe)
    Directory.Build.targets   # imports build/Win2DStatic.targets for every PublishAot project (only its
                              # targets are publish-only); trims the publish output to the exe
    build/
      Win2DStatic.props       # pinned fork release: version, URL, SHA-256 per platform, cache root
      Win2DStatic.targets     # Win2DStaticCheckPlatform (RID check), Win2DStaticFetch (download, verify,
                              # cache), Win2DStaticLink (libs, DirectPInvoke, delay-loaded graphics DLLs,
                              # feature switch), Win2DStaticStripCanvasDll
    .deps/                    # git-ignored download cache: .deps/win2d-static/<version>/<platform>/
    DesktopMcp.slnx
    probes/
      DesktopMcp.Probe/       # AOT probe exe (bootstrap, dispatcher, imaging, capture); not shipped
    src/
      DesktopMcp/             # the exe: composition root (Program.cs calls CliApp.RunAsync),
                              # app.manifest (PerMonitorV2 DPI)
      DesktopMcp.Core/        # platform-light logic: Config, ServePlan, Security, OAuth, Analytics,
                              # Scrape + HtmlToMarkdown, PowerShellRunner, FileSystem, Registry,
                              # Processes, text formatting (tables, tree renderer); CsWin32 for
                              # kernel32/ntdll (Job Object, console control, final paths,
                              # NtQuerySystemInformation, process kill)
      DesktopMcp.Desktop/     # CsWin32 and CsWinRT: Uia, Tree, Input, Capture, Imaging, Displays,
                              # VirtualDesktops, Control, Overlay, Toast, Clipboard, Apps,
                              # Dispatcher, AppRuntime.cs (Windows App SDK startup)
      DesktopMcp.Server/      # MCP SDK wiring: 21 McpServerTool subclasses, control gate filter,
                              # session registry, Kestrel pipeline, transports; Cli/ (the
                              # System.CommandLine CLI, here so DesktopMcp.Server.Tests can test it)
    tests/
      DesktopMcp.Core.Tests/
      DesktopMcp.Server.Tests/
      DesktopMcp.Desktop.Tests/   # Category=Desktop, needs an interactive session, excluded by default
    scripts/build-release.ps1     # -> windows/dist/win-x64/desktop-mcp.exe, windows/dist/win-arm64/desktop-mcp.exe;
                              # fails if a dist folder holds anything but the exe and its .pdb
    README.md
```

P0 and P0b used hand-declared `LibraryImport`s in `Desktop/Interop/Native.cs`. P1 replaced them
with CsWin32: each of `DesktopMcp.Core` and `DesktopMcp.Desktop` lists its Win32 APIs in
`NativeMethods.txt` and calls the generated `PInvoke`; new Win32 APIs are added there, not declared
by hand. The only hand-declared pieces left are the Win2D static shim export
(`Win2DStatic_DllGetActivationFactory`), a private 48-byte overlay for the `SYSTEM_PROCESS_INFORMATION`
times (the metadata only has winternl.h's struct, which hides them), and imports in tests and probes.

Boundary rule: BSTR, VARIANT, HRESULT, HWND and COM pointers never leave `DesktopMcp.Desktop`.
Its public surface is plain C# types from `DesktopMcp.Core.Snapshots` (records such as `UiElement`,
`WindowInfo`, `DisplayInfo`, `ScreenRect`, `ScreenPoint`, and the `SemanticNode` class) and
interfaces (`IUiAutomation`, `IScreenCapture`, `IInputInjector`, `IDesktopDispatcher`, ...), so
`Core` and `Server` can be tested without a desktop.

Release tags are `windows-vX.Y.Z`. The root README's port table, layout block and "single
contract" paragraph are updated to describe per-port contracts.

## 4. Tool contract

`spec/windows/tools.json` holds the 21 tools exactly as upstream declares them: names, `title`,
`description` strings verbatim, `inputSchema`, `outputSchema` and `annotations`. It is generated
once from the upstream source at the baseline commit. The input and output schemas are hand-derived
from upstream's Python signatures (the file's `$comment` says so); the user declined a check against
a live upstream server, so `tools/list` from a real upstream has not been compared. Two FastMCP artefacts are dropped: `_meta.fastmcp` on each tool and the
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
  loose for Snapshot/Screenshot `use_*`, Scrape `use_dom`/`use_sampling` and Process `force`), and list parameters
  (`loc`, `region`, `locs`, `labels`, `args`) also accept a JSON string. Validation messages are
  upstream's strings.

## 5. Threads and concurrency

The process has one root: `Main` builds a host (Generic Host for stdio, `WebApplication` for HTTP),
runs it and returns. Shutdown comes from the host lifetime (Ctrl+C, stdin EOF, SIGTERM). At
startup the process puts itself in a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so
PowerShell children cannot outlive it.

### The dispatcher thread

There is exactly one dedicated thread, created by the Windows App SDK's
`Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnDedicatedThread()` after
`AppRuntime.EnsureInitialized()`. The thread is STA (measured: `APTTYPE_MAINSTA`, because it is the
process's first STA), which suits the Win32 window and hook code. P0 first used the system
`CreateDispatcherQueueController` with explicit apartment options (`DQTAT_COM_STA`); P0b replaced it
with this API. The system owns the
thread and its message loop; we post work with `DispatcherQueue.TryEnqueue`, and a small
`DispatcherQueueSynchronizationContext` makes `await` continuations started there resume there.
On shutdown, `ShutdownQueueAsync()` drains the queue after hooks are removed and windows destroyed.

It hosts everything that needs a message loop:
- the `WH_KEYBOARD_LL` and `WH_MOUSE_LL` hooks and a message-only window (`HWND_MESSAGE` parent) that
  receives Raw Input (`RIDEV_INPUTSINK`). P4 Decisions 23 keeps the message-only window unless the Desktop
  test `RawInputReachesTheMonitorWindow` fails, in which case upstream's hidden zero-size top-level window
  replaces it; that test is pending with the user (follow-ups, P4), so the outcome is open,
- the overlay windows (control indicator, capture flash),
- `DispatcherQueueTimer`s (P4 plan, Decisions 13) for the hook reinstall (5 s), the missed-key-up
  sampler and the dispatcher beat (0.1 s; five all-up samples clear a missed key-up), the capture flash
  frames (30 ms, for 3.5 s) and, while the indicator is visible, the indicator refresh (50 ms: the
  breathing opacity and the cursor ring). The idle timeout (10 s) is not a dispatcher timer: the control
  coordinator's heartbeat on the thread pool evaluates it when it ticks the state machine (§7).

Rule: work on this thread is short and never blocks. UIA, capture, file, process and network work
are forbidden there, because a slow hook callback delays all input on the machine and a callback
that exceeds `LowLevelHooksTimeout` makes Windows silently remove the hook.

`IDesktopDispatcher` hides the mechanism. The P0 and P0b probes showed that `DispatcherQueue` can be
created and projected under NativeAOT and that LL hook callbacks are delivered on its thread (§12), so the
fallback, a `MsgWaitForMultipleObjectsEx` loop with its own work queue, was not needed.

### UIA worker and the thread pool

- **UIA** calls run one at a time on one dedicated MTA worker thread (`UiaWorker`), never on the
  dispatcher thread: a UIA client calling from a thread that owns windows can deadlock on its own
  windows. `IUIAutomation2` connection (2 s) and transaction (5 s) timeouts and per-job deadlines
  (window probe 5 s, window walk 20 s) bound a hung target app; a job past its deadline abandons the
  worker thread, the next job gets a new one, and the window and its process are skipped for 90 s
  (P2 plan, Decisions 2). These bounds protect the server, not the target app; the walk itself
  avoids the measured Chromium hang and stops at a window that stops answering WM_NULL (P2 plan,
  Decisions 36-38; §12).
- Everything else (input injection, capture, PowerShell, HTTP and the MCP SDK) runs on the thread
  pool.

### Tool-call serialisation

Gated tools (per the control policy, §7) wait on one global desktop lock (`SemaphoreSlim(1)`),
re-check the control state every 0.1 s while waiting, and keep a 15 s lease after the call ends.
Ungated tools run concurrently without the lock. With `--no-control`, every call takes the lock.
Since P4 the gate's lock replaces P3's input lock (P4 plan, Decisions 4).

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
them `get_Cached*` fails with `E_INVALIDARG`. Children are read one sibling at a time through the
control view (`GetFirstChildElementBuildCache` / `GetNextSiblingElementBuildCache` with the element
cache request), at most the first 300 per parent plus one step to learn whether more exist, so the
provider never builds more children than are read (P2 plan, Decisions 37; upstream reads all of them
with one `TreeScope_Children` fetch). A cut child list adds its own note after the tree; the element
budget does not always end the walk first.

The wrapper frees every BSTR (`SysFreeString`) and disposes every `ComVariant` before returning, and
returns `UiaElementData` records, internal to `DesktopMcp.Desktop`. The walker turns them into Core's
immutable `UiElement` records and its `SemanticNode` tree; `SemanticNode` is a class whose `Children`
is a mutable list, reversed and pruned after the walk.

### Tree and Snapshot (`Desktop/Tree`)

- **Windows walked**: `EnumWindows` filtered to visible windows on the current virtual desktop
  (documented `IVirtualDesktopManager`) that our process does not own, plus `Progman`,
  `Shell_TrayWnd` and `Shell_SecondaryTrayWnd`; processes named in
  `DESKTOP_MCP_EXCLUDE_PROCESSES` are left out of the tree walk before any UIA call on their
  windows (the window list still probes their top-level windows, as upstream does). As upstream,
  the walk covers the active window plus non-app windows, and other app windows only when a
  `region` overlaps them.
- **Classification**: upstream's `tree/config.py` constants and rules are ported into
  `TreeConfig.cs` (interactive, scrollable and informative types, roles, browser special cases,
  Word nodes from TextPattern, modal-dialog reset, window-name fixes such as Progman to "Desktop").
- **Limits**: `DESKTOP_MCP_MAX_TREE_ELEMENTS` (default 500) with upstream's truncation note
  (pointing to a browser automation MCP server instead of `use_dom`, Decisions 39); a RuntimeId
  cycle guard; three retries per window with 0.5/1/2 s backoff; 300 children per parent.
- **Target protection** (P2 plan, Decisions 36 and 38; §12): no TextPattern word read on content whose
  FrameworkId is `Chrome` (Chrome, Edge, WebView2, Electron; upstream skips it only inside a
  browser's RootWebArea), and before the window is read and before every children or word read the
  walked window must answer `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG | SMTO_BLOCK, 250 ms)`.
  A timeout leaves the window out of this Snapshot like UIA's own timeout (nothing blocked); a window
  Windows considers hung (`IsHungAppWindow`) is also skipped with its process for 90 s.
- **Virtual desktop names** use the undocumented `IVirtualDesktopManagerInternal`, with interface
  IDs keyed by build (22621 and 26100; Windows 10's 19041 is no longer supported) declared by hand, then the registry name, then
  `Desktop N`. Failure only affects the report.
- **Browser DOM**: not ported (user decision; P2 plan, Decisions 39). Upstream's DOM mode below
  `AutomationId == "RootWebArea"` and a Firefox IAccessible2 path are not planned: web page content
  is left to a browser automation MCP server (Playwright MCP, Chrome DevTools MCP). The UIA walk
  treats `RootWebArea` like any element (Decisions 4 and 36-38), and `use_dom=True` returns an error
  that points to a browser MCP server.
- **Region and display selection**, validation and error strings follow upstream.

### Snapshot text

The renderer in `Core` turns a snapshot model into text, so it is unit-tested without a desktop.
Section order and content follow upstream (cursor position, screenshot size and scale lines,
displays, desktops, focused window, opened windows, UI tree). Cleanups:
- no stray four-space indentation; every section header starts at column 0;
- tables use a deterministic implementation of tabulate's `simple` format;
- each interactive and scrollable line starts with its label: `[12] (x,y) button "OK"  [action: click]`;
- names, control types and values from other apps are escaped per field (CR, LF and TAB as `\r`, `\n` and `\t`, U+2028 and U+2029 as `\u2028` and `\u2029`, other control characters removed) so a name cannot start a fake tree line (P2 plan Decision 35);
- without an image, the result is one plain `TextContent`, not a JSON-encoded array of strings.

### Input (`Desktop/Input`)

All injection goes through `SendInput`: absolute mouse coordinates normalised over the virtual
desktop (`MOUSEEVENTF_VIRTUALDESK`), wheel events, virtual-key events with scan codes, and text as
`KEYEVENTF_UNICODE` with surrogate pairs. Every injected event carries the `dwExtraInfo` signature
`0x444D4350` ("DMCP") so the input monitor can ignore it. Cursor moves are absolute `SendInput`
moves, never `SetCursorPos` (which cannot carry the signature); a click sends the move, waits 50 ms
(skipped when the cursor is already there), then sends the press and release, so Windows Forms
controls see the hover first. `KEYEVENTF_EXTENDEDKEY` is set only on extended keys, so `alt` and
`ctrl` are the left keys, not AltGr or the Korean Han/Eng key. The count `SendInput` returns is
checked on every call, and before mouse input the window under the point, and before keyboard input
the foreground window, is checked for a higher integrity level (UIPI), so input blocked by an
elevated window gives an error instead of silently going nowhere. Otherwise timings, step counts,
drag durations, the paste path (20 or more code points without `\n`, `\t`, `{`, `}`: save
clipboard, set, Ctrl+V, restore if unchanged; a clipboard holding only non-text data is never
replaced, the text is typed instead) and Type's `clear`/`caret_position`/`press_enter` follow
upstream (P3 plan, Decisions 8, 13, 14, 21). Shortcut parsing ports upstream's `SpecialKeyNames`
table and aliases. `InputLedger` records keys and buttons held by the AI and releases them on
preemption, cancellation or shutdown.

### Capture and imaging (`Desktop/Capture`, `Desktop/Imaging`)

- **Capture**: Windows.Graphics.Capture. One `GraphicsCaptureItem` per monitor, a free-threaded
  frame pool, one frame read into a byte array. The frame pool's `IDirect3DDevice` is Win2D's shared
  device (`CanvasDevice.GetSharedDevice()`), and frames are read with
  `CanvasBitmap.CreateFromDirect3D11Surface(...).GetPixelBytes()`, so there is no hand-written D3D11
  code. Each monitor's capture item comes from `GraphicsCaptureItem.TryCreateFromDisplayId`: the
  monitor from `MonitorFromRect` and its display id from the Windows App SDK's
  `Win32Interop.GetDisplayIdFromMonitor` (P2 Task 15); this works under AOT. A full
  virtual-desktop capture composites the monitors it covers. `IsBorderRequired = false` is set
  unconditionally, because the minimum OS has the property.
  `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` is requested once per process (P2:
  Allowed). Windows marked `WDA_EXCLUDEFROMCAPTURE`, including our overlays, are excluded by the
  system.
- **Imaging**: Win2D, linked statically in NativeAOT builds (§2). Scaling
  (`scale = min(DESKTOP_MCP_SCREENSHOT_SCALE clamped to [0.1, 1], 1920/w, 1080/h)`,
  `HighQualityCubic`), annotation (2 px boxes and filled index labels in Arial 12, the cursor
  marker, reference grid lines) and PNG encoding. Box colours come from a fixed palette indexed by
  label, not random. `IImageRenderer.RenderPngAsync` is asynchronous.
- `DESKTOP_MCP_SCREENSHOT_BACKEND` is accepted and ignored; the README says so.
- `IScreenCapture` and `IImageRenderer` hide both choices. Both passed the P0 probe, so no fallback
  was needed. Callers of the Win2D entry points (`Win2DRenderer`, `GraphicsCaptureService`) must call
  `AppRuntime.EnsureInitialized()` first, which also installs the static activation hook; a missing call
  works under the JIT but fails under AOT with `REGDB_E_CLASSNOTREG`. Since P2 the entry points
  call it themselves.

### Displays

DisplayInventory and display selection use `EnumDisplayMonitors`, `GetMonitorInfo`,
`GetDpiForMonitor` (falling back to the system DPI) and `EnumDisplaySettings`, producing upstream's
fields (`index`, `device`, `primary`, `bounds`, `work_area`, `resolution`, `orientation`,
`effective_dpi`, `scale`). P2 evaluated `Microsoft.UI.Windowing.DisplayArea`: for a PerMonitorV2
process it reports the same bounds and work areas, but the Win32 path stays (P2 plan, Decisions
15). The process is PerMonitorV2 DPI-aware through its manifest, so UIA, input and capture share
physical virtual-desktop coordinates.

## 7. Desktop control

Desktop control keeps the AI from fighting the user for the mouse and keyboard. Behaviour follows
upstream; the implementation is split so the policy logic is pure and testable.

| Component | Runs on | Responsibility |
|---|---|---|
| `InputMonitor` | dispatcher thread | LL hooks and Raw Input. Reports user input: physical input and, since P4 (Open question 1), input injected by other programs (Chrome Remote Desktop, Mouse Without Borders, AutoHotkey); it ignores only events carrying our `dwExtraInfo` signature `0x444D4350`. Swallows input while suppression is active; a key or button up inherits its down's verdict, so suppression never strands a key or button down in the user's apps (P4 plan, Decisions 29). |
| `ControlStateMachine` | pure C#, `TimeProvider` injected | Turns events into state and effects. States: `disabled`, `unavailable`, `user`, `ready`, `ai`. |
| `ControlGate` | SDK call-tool filter | Policy classification, the desktop lock, the 15 s lease, checkpoints, rejection results. |
| `ControlOverlay` | dispatcher thread | Indicator: blue border on every monitor, the notice ("AI is controlling this computer" / "Move the mouse a significant distance" / "or press Ctrl + Alt + Shift + Backspace to take over"), and a ring that follows the cursor. Click-through, `WDA_EXCLUDEFROMCAPTURE`. Technology (P4 Task 1, Decisions 2; §12): Win2D renders premultiplied pixels on the thread pool and layered popups show them with `UpdateLayeredWindow(ULW_ALPHA)`; Composition plus Win2D and WinUI 3 were rejected because they load d2d1 at window creation (and WinUI 3 blends its opacity with black, not with the desktop). The indicator stays visible during captures, which exclude it, instead of being hidden around them. Also shows the #FF4500 capture flash for 3.5 s (`DESKTOP_MCP_DISABLE_FLASH` turns it off) after the capture's render, and never shows a flash layer whose capture exclusion failed. |
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
- **Input from other programs** (P4 Open question 1): injected input without our signature counts
  as user input, exactly like physical input (swallowed during `ai`, the chord works, keys and buttons
  move `ready` to `user`); its pointer path in screen pixels counts toward `mouse_takeover_pixels`
  (default 500, range 40 to 2000). Chrome Remote Desktop injects through `SendInput`, so its mouse and
  the chord take over. Absolute or null-device Raw Input (Remote Desktop/RDP, some tablets) is not
  relative motion, so over RDP moving the mouse cannot take over; only the chord does.
- **Arming**: entering `ai` shows the indicator first; suppression is armed only after the show is
  acknowledged and only if the monitor's last physical stamp is not after the `ai` entry stamp (P4
  plan, Decisions 15; the Task 4 review ruling).
- **Process allowlist** (P4 plan, Decisions 5): `[control] process_allowlist`, a list of process
  image names (default empty: every process). Just before `SendInput`, whether control is enabled or
  not, the process that owns the target window (the root window at an absolute mouse event's point,
  under the cursor for wheel and button events without coordinates, the foreground window for
  keyboard events) must be listed, else the batch sends nothing and fails with `Input blocked: the
  target window belongs to <name>, which is not in control.process_allowlist.` A batch of only key
  and button ups is never blocked. UWP apps are hosted by `ApplicationFrameHost.exe`, so allowlisting
  a UWP app by its own name does not work.
- **Notifications** are built as raw JSON: ModelContextProtocol 2.2.0 marks its logging API obsolete
  (MCP9005), so the server declares the `logging` capability and sends `notifications/message`
  without it (P4 plan, Decisions 22).

**Input-lock safety.** The hook swallows input only while a suppression deadline (1 s) is in the
future. The deadline is renewed by the control coordinator's heartbeat on the thread pool (every 50 ms while the state is ai and the indicator is visible), not by a dispatcher timer, so a stuck coordinator releases the user's input within a second (P4 plan, Decisions 12). If
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
  `--control/--no-control`, `--control-idle-timeout`, `--control-policy`. Only the options that
  have an `envvar` upstream get a `DESKTOP_MCP_*` variable, plus `DESKTOP_MCP_DEBUG`; `--transport`,
  `--host`, `--port`, `--config` and `--allow-insecure-remote` have none (P1 plan, Decisions 2).
  Serve flags given before a subcommand produce upstream's usage error. P1 uses System.CommandLine
  2.0.12. The CLI lives in DesktopMcp.Server/Cli so it can be tested; the exe's Program.cs only
  calls it. In P1, install, uninstall, auth and the HTTP transports parse their options and report
  the phase that brings them.
- Precedence: CLI or environment, then `config.toml`, then defaults.
- `~/.desktop-mcp/config.toml` (or `--config`) is parsed with Tomlyn's source generator, with
  upstream's sections (`[server]`, `[security]`, `[tools]`, `[control]`) and strict types (a TOML
  bool where an int is expected is an error). Certificate paths resolve relative to the config
  file's folder. Tomlyn 2.10.1 reads TOML 1.1, a superset of the TOML 1.0 that upstream's tomllib
  reads. Its source-generated context reads the file as an untyped table, so the strict-type
  messages are upstream's. Tomlyn's reflection fallback is off in every build, tests included.
- `ServePlan` is the validated, immutable result: SSL files both or neither, OAuth id and secret
  both or neither, a non-loopback HTTP bind requires an auth key, OAuth or
  `--allow-insecure-remote` (upstream's "Refusing to bind HTTP transport..." message), port range.
- `--tools` / `--exclude-tools` filter the registered tools; ControlStatus is always kept.
- `[control]` keys: `enabled`, `policy`, `idle_timeout_seconds`, `mouse_takeover_units`,
  `mouse_takeover_pixels` (upstream's), and `process_allowlist`, a TOML array of process image names
  checked before every `SendInput` (default empty: every process; an entry without a `.` also matches
  `<entry>.exe`; no environment variable) (P4 plan, Decisions 5).

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
- **Process**: names, memory and CPU times from `NtQuerySystemInformation(SystemProcessInformation)`
  snapshots, so processes that deny `OpenProcess` still get real CPU; CPU% from two samples a short
  interval apart (upstream always reports about 0); fuzzy name filter (partial ratio above 60) implemented in `Core`; same
  table and kill messages.
- **Clipboard**: WinRT `Clipboard` first, on a dedicated STA thread and bounded by 5 s; the Win32
  clipboard with `CF_UNICODETEXT` on a thread-pool thread is the fallback, with its own 5 s bound.
  Same messages. Probe outcome under NativeAOT (P1, Task 12): WinRT works on an STA thread and
  Win32 works, and a WinRT clipboard call on an MTA thread blocks, so the bounded fallback carries
  the call. P1 observed the JIT throwing where NativeAOT blocks, and WinRT then blocking on STA in
  the same process too. P2 measured (P2 plan, Task 18): under the JIT the MTA read succeeds; under
  NativeAOT `GetContent` returns at once and the block is the `GetTextAsync` operation, whose
  `Completed` handler never fires (under the JIT it fires on an STA thread). A live main STA does
  not help. The root cause stays open, and the STA block after an MTA call is unverified.
- **Notification**: CsWinRT `ToastNotificationManager.CreateToastNotifier(app_id).Show()` with
  ToastGeneric XML; never reads `notifier.Setting` (throws for unpackaged apps). It stays on
  `ToastNotificationManager` and not the Windows App SDK's `AppNotificationManager`, because `app_id`
  takes arbitrary AUMIDs.
- **Scrape**: `HttpClient` over `SocketsHttpHandler` whose `ConnectCallback` connects only to the
  address that passed validation; http/https only, no URL credentials, private, loopback,
  link-local, multicast, reserved and unspecified addresses blocked (with the Fake-IP DNS message
  for 198.18.0.0/15 and 2001:2::/48); `100.64.0.0/10` (CGNAT) and `fec0::/10` are also blocked,
  stricter than upstream (P1 plan, Decisions 16); at most 5 requests (4 redirects), as upstream, with
  redirects followed manually and re-validated; 10 s timeout. HTML is converted by a C# port of the
  macOS port's `HTMLToMarkdown.swift`. `use_dom` would read DOM text nodes from the last snapshot; with no DOM mode (P2 plan, Decisions 39) it always returns upstream's "No DOM information found" text.
  `use_sampling` calls the SDK's `SampleAsync` with upstream's system prompt and `max_tokens` 2048,
  returning the raw content when the client cannot sample or sampling fails. P1 ships without
  sampling and without DOM: use_dom returns upstream's "No DOM information found" text, and
  sampling returns the raw content, as upstream does for a client that cannot sample.
- **App**:
  - `launch` enumerates the Shell `AppsFolder` namespace once (`SHCreateItemFromParsingName` of
    `shell:AppsFolder`, `BHID_EnumItems`, display and parsing name per item, on an STA thread
    bounded at 10 s; Win32 shortcuts and packaged apps with their localised names) instead of
    `Get-StartApps`, and picks the best fuzzy match of the lower-cased display names (`WRatio`,
    cutoff 70). A packaged app (`<family>!<app>`) is activated with
    `IApplicationActivationManager.ActivateApplication`, which returns its PID; anything else is
    started with `ShellExecuteEx` of `shell:AppsFolder\<parsing name>` and
    `SEE_MASK_NOCLOSEPROCESS` (a PID when a process handle comes back). The launch call is
    abandoned after 10 s. It then polls every 0.5 s: up to 10 s for a visible top-level window of
    the PID, then up to 10 s more for a visible window whose title contains the name. Apps the
    server starts itself stay in its kill-on-close job and close with it (unverified for ShellExecuteEx launches until the by-hand check; §12; P3 plan, Open
    question 1 and Decisions 31).
  - `launch_executable` (`CreateProcess`, inside the job) and `switch` (WM_NULL check, restore
    with `ShowWindowAsync`, `AllowSetForegroundWindow`, `SetForegroundWindow`, then the `AttachThreadInput` sequence on its
    own thread bounded at 2 s without `BringWindowToTop`, 1 s verification; P3 plan, Decisions 7
    and 33) keep upstream's semantics and strings. `resize` uses `SetWindowPos` with
    `SWP_ASYNCWINDOWPOS` after the WM_NULL check, never a synchronous cross-process `MoveWindow`,
    and reports a failed call (P3 plan, Decisions 32).
- **WaitFor**: polls only what each condition needs (the foreground window, the focused element,
  or the window scan plus an early-exit walk of the windows Snapshot walks), never a full Snapshot
  (P3 plan, Decisions 6), with upstream's conditions, aliases, limits and messages;
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

PostHog over `HttpClient`, fire-and-forget, failures ignored. **Off by default** (user decision,
2026-10-20): nothing is sent unless `ANONYMIZED_TELEMETRY=true` **and** `POSTHOG_API_KEY` is set.
There is no default key, so the server never reports to upstream's PostHog project on its own;
`POSTHOG_HOST` defaults to `https://us.i.posthog.com`. Upstream's default is on, with its own key. `distinct_id` is a UUIDv7 stored in
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
| Core unit | config precedence and strict TOML types; ServePlan validation messages; SSRF rules; HTML to Markdown; OAuth/PKCE flows; PowerShell runner against real pwsh (encoding, timeout, tree kill); FileSystem formats; Shortcut parsing; table formatter; Snapshot text rendering from synthetic `UiElement` records and `SemanticNode` trees; fuzzy matching; control state machine with a fake `TimeProvider` (takeover, idle return, lease, policy classification) | always |
| Server | ContractTests (`tools/list` equals `spec/windows/tools.json` as JSON); in-memory Kestrel tests of middleware order, 401/403 bodies, CORS, OPTIONS, trusted host, `/health`; stdio initialize, tools/list and call | always |
| Desktop integration | the tests' own windows only (the P2 test window and, from P3, the WinForms target; never the user's windows, P3 Decisions 11): Snapshot reads, input through `GuardedInjector` (clicks, typing and paste, ToolStrip clicks, the signature), the label locator, Type's read-back, the foreground switch between test windows; capture and annotation; hooks, overlay, takeover and input-lock safety (heartbeat stops, input returns within 1 s; a stalled dispatcher cannot lock input), done in P4 (`InputMonitorDesktopTests`, `OverlayWindowDesktopTests`, `OverlayArtTests`, `CaptureFlashDesktopTests`; written, by-hand runs pending with the user). Notepad and App launch, switch and resize of real apps are by-hand checks (the follow-ups' P3 section), not automated tests | `Category=Desktop`, run by hand in an interactive session |
| AOT smoke | `build-release.ps1` publishes win-x64 and win-arm64, launches the x64 exe and runs initialize, tools/list and the headless tools that leave the desktop alone over stdio, then DisplayInventory and Snapshot without vision (a named virtual desktop and at least one tree element; the graphics stack must still be unloaded), then Screenshot and Snapshot with vision (valid PNGs); `DESKTOP_MCP_EXCLUDE_PROCESSES` can keep processes out of the walk (§12). Before the graphics check it also calls the act tools (Click, Type, Shortcut, Move, MultiSelect, MultiEdit, App, WaitFor) with arguments that inject nothing and activate nothing: argument errors, an out-of-range label and point, a shortcut parse error, App lookups of an all-punctuation name (`#`), which fuzzy processing empties so it matches no app or window, and WaitFor timeouts (P3 plan, Decisions 12). Since P4 the first session runs with `[control] enabled = false` and `DESKTOP_MCP_DISABLE_FLASH=1` and checks ControlStatus's disabled text, and the second session keeps control on and reads only ControlStatus and an ungated Wait (it never takes control; its hooks pass every event through), then checks that the hooks and the dispatcher did not load the graphics stack. It then runs the probe's `imaging` step (the AOT activation check), `uia` step (the AOT UIA check) and `overlay` step (the AOT overlay check: the indicator and one flash rendered and pushed to hidden layer windows, never shown); any IL2xxx/IL3xxx warning fails the build. The desktop calls need an unlocked interactive session; `smoke-stdio.ps1 -SkipCapture` leaves out the two capture calls | every release build |

xUnit runs under the JIT, so AOT-only failures are caught by the smoke step.

P1 results (2026-10-20, Windows build 10.0.26220.9739): default suite 742 tests passed (Core 655,
Server 76, Desktop non-desktop 11); Desktop suite passed (28 tests, by hand in an interactive
session); build-release: win-x64 16,780,800 bytes, win-arm64 17,935,360 bytes, each dist folder
holding only the exe and its pdb; stdio smoke OK under NativeAOT (initialize, tools/list equal to
spec/windows/tools.json, Wait, FileSystem, Process, Registry, PowerShell, Scrape; graphics stack
not loaded, 45 modules; the PowerShell grandchild died with the server; a second session with a
config file excluding Registry listed the other 20 tools); AOT activation check OK; clipboard
probe: `clipboard: Win32: OK`, `clipboard: WinRT on STA: OK`, `clipboard: WinRT on MTA:
TimeoutException 0x80131505 The operation has timed out.`, `clipboard: SystemClipboard OK through
WinRt (WinRT: TimeoutException 0x80131505: The WinRT clipboard did not respond within 5 s.)`
(`LastWinRtError` is never reset, so the timeout most likely comes from the write after the MTA
hang, which then fell back to Win32, while the read went through WinRT; this is an inference, not
measured); toast probe: shown. Windows Sandbox (build 26100, no Windows App SDK runtime, 2026-10-20):
the server started and passed initialize, the contract comparison, Wait and the headless tools,
which backs the lazy-initialisation claim in §10. The kill-on-close check failed there, because
processes the server starts after it joins its job are not placed in that job on that host. The
P4 follow-up "Kill-on-close job inheritance" tracks this.

P2 results (2026-10-20, Windows build 10.0.26220.9739; the default suite and build-release reran on
the final branch after Tasks 20-22 and the final-review fixes): default suite 1197 tests passed
(Core 861, Server 88, Desktop non-desktop 248); Desktop suite by hand: 33 run, 32 passed, and
`ClipboardTests.SystemClipboardRoundTripsThroughWinRt` failed twice (the WinRT read after a WinRT
write returned null; the clipboard code is unchanged since P1, and Windows Sandbox and Chrome Remote
Desktop were open, both of which can sync the clipboard; not investigated). Three Desktop tests that
read or walk the user's real windows were not run, so that nothing could reach VS Code (§12):
`SnapshotServiceDesktopTests`, `WindowCatalogDesktopTests.ListsTheTestWindowAsANormalApplicationWindow`
and `UiaSessionTests.CachesTheTraversalPropertiesForTheRootsChildren`; the user ran them afterwards with VS Code open, and all three passed (2026-10-20). build-release: win-x64
18,023,936 bytes, win-arm64 19,221,504 bytes, each dist folder holding only the exe and its pdb, no
publish warnings; stdio smoke OK under NativeAOT (adds DisplayInventory and Snapshot without vision
before the graphics check, 59 modules, then Screenshot and Snapshot with vision; Code.exe excluded,
no Code process running; walk with Decisions 36-38; 1 display, active desktop "Desktop 1", 55 tree
elements of which 33 labelled, a 1728x1080 Screenshot PNG and the 200x120 region PNG; the server
exited 26 ms after stdin closed; earlier runs 23-25 ms); AOT activation and UIA checks OK; capture probe 2560x1600 in 174 ms, borderSuppressed=True,
borderlessAccess=Allowed; capture border with the user watching (2026-10-20, build 26220, 8 probe captures of the 2560x1600
monitor): no yellow border and no capture indicator seen; DisplayArea evaluation: agrees on 1 monitor (2560x1600 at 144 DPI), multi-monitor and
mixed DPI unproven; multi-monitor capture composition is tested with fakes only; Windows Sandbox
(no Windows App SDK runtime, run by the user on 2026-10-20 without `-SkipCapture`): initialize, the
tools/list comparison, the headless tools, DisplayInventory, Snapshot without vision (real UIA tree
and virtual-desktop assertions) and the graphics-stack check passed, so the UIA path needs no
runtime; Screenshot then failed as expected, without a crash, with `Error capturing screenshot: The Windows App SDK runtime (Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe, x64) is not installed: TryCreatePackageDependency failed with HRESULT 0x80670016. Install the Windows App SDK 2.5 runtime (2.5.1.0) or later from https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads.. Please try again.`
(the final review fixed the doubled period: the wrapper trims the message's trailing periods). The user's `-SkipCapture` rerun passed the whole first
session (the server exited within 5 s of stdin closing) and then failed, as expected, at the
kill-on-close check (`ping 6272 outlived the server: no kill-on-close job`; see the P4 follow-up
"Kill-on-close job inheritance"), so the second session (the excluding config) did not run.
Clipboard probe as in P1; toast probe: shown. Desktop tests need a PerMonitorV2 test process
(`TestDpi`) and use their own test window. Tests that touch user state: the clipboard tests save
and restore the clipboard text, and the toast test leaves a notification.

P3 results (2026-10-20, Windows build 10.0.26220.9739): default suite 1687 (Core 962, Server 214,
Desktop non-desktop 511); Desktop suite by hand: not run yet (pending with the user; the commands
are in the follow-ups' P3 section); build-release: win-x64 18,688,512 bytes, win-arm64 19,916,288
bytes, each dist folder holding only the exe and its pdb, no publish warnings; stdio smoke OK under
NativeAOT with 13 act-tool checks and no input sent (1 display, active desktop "Desktop 1", 85 tree
elements of which 58 labelled; graphics stack not loaded, 86 modules; a 1728x1080 Screenshot PNG
and the 200x120 region PNG; the PowerShell grandchild died with the server, which exited 29 ms
after stdin closed; the excluding config listed 20 tools; the smoke alone passed again against the same exe after the
App rows moved to the all-punctuation name); AOT activation and UIA checks OK;
by-hand act session: not run (pending with the user, with the App launch, `launch_executable`
and job checks in the follow-ups' P3 section). Input tests inject only through GuardedInjector
into the tests' own windows.

P4 results (2026-10-21, Windows build 10.0.26220.9739): default suite 2034 (Core 1033, Server 272, Desktop non-desktop 729); Desktop suite 0 by hand, all pending (`InputMonitorDesktopTests` 8, `OverlayWindowDesktopTests` 5, `CaptureFlashDesktopTests` 2, `OverlayArtTests` (run off-screen by the Task 8 implementer, 6 of 6), the P3 tests still pending, and `SnapshotServiceDesktopTests`, which now flash); build-release: win-x64 19,095,552 bytes, win-arm64 20,341,248 bytes, each dist folder holding only the exe and its pdb, no publish warnings; stdio smoke OK under NativeAOT with the control session (ready) and no control taken (first session with control disabled and the flash off: ControlStatus disabled, 13 act-tool checks with no input sent, 1 display, active desktop "Desktop 1", 187 tree elements of which 159 labelled, graphics stack not loaded with 86 modules, a 1728x1080 Screenshot PNG and the 200x120 region PNG, the PowerShell grandchild died with the server, which exited 31 ms after stdin closed; second session: the excluding config listed 20 tools, ControlStatus `ready`, Wait ungated, the hooks and the dispatcher loaded no graphics DLL, exit within 5 s); AOT activation and UIA checks OK; AOT overlay check OK (hidden): `overlay: 11 layers, 4406824 bytes rendered, hidden -> OK` (one monitor: four edges, the notice glow and notice, the cursor ring and four flash strips); by-hand control session: pending with the user (follow-ups, P4). One default-suite test is flaky: `ControlNotifierTests.TheReturnIsAnnouncedAsAiControlAvailable` failed in 4 of 5 full-suite runs and in about 1 of 6 Server-only runs, every time with the two notifications reversed (`AI_CONTROL_AVAILABLE` before `USER_CONTROL`): `ControlNotifier` starts each session's send with its own `Task.Run`, so two quick changes can reach a session out of order (the Task 13 deferred minor "per-session sends can reorder two quick events", now a test failure; P4 leftovers, final review). The counts above are the passing runs.

## 10. Differences from upstream (for the Windows README)

- **Naming**: `desktop-mcp`, `~/.desktop-mcp`, `DESKTOP_MCP_*`. No Python, uv, PowerShell 5.1,
  mkcert or openssl needed.
- **Installation**: Windows 11 22H2 or later and the system Windows App SDK 2.5 runtime are required.
  The runtime is bootstrapped lazily by the desktop services: if it is missing, the server still starts
  and headless tools work, while desktop tools return an error result that says how to install the
  runtime. With desktop control enabled (the default) the runtime is bootstrapped at startup, because
  the hooks live on its dispatcher thread; without it control is `unavailable` and the gated tools
  return `CONTROL_UNAVAILABLE` (fail closed, P4 Open question 2), while `--no-control` runs them
  unprotected. `desktop-mcp.exe` is the only shipped file; Win2D is linked into it.
- **Output cleanups**: no stray indentation in Snapshot/Screenshot text; labels shown in the UI
  tree text; a plain text result instead of a JSON-encoded array when there is no image; Scroll
  without `loc` reports `Scrolled ... at current cursor position (x,y).` instead of an empty
  string; Click with `clicks` of 3 or more reports `Clicked N times` instead of `None`;
  annotation colours are fixed per label; the `initialize` instructions have no leading or
  trailing newline; a label action reports the point it actually used; a label that cannot be
  acted on gives a specific error (stale, disabled, off screen, covered by another window, or its window
  not responding) instead of acting at the stored point (a label whose point another window covers,
  other than a top-level window on its own window's GUI thread (an owned or unowned menu, or a
  combo box's drop-down list), sends nothing: P3 plan, Decisions 42); Type and each MultiEdit item read the focused element's
  value back and add one `Warning: ... does not contain the typed text; it reads '...'.` line on
  a mismatch.
- **Bug fixes**: scrollable elements report their box centre (upstream picks a random point);
  WaitFor `element_enabled` checks the enabled state; telemetry events are sent when enabled (opt-in, see Telemetry); `/health` exists;
  Process CPU% is measured; the unreachable `takeover_pending` state is gone; label actions
  look the element up again and act at its ClickablePoint instead of the stored centre; WaitFor
  and App leave the label map alone, so labels keep resolving against the last Snapshot; points
  outside the virtual desktop are rejected for every pointer action (Move, drag, Scroll,
  MultiSelect), not only Click and Type; a shortcut is validated before anything is sent (upstream
  could leave Ctrl held); `clicks` below 0 is an error; non-ASCII app and window names (Korean,
  Japanese) can match in App; a key or button up inherits its down's verdict while control
  suppresses input, so no key or button stays down in the user's apps (upstream decides each event on
  its own, P4 plan, Decisions 29).
- **Protocol**: the legacy SSE message endpoint is `/message` (clients follow the `endpoint`
  event); no FastMCP `_meta` on tools; JSON serialisation details are the MCP C# SDK's.
- **Implementation**: capture uses Windows.Graphics.Capture, so `DESKTOP_MCP_SCREENSHOT_BACKEND`
  is ignored; Registry, Notification, the app list and `install` do not use PowerShell;
  `auth --with-tls` generates its own self-signed certificate. Desktop control (P4): input injected
  by other programs (Chrome Remote Desktop, Mouse Without Borders, AutoHotkey) counts as user input,
  and only our signed input is ignored (Open question 1; upstream ignores every injected event, so a
  remote user could not take over); `mouse_takeover_pixels` applies to that foreign injected motion
  (upstream accepts the key and ignores it); the indicator stays visible during captures, excluded
  from them with `WDA_EXCLUDEFROMCAPTURE`, instead of being hidden around each capture, and
  suppression is not paused for captures; the capture flash also covers display-selected captures
  with an inward halo inside each captured monitor (upstream draws the outward halo of the union,
  which falls off-screen), is shown after the capture's render, and is not shown at all when a layer
  cannot be excluded from capture; the server declares the `logging` capability and sends
  `notifications/message` as raw JSON (ModelContextProtocol 2.2.0 marks its logging API obsolete);
  `[control] process_allowlist` restricts injected input to listed processes.
- **P1 decisions**: P1's deliberate departures from upstream in the CLI, config and headless tools
  are listed in the P1 plan's "Decisions on upstream ambiguities"
  (`docs/superpowers/plans/2026-10-19-windows-p1-headless.md`), which also records the rulings
  made during P1's reviews (codec names, FileSystem overlap and links, Registry, Process, Scrape
  address rules, HTML to Markdown limits).
- **P2 decisions**: P2's departures from upstream for Snapshot, Screenshot and DisplayInventory are
  listed in the P2 plan's "Decisions on upstream ambiguities"
  (`docs/superpowers/plans/2026-10-19-windows-p2-desktop-read.md`): the text layout and labels,
  `Screenshot Backend: wgc`, regions clipped to the screen, window order, the UIA hang breaker,
  `use_dom` (no DOM mode, Decisions 39), no word elements for Chromium content, and at most 300 children per parent.
- **P3 decisions**: P3's departures from upstream for the input and act tools (SendInput only with
  the signature, the 50 ms click pause, extended keys, the label re-find and gate, the read-back,
  WaitFor without full Snapshots, the bounded foreground switch, App's AppsFolder launch, async
  resize and fuzzy matching, the argument shapes) are listed in the P3 plan's "Decisions on
  upstream ambiguities" (`docs/superpowers/plans/2026-10-20-windows-p3-desktop-act.md`).
- **P4 decisions**: P4's departures from upstream for desktop control (no `takeover_pending`
  state, no hide-for-capture, no `rotating` pause, the thread-pool heartbeat, the monitor start that
  never stops the server, the arming rule, the up-inherits-down rule, the process allowlist, the
  flash geometry, the raw-JSON logging) are listed in the P4 plan's "Decisions on upstream ambiguities" (docs/superpowers/plans/2026-10-20-windows-p4-desktop-control.md).

## 11. Phases

One implementation plan, executed phase by phase with a review checkpoint after each. Riskiest
work first.

| Phase | Scope | Outcome |
|---|---|---|
| P0 Foundations and probes | Move `spec/tools.json` to `spec/macos/` and fix the macOS test path; write `spec/windows/tools.json`; solution skeleton and build settings; AOT probes for `DispatcherQueue` + LL hooks, Windows.Graphics.Capture, Win2D imaging; spec-driven `McpServerTool` base class and ContractTests | An empty server answers `tools/list` |
| P0b Single exe | Windows 11 22H2 minimum; self-bootstrap of the system Windows App SDK 2.5 through the Dynamic Dependencies API; dispatcher and display ids on the Windows App SDK; Win2D linked statically from the pinned fork release; publish output trimmed to one exe | `dist/<rid>/desktop-mcp.exe` runs alone in an empty folder |
| P1 Server core and headless tools | CLI, config, ServePlan, stdio; Wait, PowerShell, FileSystem, Clipboard, Process, Registry, Notification, Scrape without sampling | A useful server without desktop access (done; results in §9) |
| P2 Desktop read | Dispatcher (fixed in P2, first started in P4: P2 plan, Decisions 21), UIA wrapper, Tree, Snapshot without DOM, capture, imaging, Screenshot, DisplayInventory, virtual desktops | The agent can see the screen (done except a rerun of the WinRT clipboard test and the three Desktop tests left out of the final run; results in §9) |
| P3 Desktop act | Input, InputLedger, Click, Type, Scroll, Move, Shortcut, MultiSelect, MultiEdit, App, WaitFor | Upstream's core workflows (done except the Desktop suite and the by-hand act session, pending with the user; results in §9) |
| P4 Desktop control | InputMonitor, state machine, overlay, capture flash, gate, notifier, ControlStatus | Protection against fighting the user (done; results in §9) |
| P5 HTTP | Kestrel, Streamable HTTP and SSE, middleware, TLS, OAuth, `/health`, Scrape sampling, telemetry | Remote use |
| P6 Finish | Install, uninstall, auth, release script for x64 and arm64 (started in P0b, which already trims the output to one exe), Windows README, root README update | `windows-v0.1.0` |

## 12. Risks

P0 probe results were measured on Windows build 10.0.26220 (Insider).

| Risk | Status / mitigation |
|---|---|
| `DispatcherQueue` creation/projection or LL hook delivery fails under AOT | Measured in P0: the DispatcherQueue thread is created and projected under AOT; startup plus the first invoke takes about 10 ms; timer ticks run on the dispatcher; a `WH_MOUSE_LL` hook installed on it received 2 of 2 injected signed moves, flagged injected, on the dispatcher thread. The `MsgWaitForMultipleObjectsEx` fallback behind `IDesktopDispatcher` was not needed. |
| Windows.Graphics.Capture needs D3D11/WinRT interop that misbehaves under AOT, or its border cannot be hidden | Measured in P0: the primary monitor (2560x1600, physical, PerMonitorV2) was captured in 146-157 ms under AOT. `IsBorderRequired = false` was accepted without error. P2: the AOT capture probe reports borderSuppressed=True and borderlessAccess=Allowed (2560x1600); with the user watching (2026-10-20, build 26220, 8 captures), no yellow border and no capture indicator appeared. The GDI fallback behind `IScreenCapture` was not needed. |
| The imaging library produces trim/AOT warnings or adds too much size | Measured in P0 with Win2D: a 1920x1080 frame with 40 annotations at scale 0.75 gives a 45 KB PNG in 102-149 ms; the probe exe grew by about 461 KB; 0 warnings. In P0b the statically linked build rendered the same image in 94 ms. ImageSharp was dropped for licensing and vulnerability reasons (§2). |
| The Windows App SDK metapackage copied about 113 MB per RID of unused files (DirectML, onnxruntime, AI.MachineLearning, WebView2) into publish output | Solved in P0b: the build references the Runtime and InteractiveExperiences component packages, and `build-release.ps1` fails unless each `dist/<rid>` folder holds only `desktop-mcp.exe` and its `.pdb`. |
| The Windows App SDK 2.5 runtime is not installed on the user's machine | `AppRuntime.EnsureInitialized()` throws with an install hint; documented as an installation requirement (§10). |
| The self-bootstrap needs Windows 11 22H2 (the Dynamic Dependencies API) | The minimum OS is 22H2 (§2). `AppRuntime.EnsureInitialized` checks `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)` before the kernelbase call and throws an `InvalidOperationException` that names Windows 11 22H2; a missing kernelbase export becomes the same exception. |
| Statically linked Win2D would load the graphics stack (d2d1, d3d11, dwrite) at the start of every run, headless ones included | Solved in P0b: `Win2DStatic.targets` delay-loads d2d1.dll, d3d11.dll, dwrite.dll, d3dcompiler_47.dll and api-ms-win-shcore-stream-winrt-l1-1-0.dll, so they load on the first Win2D call. `smoke-stdio.ps1` fails if a headless run (initialize, tools/list, `Wait`) has any of the DLLs loaded. |
| We own the Win2D static build: the fork is unofficial and unsupported, and only CsWinRT activation is routed (a direct `RoGetActivationFactory` call for a Canvas class fails with `REGDB_E_CLASSNOTREG`) | The release is pinned by version and SHA-256, cross-checked against a fresh download. Moving to a new Win2D release means bumping the pinned version and hashes. |
| The ARM64 exe is build-verified only; it was never run (no ARM64 machine) | Run the smoke test and the probe on ARM64 hardware before the release. |
| Concurrent cold-cache publishes (for example publishing the solution) can race on the Win2D download | The release script publishes one project at a time; making the fetch safe is a P6 follow-up. |
| Each exe `.pdb` is about 115 MB, and the Win2D cache needs about 173 MB per platform | The `.pdb` is not shipped; the cache is git-ignored in `windows/.deps/`. |
| The undocumented virtual-desktop interface changes in a new Windows build | Only desktop names depend on it; fall back to registry names, then `Desktop N` |
| The MCP SDK's serialisation or legacy SSE support changes | Pin the SDK version; ContractTests and HTTP tests catch drift |
| LL hooks swallow input and lock the user out | Heartbeat-renewed suppression deadline (1 s, renewed every 50 ms by the coordinator on the thread pool), `LowLevelHooksTimeout`, fail-open on a lapsed deadline, a hook exception, a lost desktop or a queue overflow; an up inherits its down's verdict, so nothing stays held. Measured in the default suite with fakes: `PastTheDeadlineTheHookPassesAndRequestsFailOpen` (Task 6), `AStoppedHeartbeatLetsTheDeadlineLapse` (Task 11). The real-hook Desktop tests (`InputMonitorDesktopTests`: `SuppressionLapsesWithinOneSecondWhenTheHeartbeatStops`, `AStalledDispatcherCannotLockInput` and six more) are written and compile but have not run: they swallow input on the user's live machine, so they are pending with the user (follow-ups, P4). The P4 release smoke installed the real hooks on the user's session in pass-through mode only (never `ai`). |
| UIA calls into hung apps | IUIAutomation2 timeouts, per-job deadlines, a replaceable UIA worker and a 90 s block of hung windows (P2 plan, Decisions 2) |
| Snapshot's UIA walk hangs a Chromium app (cause measured; mitigated by P2 Decisions 36-38) | Windows logged an AppHang for VS Code (Code.exe 1.142) twice on 2026-10-20, the second during the P2 smoke's Snapshot, and VS Code closed. P2 Task 21 measured the cause on Edge with a page of N sibling buttons: the walker's word read on the page's DocumentControl. `GetCurrentPattern(TextPattern)` plus `GetVisibleRanges` alone blocks the UI thread for 5.2 s at 1,000 buttons, about 46 s at 3,000, and more than 65 s at 10,000 (roughly quadratic). Neither the cache request size nor accessibility first-enable nor the window-list probe causes it. Mitigated in Task 22 (P2 Decisions 36-38): no word read on content whose FrameworkId is `Chrome`; children by sibling navigation, at most 300 per parent; and a WM_NULL check (250 ms) before the window is read and before each children or word read: a timeout leaves the window out of that Snapshot, and only a window Windows considers hung (`IsHungAppWindow`) is blocked with its process for 90 s; a cut child list adds its own note after the tree. Measured after the change (JIT probe, product walk): on a 10,000-button Edge page, 370 elements in 0.35 s with no failed ping; on a 1,000-button page, 0.35-0.37 s with no failed ping; on a fresh VS Code with a 50,000-line file, 0.15-0.23 s with no failed ping. The hang itself was never reproduced on VS Code. On 2026-10-20 the user ran the AOT smoke with no exclusion and their VS Code open (86 tree elements) and the three real-window Desktop tests: all passed, VS Code stayed responsive, and no AppHang was logged during the runs. So the smoke's Code.exe exclusion and `build-release.ps1`'s skip of the probe's `uia` step were removed. (One AppHang from `old_mscopilot_proxy.exe` 156.0.4314.29, an Edge Beta component, was logged at 14:35 that day, about 12 minutes after the final build-release run; its cause is unknown.) The guard cannot interrupt a call already inside a provider. |
| The Job Object kills every process the server started when the server exits, including apps started through the PowerShell tool (Start-Process) and by App `launch` and `launch_executable` | Spec §5 behaviour, kept in P1 and, for launched apps, in P3 (user decision, 2026-10-20; P3 plan, Open question 1); upstream has no job, so its launched apps survive. Launched apps die with the server, with unsaved work, except packaged apps (activated by the system through `IApplicationActivationManager`) and apps that hand off to a broker or an existing instance, which the system starts outside the job. Revisit if users report it; JOB_OBJECT_LIMIT_BREAKAWAY_OK plus CREATE_BREAKAWAY_FROM_JOB would let chosen launches escape. |
| Under NativeAOT, a WinRT clipboard call on an MTA thread blocks | Known issue. P1 observed the block (where it saw the JIT throw) and WinRT then blocking on STA in the same process. P2 measured that under the JIT the MTA read succeeds, and that under NativeAOT the `GetTextAsync` operation's `Completed` handler never fires; a live main STA does not help. The root cause stays open, and the STA block is unverified. `SystemClipboard` calls WinRT only on its STA thread, bounds it by 5 s and falls back to the Win32 clipboard, bounded by its own 5 s. |
| Overlay technology (P4 Task 1) | Verdict `UpdateLayeredWindow` (P4 plan Decisions 2), measured 2026-10-20 on build 26220, win-x64 NativeAOT. WinUI 3: a throwaway copy of the probe with `UseWinUI`, the `Microsoft.WindowsAppSDK.WinUI` and `Microsoft.WindowsAppSDK.Foundation` exclusions lifted and a minimal `Application` opening one `Window` without XAML markup published with 0 warnings, but the folder held `Microsoft.WindowsAppRuntime.Bootstrap.dll` and `Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll` (Foundation's native assets) besides the exe and `.pdb`: rejected. No `.pri` reached the publish folder (with `AppxGeneratePriEnabled` on, `desktop-mcp-probe.pri` appeared only in `bin`); with Foundation's native assets excluded, or with Foundation left excluded, the folder held only the exe and `.pdb`. Task 1 never ran the WinUI window. A later measurement (WinUI 3 spike, 2026-10-20, build 26220, user-approved; Foundation's native assets excluded, `UseWinUI` and `Microsoft.WindowsAppSDK.WinUI` only): the publish held only the exe and `.pdb` with 0 warnings, and a `Window` without an `Application` object started under NativeAOT on the existing dispatcher thread (`WindowsXamlManager.InitializeForCurrentThread`, no `.pri`), but `graphics-not-loaded: FAIL` (XAML initialisation and window creation load d2d1, d3d11 and dcomp before any Win2D call); `Grid.Opacity` 0.5 gave rgb(34,109,192), a blend with the window's opaque black background, not with the desktop (about rgb(93,144,199)); setting a XAML brush off the UI thread threw `RPC_E_WRONG_THREAD` (0x8001010E), so off-dispatcher drawing needs a Win2D composition surface, which is the Composition candidate again; the exe grew by 550,400 bytes (+7.7%), and the shown window cost +89.0 MB working set over the dispatcher baseline against +29.0 MB for a ULW layer with its first Win2D upload (not shown). WinUI 3 rejected again; the verdict stands. Composition (a `Microsoft.UI.Composition` `ContentIsland` with a Win2D `CompositionDrawingSurface` in our popup through `DesktopChildSiteBridge`): `graphics-not-loaded: FAIL (d2d1 after dispatcher start=False, before the first Win2D call=True)`, because creating the compositor and the bridge loads d2d1; `click-through`, `visible` (rgb(45,145,255)), `capture-excluded` (0 of 80000 pixels), `opacity` (rgb(45,145,255) -> rgb(94,144,199)), `off-dispatcher` and `cleanup` OK: rejected. `UpdateLayeredWindow` (`desktop-mcp-probe overlay-spike`): `graphics-not-loaded: OK (False, False)`, `click-through: OK` (the root window at the centre is the window below), `visible: OK (WDA_NONE, screen pixel rgb(45,145,255))`, `capture-excluded: OK (WDA_EXCLUDEFROMCAPTURE, 0 of 80000 captured pixels within 8 of the colour)`, `opacity: OK (SourceConstantAlpha 128 with a null source DC: rgb(45,145,255) -> rgb(94,144,199))`, `off-dispatcher: OK` (Win2D render and `UpdateLayeredWindow` on a thread-pool thread), `cleanup: OK` -> ACCEPT. Both probe publishes held only the exe and `.pdb` with 0 warnings. After Task 1 moved the ULW code out of its candidate abstraction, the winner-only `overlay-spike` probe was republished but not re-run (the approval covered one run per candidate). The product path (`LayeredWindow`, `OverlayArt`) is checked on every release build by the probe's hidden `overlay` step (§9), which never shows a layer; its on-screen behaviour is covered by `OverlayWindowDesktopTests` and the by-hand control session, pending with the user. |
