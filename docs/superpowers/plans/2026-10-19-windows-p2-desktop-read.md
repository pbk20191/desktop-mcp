# Windows port P2: desktop read, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the agent see the screen: UI Automation through CsWin32's `[GeneratedComInterface]` interfaces, the window list and UI tree, virtual desktops, displays, Windows.Graphics.Capture with Win2D annotation, and the Snapshot, Screenshot and DisplayInventory tools with upstream's output, all under NativeAOT.

**Architecture:**
- `DesktopMcp.Core` gains the snapshot model (`ScreenRect`, `WindowInfo`, `DisplayInfo`, `UiElement`, `SemanticNode`, `TreeSnapshot`, `DesktopSnapshot`), argument and region selection, the region filter, the environment settings and the Snapshot/Screenshot text renderer. All of it is pure and unit-tested.
- `DesktopMcp.Desktop` gains UIA on a replaceable MTA worker thread with deadlines (the hang breaker), an iterative tree walker over an `ITreeSource` seam, the window catalog, virtual desktops, Win32 displays, multi-monitor capture and the reworked Win2D renderer. `SnapshotService` orchestrates them through injected dependencies, so its logic is unit-tested with fakes; real-desktop tests use a window the tests create themselves.
- `DesktopMcp.Server` gains `SnapshotTool`, `ScreenshotTool` and `DisplayInventoryTool`. Only capture and imaging initialise the Windows App SDK runtime, inside their entry points.

**Tech Stack:** .NET 10 NativeAOT, CsWin32 0.3.347 as a build task with `comInterop.useComSourceGenerators` (`[GeneratedComInterface]`, `ComVariant`), CsWinRT (Windows.Graphics.Capture, Microsoft.UI.Windowing, Win2D 1.4.0 linked statically), ModelContextProtocol 2.2.0, xUnit 2.9.3. No new packages.

**Spec:** `docs/superpowers/specs/2026-10-19-windows-port-design.md`: §5 (threads, UIA serialisation and timeouts, shared state, cancellation), §6 (UIA, Tree and Snapshot, Snapshot text, capture and imaging, displays), §9 (testing), §10 (differences), §11 (P2 row), §12 (risks). The upstream reference is CursorTouch/Windows-MCP at `128f2960c79b74bd2630f4dbc9d4f664f4ce89b3`; paths such as `tree/service.py:446` are relative to `src/windows_mcp/` in that tree. Every output string, default and limit comes from there unless spec §10 or the Decisions below say otherwise. The previous phase is `docs/superpowers/plans/2026-10-19-windows-p1-headless.md`; its Decisions still hold.

**Scope (spec §11, P2 row, and `spec/windows/tools.json`):** the tools that read the desktop without acting on it: **Snapshot** (without the browser DOM), **Screenshot** and **DisplayInventory**. WaitFor polls the tree but belongs to P3 with the other workflow tools (spec §11); ControlStatus is P4.

## Global Constraints

- TFM `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` **10.0.22621.0**; RIDs `win-x64` and `win-arm64` (default from the dotnet process architecture, already in `windows/Directory.Build.props`).
- `TreatWarningsAsErrors`, `IsAotCompatible`, no warning suppression. No new package references in P2.
- **The publish folder for each RID contains only `desktop-mcp.exe`** (plus `.pdb`), and that exe alone runs; the probe exe has the same rule. `pwsh scripts/build-release.ps1` enforces it and must keep passing: the folder check, the stdio smoke test, the AOT activation check, and (from Task 1) the AOT UIA check.
- Win32 and COM bindings come from CsWin32 0.3.347 through each project's `NativeMethods.txt`; new Win32 APIs are added there, never declared by hand. From Task 1, `DesktopMcp.Desktop/NativeMethods.json` is exactly `{"$schema": "https://aka.ms/CsWin32.schema.json", "public": false, "allowMarshaling": true, "comInterop": {"useComSourceGenerators": true}}` and the project sets `<CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>`; `DesktopMcp.Core` keeps `allowMarshaling: false`. **No built-in COM interop** (`[ComImport]`, `Marshal.GetObjectForIUnknown`, `dynamic` COM): it does not exist under NativeAOT. The only hand-declared interop is the Win2D shim export, the `SYSTEM_PROCESS_INFORMATION` overlay, the undocumented virtual-desktop interfaces (`[GeneratedComInterface]`, Task 9) and imports in tests and probes.
- Boundary rule (spec §3): BSTR, VARIANT, SAFEARRAY, HRESULT, HWND and COM pointers never leave `DesktopMcp.Desktop`'s public surface; it exposes plain records (`DesktopMcp.Core.Snapshots`) and interfaces. COM and WinRT failures cross the `IScreenCapture`, `IImageRenderer`, `IDesktopDispatcher` and UIA boundaries as `DesktopException` with `"<operation> failed: <message> (HRESULT 0x...)"` (Task 5).
- Every BSTR is freed (`SysFreeString`) and every `ComVariant` disposed before a UIA wrapper returns (spec §6).
- **Bounded blocking:** any WinRT or COM call that can block runs under a deadline, and expiry becomes a clear error: UIA jobs through `UiaWorker` (window probe 5 s, window walk 20 s, plus IUIAutomation2 connection 2 s and transaction 5 s), a capture frame 2 s, the borderless-access request 2 s, virtual-desktop calls 2 s, dispatcher start 5 s. Under NativeAOT WinRT can block: P1 observed the WinRT clipboard blocking on an MTA thread where the JIT threw; P2 measured that the JIT MTA read succeeds and that the NativeAOT `GetTextAsync` `Completed` handler never fires (Task 18); P1's report that STA then blocks too is unverified.
- **Untrusted and huge inputs:** UIA and semantic trees are walked with explicit stacks, never recursion (a StackOverflow kills the server). Caps: the element budget (`DESKTOP_MCP_MAX_TREE_ELEMENTS`, default 500), 50 visits per budget element, depth 256, 300 children per element (the first ones, by sibling navigation; Decisions 37), 10,000 word steps per text control; the cycle guard uses a set of the current path, so nothing is quadratic.
- **Seams:** every external dependency (Win32, UIA, shell, capture, renderer, clock, environment, delays) sits behind an internal interface or delegate, so the always-run tests use fakes. Real-desktop tests carry `[Trait("Category", "Desktop")]`, which `tests/default.runsettings` excludes; run them with `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath=`.
- Desktop tests never act on the user's applications: they read the desktop and walk a window the test creates (`TestWindow`, Task 11). P2 injects no input. Tests that touch user state say so: the P1 clipboard tests save and restore the clipboard text, and the toast test leaves a notification in the notification centre.
- UIA never runs on the dispatcher thread (spec §5); it runs only on the `UiaWorker` MTA thread. P2 does not start the dispatcher (Decisions 21).
- Windows App SDK initialisation stays lazy: the server starts and headless tools work without the runtime; `AppRuntime.EnsureInitialized()` is called only inside `Win2DRenderer.RenderPngAsync`, `GraphicsCaptureService.CaptureAsync` and `DispatcherQueueThread.Start`. Snapshot without vision and DisplayInventory need no runtime. Without it, vision fails with upstream's error text carrying the install hint. `scripts/smoke-stdio.ps1` keeps failing if a run without capture loads d2d1, d3d11, dwrite or d3dcompiler_47.
- Naming follows the macOS port: `desktop-mcp`, `~/.desktop-mcp`, `DESKTOP_MCP_*` (`DESKTOP_MCP_MAX_TREE_ELEMENTS`, `DESKTOP_MCP_EXCLUDE_PROCESSES`, `DESKTOP_MCP_SCREENSHOT_SCALE`, `DESKTOP_MCP_PROFILE_SNAPSHOT`; `DESKTOP_MCP_SCREENSHOT_BACKEND` is ignored).
- The tool contract `spec/windows/tools.json` does not change. Output text and behaviour match upstream except where spec §10 or the Decisions below say otherwise.
- Result shapes (spec §4): Snapshot and Screenshot have no outputSchema and return `[TextContent]` or `[TextContent, ImageContent(image/png)]` without structuredContent; DisplayInventory returns its JSON array as text plus `structuredContent: {"result": [...]}`; argument type errors are `isError` results (`Error calling tool '<Name>': <message>`, from `SpecTool`).
- Commit messages end with a blank line and `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Work happens on branch `windows-p2`.

All `dotnet` and `pwsh` commands run from `windows/`; `git` commands run from the repository root.

## Decisions on upstream ambiguities

Each decision is binding for the tasks; the citation names the upstream code (or spec text) it departs from or interprets.

The user confirmed the recommended answers on 2026-10-20:
- `use_dom` returns upstream's "not supported yet" text until P6, and browser windows are walked like any app.
- The OCR fallback is deferred to P6.
- Regions are clipped to the virtual screen.
- The Backend line prints `wgc`.
- The Snapshot layout is as planned.
- DisplayInventory stays on Win32 and prints compact JSON.
- The hang-breaker values are as planned.
- The session-0 and lock-screen texts are as planned.

1. **CsWin32 COM generation** (spec §2 "P2 adds `comInterop.useComSourceGenerators`"). The option only takes effect when CsWin32 runs as a build task, and COM interfaces are only emitted as interfaces when `allowMarshaling` is `true` (with `false` they are structs of function pointers). So `DesktopMcp.Desktop` switches to `allowMarshaling: true` plus `comInterop.useComSourceGenerators: true` and `<CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>`. While this plan was written, a scratch copy of the P1 tree confirmed that: the existing Desktop code (AppRuntime, clipboard, Win2D activation) compiles unchanged with `DisableRuntimeMarshalling`; `CUIAutomation8.CreateInstance<IUIAutomation6>()`, `ElementFromHandleBuildCache`, `BuildUpdatedCache` + `GetCachedChildren`, cached BSTR getters, `ComVariant` values (VT_I4, VT_BOOL, VT_BSTR, VT_ARRAY|VT_I4 RuntimeIds read through `SafeArrayAccessData`), the documented `IVirtualDesktopManager` and hand-declared `[GeneratedComInterface]` interfaces all work under the JIT and under NativeAOT, with zero trim or AOT warnings. The generated members are `get_X`/`put_X` methods, not properties; methods that return an interface return null for a null pointer. Task 1 turns this into the repository's spike with a probe step in `build-release.ps1`.
2. **UIA threads and the hang breaker** (spec §5 "UIA calls run on thread-pool threads, serialised by a `SemaphoreSlim`"; P0 follow-up "UIA hang breaker"; upstream has none, `tree/service.py:275-303` retries everything). UIA jobs run one at a time on one dedicated MTA thread that owns the `IUIAutomation6` client (`UiaWorker`). A window probe has 5 s and a window walk 20 s; IUIAutomation2 sets the connection timeout to 2 s and the transaction timeout to 5 s. A job past its deadline gives `UiaTimeoutException`; its thread is abandoned and the next job gets a new thread and client. A timeout is not retried. Only the hang breaker's deadline (`UiaTimeoutException`) blocks the window and its process for 90 s, and a snapshot skips blocked windows; UIA's own timeout (`UIA_E_TIMEOUT`, `RPC_E_TIMEOUT`) leaves the window out of that snapshot and blocks nothing, because no thread is left stuck (amended after f081403; Decisions 38 adds the hung-window case, which blocks like the deadline). With 4 abandoned threads still blocked, UIA jobs fail at once with "UI Automation is not responding: N calls are still blocked in other applications." The serialisation the spec asks for is kept (one job at a time); the thread is dedicated rather than pooled so it can be replaced.
3. **Window order and Depth** (`desktop/service.py:1083-1105` builds a Python set; `1199` enumerates it as the Depth; `175-192` walks `list(set)`). Set order is arbitrary. Candidates are taken in `EnumWindows` z-order (topmost first), then `Progman`, `Shell_TrayWnd` and `Shell_SecondaryTrayWnd` when not already present; Depth is the index in that list, and the non-application windows are walked in that order.
4. **No browser DOM in P2** (`tree/service.py:852-860` enters DOM mode below `RootWebArea`, `1013-1050` builds the browser's semantic tree from the DOM lists; `tools/scrape.py:36-49`). Spec §11 puts DOM mode in P6. In P2, `RootWebArea` is walked like any element and a browser window gets the same semantic tree as other applications; the browser-specific classification rules (offscreen list items, data items, groups) still apply. `use_dom=True` (with `use_ui_tree`) gives upstream's error text with the message `use_dom=True is not supported: this server reads browsers through UI Automation only. For web page content, use a browser automation MCP server such as Playwright MCP or Chrome DevTools MCP`. Scrape's `use_dom` keeps P1's "No DOM information found" text (P1 Decisions 17), because only DOM mode produces DOM text nodes. Superseded in part by Decisions 39: DOM mode is not planned for P6 either.
5. **Scrollable elements** (`tree/service.py:487-500`; spec §10). The centre is the box centre, not upstream's random point. The scroll flags and percentages come from the cache request (spec §6 lists them), not from upstream's live `GetCurrentPattern` calls (`uia/controls.py:1533-1553`); the values are the same.
6. **Only the metadata the text renders** (`tree/service.py:617-750` reads `help_text`, `selected_text`/`all_selected` through a live TextPattern `GetSelection`, `is_selection_required`, `is_selected` and `selection`; `tree/views.py:23-66` renders none of them). P2 reads focus, password, value, range, toggle, expand/collapse state and shortcut only. P3 adds what WaitFor needs (spec §8: `element_enabled` reads IsEnabled).
7. **Labels and modal dialogs** (spec §6 "each interactive and scrollable line starts with its label"; `desktop/service.py:663-675` resolves label `n` against the interactive list, then the scrollable list; `tree/service.py:877-878` clears the window's interactive list at a modal dialog but leaves their semantic nodes). The label is the element's index in the interactive list, scrollable labels continue after the last interactive one, and labels are computed after the region filter, as upstream's lists are. Semantic nodes of elements a modal dialog discarded are removed before structural pruning, so every interactive and scrollable line has a label that resolves.
8. **Window name fixes** (`tree/service.py:911-920` compares the window *title* with `Progman`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd` and `Microsoft.UI.Content.PopupWindowSiteBridge`; spec §6 "window-name fixes such as Progman to Desktop"). The desktop's title is "Program Manager", so upstream's check never fires for it. The fixed names (Desktop, Taskbar, Context Menu) apply by class name, and still by title.
9. **Iteration and caps** (`tree/service.py:446-909` recurses; `tree/budget.py`). The walker, the semantic-tree operations, the region filter and the tree renderer use explicit stacks. Elements deeper than 256 below the window are not visited (cut silently, like the cycle guard). Every visited element counts against a visit cap of 50 per budget element, which marks the capture truncated like the element limit. Until Task 22, at most the last 10,000 children of an element were read (children are visited right to left, so the last ones are visited first); Decisions 37 now reads the first 300. Exception: the fallback sibling walk (Decisions 26, used when `BuildUpdatedCache` fails) keeps the first 10,000, because it walks forward and cannot know the count (preflight F6). Superseded for children by Decisions 37 (Task 22). Words are read up to the remaining budget plus one, and at most 10,000 word moves per control (`uia/controls.py:1240-1294` reads every visible word first). Upstream's budget behaviour is kept: an interactive or scrollable element is appended even when `try_consume` fails, and reaching the limit exactly marks the capture truncated.
10. **Snapshot text layout** (`tools/_snapshot_helpers.py:339-357` builds the text with `dedent` over interpolated tables, which leaves four-space indents and stray blank lines; spec §6 asks for column-0 headers). The text is: the metadata lines (`Cursor Position`, the size or original size and coordinate scale, `Visible Displays`, `Selected Displays`, `Screenshot Region` with `Coordinate Space`, `Screenshot Backend`, Screenshot's note), then `Active Desktop:`, `All Desktops:`, `Focused Window:`, `Opened Windows:` and, for Snapshot, `UI Tree:`, each header at column 0 with its content on the following lines and exactly one blank line between sections, no leading or trailing newline. `Cursor Position: None` is kept when a region excludes the cursor (upstream prints Python's `None`). The tree keeps upstream's connectors, including the four-space indent under `desktop`.
11. **Screenshot backend** (`desktop/screenshot.py:327-375`, `desktop/service.py:289-291`). With vision, the metadata says `Screenshot Backend: wgc` (Windows.Graphics.Capture). `DESKTOP_MCP_SCREENSHOT_BACKEND` is accepted and ignored (spec §6).
12. **Annotation drawing** (`desktop/service.py:1385-1486` draws at full size with PIL, then resizes with LANCZOS). Boxes, labels, the grid and the cursor marker are drawn after scaling, so labels stay legible; the output size is truncated like PIL's `int(w * scale)`; scaling is Win2D's HighQualityCubic. Box colours come from the fixed palette (spec §10) with dark label text on light colours; a box smaller than 4 px is filled instead of stroked; the label is its text bounds plus 2 px padding, right-aligned 2 px above the box, below it when there is no room, kept inside the image. The cursor marker (red ring, cross, "CURSOR" label) keeps upstream's pixel sizes in the output image.
13. **Grid and cursor need annotation** (`desktop/service.py:227-236` draws them only in the annotated path). Screenshot's reference lines therefore need `use_annotation=true`, as upstream. Grid counts larger than the image are capped to the image size (upstream would loop over every line).
14. **Capture area** (`desktop/screenshot.py:213-271` grabs a bounding box with mss or PIL; `desktop/service.py:1291-1318` keeps a region that only partly overlaps the screen). Each monitor that overlaps the area is captured with Windows.Graphics.Capture and composited; pixels no monitor covers are black. A region that extends past the virtual screen is clipped to it, and the clipped region is the one reported and captured, which bounds the image to the screen.
15. **Displays** (`uia/core.py:659-828`; spec §2 "Planned: P2 DisplayInventory through DisplayArea plus GetDpiForMonitor", §6 "P2 evaluates DisplayArea"). Evaluated: for a PerMonitorV2 process, `DisplayArea.FindAll()` reports the same bounds, work areas and primary flag as `GetMonitorInfo` (measured on one 2560x1600 monitor while writing this plan; Task 8 keeps it as a Desktop test). Not adopted: the device name and the active-display index still need `GetMonitorInfo` and `EnumDisplayDevices`, and DisplayArea would make DisplayInventory and Snapshot need the Windows App SDK runtime. DisplayInventory's text is compact JSON in upstream's key order, with `scale` written as Python prints a float (`1.0`, `1.5`, `1.041667`).
16. **Argument types** (pydantic validates `display: list[int] | None` and `region: list[int] | str | None` before upstream's code; P1 Decisions 5). `display` must be null or a list whose items are integers by `Coercion.Integer`'s rules (JSON integers, integral numbers, integer strings), else the tool error `display must be a list of integers`. `region` must be null, a string, or such a list, else `region must be a list of integers or a string`. A region string is parsed as JSON inside the capture, so a parse failure gives upstream's error text with .NET's parser message instead of Python's. After parsing, region items must be JSON integers (`isinstance(item, int)`, so not floats or booleans). Region parsing checks the value's shape itself, which covers the P3 follow-up about `Coercion.JsonList` for `region` (it remains for `loc`).
17. **Error results** (`tools/snapshot.py:61-70` and `107-114`). After the argument type checks, every failure, including a missing Windows App SDK runtime, a hung application and the session check, is a normal (not `isError`) text result: `Error capturing desktop state: <message>. Please try again.` for Snapshot and `Error capturing screenshot: <message>. Please try again.` for Screenshot.
18. **Interactive session** (spec §5 "Without an interactive session ... desktop tools return an error result"). After argument validation and before reading windows or pixels, `DesktopSession.ThrowIfUnavailable` checks that the process is not in session 0 and that the input desktop is `Default`. Messages: `No interactive desktop: the server runs in session 0 (as a service). Run it in a signed-in user's session to use the desktop tools.` and `The desktop is not available: the screen is locked or a secure desktop (such as a UAC prompt) is showing.` P4's control state machine reuses the same facts.
19. **Virtual desktops** (`vdm/core.py:137-148, 370-689`). Only reading is ported (P2 has no desktop-switching tool). The internal manager IID is chosen by build (26100 and later: `53F5CA0B-...`; 22621: `A3175F2D-...`; Windows 10's IID is dropped with Windows 10, spec §2). Names come from the registry value `Name`, else `Desktop N` by position; a desktop whose id cannot be read is skipped but keeps its number; the current desktop missing from the list is `Unknown`; any failure or a call longer than 2 s gives `Default Desktop` (`desktop/service.py:154-162`). A window whose desktop cannot be checked is kept (fail open, `vdm/core.py:415-420`).
20. **OCR fallback deferred to P6** (P0 follow-up "OCR fallback in Snapshot"). It would capture every window whose UIA tree is empty on every Snapshot, even without `use_vision`, which adds capture cost and, from P4, the capture flash to plain snapshots, and it changes Snapshot's text beyond upstream. It belongs with the optional Ocr tool and the MSAA fallback already planned for P6 (follow-ups, P6).
21. **The dispatcher is not started in P2** (P0 follow-up: "the dispatcher thread is the process's main STA ... revisit if a later phase expects to own the main STA"). Nothing in P2 needs a message loop; P4 (hooks, overlays, flash) is the first user. `DispatcherQueueThread` gets its fixes (Task 7) and stays unused until then; Task 18 measures whether a live main STA changes the WinRT clipboard behaviour.
22. **Watchdog deferred** (spec §8 `DESKTOP_MCP_WATCHDOG`, `tree/service.py:1062-1078`): a UIA focus-changed event handler needs a COM callback object and a thread policy for events; it is a diagnostics feature and moves to P6 with `desktop-mcp doctor`.
23. **Retry delays** (`tree/service.py:277` waits inside `get_nodes` and `296-299` sleeps again before the retry, so upstream waits 1, 2 and 4 s). The delays are 0.5, 1 and 2 s, once each, as spec §6 states.
24. **Screen box** (`tree/service.py:104-108` caches the virtual screen when the server starts). The virtual screen is read on every snapshot, so monitor changes are seen.
25. **Focused window and failed windows** (`desktop/service.py:1174-1190` climbs UIA parents to the top-level window; `1242-1244` empties the whole window list when any window raises). The focused window's top-level window comes from Win32 `GetAncestor(GA_ROOT)`, with no UIA call. A window whose probe fails is skipped on its own.
26. **Children fallback** (`tree/cache_utils.py:201-213` falls back to an uncached `GetChildren`). When the cached children fetch fails for a reason other than a dead element or a timeout, the children are read through the control-view walker with the element cache request (`GetFirstChildElementBuildCache` / `GetNextSiblingElementBuildCache`). Superseded by Decisions 37 (Task 22): sibling navigation is now the only children read.
27. **Case** (`tree/views.py:78-82` lowercases `localized_control_type.title()`). Control types are stored as UIA's localized string and lowercased with `ToLowerInvariant` for the text; the action map therefore matches English control types only, as upstream does.
28. **Process names** (`tree/service.py:50-68` and `desktop/service.py:407-413` use psutil). The executable name comes from `QueryFullProcessImageName` with limited-information access (which works for elevated processes), then the system process list; it decides browser windows and `DESKTOP_MCP_EXCLUDE_PROCESSES`.
29. **Profiling** (`desktop/service.py:294-311`, `tools/_snapshot_helpers.py:239-251` log through Python logging). With `DESKTOP_MCP_PROFILE_SNAPSHOT` set, one line `Snapshot profile: desktop_context_ms=... tree_capture_ms=... region_filter_ms=... [screenshot_capture_ms=... screenshot_render_ms=...] total_ms=... use_vision=... use_dom=... use_ui_tree=... use_annotation=... displays=...` goes to stderr, where the stdio server's logs go.
30. **COM release** (Task 10). Explicit release does not work with CsWin32's shared COM wrappers: `ComObject.FinalRelease` is a no-op for them. UIA objects are released by their finalizers, and the worker runs a gen1 GC after any UIA job that allocated 1 MB or more; the measured peak dropped from 96 MB to 34 MB.
31. **UIA first use is not thread-safe** (Task 11). UIAutomationCore's first use in a process fails `CreateCacheRequest` with `E_FAIL` on all but one of several threads that start at once, so `UiaSession.Create` holds a static lock.
32. **Deadline structure** (Task 16). The window scan and the tree walk have no outer deadline; they rely on the inner bounds (per-job UIA deadlines, the stuck-worker limit, bounded retries), as upstream has no global deadline. Capture is bounded at 2 s + 6 s per monitor + 5 s, and render at 15 s.
33. **Virtual-desktop gate** (Task 9). One shell call runs at a time. A caller waits up to 2 s for the gate and then gets its own 2 s for the call; it falls back to `Default Desktop` at once, without waiting, only while an earlier call is abandoned (still blocked in the shell).
34. **FileSystem delete and move** are unchanged in P2: they act on a link itself, never its target, as P1 decided during its reviews.
35. **Control characters in the Snapshot text** (Task 20; user decision, 2026-10-20). Names and values from other apps (tree names, control types, values and shortcuts; window and desktop names) have CR, LF and TAB written as `\r`, `\n` and `\t`, U+2028 and U+2029 as `\u2028` and `\u2029`, and other C0 and C1 control characters removed, so a name cannot start a fake tree line. Upstream prints them raw. Backslashes are not escaped, so the escape is not reversible.
36. **No word read on Chromium content** (Task 22; user decision, 2026-10-20). The walker does not call `GetWords` (TextPattern `GetVisibleRanges` and the word ranges) on an element whose FrameworkId is `Chrome`, which covers Chrome, Edge, WebView2 and Electron apps such as VS Code. The element itself is still listed; only its word elements are missing. Why: Task 21 measured that Chromium's `GetCurrentPattern(TextPattern)` plus `GetVisibleRanges` on a web document holds the app's UI thread for 5.2 s at 1,000 sibling buttons, about 46 s at 3,000 and more than 65 s at 5,000 and 10,000 (roughly quadratic), long after UIA's 5 s transaction timeout gave up, and once Edge was closed as hung (AppHang), as VS Code was twice. Upstream skips words only below a browser's RootWebArea (`not is_dom`, `tree/service.py:656` and `853`); this port goes further and skips them on all Chromium content, because VS Code (Electron, not in upstream's browser list) is where the hang was seen and upstream would read words there. Chromium edits and documents (including the omnibox and Monaco) lose word positions; their values are kept. Firefox (Gecko) and CEF apps that do not report `Chrome` are not covered or measured.
37. **Children by sibling navigation, at most 300 per parent** (Task 22; user decision, 2026-10-20; fix round 1). `UiaTreeSource.GetChildren` steps through the control view with `GetFirstChildElementBuildCache` / `GetNextSiblingElementBuildCache` and the element cache request, and keeps the first 300 children in document order (`MaxChildren`). After the 300th it takes one more step only to learn whether a 301st child exists (it is not read); if so, the tree text ends with a port-only note after any element-limit note: `... [truncated: some elements have more than 300 children; only the first 300 of each were read.]` (the element-limit note is unchanged and both are printed when both apply). Upstream reads all children with one cached `TreeScope_Children` fetch (`tree/cache_utils.py:173-213`); this port used the same fetch with a cap of 10,000 (the last ones, because the walk visits children right to left; Decisions 9), but a TreeScope_Children fetch makes the provider build every child before any cut. Task 21: 300 children of a 10,000-button page in 0.34 s with no failed WM_NULL ping, about 0.4 ms per sibling step. The walk still visits children right to left, so for a parent with more than 300 children it visits children 299 down to 0, and the element budget can run out before the first children are reached. The budget does not always end the walk before the cap matters: on the 1,000-button page the walk read 300 children and output 325 elements without reaching the 500-element limit, so the cut note is the only sign that children were left out. A dead parent has no children; a sibling that dies during the steps ends the list with what was read; a timeout or any other failure ends the walk as before. This replaces the cached fetch and its fallback (Decisions 26).
38. **Responsiveness guard** (Task 22; user decision, 2026-10-20; fix round 1). Before a window is read (`ReadWindow`) and before each children read and each word read of its walk, the walked top-level window must answer `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG | SMTO_BLOCK, 250 ms)`. If the call times out (`GetLastError() == ERROR_TIMEOUT`), the walk stops with `UiaNotRespondingException`, handled like UIA's own timeout: the window is left out of this Snapshot, not retried, and nothing is blocked (no thread is left stuck, so a block has no purpose). Only when Windows considers the window hung (`IsHungAppWindow`) does the walk stop with the hang breaker's `UiaTimeoutException`, so the window and its process are skipped for 90 s. Any other failed call (for example a window that no longer exists) counts as answering, so the next UIA call reports it. A hung window therefore costs at most 250 ms per Snapshot without any UIA call. The guard cannot interrupt a call already inside the provider (in Task 21 a single `GetVisibleRanges` call blocked for over 60 s); it stops the walk from making the next call into an app that has stopped answering. The window probe (WindowCatalog) is not guarded; Task 21 measured it harmless.
39. **No browser DOM mode** (user decision, 2026-10-20). The port does not need to mirror upstream exactly here: browsers are better driven by a browser automation MCP server (Playwright MCP, Chrome DevTools MCP), which reads the real DOM. So P6 does not add upstream's DOM mode (`tree/service.py:852-860`, `1013-1050`) or the Firefox IAccessible2 path. The `use_dom` parameters stay in the tool contract: Snapshot and Screenshot answer `use_dom=True` with an error that points to a browser MCP server, and Scrape's `use_dom` keeps upstream's "No DOM information found" text. The UIA tree still covers browser windows (tabs, address bar and page elements, with Decisions 36-38), so basic clicks work without a browser MCP server. The element-limit note after a truncated tree (upstream `tree/views.py:89-95`) suggests `use_dom=True for a browser page`, which always fails here; the port's note says `use a browser automation MCP server for a web page` instead and is otherwise upstream's text (final review).

## Follow-ups this plan picks up

From the "## P2" section of `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`:

| Follow-up | Where |
|---|---|
| COMException/HRESULT translation at the `IScreenCapture` / `IImageRenderer` / `IDesktopDispatcher` boundary | Task 5 (`ComErrors`, `DesktopException`); applied in Tasks 7, 11, 14, 15 |
| DisplayInventory: evaluate `DisplayArea` plus `GetDpiForMonitor` | Task 8 (evaluation test), Decisions 15 |
| Win2D entry points call `AppRuntime.EnsureInitialized()` themselves | Task 14 (renderer), Task 15 (capture) |
| `DispatcherQueueThread`: controller leak and no timeout in `Start()`; `DisposeAsync` hop; main STA | Task 7; Decisions 21 |
| `Win2DRenderer`: `ReadAsync` buffer, `Bgra` length, label clamping and 2 px guard, label text contrast, device-lost retry, edge and label tests | Task 14 |
| `AppRuntime`: race test, failing call in the message, architecture comment, `PackageFullName` fallback label, downloads link, "or later" | Task 6 |
| `GraphicsCaptureService`: late frame and `FrameArrived`, multithread protection, `Pack` internal and tested, `LastBorderSuppressed` off the public API, `RequestAccessAsync(Borderless)`, border check with the user watching | Task 15; Task 19 Step 6 (needs the user) |
| UIA hang breaker | Task 10, used by Task 13; Decisions 2 |
| Scrape `use_dom` from the last snapshot | Deferred to P6 with DOM mode: Decisions 4 |
| Clipboard: WinRT on an MTA thread blocks under NativeAOT; find the root cause | Task 18 |
| OCR fallback in Snapshot | Deferred to P6: Decisions 20 |

From "P1 leftovers", the items that touch code P2 changes: `build-release.ps1` prints a skip line instead of silently skipping the AOT checks (Task 1); `smoke-stdio.ps1` kills the server even when `Close()` throws and no longer masks the body's error from `finally` (Task 19). The others stay in the list: the Notification blank-message case is in Toasts, which P2 does not touch, and tests still hand-declare a few imports (P2's `TestWindow` and `TestDpi` add to them, for the same reason: test-only Win32 calls stay out of product code).

## File map

```
windows/
  scripts/build-release.ps1                          (AOT UIA check, skip line; Task 1)
  scripts/smoke-stdio.ps1                            (DisplayInventory, Snapshot, Screenshot under AOT; Task 19)
  probes/DesktopMcp.Probe/Program.cs                 (uia, clipboard-mta; new renderer and capture APIs)
  probes/DesktopMcp.Probe/UiaProbe.cs                (Task 1)
  probes/DesktopMcp.Probe/ClipboardMtaProbe.cs       (Task 18)
  src/DesktopMcp.Core/Text/PyText.cs                 (FloatRepr, Round, RoundToInteger, Capitalize; Task 2)
  src/DesktopMcp.Core/Snapshots/Geometry.cs          (ScreenPoint, PixelSize, ScreenRect; Task 2)
  src/DesktopMcp.Core/Snapshots/SnapshotModel.cs     (WindowInfo, DisplayInfo, UiElement, SemanticNode, TreeSnapshot, DesktopSnapshot; Task 2)
  src/DesktopMcp.Core/Snapshots/SemanticTree.cs      (Task 2)
  src/DesktopMcp.Core/Snapshots/UnicodeCleanup.cs    (Task 2)
  src/DesktopMcp.Core/Snapshots/SnapshotSelection.cs (Task 3)
  src/DesktopMcp.Core/Snapshots/RegionFilter.cs      (Task 3)
  src/DesktopMcp.Core/Snapshots/SnapshotSettings.cs  (Task 3)
  src/DesktopMcp.Core/Snapshots/TreeText.cs          (Task 4)
  src/DesktopMcp.Core/Snapshots/SnapshotText.cs      (Task 4)
  src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj   (CsWin32RunAsBuildTask; Task 1)
  src/DesktopMcp.Desktop/NativeMethods.json          (Task 1)
  src/DesktopMcp.Desktop/NativeMethods.txt           (Tasks 1, 8, 9, 13, 15, 16)
  src/DesktopMcp.Desktop/AppRuntime.cs               (Task 6)
  src/DesktopMcp.Desktop/Errors/DesktopException.cs  (Task 5)
  src/DesktopMcp.Desktop/Errors/ComErrors.cs         (Task 5)
  src/DesktopMcp.Desktop/Dispatching/DispatcherQueueThread.cs (Task 7)
  src/DesktopMcp.Desktop/Displays/Win32Displays.cs   (IDisplayProvider; Task 8)
  src/DesktopMcp.Desktop/VirtualDesktops/VirtualDesktopInterop.cs (Task 9)
  src/DesktopMcp.Desktop/VirtualDesktops/VirtualDesktopService.cs (IVirtualDesktops; Task 9)
  src/DesktopMcp.Desktop/Uia/UiaSession.cs           (Task 1)
  src/DesktopMcp.Desktop/Uia/UiaWorker.cs            (UiaWorker, HangBlockList; Task 10)
  src/DesktopMcp.Desktop/Uia/UiaValues.cs            (Task 11)
  src/DesktopMcp.Desktop/Uia/UiaTreeSource.cs        (WindowProbe, UiaTreeSource; Task 11)
  src/DesktopMcp.Desktop/Tree/TreeSource.cs          (UiaElementData, WordBox, ITreeSource; Task 11)
  src/DesktopMcp.Desktop/Tree/TreeConfig.cs          (Task 12)
  src/DesktopMcp.Desktop/Tree/ElementBudget.cs       (Task 12)
  src/DesktopMcp.Desktop/Tree/TreeWalker.cs          (WindowContext, WindowWalk, TreeWalker; Task 12)
  src/DesktopMcp.Desktop/Tree/WindowSystem.cs        (IWindowSystem, Win32WindowSystem; Task 13)
  src/DesktopMcp.Desktop/Tree/WindowCatalog.cs       (WindowScan, IWindowScanner, IWindowProbe, UiaWindowProbe, WindowCatalog; Task 13)
  src/DesktopMcp.Desktop/Tree/TreeService.cs         (ITreeCapture, IWindowWalker, UiaWindowWalker, TreeService; Task 13)
  src/DesktopMcp.Desktop/Imaging/IImageRenderer.cs   (PixelPoint, GridLines, RenderOptions; Task 14)
  src/DesktopMcp.Desktop/Imaging/AnnotationLayout.cs (Task 14)
  src/DesktopMcp.Desktop/Imaging/Win2DRenderer.cs    (Task 14)
  src/DesktopMcp.Desktop/Capture/IScreenCapture.cs   (Task 15)
  src/DesktopMcp.Desktop/Capture/FrameCompositor.cs  (Task 15)
  src/DesktopMcp.Desktop/Capture/GraphicsCaptureService.cs (Task 15)
  src/DesktopMcp.Desktop/Snapshots/DesktopSession.cs (Task 16)
  src/DesktopMcp.Desktop/Snapshots/SnapshotService.cs (SnapshotRequest, DesktopStateStore, IDesktopSnapshots, SnapshotDependencies; Task 16)
  src/DesktopMcp.Server/Tools/ToolResults.cs         (Json in Task 8, Content in Task 17)
  src/DesktopMcp.Server/Tools/ToolServices.cs        (Displays in Task 8, Snapshots in Task 17)
  src/DesktopMcp.Server/Tools/ToolRegistry.cs        (Tasks 8 and 17)
  src/DesktopMcp.Server/Tools/DisplayInventoryTool.cs (Task 8)
  src/DesktopMcp.Server/Tools/SnapshotTool.cs        (SnapshotTool, ScreenshotTool; Task 17)
  tests/DesktopMcp.Core.Tests/Text/PyTextFloatTests.cs
  tests/DesktopMcp.Core.Tests/Snapshots/{GeometryTests,SemanticTreeTests,UnicodeCleanupTests,SnapshotSelectionTests,RegionFilterTests,SnapshotSettingsTests,SnapshotTextTests}.cs
  tests/DesktopMcp.Desktop.Tests/{TestDpi,TestWindow}.cs
  tests/DesktopMcp.Desktop.Tests/{AppRuntimeInitializerTests,DispatcherStartupTests,DispatcherQueueThreadTests,Win2DRendererTests,GraphicsCaptureServiceTests}.cs
  tests/DesktopMcp.Desktop.Tests/Errors/ComErrorsTests.cs
  tests/DesktopMcp.Desktop.Tests/Displays/Win32DisplaysTests.cs
  tests/DesktopMcp.Desktop.Tests/VirtualDesktops/VirtualDesktopServiceTests.cs
  tests/DesktopMcp.Desktop.Tests/Uia/{UiaSessionTests,UiaWorkerTests,UiaTreeSourceTests}.cs
  tests/DesktopMcp.Desktop.Tests/Tree/{ElementBudgetTests,TreeWalkerTests,WindowCatalogTests,TreeServiceTests}.cs
  tests/DesktopMcp.Desktop.Tests/Snapshots/SnapshotServiceTests.cs
  tests/DesktopMcp.Server.Tests/Tools/{DisplayInventoryToolTests,SnapshotToolTests}.cs
docs/superpowers/specs/2026-10-19-windows-port-design.md   (P2 results and decisions; Task 19)
docs/superpowers/plans/2026-10-19-windows-p0-followups.md  (P2 items closed or moved; Task 19)
```

## Tasks

1. UIA through CsWin32 COM source generators: the AOT spike
2. Core snapshot model, geometry and Python number formatting
3. Core selection, region filter and snapshot settings
4. Core Snapshot and Screenshot text
5. Desktop errors: DesktopException and COM translation
6. AppRuntime follow-ups
7. DispatcherQueueThread follow-ups
8. Displays and the DisplayInventory tool
9. Virtual desktops
10. UIA worker and hang breaker
11. UIA tree source and the test window
12. Tree classification, budget and the iterative walker
13. Window catalog and tree service
14. Win2D renderer follow-ups, cursor marker and grid
15. Multi-monitor capture and the capture follow-ups
16. Snapshot service
17. Snapshot and Screenshot tools
18. Clipboard: the WinRT-on-MTA root cause
19. Release verification and documentation

---

### Task 1: UIA through CsWin32 COM source generators: the AOT spike

Spec §2 plans UIA on CsWin32's `[GeneratedComInterface]` interfaces because NativeAOT has no built-in COM interop. This task proves it in the repository before anything depends on it: the generator settings (Decisions 1), the UIA client with its timeouts and the traversal cache requests (spec §6), a Desktop test under the JIT and a probe step under NativeAOT. It also closes the P1 leftover "build-release.ps1 silently skips the AOT activation check".

**Files:**
- Modify: `windows/src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj`
- Replace: `windows/src/DesktopMcp.Desktop/NativeMethods.json`
- Modify: `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (append)
- Create: `windows/src/DesktopMcp.Desktop/Uia/UiaSession.cs`
- Create: `windows/probes/DesktopMcp.Probe/UiaProbe.cs`; modify `windows/probes/DesktopMcp.Probe/Program.cs`
- Modify: `windows/scripts/build-release.ps1`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Uia/UiaSessionTests.cs` (Category=Desktop)

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - CsWin32-generated, internal, in namespace `Windows.Win32.UI.Accessibility`: `CUIAutomation8.CreateInstance<T>()`, `IUIAutomation6` (inherits `IUIAutomation2`'s `put_ConnectionTimeout(uint)`, `put_TransactionTimeout(uint)` and `IUIAutomation`'s `GetRootElement()`, `ElementFromHandleBuildCache(HWND, IUIAutomationCacheRequest)`, `CreateCacheRequest()`, `get_ControlViewWalker()`), `IUIAutomationElement` (`get_Cached*()` returning `BSTR`/`BOOL`/`RECT`/`UIA_CONTROLTYPE_ID`, `GetCachedPropertyValue(UIA_PROPERTY_ID)` returning `ComVariant`, `BuildUpdatedCache`, `GetCachedChildren`, `GetCurrentPatternAs(UIA_PATTERN_ID, Guid*, out void*)`), `IUIAutomationElementArray` (`get_Length()`, `GetElement(int)`), `IUIAutomationCacheRequest`, `IUIAutomationTreeWalker`, `IUIAutomationTextPattern`, `IUIAutomationTextRange`, `IUIAutomationTextRangeArray`, the `UIA_*_ID` enums, `TreeScope`, `TextUnit`, `TextPatternRangeEndpoint`; `PInvoke.SysFreeString`, `SafeArrayGetLBound/GetUBound/AccessData/UnaccessData/Destroy`, `GetDpiForSystem`, `GetForegroundWindow`.
  - `internal sealed class DesktopMcp.Desktop.Uia.UiaSession`: `static UiaSession Create()`, `const uint ConnectionTimeoutMs = 2000`, `const uint TransactionTimeoutMs = 5000`, properties `IUIAutomation6 Automation`, `IUIAutomationCacheRequest ElementRequest` (TreeScope_Element), `ChildrenRequest` (TreeScope_Children), `ProbeRequest` (window-list properties), `IUIAutomationTreeWalker ControlView`.
  - Probe command `desktop-mcp-probe uia` (exit 0 on success) and the `build-release.ps1` lines `AOT UIA check OK: ...` or `AOT activation and UIA checks skipped: ...`.

- [ ] **Step 1: Write the failing test**

`windows/tests/DesktopMcp.Desktop.Tests/Uia/UiaSessionTests.cs`:

```csharp
using DesktopMcp.Desktop.Uia;
using Windows.Win32;

namespace DesktopMcp.Desktop.Tests.Uia;

[Trait("Category", "Desktop")]
public class UiaSessionTests
{
    [Fact]
    public async Task CreatesTheClientOnAnMtaThreadAndReadsTheRoot()
    {
        var (name, connection, transaction) = await OnMtaThread(() =>
        {
            var session = UiaSession.Create();
            var root = session.Automation.GetRootElement();
            var bstr = root.get_CurrentName();
            try { return (bstr.ToString() ?? "", session.Automation.get_ConnectionTimeout(), session.Automation.get_TransactionTimeout()); }
            finally { PInvoke.SysFreeString(bstr); }
        });
        Assert.NotEqual("", name);
        Assert.Equal(UiaSession.ConnectionTimeoutMs, connection);
        Assert.Equal(UiaSession.TransactionTimeoutMs, transaction);
    }

    [Fact]
    public async Task CachesTheTraversalPropertiesForTheRootsChildren()
    {
        var count = await OnMtaThread(() =>
        {
            var session = UiaSession.Create();
            var children = session.Automation.GetRootElement().BuildUpdatedCache(session.ChildrenRequest).GetCachedChildren();
            var length = children?.get_Length() ?? 0;
            for (var i = 0; i < length; i++)
            {
                var child = children!.GetElement(i);
                // Each read throws E_INVALIDARG if the property is not in the cache request.
                PInvoke.SysFreeString(child.get_CachedName());
                _ = child.get_CachedBoundingRectangle();
                using var role = child.GetCachedPropertyValue(Windows.Win32.UI.Accessibility.UIA_PROPERTY_ID.UIA_LegacyIAccessibleRolePropertyId);
            }
            return length;
        });
        Assert.True(count > 0, "the desktop root has no top-level children");
    }

    private static Task<T> OnMtaThread<T>(Func<T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(work()); }
            catch (Exception e) { result.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `error CS0234`/`CS0246` for `DesktopMcp.Desktop.Uia` and `UiaSession` (and `Windows.Win32.UI.Accessibility` is not generated yet).

- [ ] **Step 3: Switch DesktopMcp.Desktop to CsWin32's COM source generators**

Replace `windows/src/DesktopMcp.Desktop/NativeMethods.json` with:

```json
{
  "$schema": "https://aka.ms/CsWin32.schema.json",
  "public": false,
  "allowMarshaling": true,
  "comInterop": {
    "useComSourceGenerators": true
  }
}
```

In `windows/src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj`, replace

```xml
    <DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>
```

with

```xml
    <DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>
    <!-- CsWin32 emits COM interfaces as [GeneratedComInterface] (NativeMethods.json comInterop.useComSourceGenerators) only
         when it runs as a build task; built-in COM interop does not exist under NativeAOT. -->
    <CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>
```

Append to `windows/src/DesktopMcp.Desktop/NativeMethods.txt`:

```text
// UI Automation client (spec §6), as [GeneratedComInterface] interfaces
CUIAutomation8
IUIAutomation6
IUIAutomationElement
IUIAutomationElementArray
IUIAutomationCacheRequest
IUIAutomationTreeWalker
IUIAutomationTextPattern
IUIAutomationTextRange
IUIAutomationTextRangeArray
UIA_PROPERTY_ID
UIA_PATTERN_ID
UIA_CONTROLTYPE_ID
UIA_TEXTATTRIBUTE_ID
TreeScope
TextUnit
TextPatternRangeEndpoint
SysFreeString
SafeArrayGetLBound
SafeArrayGetUBound
SafeArrayAccessData
SafeArrayUnaccessData
SafeArrayDestroy
GetDpiForSystem
GetForegroundWindow
```

- [ ] **Step 4: Check that the existing code still builds and the interfaces are generated**

Run: `dotnet build src/DesktopMcp.Desktop`
Expected: `0 Warning(s)`, `0 Error(s)`. AppRuntime, the clipboard and the Win2D activation compile unchanged (Decisions 1).

Run:
```powershell
Select-String -Path src/DesktopMcp.Desktop/obj/Debug/net10.0-windows10.0.22621.0/*/Generated/CsWin32/Windows.Win32.NativeMethods.g.cs -Pattern 'GeneratedComInterface\(\)\]' | Measure-Object | Select-Object -ExpandProperty Count
```
Expected: a number above 10 (`IUIAutomation`, `IUIAutomationElement`, the event handler interfaces and others are `[GeneratedComInterface]`). If it is 0, `CsWin32RunAsBuildTask` did not take effect: stop and report.

- [ ] **Step 5: Create the UIA session**

`windows/src/DesktopMcp.Desktop/Uia/UiaSession.cs`:

```csharp
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Desktop.Uia;

/// <summary>
/// One UI Automation client (CUIAutomation8 as IUIAutomation6) and the cache requests the tree walk uses. Create it on
/// the UIA worker thread (MTA) and use it only there (spec §5: never on the dispatcher thread).
/// </summary>
internal sealed class UiaSession
{
    /// <summary>IUIAutomation2 connection timeout: how long UIA waits for a provider to accept a call (spec §5).</summary>
    public const uint ConnectionTimeoutMs = 2000;

    /// <summary>IUIAutomation2 transaction timeout: how long one cross-process UIA call may take (spec §5).</summary>
    public const uint TransactionTimeoutMs = 5000;

    // upstream tree/cache_utils.py:55-124, the property set spec §6 lists.
    private static readonly UIA_PROPERTY_ID[] TraversalProperties =
    [
        UIA_PROPERTY_ID.UIA_NamePropertyId,
        UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
        UIA_PROPERTY_ID.UIA_LocalizedControlTypePropertyId,
        UIA_PROPERTY_ID.UIA_AcceleratorKeyPropertyId,
        UIA_PROPERTY_ID.UIA_ClassNamePropertyId,
        UIA_PROPERTY_ID.UIA_ControlTypePropertyId,
        UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId,
        UIA_PROPERTY_ID.UIA_IsContentElementPropertyId,
        UIA_PROPERTY_ID.UIA_FrameworkIdPropertyId,
        UIA_PROPERTY_ID.UIA_ClickablePointPropertyId,
        UIA_PROPERTY_ID.UIA_IsEnabledPropertyId,
        UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId,
        UIA_PROPERTY_ID.UIA_IsControlElementPropertyId,
        UIA_PROPERTY_ID.UIA_HasKeyboardFocusPropertyId,
        UIA_PROPERTY_ID.UIA_IsKeyboardFocusablePropertyId,
        UIA_PROPERTY_ID.UIA_IsPasswordPropertyId,
        UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId,
        UIA_PROPERTY_ID.UIA_HelpTextPropertyId,
        UIA_PROPERTY_ID.UIA_LegacyIAccessibleRolePropertyId,
        UIA_PROPERTY_ID.UIA_LegacyIAccessibleValuePropertyId,
        UIA_PROPERTY_ID.UIA_LegacyIAccessibleDefaultActionPropertyId,
        UIA_PROPERTY_ID.UIA_LegacyIAccessibleStatePropertyId,
        UIA_PROPERTY_ID.UIA_ScrollHorizontallyScrollablePropertyId,
        UIA_PROPERTY_ID.UIA_ScrollVerticallyScrollablePropertyId,
        UIA_PROPERTY_ID.UIA_ScrollHorizontalScrollPercentPropertyId,
        UIA_PROPERTY_ID.UIA_ScrollVerticalScrollPercentPropertyId,
        UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId,
        UIA_PROPERTY_ID.UIA_SelectionCanSelectMultiplePropertyId,
        UIA_PROPERTY_ID.UIA_SelectionIsSelectionRequiredPropertyId,
        UIA_PROPERTY_ID.UIA_SelectionItemIsSelectedPropertyId,
        UIA_PROPERTY_ID.UIA_WindowIsModalPropertyId,
        UIA_PROPERTY_ID.UIA_ToggleToggleStatePropertyId,
        UIA_PROPERTY_ID.UIA_RangeValueValuePropertyId,
        UIA_PROPERTY_ID.UIA_RangeValueMinimumPropertyId,
        UIA_PROPERTY_ID.UIA_RangeValueMaximumPropertyId,
    ];

    // The LegacyIAccessible property ids above must be in the request, or get_Cached* fails with E_INVALIDARG (spec §6).
    private static readonly UIA_PATTERN_ID[] TraversalPatterns =
    [
        UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId,
        UIA_PATTERN_ID.UIA_ScrollPatternId,
        UIA_PATTERN_ID.UIA_WindowPatternId,
    ];

    // What the window list needs from each top-level window (upstream desktop/service.py:1192-1245).
    private static readonly UIA_PROPERTY_ID[] ProbeProperties =
    [
        UIA_PROPERTY_ID.UIA_NamePropertyId,
        UIA_PROPERTY_ID.UIA_ControlTypePropertyId,
        UIA_PROPERTY_ID.UIA_ClassNamePropertyId,
        UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId,
        UIA_PROPERTY_ID.UIA_ProcessIdPropertyId,
        UIA_PROPERTY_ID.UIA_IsWindowPatternAvailablePropertyId,
        UIA_PROPERTY_ID.UIA_WindowCanMinimizePropertyId,
        UIA_PROPERTY_ID.UIA_WindowCanMaximizePropertyId,
    ];

    private UiaSession(IUIAutomation6 automation)
    {
        Automation = automation;
        ElementRequest = CreateRequest(automation, TraversalProperties, TraversalPatterns, TreeScope.TreeScope_Element);
        // Children only: BuildUpdatedCache with this request returns a placeholder whose cached children carry the
        // properties (upstream tree/cache_utils.py:173-186).
        ChildrenRequest = CreateRequest(automation, TraversalProperties, TraversalPatterns, TreeScope.TreeScope_Children);
        ProbeRequest = CreateRequest(automation, ProbeProperties, [UIA_PATTERN_ID.UIA_WindowPatternId], TreeScope.TreeScope_Element);
        ControlView = automation.get_ControlViewWalker();
    }

    public IUIAutomation6 Automation { get; }
    public IUIAutomationCacheRequest ElementRequest { get; }
    public IUIAutomationCacheRequest ChildrenRequest { get; }
    public IUIAutomationCacheRequest ProbeRequest { get; }
    public IUIAutomationTreeWalker ControlView { get; }

    /// <summary>Creates the client with the IUIAutomation2 timeouts set (spec §5, §12 "UIA calls into hung apps").</summary>
    public static UiaSession Create()
    {
        var automation = CUIAutomation8.CreateInstance<IUIAutomation6>();
        automation.put_ConnectionTimeout(ConnectionTimeoutMs);
        automation.put_TransactionTimeout(TransactionTimeoutMs);
        return new UiaSession(automation);
    }

    private static IUIAutomationCacheRequest CreateRequest(
        IUIAutomation6 automation, UIA_PROPERTY_ID[] properties, UIA_PATTERN_ID[] patterns, TreeScope scope)
    {
        var request = automation.CreateCacheRequest();
        foreach (var property in properties) request.AddProperty(property);
        foreach (var pattern in patterns) request.AddPattern(pattern);
        request.put_TreeScope(scope);
        return request;
    }
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for the three test projects.

Run (interactive session; the clipboard tests change the clipboard text and restore it):
`dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~UiaSession|FullyQualifiedName~AppRuntime|FullyQualifiedName~Win2D|FullyQualifiedName~Clipboard"`
Expected: `Passed!` with `Failed: 0`, including both `UiaSessionTests`.

- [ ] **Step 7: Add the probe step**

`windows/probes/DesktopMcp.Probe/UiaProbe.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Uia;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Probe;

/// <summary>
/// UI Automation through CsWin32's [GeneratedComInterface] interfaces under NativeAOT (P2 Task 1): creates the client
/// on an MTA thread, reads the root element, then the foreground window and its cached children with the traversal
/// cache request. Read-only: it never sets focus or invokes a pattern.
/// </summary>
internal static class UiaProbe
{
    public static async Task<bool> RunAsync()
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Run()); }
            catch (Exception e) { result.SetException(e); }
        }) { IsBackground = true, Name = "uia probe" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        try
        {
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception e)
        {
            Console.WriteLine($"uia: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            return false;
        }
    }

    private static unsafe bool Run()
    {
        var clock = Stopwatch.StartNew();
        var session = UiaSession.Create();
        var root = session.Automation.GetRootElement();
        var rootName = Take(root.get_CurrentName());
        Console.WriteLine($"uia: client created and root read in {clock.ElapsedMilliseconds} ms, root='{rootName}', " +
            $"timeouts={session.Automation.get_ConnectionTimeout()}/{session.Automation.get_TransactionTimeout()} ms");

        var foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            Console.WriteLine("uia: no foreground window; skipping the cached walk");
            return true;
        }
        clock.Restart();
        var window = session.Automation.ElementFromHandleBuildCache(foreground, session.ElementRequest);
        var name = Take(window.get_CachedName());
        var children = window.BuildUpdatedCache(session.ChildrenRequest).GetCachedChildren();
        var count = children?.get_Length() ?? 0;
        var runtimeIds = 0;
        for (var i = 0; i < count; i++)
        {
            var child = children!.GetElement(i);
            _ = Take(child.get_CachedName());
            using var runtimeId = child.GetCachedPropertyValue(UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId);
            if (runtimeId.VarType == (VarEnum.VT_ARRAY | VarEnum.VT_I4)) runtimeIds++;
        }
        Console.WriteLine($"uia: foreground '{name}' ({window.get_CachedControlType()}), {count} cached children " +
            $"({runtimeIds} with a RuntimeId) in {clock.ElapsedMilliseconds} ms, nativeAot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
        return true;
    }

    private static string Take(BSTR value)
    {
        try { return value.ToString() ?? ""; }
        finally { PInvoke.SysFreeString(value); }
    }
}
```

In `windows/probes/DesktopMcp.Probe/Program.cs`, replace the usage comment line with

```csharp
// AOT probes. Usage: desktop-mcp-probe bootstrap | dispatcher | imaging [out.png] | capture [out.png] | clipboard | toast | uia
```

and add this arm after `"toast" => ProbeToast(),`:

```csharp
    "uia" => await UiaProbe.RunAsync(),
```

- [ ] **Step 8: Run the probe under NativeAOT from the release script**

In `windows/scripts/build-release.ps1`, replace the end of the activation block

```powershell
    finally { Remove-Item $png -ErrorAction SilentlyContinue }
}
```

with

```powershell
    finally { Remove-Item $png -ErrorAction SilentlyContinue }
    # UI Automation through CsWin32's [GeneratedComInterface] interfaces, which only NativeAOT can prove (P2 Task 1).
    & (Join-Path $probeOut 'desktop-mcp-probe.exe') uia
    if ($LASTEXITCODE -ne 0) { throw "AOT UIA check failed: desktop-mcp-probe uia exited with $LASTEXITCODE" }
    Write-Host 'AOT UIA check OK: the NativeAOT probe walked the foreground window through UI Automation'
}
else {
    Write-Host 'AOT activation and UIA checks skipped: they need win-x64 in -Runtimes and an x64 host'
}
```

- [ ] **Step 9: Run the release script for x64**

Run (interactive session): `pwsh scripts/build-release.ps1 -Runtimes win-x64`
Expected, in order: no warning in the publish output; `win-x64: <size> MB, folder OK (desktop-mcp.exe, desktop-mcp.pdb)`; `smoke OK: ...`; `imaging: ... nativeAot=True, Microsoft.Graphics.Canvas.dll loaded=False`; `AOT activation check OK: ...`; `uia: client created and root read in <n> ms, root='<desktop name>', timeouts=2000/5000 ms`, then either `uia: foreground '<title>' (...), <n> cached children (<n> with a RuntimeId) in <n> ms, nativeAot=True` or, without a foreground window, `uia: no foreground window; skipping the cached walk`; `AOT UIA check OK: ...`. Exit code 0.

Run: `pwsh scripts/build-release.ps1 -Runtimes win-arm64`
Expected: the arm64 folder line, then `AOT activation and UIA checks skipped: they need win-x64 in -Runtimes and an x64 host`.

- [ ] **Step 10: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj windows/src/DesktopMcp.Desktop/NativeMethods.json windows/src/DesktopMcp.Desktop/NativeMethods.txt windows/src/DesktopMcp.Desktop/Uia/UiaSession.cs windows/probes/DesktopMcp.Probe/UiaProbe.cs windows/probes/DesktopMcp.Probe/Program.cs windows/scripts/build-release.ps1 windows/tests/DesktopMcp.Desktop.Tests/Uia/UiaSessionTests.cs
git commit -m "Generate UIA as [GeneratedComInterface] interfaces and prove them under NativeAOT" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Core snapshot model, geometry and Python number formatting

The plain records that cross the Desktop boundary (spec §3), the geometry upstream's BoundingBox and Rect provide, the whole-tree operations without recursion, and the Python formatting the Snapshot text needs: `repr(float)` for slider values and scroll percentages, `round()`, and `str.capitalize()`.

**Files:**
- Modify: `windows/src/DesktopMcp.Core/Text/PyText.cs`
- Create: `windows/src/DesktopMcp.Core/Snapshots/Geometry.cs`, `SnapshotModel.cs`, `SemanticTree.cs`, `UnicodeCleanup.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Text/PyTextFloatTests.cs`, `windows/tests/DesktopMcp.Core.Tests/Snapshots/GeometryTests.cs`, `SemanticTreeTests.cs`, `UnicodeCleanupTests.cs`

**Interfaces:**
- Consumes: `PyText.Fixed(double, int)` (P1).
- Produces (namespace `DesktopMcp.Core.Snapshots`, all public):
  - `readonly record struct ScreenPoint(int X, int Y)` with `string ToText()` → `"(x,y)"`; `readonly record struct PixelSize(int Width, int Height)` with `ToText()` → `"(w,h)"`.
  - `readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)`: `Width`, `Height`, `IsEmpty`, `Center` (floor), `Intersect(ScreenRect)`, `Contains(ScreenPoint)`, `ToXyxy()`, `static ScreenRect? Clip(ScreenRect box, ScreenRect region)`.
  - `enum WindowStatus { Maximized, Minimized, Normal, Hidden }` and `WindowStatusText.ToText(this WindowStatus)`.
  - `sealed record WindowInfo(string Name, int Depth, WindowStatus Status, ScreenRect Bounds, long Handle, int ProcessId, bool IsBrowser)`.
  - `sealed record DisplayInfo(int Index, string DeviceName, ScreenRect Bounds, bool Primary, ScreenRect? WorkArea, int? EffectiveDpi, double? Scale, string Orientation)`.
  - `sealed record VirtualDesktopInfo(string Id, string Name)` with `static Default`.
  - `sealed record UiMetadata` (init properties `HasFocus`, `IsPassword`, `Value`, `Minimum`, `Maximum`, `ToggleState`, `ExpandCollapseState`, `Shortcut`, `HorizontallyScrollable`, `HorizontalScrollPercent`, `VerticallyScrollable`, `VerticalScrollPercent`; `static Empty`).
  - `sealed record UiElement(int Id, string Name, string ControlType, string WindowName, ScreenRect Box, ScreenPoint Center, UiMetadata Metadata)`.
  - `enum SemanticKind { Desktop, Window, Structural, Interactive, Scrollable, Informative }`; `sealed class SemanticNode(SemanticKind kind, string controlType, string name)` with `Box`, `Center` (settable), `int? ElementId` and `Metadata` (init), `List<SemanticNode> Children`.
  - `sealed record TreeSnapshot(IReadOnlyList<UiElement> Interactive, IReadOnlyList<UiElement> Scrollable, SemanticNode? Root, bool Truncated, int ElementLimit)` with `static Empty`.
  - `sealed record DesktopSnapshot` (required `ActiveDesktop`, `AllDesktops`, `Windows`, `Displays`, `Tree`; optional `ActiveWindow`, `Cursor`, `OriginalSize`, `Scale`, `Region`, `SelectedDisplays` (`IReadOnlyList<long>?`), `Backend`, `Png`).
  - `static class SemanticTree`: `ReverseChildren(SemanticNode)`, `PruneStructural(SemanticNode)`, `DropElements(SemanticNode, IReadOnlySet<int>)`, `List<SemanticNode> PostOrder(SemanticNode)`.
  - `static class UnicodeCleanup`: `RemovePrivateUse(string)`, `RepairSurrogates(string)`.
  - `PyText.FloatRepr(double)`, `PyText.Round(double, int)`, `PyText.RoundToInteger(double)` (long), `PyText.Capitalize(string)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/Text/PyTextFloatTests.cs`:

```csharp
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

public class PyTextFloatTests
{
    // Expected values are Python 3.13's repr().
    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(100.0, "100.0")]
    [InlineData(1.5, "1.5")]
    [InlineData(33.33, "33.33")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(0.000123, "0.000123")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.2345678901234568e+17, "1.2345678901234568e+17")]
    [InlineData(1.3333333333333333, "1.3333333333333333")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(double.PositiveInfinity, "inf")]
    [InlineData(double.NaN, "nan")]
    public void FloatReprMatchesPython(double value, string expected) => Assert.Equal(expected, PyText.FloatRepr(value));

    [Theory]
    [InlineData(2.675, 2, 2.67)]
    [InlineData(1.0 / 0.675, 6, 1.481481)]
    [InlineData(33.335, 2, 33.34)]
    [InlineData(-0.001, 2, -0.0)]
    public void RoundMatchesPython(double value, int digits, double expected) => Assert.Equal(expected, PyText.Round(value, digits));

    [Theory]
    [InlineData(296.2962, 296L)]
    [InlineData(222.5, 222L)]
    [InlineData(223.5, 224L)]
    public void RoundToIntegerIsHalfToEven(double value, long expected) => Assert.Equal(expected, PyText.RoundToInteger(value));

    [Theory]
    [InlineData("list item", "List item")]
    [InlineData("PANE", "Pane")]
    [InlineData("", "")]
    public void CapitalizeMatchesPython(string value, string expected) => Assert.Equal(expected, PyText.Capitalize(value));
}
```

`windows/tests/DesktopMcp.Core.Tests/Snapshots/GeometryTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class GeometryTests
{
    [Fact]
    public void CenterFloorsLikePython()
    {
        Assert.Equal(new ScreenPoint(15, 25), new ScreenRect(10, 20, 21, 31).Center);
        Assert.Equal(new ScreenPoint(-2, -2), new ScreenRect(0, 0, -3, -3).Center);
    }

    [Fact]
    public void TextFormsMatchUpstream()
    {
        Assert.Equal("(1,2,30,40)", new ScreenRect(1, 2, 30, 40).ToXyxy());
        Assert.Equal("(5,6)", new ScreenPoint(5, 6).ToText());
        Assert.Equal("(1920,1080)", new PixelSize(1920, 1080).ToText());
    }

    [Fact]
    public void ClipReturnsTheOverlapOrNull()
    {
        var region = new ScreenRect(0, 0, 100, 100);
        Assert.Equal(new ScreenRect(50, 50, 100, 100), ScreenRect.Clip(new ScreenRect(50, 50, 150, 150), region));
        Assert.Null(ScreenRect.Clip(new ScreenRect(100, 0, 150, 50), region));
    }

    [Fact]
    public void ContainsExcludesTheRightAndBottomEdges()
    {
        var rect = new ScreenRect(0, 0, 10, 10);
        Assert.True(rect.Contains(new ScreenPoint(9, 9)));
        Assert.False(rect.Contains(new ScreenPoint(10, 5)));
    }
}
```

`windows/tests/DesktopMcp.Core.Tests/Snapshots/SemanticTreeTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class SemanticTreeTests
{
    [Fact]
    public void ReverseChildrenRestoresDocumentOrderAtEveryLevel()
    {
        var root = Node(SemanticKind.Desktop, "desktop", Node(SemanticKind.Window, "w", Leaf("b"), Leaf("a")));
        SemanticTree.ReverseChildren(root);
        Assert.Equal(["a", "b"], root.Children[0].Children.Select(c => c.Name));
    }

    [Fact]
    public void PruneStructuralRemovesEmptyContainersBottomUp()
    {
        var root = Node(SemanticKind.Desktop, "desktop",
            Node(SemanticKind.Window, "w",
                Node(SemanticKind.Structural, "outer", Node(SemanticKind.Structural, "inner")),
                Node(SemanticKind.Structural, "kept", Leaf("x"))));
        SemanticTree.PruneStructural(root);
        var window = Assert.Single(root.Children);
        Assert.Equal(["kept"], window.Children.Select(c => c.Name));
    }

    [Fact]
    public void DropElementsRemovesNodesWhoseElementIsGone()
    {
        var root = Node(SemanticKind.Desktop, "desktop", Node(SemanticKind.Window, "w", Leaf("a", 1), Leaf("b", 2)));
        SemanticTree.DropElements(root, new HashSet<int> { 2 });
        Assert.Equal(["b"], root.Children[0].Children.Select(c => c.Name));
    }

    [Fact]
    public void ADeepChainDoesNotOverflowTheStack()
    {
        var root = Node(SemanticKind.Desktop, "desktop");
        var current = root;
        for (var i = 0; i < 200_000; i++)
        {
            var next = Node(SemanticKind.Structural, "s");
            current.Children.Add(next);
            current = next;
        }
        SemanticTree.PruneStructural(root);
        Assert.Empty(root.Children);
    }

    private static SemanticNode Leaf(string name, int id = 0) => new(SemanticKind.Interactive, "button", name) { ElementId = id };

    private static SemanticNode Node(SemanticKind kind, string name, params SemanticNode[] children)
    {
        var node = new SemanticNode(kind, kind.ToString(), name);
        node.Children.AddRange(children);
        return node;
    }
}
```

`windows/tests/DesktopMcp.Core.Tests/Snapshots/UnicodeCleanupTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class UnicodeCleanupTests
{
    [Fact]
    public void RemovesPrivateUseCharactersInEveryPlane() =>
        Assert.Equal("ab c", UnicodeCleanup.RemovePrivateUse("a\uE001b \U000F0001c\U00100000"));

    [Fact]
    public void KeepsOrdinaryTextUnchanged()
    {
        const string text = "Explorer \U0001F437 창";
        Assert.Same(text, UnicodeCleanup.RemovePrivateUse(text));
        Assert.Same(text, UnicodeCleanup.RepairSurrogates(text));
    }

    [Fact]
    public void ReplacesUnpairedSurrogates() =>
        Assert.Equal("a\uFFFDb\uFFFD\U0001F437", UnicodeCleanup.RepairSurrogates("a\uD83Db\uDC37\U0001F437"));
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Core.Tests`
Expected: FAIL with `CS0234` (`DesktopMcp.Core.Snapshots` does not exist) and `CS0117` (`PyText` has no `FloatRepr`).

- [ ] **Step 3: Add the Python number formatting**

In `windows/src/DesktopMcp.Core/Text/PyText.cs`, insert before `/// <summary>Python's <c>repr(str)</c> for the strings our messages quote.</summary>`:

```csharp
    /// <summary>
    /// Python's <c>repr(float)</c> (also <c>str(float)</c>): the shortest digits that round-trip, in exponent form when
    /// the decimal exponent is below -4 or above 15 (<c>1e-05</c>, <c>1e+16</c>), else with at least one decimal (<c>100.0</c>).
    /// </summary>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";
        // .NET's "R" is also the shortest round-trip form, only spelled differently ("1E-05", "1E+16", "0.0001").
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var e = shortest.IndexOf('E');
        var mantissa = e < 0 ? shortest : shortest[..e];
        var exponent = e < 0 ? 0 : int.Parse(shortest[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var allDigits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var integerLength = point < 0 ? mantissa.Length : point;
        var leadingZeros = allDigits.Length - allDigits.TrimStart('0').Length;
        var digits = allDigits.TrimStart('0').TrimEnd('0');
        // value == 0.<digits> * 10^decimalPoint
        var decimalPoint = integerLength - leadingZeros + exponent;
        string text;
        if (decimalPoint is > 16 or < -3)
        {
            var power = decimalPoint - 1;
            text = $"{digits[0]}{(digits.Length > 1 ? "." + digits[1..] : "")}e{(power < 0 ? '-' : '+')}{Math.Abs(power):00}";
        }
        else if (decimalPoint <= 0) text = "0." + new string('0', -decimalPoint) + digits;
        else if (decimalPoint >= digits.Length) text = digits + new string('0', decimalPoint - digits.Length) + ".0";
        else text = digits[..decimalPoint] + "." + digits[decimalPoint..];
        return double.IsNegative(value) ? "-" + text : text;
    }

    /// <summary>Python's <c>round(x, digits)</c>: the exact binary value rounded half to even, as the nearest double.</summary>
    public static double Round(double value, int digits) =>
        double.IsFinite(value) ? double.Parse(Fixed(value, digits), CultureInfo.InvariantCulture) : value;

    /// <summary>Python's <c>round(x)</c> with no digits: half to even on the exact value.</summary>
    public static long RoundToInteger(double value) => (long)Math.Round(value, MidpointRounding.ToEven);

    /// <summary>Python's <c>str.capitalize()</c> for the strings UIA returns: first character upper, the rest lower.</summary>
    public static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

```

- [ ] **Step 4: Create the model**

`windows/src/DesktopMcp.Core/Snapshots/Geometry.cs`:

```csharp
namespace DesktopMcp.Core.Snapshots;

/// <summary>A point in physical virtual-desktop pixels.</summary>
public readonly record struct ScreenPoint(int X, int Y)
{
    /// <summary>upstream tree/views.py:253-254 Center.to_string: <c>(x,y)</c>.</summary>
    public string ToText() => $"({X},{Y})";
}

/// <summary>A size in pixels.</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    /// <summary>upstream desktop/views.py:53-54 Size.to_string: <c>(w,h)</c>.</summary>
    public string ToText() => $"({Width},{Height})";
}

/// <summary>
/// A rectangle in physical virtual-desktop pixels with exclusive Right and Bottom, like upstream's uia Rect
/// (uia/core.py:2187-2218) and BoundingBox (tree/views.py:212-245).
/// </summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>upstream uia/core.py:2205-2206 isempty.</summary>
    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>upstream tree/views.py:232-233 get_center, with Python's floor division.</summary>
    public ScreenPoint Center => new(Left + (Width >> 1), Top + (Height >> 1));

    /// <summary>upstream uia/core.py:2211-2218: the result is inverted (negative size) when the rectangles are disjoint.</summary>
    public ScreenRect Intersect(ScreenRect other) => new(
        Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));

    /// <summary>upstream desktop/service.py:1502-1504 _point_in_region.</summary>
    public bool Contains(ScreenPoint point) => Left <= point.X && point.X < Right && Top <= point.Y && point.Y < Bottom;

    /// <summary>upstream tree/views.py:238-240 xyxy_to_string: <c>(left,top,right,bottom)</c>.</summary>
    public string ToXyxy() => $"({Left},{Top},{Right},{Bottom})";

    /// <summary>upstream desktop/service.py:1507-1525 _clip_bounding_box_to_region: null when nothing overlaps.</summary>
    public static ScreenRect? Clip(ScreenRect box, ScreenRect region)
    {
        var clipped = box.Intersect(region);
        return clipped.Right <= clipped.Left || clipped.Bottom <= clipped.Top ? null : clipped;
    }
}
```

`windows/src/DesktopMcp.Core/Snapshots/SnapshotModel.cs`:

```csharp
namespace DesktopMcp.Core.Snapshots;

/// <summary>upstream desktop/views.py:20-24.</summary>
public enum WindowStatus
{
    Maximized,
    Minimized,
    Normal,
    Hidden,
}

public static class WindowStatusText
{
    /// <summary>The value upstream prints in the window tables.</summary>
    public static string ToText(this WindowStatus status) => status switch
    {
        WindowStatus.Maximized => "Maximized",
        WindowStatus.Minimized => "Minimized",
        WindowStatus.Normal => "Normal",
        _ => "Hidden",
    };
}

/// <summary>A top-level window (upstream desktop/views.py:27-45). Depth is its index in the z-ordered window scan.</summary>
public sealed record WindowInfo(string Name, int Depth, WindowStatus Status, ScreenRect Bounds, long Handle, int ProcessId, bool IsBrowser);

/// <summary>An active display (upstream uia/core.py:624-635 DisplayInfo). Index is the zero-based active display index.</summary>
public sealed record DisplayInfo(
    int Index, string DeviceName, ScreenRect Bounds, bool Primary, ScreenRect? WorkArea, int? EffectiveDpi, double? Scale, string Orientation);

/// <summary>A virtual desktop (upstream vdm/core.py:621-689); Id is the GUID in braces, upper case.</summary>
public sealed record VirtualDesktopInfo(string Id, string Name)
{
    /// <summary>upstream desktop/service.py:154-162 and vdm/core.py:627-634: the fallback when the desktops cannot be read.</summary>
    public static VirtualDesktopInfo Default { get; } = new("00000000-0000-0000-0000-000000000000", "Default Desktop");
}

/// <summary>
/// The metadata upstream's tree text renders (tree/views.py:23-66). Value, Minimum and Maximum hold Python's str() of
/// upstream's value: a stripped string, "(empty)", or a float repr for sliders.
/// </summary>
public sealed record UiMetadata
{
    public static UiMetadata Empty { get; } = new();

    public bool HasFocus { get; init; }
    public bool IsPassword { get; init; }
    public string? Value { get; init; }
    public string? Minimum { get; init; }
    public string? Maximum { get; init; }
    public string? ToggleState { get; init; }
    public string? ExpandCollapseState { get; init; }
    public string? Shortcut { get; init; }
    public bool HorizontallyScrollable { get; init; }
    public double HorizontalScrollPercent { get; init; }
    public bool VerticallyScrollable { get; init; }
    public double VerticalScrollPercent { get; init; }
}

/// <summary>
/// An interactive or scrollable element (upstream TreeElementNode / ScrollElementNode, tree/views.py:257-282). Id is
/// unique within one capture and links the element to its node in the semantic tree.
/// </summary>
public sealed record UiElement(int Id, string Name, string ControlType, string WindowName, ScreenRect Box, ScreenPoint Center, UiMetadata Metadata);

/// <summary>upstream tree/views.py:104-112 element_type.</summary>
public enum SemanticKind
{
    Desktop,
    Window,
    Structural,
    Interactive,
    Scrollable,
    Informative,
}

/// <summary>A node of the semantic UI tree (upstream tree/views.py:102-123). Built during the walk, then read-only.</summary>
public sealed class SemanticNode(SemanticKind kind, string controlType, string name)
{
    public SemanticKind Kind { get; } = kind;
    public string ControlType { get; } = controlType;
    public string Name { get; } = name;
    public ScreenRect? Box { get; set; }
    public ScreenPoint? Center { get; set; }

    /// <summary>The <see cref="UiElement.Id"/> of an interactive or scrollable node.</summary>
    public int? ElementId { get; init; }

    public UiMetadata Metadata { get; init; } = UiMetadata.Empty;
    public List<SemanticNode> Children { get; } = [];
}

/// <summary>upstream TreeState (tree/views.py:172-209).</summary>
public sealed record TreeSnapshot(
    IReadOnlyList<UiElement> Interactive, IReadOnlyList<UiElement> Scrollable, SemanticNode? Root, bool Truncated, int ElementLimit)
{
    /// <summary>No UI tree (Screenshot, or Snapshot with use_ui_tree=False).</summary>
    public static TreeSnapshot Empty { get; } = new([], [], null, false, 0);
}

/// <summary>upstream DesktopState (desktop/views.py:65-103) plus the PNG.</summary>
public sealed record DesktopSnapshot
{
    public required VirtualDesktopInfo ActiveDesktop { get; init; }
    public required IReadOnlyList<VirtualDesktopInfo> AllDesktops { get; init; }
    public WindowInfo? ActiveWindow { get; init; }
    public required IReadOnlyList<WindowInfo> Windows { get; init; }

    /// <summary>Null when a region excludes the cursor; upstream then prints "None".</summary>
    public ScreenPoint? Cursor { get; init; }

    public PixelSize? OriginalSize { get; init; }
    public double? Scale { get; init; }
    public ScreenRect? Region { get; init; }
    public IReadOnlyList<long>? SelectedDisplays { get; init; }
    public required IReadOnlyList<DisplayInfo> Displays { get; init; }
    public string? Backend { get; init; }
    public required TreeSnapshot Tree { get; init; }
    public byte[]? Png { get; init; }
}
```

`windows/src/DesktopMcp.Core/Snapshots/SemanticTree.cs`:

```csharp
namespace DesktopMcp.Core.Snapshots;

/// <summary>
/// Whole-tree operations on <see cref="SemanticNode"/>s. Upstream recurses (tree/views.py:157-169); a UIA tree can be
/// deep, so these use explicit stacks and never recurse.
/// </summary>
public static class SemanticTree
{
    /// <summary>upstream tree/views.py:165-169: the walk collects children right to left; restore document order.</summary>
    public static void ReverseChildren(SemanticNode root)
    {
        var stack = new Stack<SemanticNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            node.Children.Reverse();
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    /// <summary>upstream tree/views.py:157-162: removes structural nodes left without children, bottom up.</summary>
    public static void PruneStructural(SemanticNode root)
    {
        foreach (var node in PostOrder(root))
            node.Children.RemoveAll(child => child.Kind == SemanticKind.Structural && child.Children.Count == 0);
    }

    /// <summary>Removes interactive and scrollable nodes whose element is not in <paramref name="liveIds"/> (Decisions 7).</summary>
    public static void DropElements(SemanticNode root, IReadOnlySet<int> liveIds)
    {
        foreach (var node in PostOrder(root))
            node.Children.RemoveAll(child => child.ElementId is { } id && !liveIds.Contains(id));
    }

    /// <summary>Every node with its children before it.</summary>
    public static List<SemanticNode> PostOrder(SemanticNode root)
    {
        var order = new List<SemanticNode>();
        var stack = new Stack<SemanticNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            foreach (var child in node.Children) stack.Push(child);
        }
        order.Reverse();
        return order;
    }
}
```

`windows/src/DesktopMcp.Core/Snapshots/UnicodeCleanup.cs`:

```csharp
using System.Text;

namespace DesktopMcp.Core.Snapshots;

/// <summary>upstream desktop/utils.py:195-241, applied to the Snapshot text (tools/_snapshot_helpers.py:282-299).</summary>
public static class UnicodeCleanup
{
    /// <summary>Removes Private Use Area characters (BMP, planes 15 and 16), which apps such as VS Code put in names.</summary>
    public static string RemovePrivateUse(string text)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var paired = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var length = paired ? 2 : 1;
            var codePoint = paired ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            var privateUse = codePoint is >= 0xE000 and <= 0xF8FF or >= 0xF0000 and <= 0xFFFFD or >= 0x100000 and <= 0x10FFFD;
            if (privateUse) builder ??= new StringBuilder(text, 0, i, text.Length);
            else builder?.Append(text, i, length);
            i += length - 1;
        }
        return builder?.ToString() ?? text;
    }

    /// <summary>Replaces unpaired UTF-16 surrogates with U+FFFD, so the response always encodes as UTF-8.</summary>
    public static string RepairSurrogates(string text)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }
            if (char.IsSurrogate(c))
            {
                builder ??= new StringBuilder(text, 0, i, text.Length);
                builder.Append('�');
                continue;
            }
            builder?.Append(c);
        }
        return builder?.ToString() ?? text;
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests`
Expected: `Passed!` with `Failed: 0` (the P1 tests plus `PyTextFloatTests`, `GeometryTests`, `SemanticTreeTests` and `UnicodeCleanupTests`; the 200,000-deep chain in `ADeepChainDoesNotOverflowTheStack` passes without a stack overflow).

- [ ] **Step 6: Commit**

```bash
git add windows/src/DesktopMcp.Core/Text/PyText.cs windows/src/DesktopMcp.Core/Snapshots windows/tests/DesktopMcp.Core.Tests/Text/PyTextFloatTests.cs windows/tests/DesktopMcp.Core.Tests/Snapshots
git commit -m "Add the snapshot model, geometry and Python float formatting to Core" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Core selection, region filter and snapshot settings

upstream's display and region validation (`desktop/service.py:1262-1364`) with its messages, the argument type checks pydantic does upstream (Decisions 16), the region filter (`desktop/service.py:1527-1657`, without recursion) and the environment settings: the element limit (`tree/budget.py:22-52`), excluded processes (`tree/service.py:34-47`), the screenshot scale (`tools/_snapshot_helpers.py:24-34`, `desktop/service.py:244-255`) and the profile flag.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Snapshots/SnapshotSelection.cs`, `RegionFilter.cs`, `SnapshotSettings.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Snapshots/SnapshotSelectionTests.cs`, `RegionFilterTests.cs`, `SnapshotSettingsTests.cs`

**Interfaces:**
- Consumes: `ScreenRect`, `ScreenPoint`, `DisplayInfo`, `WindowInfo`, `UiElement`, `SemanticNode`, `TreeSnapshot`, `SemanticTree.PostOrder` (Task 2); `Coercion.Integer(JsonElement, string)` (P1).
- Produces (namespace `DesktopMcp.Core.Snapshots`):
  - `sealed record RegionArgument(IReadOnlyList<long>? Values, string? Json)`.
  - `static class SnapshotSelection`: constants `DisplayIndicesMessage`, `RegionShapeMessage`, `RegionIntegersMessage`, `RegionOrderMessage`; `IReadOnlyList<long>? ReadDisplayArgument(JsonElement?)`, `RegionArgument? ReadRegionArgument(JsonElement?)`, `IReadOnlyList<long>? ParseDisplays(IReadOnlyList<long>?)`, `ScreenRect? ParseRegion(RegionArgument?, ScreenRect virtualScreen)`, `ScreenRect DisplayUnion(IReadOnlyList<long>, IReadOnlyList<DisplayInfo>)`. All throw `ArgumentException` with the message the tool reports.
  - `static class RegionFilter`: `WindowInfo? Window(WindowInfo?, ScreenRect)`, `IReadOnlyList<WindowInfo> Windows(IEnumerable<WindowInfo>, ScreenRect)`, `ScreenPoint? Cursor(ScreenPoint?, ScreenRect)`, `TreeSnapshot Tree(TreeSnapshot, ScreenRect)`.
  - `static class SnapshotSettings`: the four `*Variable` names, `DefaultMaxTreeElements = 500`, `MaxImageWidth = 1920`, `MaxImageHeight = 1080`, `int MaxTreeElements(string?)`, `IReadOnlySet<string> ExcludedProcesses(string?)`, `bool ProfileEnabled(string?)`, `double ScreenshotScale(string?)`, `double ImageScale(string?, int width, int height)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/Snapshots/SnapshotSelectionTests.cs`:

```csharp
using System.Text.Json;
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class SnapshotSelectionTests
{
    private static readonly ScreenRect Screen = new(-1920, 0, 2560, 1600);

    private static readonly DisplayInfo[] Displays =
    [
        new(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1600), true, null, 144, 1.5, "landscape"),
        new(1, @"\\.\DISPLAY2", new ScreenRect(-1920, 0, 0, 1080), false, null, 96, 1.0, "landscape"),
    ];

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void DisplayArgumentAcceptsNullAndIntegerLists()
    {
        Assert.Null(SnapshotSelection.ReadDisplayArgument(null));
        Assert.Equal([0L, 1L], SnapshotSelection.ReadDisplayArgument(Json("[0, \"1\"]")));
        Assert.Equal("display must be a list of integers",
            Assert.Throws<ArgumentException>(() => SnapshotSelection.ReadDisplayArgument(Json("0"))).Message);
        Assert.Equal("display must be a list of integers",
            Assert.Throws<ArgumentException>(() => SnapshotSelection.ReadDisplayArgument(Json("[true]"))).Message);
    }

    [Fact]
    public void ParseDisplaysDropsDuplicatesAndRejectsNegatives()
    {
        Assert.Equal([1L, 0L], SnapshotSelection.ParseDisplays([1, 0, 1]));
        Assert.Null(SnapshotSelection.ParseDisplays([]));
        Assert.Equal(SnapshotSelection.DisplayIndicesMessage,
            Assert.Throws<ArgumentException>(() => SnapshotSelection.ParseDisplays([0, -1])).Message);
    }

    [Fact]
    public void DisplayUnionCoversTheSelectedDisplays()
    {
        Assert.Equal(new ScreenRect(-1920, 0, 2560, 1600), SnapshotSelection.DisplayUnion([0, 1], Displays));
        Assert.Equal("Invalid display index 4. Available displays: 0,1",
            Assert.Throws<ArgumentException>(() => SnapshotSelection.DisplayUnion([0, 4], Displays)).Message);
        Assert.Equal("No displays detected", Assert.Throws<ArgumentException>(() => SnapshotSelection.DisplayUnion([0], [])).Message);
    }

    [Fact]
    public void RegionFromAListOrAJsonString()
    {
        Assert.Equal(new ScreenRect(10, 20, 110, 220), SnapshotSelection.ParseRegion(new RegionArgument([10, 20, 110, 220], null), Screen));
        Assert.Equal(new ScreenRect(10, 20, 110, 220), SnapshotSelection.ParseRegion(new RegionArgument(null, "[10, 20, 110, 220]"), Screen));
        Assert.Null(SnapshotSelection.ParseRegion(null, Screen));
    }

    [Theory]
    [InlineData("[1, 2, 3]", SnapshotSelection.RegionShapeMessage)]
    [InlineData("{\"left\": 1}", SnapshotSelection.RegionShapeMessage)]
    [InlineData("[1, 2, 3.5, 4]", SnapshotSelection.RegionIntegersMessage)]
    [InlineData("[1, 2, true, 4]", SnapshotSelection.RegionIntegersMessage)]
    [InlineData("[10, 10, 5, 20]", SnapshotSelection.RegionOrderMessage)]
    [InlineData("[5000, 0, 6000, 100]", "region [5000, 0, 6000, 100] does not overlap the virtual screen bounds (-1920,0,2560,1600)")]
    public void RegionErrorsUseUpstreamMessages(string json, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => SnapshotSelection.ParseRegion(new RegionArgument(null, json), Screen)).Message);

    [Fact]
    public void InvalidJsonReportsTheParserMessage() =>
        Assert.NotEmpty(Assert.Throws<ArgumentException>(() => SnapshotSelection.ParseRegion(new RegionArgument(null, "nope"), Screen)).Message);

    [Fact]
    public void ARegionPastTheScreenIsClippedToIt() =>
        Assert.Equal(new ScreenRect(2000, 1000, 2560, 1600),
            SnapshotSelection.ParseRegion(new RegionArgument([2000, 1000, 9_000_000_000, 1700], null), Screen));

    [Fact]
    public void RegionArgumentRejectsOtherTypes() =>
        Assert.Equal("region must be a list of integers or a string",
            Assert.Throws<ArgumentException>(() => SnapshotSelection.ReadRegionArgument(Json("5"))).Message);
}
```

`windows/tests/DesktopMcp.Core.Tests/Snapshots/RegionFilterTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class RegionFilterTests
{
    private static readonly ScreenRect Region = new(0, 0, 100, 100);

    [Fact]
    public void WindowsAreClippedOrDropped()
    {
        var inside = new WindowInfo("A", 0, WindowStatus.Normal, new ScreenRect(50, 50, 150, 150), 1, 10, false);
        var outside = inside with { Name = "B", Bounds = new ScreenRect(200, 200, 300, 300) };
        var filtered = RegionFilter.Windows([inside, outside], Region);
        Assert.Equal(new ScreenRect(50, 50, 100, 100), Assert.Single(filtered).Bounds);
    }

    [Fact]
    public void ACursorOutsideTheRegionIsDropped()
    {
        Assert.Null(RegionFilter.Cursor(new ScreenPoint(100, 5), Region));
        Assert.Equal(new ScreenPoint(5, 5), RegionFilter.Cursor(new ScreenPoint(5, 5), Region));
    }

    [Fact]
    public void ElementsAreClippedAndRecentred()
    {
        var element = new UiElement(7, "OK", "button", "App", new ScreenRect(80, 80, 120, 120), new ScreenPoint(100, 100), UiMetadata.Empty);
        var gone = element with { Id = 8, Box = new ScreenRect(150, 150, 160, 160) };
        var tree = new TreeSnapshot([element, gone], [], null, false, 500);
        var filtered = RegionFilter.Tree(tree, Region);
        var kept = Assert.Single(filtered.Interactive);
        Assert.Equal((new ScreenRect(80, 80, 100, 100), new ScreenPoint(90, 90)), (kept.Box, kept.Center));
    }

    [Fact]
    public void TheSemanticTreeKeepsOnlyBranchesWithVisibleElements()
    {
        var root = new SemanticNode(SemanticKind.Desktop, "Desktop", "Desktop");
        var keptWindow = new SemanticNode(SemanticKind.Window, "Window", "Kept");
        keptWindow.Children.Add(new SemanticNode(SemanticKind.Interactive, "button", "OK") { Box = new ScreenRect(10, 10, 20, 20), ElementId = 1 });
        var emptyWindow = new SemanticNode(SemanticKind.Window, "Window", "Gone");
        emptyWindow.Children.Add(new SemanticNode(SemanticKind.Interactive, "button", "Far") { Box = new ScreenRect(500, 500, 510, 510), ElementId = 2 });
        root.Children.AddRange([keptWindow, emptyWindow]);

        var filtered = RegionFilter.Tree(new TreeSnapshot([], [], root, false, 500), Region).Root!;

        Assert.Equal(Region, filtered.Box);
        var window = Assert.Single(filtered.Children);
        Assert.Equal("Kept", window.Name);
        Assert.Equal(new ScreenPoint(15, 15), Assert.Single(window.Children).Center);
        Assert.Equal(2, root.Children.Count);
    }
}
```

`windows/tests/DesktopMcp.Core.Tests/Snapshots/SnapshotSettingsTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class SnapshotSettingsTests
{
    [Theory]
    [InlineData(null, 500)]
    [InlineData("  ", 500)]
    [InlineData("abc", 500)]
    [InlineData("0", 500)]
    [InlineData("-5", 500)]
    [InlineData(" 1_000 ", 1000)]
    [InlineData("+25", 25)]
    [InlineData("99999999999999999999999", int.MaxValue)]
    public void MaxTreeElementsFollowsUpstream(string? raw, int expected) => Assert.Equal(expected, SnapshotSettings.MaxTreeElements(raw));

    [Fact]
    public void ExcludedProcessesAreTrimmedAndCaseFolded() =>
        Assert.Equal(new HashSet<string> { "chrome.exe", "code.exe" }, SnapshotSettings.ExcludedProcesses(" Chrome.EXE, ,code.exe "));

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData("0.5", 0.5)]
    [InlineData("5", 1.0)]
    [InlineData("0.01", 0.1)]
    [InlineData("bad", 1.0)]
    [InlineData("nan", 1.0)]
    [InlineData("-inf", 0.1)]
    public void ScreenshotScaleIsClamped(string? raw, double expected) => Assert.Equal(expected, SnapshotSettings.ScreenshotScale(raw));

    [Theory]
    [InlineData(null, 2560, 1600, 0.675)]
    [InlineData(null, 1920, 1080, 1.0)]
    [InlineData("0.5", 1280, 720, 0.5)]
    [InlineData(null, 3840, 1080, 0.5)]
    public void ImageScaleFitsInto1920By1080(string? raw, int width, int height, double expected) =>
        Assert.Equal(expected, SnapshotSettings.ImageScale(raw, width, height));

    [Theory]
    [InlineData("1", true)]
    [InlineData(" Yes ", true)]
    [InlineData("0", false)]
    [InlineData(null, false)]
    public void ProfileFlag(string? raw, bool expected) => Assert.Equal(expected, SnapshotSettings.ProfileEnabled(raw));
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Core.Tests`
Expected: FAIL with `CS0103`/`CS0246` for `SnapshotSelection`, `RegionArgument`, `RegionFilter` and `SnapshotSettings`.

- [ ] **Step 3: Implement**

`windows/src/DesktopMcp.Core/Snapshots/SnapshotSelection.cs`:

```csharp
using System.Text.Json;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Snapshots;

/// <summary>
/// The Snapshot/Screenshot <c>region</c> argument after its type check: a list of integers, or a string that upstream
/// parses as JSON (tools/_snapshot_helpers.py:169-172).
/// </summary>
public sealed record RegionArgument(IReadOnlyList<long>? Values, string? Json);

/// <summary>
/// Display and region selection (upstream desktop/service.py:1262-1364). The Read* methods are the argument type checks
/// that pydantic does upstream; the Parse* methods are upstream's own validation with its messages.
/// </summary>
public static class SnapshotSelection
{
    public const string DisplayIndicesMessage = "display must contain only zero-based active display indices";
    public const string RegionShapeMessage = "region must be a JSON array of 4 integers [left, top, right, bottom]";
    public const string RegionIntegersMessage = "region must contain only integers [left, top, right, bottom]";
    public const string RegionOrderMessage = "region must satisfy right > left and bottom > top";

    /// <summary><c>display: list[int] | None</c>: JSON null or absent, or a list of integers (Decisions 16).</summary>
    public static IReadOnlyList<long>? ReadDisplayArgument(JsonElement? value)
    {
        if (value is null) return null;
        if (value.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException("display must be a list of integers");
        return value.Value.EnumerateArray().Select(item => Integer(item, "display must be a list of integers")).ToArray();
    }

    /// <summary><c>region: list[int] | str | None</c> (Decisions 16).</summary>
    public static RegionArgument? ReadRegionArgument(JsonElement? value)
    {
        if (value is null) return null;
        return value.Value.ValueKind switch
        {
            JsonValueKind.String => new RegionArgument(null, value.Value.GetString()!),
            JsonValueKind.Array => new RegionArgument(
                value.Value.EnumerateArray().Select(item => Integer(item, "region must be a list of integers or a string")).ToArray(), null),
            _ => throw new ArgumentException("region must be a list of integers or a string"),
        };
    }

    /// <summary>upstream desktop/service.py:1262-1289 parse_display_selection: rejects negatives, drops duplicates, keeps order.</summary>
    public static IReadOnlyList<long>? ParseDisplays(IReadOnlyList<long>? values)
    {
        if (values is null) return null;
        var unique = new List<long>();
        foreach (var value in values)
        {
            if (value < 0) throw new ArgumentException(DisplayIndicesMessage);
            if (!unique.Contains(value)) unique.Add(value);
        }
        return unique.Count == 0 ? null : unique;
    }

    /// <summary>
    /// upstream desktop/service.py:1291-1318 parse_region_selection, after _as_region's json.loads. The result is the region
    /// clipped to the virtual screen (Decisions 14).
    /// </summary>
    public static ScreenRect? ParseRegion(RegionArgument? argument, ScreenRect virtualScreen)
    {
        if (argument is null) return null;
        var values = argument.Values ?? ParseJson(argument.Json!);
        if (values.Count != 4) throw new ArgumentException(RegionShapeMessage);
        var (left, top, right, bottom) = (values[0], values[1], values[2], values[3]);
        if (right <= left || bottom <= top) throw new ArgumentException(RegionOrderMessage);
        var overlapLeft = Math.Max(left, virtualScreen.Left);
        var overlapTop = Math.Max(top, virtualScreen.Top);
        var overlapRight = Math.Min(right, virtualScreen.Right);
        var overlapBottom = Math.Min(bottom, virtualScreen.Bottom);
        if (overlapRight <= overlapLeft || overlapBottom <= overlapTop)
            throw new ArgumentException(
                $"region [{left}, {top}, {right}, {bottom}] does not overlap the virtual screen bounds {virtualScreen.ToXyxy()}");
        return new ScreenRect((int)overlapLeft, (int)overlapTop, (int)overlapRight, (int)overlapBottom);
    }

    /// <summary>upstream desktop/service.py:1333-1364 get_display_union_rect.</summary>
    public static ScreenRect DisplayUnion(IReadOnlyList<long> indices, IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) throw new ArgumentException("No displays detected");
        var byIndex = displays.ToDictionary(d => (long)d.Index);
        var invalid = indices.Where(index => !byIndex.ContainsKey(index)).ToArray();
        if (invalid.Length > 0)
            throw new ArgumentException(
                $"Invalid display index {invalid[0]}. Available displays: {string.Join(",", displays.Select(d => d.Index))}");
        var selected = indices.Select(index => byIndex[index].Bounds).ToArray();
        return new ScreenRect(selected.Min(r => r.Left), selected.Min(r => r.Top), selected.Max(r => r.Right), selected.Max(r => r.Bottom));
    }

    // json.loads, then the list and integer checks (a bool or a float is not an integer, as isinstance(item, int) upstream).
    private static IReadOnlyList<long> ParseJson(string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new ArgumentException(e.Message);
        }
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 4) throw new ArgumentException(RegionShapeMessage);
        return root.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number)
                ? number
                : throw new ArgumentException(RegionIntegersMessage))
            .ToArray();
    }

    // pydantic's lax int: JSON integers, integral floats and integer strings (Coercion.Integer), but never booleans.
    private static long Integer(JsonElement item, string message)
    {
        try { return Coercion.Integer(item, "item"); }
        catch (ArgumentException) { throw new ArgumentException(message); }
    }
}
```

`windows/src/DesktopMcp.Core/Snapshots/RegionFilter.cs`:

```csharp
namespace DesktopMcp.Core.Snapshots;

/// <summary>
/// Cuts a snapshot down to a capture rectangle (upstream desktop/service.py:1527-1657). Elements and windows are clipped
/// to the rectangle and dropped when nothing is left; the semantic tree is filtered bottom up without recursion.
/// </summary>
public static class RegionFilter
{
    public static WindowInfo? Window(WindowInfo? window, ScreenRect region) =>
        window is not null && ScreenRect.Clip(window.Bounds, region) is { } clipped ? window with { Bounds = clipped } : null;

    public static IReadOnlyList<WindowInfo> Windows(IEnumerable<WindowInfo> windows, ScreenRect region) =>
        windows.Select(window => Window(window, region)).OfType<WindowInfo>().ToArray();

    /// <summary>upstream desktop/service.py:217-218: a cursor outside the region is not reported.</summary>
    public static ScreenPoint? Cursor(ScreenPoint? cursor, ScreenRect region) =>
        cursor is { } point && region.Contains(point) ? point : null;

    public static TreeSnapshot Tree(TreeSnapshot tree, ScreenRect region) => tree with
    {
        Interactive = Elements(tree.Interactive, region),
        Scrollable = Elements(tree.Scrollable, region),
        Root = tree.Root is null ? null : Semantic(tree.Root, region),
    };

    private static IReadOnlyList<UiElement> Elements(IReadOnlyList<UiElement> elements, ScreenRect region) =>
        elements
            .Select(element => ScreenRect.Clip(element.Box, region) is { } clipped ? element with { Box = clipped, Center = clipped.Center } : null)
            .OfType<UiElement>()
            .ToArray();

    // upstream desktop/service.py:1579-1612 and 1631-1637.
    private static SemanticNode? Semantic(SemanticNode root, ScreenRect region)
    {
        var kept = new Dictionary<SemanticNode, SemanticNode?>(ReferenceEqualityComparer.Instance);
        foreach (var node in SemanticTree.PostOrder(root))
        {
            ScreenRect? clipped = null;
            if (node.Box is { } box)
            {
                clipped = ScreenRect.Clip(box, region);
                if (clipped is null)
                {
                    kept[node] = null;
                    continue;
                }
            }
            var children = node.Children.Select(child => kept[child]).OfType<SemanticNode>().ToList();
            if (node.Kind != SemanticKind.Desktop && clipped is null && children.Count == 0)
            {
                kept[node] = null;
                continue;
            }
            var copy = new SemanticNode(node.Kind, node.ControlType, node.Name)
            {
                Box = clipped,
                Center = clipped?.Center ?? node.Center,
                ElementId = node.ElementId,
                Metadata = node.Metadata,
            };
            copy.Children.AddRange(children);
            kept[node] = copy;
        }
        var result = kept[root];
        if (result is not null)
        {
            result.Box = region;
            result.Center = region.Center;
        }
        return result;
    }
}
```

`windows/src/DesktopMcp.Core/Snapshots/SnapshotSettings.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.Snapshots;

/// <summary>The environment settings that shape a snapshot; each takes the raw variable value (null when unset).</summary>
public static partial class SnapshotSettings
{
    public const string MaxTreeElementsVariable = "DESKTOP_MCP_MAX_TREE_ELEMENTS";
    public const string ExcludeProcessesVariable = "DESKTOP_MCP_EXCLUDE_PROCESSES";
    public const string ScreenshotScaleVariable = "DESKTOP_MCP_SCREENSHOT_SCALE";
    public const string ProfileSnapshotVariable = "DESKTOP_MCP_PROFILE_SNAPSHOT";

    /// <summary>upstream tree/budget.py:19.</summary>
    public const int DefaultMaxTreeElements = 500;

    /// <summary>upstream tools/_snapshot_helpers.py:21 MAX_IMAGE_WIDTH, MAX_IMAGE_HEIGHT.</summary>
    public const int MaxImageWidth = 1920;
    public const int MaxImageHeight = 1080;

    /// <summary>
    /// upstream tree/budget.py:22-52 resolve_max_tree_elements: Python's int() of the stripped value; blank, invalid or
    /// below 1 gives the default.
    /// </summary>
    public static int MaxTreeElements(string? raw)
    {
        var text = raw?.Trim() ?? "";
        if (!PythonInteger().IsMatch(text)) return DefaultMaxTreeElements;
        var digits = text.Replace("_", "");
        if (digits.StartsWith('-')) return DefaultMaxTreeElements;
        // Python's int is unbounded; anything past long.MaxValue is effectively no limit.
        if (!long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return int.MaxValue;
        return value < 1 ? DefaultMaxTreeElements : (int)Math.Min(value, int.MaxValue);
    }

    /// <summary>upstream tree/service.py:34-47: comma-separated process names, stripped and case-folded.</summary>
    public static IReadOnlySet<string> ExcludedProcesses(string? raw) =>
        (raw ?? "").Split(',').Select(part => part.Trim()).Where(part => part.Length > 0)
            .Select(part => part.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>upstream tools/_snapshot_helpers.py:37-39.</summary>
    public static bool ProfileEnabled(string? raw) => raw?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    /// <summary>
    /// upstream tools/_snapshot_helpers.py:24-34 _screenshot_scale: Python's float() of the value (default 1.0), clamped
    /// to [0.1, 1.0]; an invalid value or NaN gives 1.0.
    /// </summary>
    public static double ScreenshotScale(string? raw)
    {
        var text = (raw ?? "1.0").Trim().ToLowerInvariant();
        double value = text switch
        {
            "inf" or "+inf" or "infinity" or "+infinity" => double.PositiveInfinity,
            "-inf" or "-infinity" => double.NegativeInfinity,
            "nan" or "+nan" or "-nan" => double.NaN,
            _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1.0,
        };
        if (double.IsNaN(value)) return 1.0;
        return Math.Clamp(value, 0.1, 1.0);
    }

    /// <summary>upstream desktop/service.py:244-255: the configured scale, lowered further to fit 1920x1080.</summary>
    public static double ImageScale(string? raw, int width, int height)
    {
        var fitWidth = width > MaxImageWidth ? (double)MaxImageWidth / width : 1.0;
        var fitHeight = height > MaxImageHeight ? (double)MaxImageHeight / height : 1.0;
        return Math.Min(ScreenshotScale(raw), Math.Min(fitWidth, fitHeight));
    }

    [GeneratedRegex("^[+-]?[0-9]+(_[0-9]+)*$")]
    private static partial Regex PythonInteger();
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Snapshots windows/tests/DesktopMcp.Core.Tests/Snapshots
git commit -m "Port display and region selection, the region filter and the snapshot settings" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Core Snapshot and Screenshot text

The renderer spec §6 puts in Core: upstream's semantic tree text (`tree/views.py:7-209`) with labels, and the metadata and table sections (`tools/_snapshot_helpers.py:266-362`, `desktop/views.py:82-103`) with the cleanups of spec §6 and Decisions 10. Tables use P1's tabulate port.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Snapshots/TreeText.cs`, `SnapshotText.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Snapshots/SnapshotTextTests.cs`

**Interfaces:**
- Consumes: the Task 2 model, `SnapshotSettings.MaxTreeElementsVariable` (Task 3), `TableFormatter.Simple` and `PyText` (P1, Task 2).
- Produces:
  - `static class TreeText`: `string ActionFor(string lowerCaseControlType)`, `string InteractiveMetadata(UiMetadata)`, `string ScrollMetadata(UiMetadata)`, `string TruncationNote(int elementLimit)`, `string Render(TreeSnapshot)`.
  - `static class SnapshotText`: `const string ScreenshotNote`; `string Render(DesktopSnapshot snapshot, bool includeUiDetails, string? uiDetailNote)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/Snapshots/SnapshotTextTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class SnapshotTextTests
{
    private static readonly DisplayInfo Primary = new(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1600), true, null, 144, 1.5, "landscape");

    private static TreeSnapshot SampleTree(bool truncated = false)
    {
        var close = new UiElement(3, "Close", "button", "Notepad", new ScreenRect(0, 10, 20, 30), new ScreenPoint(10, 20), UiMetadata.Empty);
        var editor = new UiElement(5, "Text editor", "edit", "Notepad", new ScreenRect(0, 0, 100, 120), new ScreenPoint(50, 60),
            new UiMetadata { HasFocus = true, Value = "hello" });
        var body = new UiElement(9, "Body", "pane", "Notepad", new ScreenRect(0, 0, 200, 400), new ScreenPoint(100, 200),
            new UiMetadata { VerticallyScrollable = true, VerticalScrollPercent = 33.33 });

        var root = new SemanticNode(SemanticKind.Desktop, "Desktop", "Desktop");
        var window = new SemanticNode(SemanticKind.Window, "Window", "Notepad");
        var pane = new SemanticNode(SemanticKind.Structural, "Pane", "Editor");
        pane.Children.Add(Semantic(SemanticKind.Interactive, editor));
        window.Children.AddRange([pane, Semantic(SemanticKind.Interactive, close), Semantic(SemanticKind.Scrollable, body)]);
        root.Children.Add(window);
        return new TreeSnapshot([close, editor], [body], root, truncated, 500);
    }

    private static SemanticNode Semantic(SemanticKind kind, UiElement element) =>
        new(kind, element.ControlType, element.Name) { Box = element.Box, Center = element.Center, ElementId = element.Id, Metadata = element.Metadata };

    [Fact]
    public void TreeLinesCarryLabelsAndUpstreamConnectors()
    {
        Assert.Equal(
            """
            desktop
                └── window "Notepad"
                    ├── pane "Editor"
                    │   └── [1] (50,60) edit "Text editor"  [action: fill]  [focused]  [value:"hello"]
                    ├── [0] (10,20) button "Close"  [action: click]
                    └── [2] (100,200) pane "Body"  [action: click]  [v:33.33%]
            """.ReplaceLineEndings("\n"),
            TreeText.Render(SampleTree()));
    }

    [Fact]
    public void ATruncatedTreeEndsWithTheNote()
    {
        var text = TreeText.Render(SampleTree(truncated: true));
        Assert.EndsWith(
            "\n\n... [truncated: reached the 500-element capture limit — some elements were not visited. Narrow the view " +
            "(filter by window, scroll to the area of interest, use_dom=True for a browser page) or raise " +
            "DESKTOP_MCP_MAX_TREE_ELEMENTS for a complete tree.]", text);
    }

    [Fact]
    public void NoTreeRendersAsNoElements() => Assert.Equal("No elements", TreeText.Render(TreeSnapshot.Empty));

    [Theory]
    [InlineData("(empty)", "")]
    [InlineData("", "")]
    [InlineData("0.0", "  [value:\"0.0\"]")]
    public void ValueIsShownUnlessEmpty(string value, string expected) =>
        Assert.Equal(expected, TreeText.InteractiveMetadata(new UiMetadata { Value = value }));

    [Fact]
    public void MetadataOrderFollowsUpstream() =>
        Assert.Equal("  [password]  [value:\"5.0\"]  [range:0.0-10.0]  [toggle:on]  [state:collapsed]  [shortcut:Ctrl+S]",
            TreeText.InteractiveMetadata(new UiMetadata
            {
                IsPassword = true, Value = "5.0", Minimum = "0.0", Maximum = "10.0", ToggleState = "on", ExpandCollapseState = "collapsed", Shortcut = "Ctrl+S",
            }));

    [Theory]
    [InlineData("edit", "fill")]
    [InlineData("check box", "toggle")]
    [InlineData("document", "scroll")]
    [InlineData("단추", "click")]
    public void ActionsByControlType(string controlType, string action) => Assert.Equal(action, TreeText.ActionFor(controlType));

    [Fact]
    public void SnapshotTextHasNoIndentationAndOneBlankLineBetweenSections()
    {
        var snapshot = new DesktopSnapshot
        {
            ActiveDesktop = new VirtualDesktopInfo("{A}", "Desktop 1"),
            AllDesktops = [new VirtualDesktopInfo("{A}", "Desktop 1"), new VirtualDesktopInfo("{B}", "Work")],
            ActiveWindow = new WindowInfo("Notepad", 0, WindowStatus.Normal, new ScreenRect(0, 0, 800, 600), 1234, 42, false),
            Windows = [],
            Cursor = new ScreenPoint(5, 6),
            Displays = [Primary],
            Tree = SampleTree(),
        };
        Assert.Equal(
            """
            Cursor Position: (5, 6)
            Visible Displays: 0:\\.\DISPLAY1 (0,0,2560,1600) primary

            Active Desktop:
            Name
            ---------
            Desktop 1

            All Desktops:
            Name
            ---------
            Desktop 1
            Work

            Focused Window:
            Name       Depth  Status      Width    Height    Handle
            -------  -------  --------  -------  --------  --------
            Notepad        0  Normal        800       600      1234

            Opened Windows:
            No windows found

            UI Tree:
            desktop
                └── window "Notepad"
                    ├── pane "Editor"
                    │   └── [1] (50,60) edit "Text editor"  [action: fill]  [focused]  [value:"hello"]
                    ├── [0] (10,20) button "Close"  [action: click]
                    └── [2] (100,200) pane "Body"  [action: click]  [v:33.33%]
            """.ReplaceLineEndings("\n"),
            SnapshotText.Render(snapshot, includeUiDetails: true, uiDetailNote: null));
    }

    [Fact]
    public void ScreenshotMetadataDescribesTheScaleRegionAndBackend()
    {
        var snapshot = new DesktopSnapshot
        {
            ActiveDesktop = VirtualDesktopInfo.Default,
            AllDesktops = [VirtualDesktopInfo.Default],
            Windows = [],
            Cursor = null,
            OriginalSize = new PixelSize(2560, 1600),
            Scale = 0.675,
            Region = new ScreenRect(0, 0, 2560, 1600),
            SelectedDisplays = [0],
            Displays = [Primary],
            Backend = "wgc",
            Tree = TreeSnapshot.Empty,
            Png = [1],
        };
        var text = SnapshotText.Render(snapshot, includeUiDetails: false, SnapshotText.ScreenshotNote);
        Assert.StartsWith(
            """
            Cursor Position: None
            Screenshot Original Size: (2560,1600)
            Screenshot Coordinate Scale: 1.481481 — image pixels are downscaled; multiply every image pixel coordinate by 1.481481 before passing to Click, Move, Scroll, or any loc= argument (e.g. image pixel (200, 150) → screen coordinate (296, 222))
            Visible Displays: 0:\\.\DISPLAY1 (0,0,2560,1600) primary
            Selected Displays: 0
            Screenshot Region: (0,0,2560,1600)
            Coordinate Space: Virtual desktop coordinates
            Screenshot Backend: wgc
            UI Tree: Skipped for fast screenshot-only capture. Call Snapshot when you need interactive or scrollable elements.

            Active Desktop:
            """.ReplaceLineEndings("\n"),
            text);
        Assert.EndsWith("Opened Windows:\nNo windows found", text);
    }

    [Fact]
    public void AnUnscaledScreenshotReportsItsSize()
    {
        var snapshot = new DesktopSnapshot
        {
            ActiveDesktop = VirtualDesktopInfo.Default,
            AllDesktops = [VirtualDesktopInfo.Default],
            Windows = [],
            OriginalSize = new PixelSize(800, 600),
            Scale = 1.0,
            Displays = [],
            Tree = TreeSnapshot.Empty,
        };
        Assert.StartsWith("Cursor Position: None\nScreenshot Size: (800,600)\n\nActive Desktop:", SnapshotText.Render(snapshot, false, null));
    }

    [Fact]
    public void PrivateUseCharactersAreRemovedFromTheTreeOnly()
    {
        var root = new SemanticNode(SemanticKind.Desktop, "Desktop", "Desktop");
        root.Children.Add(new SemanticNode(SemanticKind.Window, "Window", "Code\uE70F"));
        var snapshot = new DesktopSnapshot
        {
            ActiveDesktop = VirtualDesktopInfo.Default,
            AllDesktops = [VirtualDesktopInfo.Default],
            ActiveWindow = new WindowInfo("Code\uE70F", 0, WindowStatus.Normal, new ScreenRect(0, 0, 10, 10), 1, 1, false),
            Windows = [],
            Displays = [],
            Tree = new TreeSnapshot([], [], root, false, 500),
        };
        var text = SnapshotText.Render(snapshot, true, null);
        Assert.Contains("window \"Code\"", text);
        Assert.Contains("Code\uE70F", text);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Core.Tests`
Expected: FAIL with `CS0103` for `TreeText` and `SnapshotText`.

- [ ] **Step 3: Implement**

`windows/src/DesktopMcp.Core/Snapshots/TreeText.cs`:

```csharp
using System.Text;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Snapshots;

/// <summary>
/// The "UI Tree:" text (upstream tree/views.py:7-209, semantic_tree_to_string). Interactive and scrollable lines start
/// with their label (spec §6): the index in the interactive list, then the scrollable list continuing after it (upstream
/// desktop/service.py:663-675 resolves labels that way).
/// </summary>
public static class TreeText
{
    // upstream tree/views.py:7-16.
    private static readonly Dictionary<string, string> Actions = new(StringComparer.Ordinal)
    {
        ["edit"] = "fill",
        ["check box"] = "toggle",
        ["checkbox"] = "toggle",
        ["combo box"] = "select",
        ["combobox"] = "select",
        ["slider"] = "slide",
        ["radio button"] = "select",
        ["document"] = "scroll",
    };

    /// <summary>upstream tree/views.py:19-20; the key is the lower-cased control type.</summary>
    public static string ActionFor(string controlType) => Actions.GetValueOrDefault(controlType, "click");

    /// <summary>upstream tree/views.py:23-51.</summary>
    public static string InteractiveMetadata(UiMetadata metadata)
    {
        var parts = new List<string>();
        if (metadata.HasFocus) parts.Add("focused");
        if (metadata.IsPassword) parts.Add("password");
        if (metadata.Value is { } value && value is not ("" or "(empty)")) parts.Add($"value:\"{value}\"");
        if (metadata is { Minimum: { } minimum, Maximum: { } maximum }) parts.Add($"range:{minimum}-{maximum}");
        if (!string.IsNullOrEmpty(metadata.ToggleState)) parts.Add($"toggle:{metadata.ToggleState}");
        if (!string.IsNullOrEmpty(metadata.ExpandCollapseState)) parts.Add($"state:{metadata.ExpandCollapseState}");
        if (!string.IsNullOrEmpty(metadata.Shortcut)) parts.Add($"shortcut:{metadata.Shortcut}");
        return Join(parts);
    }

    /// <summary>upstream tree/views.py:54-66.</summary>
    public static string ScrollMetadata(UiMetadata metadata)
    {
        var parts = new List<string>();
        if (metadata.HasFocus) parts.Add("focused");
        if (metadata.VerticallyScrollable) parts.Add($"v:{PyText.FloatRepr(metadata.VerticalScrollPercent)}%");
        if (metadata.HorizontallyScrollable) parts.Add($"h:{PyText.FloatRepr(metadata.HorizontalScrollPercent)}%");
        return Join(parts);
    }

    /// <summary>upstream tree/views.py:89-95, with this port's variable name.</summary>
    public static string TruncationNote(int elementLimit) =>
        $"... [truncated: reached the {elementLimit}-element capture limit — some elements were not visited. Narrow the view " +
        "(filter by window, scroll to the area of interest, use_dom=True for a browser page) or raise " +
        $"{SnapshotSettings.MaxTreeElementsVariable} for a complete tree.]";

    /// <summary>upstream tree/views.py:185-193 semantic_tree_to_string, iterative, with labels.</summary>
    public static string Render(TreeSnapshot tree)
    {
        if (tree.Root is null) return "No elements";
        var labels = new Dictionary<int, int>();
        for (var i = 0; i < tree.Interactive.Count; i++) labels[tree.Interactive[i].Id] = i;
        for (var i = 0; i < tree.Scrollable.Count; i++) labels[tree.Scrollable[i].Id] = tree.Interactive.Count + i;

        var lines = new List<string>();
        var stack = new Stack<(SemanticNode Node, string Prefix, bool IsLast)>();
        stack.Push((tree.Root, "", true));
        while (stack.Count > 0)
        {
            var (node, prefix, isLast) = stack.Pop();
            lines.Add(node.Kind == SemanticKind.Desktop ? "desktop" : $"{prefix}{(isLast ? "└── " : "├── ")}{Format(node, labels)}");
            var childPrefix = prefix + (isLast ? "    " : "│   ");
            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push((node.Children[i], childPrefix, i == node.Children.Count - 1));
        }
        var text = string.Join("\n", lines);
        return tree.Truncated ? text + "\n\n" + TruncationNote(tree.ElementLimit) : text;
    }

    // upstream tree/views.py:126-138, plus the label.
    private static string Format(SemanticNode node, IReadOnlyDictionary<int, int> labels)
    {
        var controlType = node.ControlType.ToLowerInvariant();
        switch (node.Kind)
        {
            case SemanticKind.Window:
                return $"window \"{node.Name}\"";
            case SemanticKind.Interactive or SemanticKind.Scrollable:
                var builder = new StringBuilder();
                if (node.ElementId is { } id && labels.TryGetValue(id, out var label)) builder.Append('[').Append(label).Append("] ");
                builder.Append(node.Center?.ToText() ?? "(?)").Append(' ').Append(controlType)
                    .Append(" \"").Append(node.Name).Append("\"  [action: ").Append(ActionFor(controlType)).Append(']')
                    .Append(node.Kind == SemanticKind.Interactive ? InteractiveMetadata(node.Metadata) : ScrollMetadata(node.Metadata));
                return builder.ToString();
            default:
                return $"{controlType} \"{node.Name}\"";
        }
    }

    private static string Join(List<string> parts) =>
        parts.Count == 0 ? "" : "  " + string.Join("  ", parts.Select(part => $"[{part}]"));
}
```

`windows/src/DesktopMcp.Core/Snapshots/SnapshotText.cs`:

```csharp
using System.Globalization;
using System.Text;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Snapshots;

/// <summary>
/// The Snapshot and Screenshot text (upstream tools/_snapshot_helpers.py:266-362, desktop/views.py:82-103) with spec §6's
/// cleanups: no indentation from dedent, every section header at column 0, one blank line between sections (Decisions 10).
/// </summary>
public static class SnapshotText
{
    /// <summary>upstream tools/snapshot.py:119, the note Screenshot adds.</summary>
    public const string ScreenshotNote =
        "UI Tree: Skipped for fast screenshot-only capture. Call Snapshot when you need interactive or scrollable elements.";

    private static readonly string[] WindowHeaders = ["Name", "Depth", "Status", "Width", "Height", "Handle"];

    public static string Render(DesktopSnapshot snapshot, bool includeUiDetails, string? uiDetailNote)
    {
        var text = new StringBuilder()
            .Append(Metadata(snapshot, uiDetailNote))
            .Append("\n\nActive Desktop:\n").Append(DesktopTable([snapshot.ActiveDesktop]))
            .Append("\n\nAll Desktops:\n").Append(DesktopTable(snapshot.AllDesktops))
            .Append("\n\nFocused Window:\n").Append(snapshot.ActiveWindow is { } active ? WindowTable([active]) : "No active window found")
            .Append("\n\nOpened Windows:\n").Append(snapshot.Windows.Count > 0 ? WindowTable(snapshot.Windows) : "No windows found");
        if (includeUiDetails)
            text.Append("\n\nUI Tree:\n").Append(UnicodeCleanup.RemovePrivateUse(TreeText.Render(snapshot.Tree)));
        return UnicodeCleanup.RepairSurrogates(text.ToString());
    }

    // upstream tools/_snapshot_helpers.py:308-337, without the trailing newline.
    private static string Metadata(DesktopSnapshot snapshot, string? uiDetailNote)
    {
        var lines = new List<string>
        {
            "Cursor Position: " + (snapshot.Cursor is { } cursor ? $"({cursor.X}, {cursor.Y})" : "None"),
        };
        if (snapshot.OriginalSize is { } size)
        {
            var scale = snapshot.Scale ?? 1.0;
            if (scale < 1.0)
            {
                var coordinateScale = PyText.Round(1.0 / scale, 6);
                var factor = PyText.FloatRepr(coordinateScale);
                lines.Add($"Screenshot Original Size: {size.ToText()}");
                lines.Add($"Screenshot Coordinate Scale: {factor} — image pixels are downscaled; multiply every image pixel coordinate by " +
                    $"{factor} before passing to Click, Move, Scroll, or any loc= argument (e.g. image pixel (200, 150) → screen coordinate " +
                    $"({PyText.RoundToInteger(200 * coordinateScale)}, {PyText.RoundToInteger(150 * coordinateScale)}))");
            }
            else
            {
                lines.Add($"Screenshot Size: {size.ToText()}");
            }
        }
        if (snapshot.Displays.Count > 0)
            lines.Add("Visible Displays: " + string.Join("; ", snapshot.Displays.Select(d =>
                $"{d.Index}:{d.DeviceName} {d.Bounds.ToXyxy()}{(d.Primary ? " primary" : "")}")));
        if (snapshot.SelectedDisplays is { Count: > 0 } selected)
            lines.Add("Selected Displays: " + string.Join(",", selected.Select(i => i.ToString(CultureInfo.InvariantCulture))));
        if (snapshot.Region is { } region)
        {
            lines.Add($"Screenshot Region: {region.ToXyxy()}");
            lines.Add("Coordinate Space: Virtual desktop coordinates");
        }
        if (snapshot.Backend is { } backend) lines.Add($"Screenshot Backend: {backend}");
        if (uiDetailNote is not null) lines.Add(uiDetailNote);
        return string.Join("\n", lines);
    }

    // upstream desktop/views.py:82-90.
    private static string DesktopTable(IEnumerable<VirtualDesktopInfo> desktops) =>
        TableFormatter.Simple(["Name"], desktops.Select(d => (IReadOnlyList<string>)[d.Name]).ToArray());

    // upstream desktop/views.py:37-45 and 92-103.
    private static string WindowTable(IEnumerable<WindowInfo> windows) =>
        TableFormatter.Simple(WindowHeaders, windows.Select(w => (IReadOnlyList<string>)
        [
            w.Name,
            w.Depth.ToString(CultureInfo.InvariantCulture),
            w.Status.ToText(),
            w.Bounds.Width.ToString(CultureInfo.InvariantCulture),
            w.Bounds.Height.ToString(CultureInfo.InvariantCulture),
            w.Handle.ToString(CultureInfo.InvariantCulture),
        ]).ToArray());
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~SnapshotTextTests"`
Expected: `Passed!` with `Failed: 0` (15 tests).

Run: `dotnet test tests/DesktopMcp.Core.Tests`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Snapshots/TreeText.cs windows/src/DesktopMcp.Core/Snapshots/SnapshotText.cs windows/tests/DesktopMcp.Core.Tests/Snapshots/SnapshotTextTests.cs
git commit -m "Render the Snapshot and Screenshot text with labels and without stray indentation" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Desktop errors: DesktopException and COM translation

The P0 follow-up: COM and WinRT failures become a Desktop-level exception with a message for the tool result (spec §3 boundary rule). The same helper classifies UIA's dead-element and timeout HRESULTs (`uia/exceptions.py:57-102`), which Tasks 10 to 13 need.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Errors/DesktopException.cs`, `ComErrors.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Errors/ComErrorsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `DesktopMcp.Desktop.Errors`, public):
  - `class DesktopException : Exception` (ctors `(string)` and `(string, Exception)`); `sealed class UiaTimeoutException(string) : DesktopException`; `sealed class UiaUnavailableException(string) : DesktopException`.
  - `static class ComErrors`: `bool IsDeadElement(Exception)`, `bool IsTimeout(Exception)`, `bool ShouldTranslate(Exception)`, `Exception Translate(string operation, Exception)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/Errors/ComErrorsTests.cs`:

```csharp
using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Tests.Errors;

public class ComErrorsTests
{
    [Fact]
    public void TranslatesComExceptionsWithTheHresult()
    {
        var error = ComErrors.Translate("Screen capture", new COMException("The device was removed.", unchecked((int)0x887A0005)));
        var desktop = Assert.IsType<DesktopException>(error);
        Assert.Equal("Screen capture failed: The device was removed. (HRESULT 0x887A0005)", desktop.Message);
        Assert.IsType<COMException>(desktop.InnerException);
    }

    [Fact]
    public void AnEmptyMessageStillNamesTheOperation() =>
        Assert.Equal("Image rendering failed (HRESULT 0x80004005)",
            ComErrors.Translate("Image rendering", new COMException("", unchecked((int)0x80004005))).Message);

    [Fact]
    public void ProjectedWinRtFailuresAreTranslatedToo() =>
        Assert.IsType<DesktopException>(ComErrors.Translate("Screen capture", new UnauthorizedAccessException("denied")));

    [Fact]
    public void OtherExceptionsPassThrough()
    {
        var argument = new ArgumentException("bad");
        Assert.Same(argument, ComErrors.Translate("x", argument));
        var canceled = new OperationCanceledException();
        Assert.Same(canceled, ComErrors.Translate("x", canceled));
        var ours = new DesktopException("already clear");
        Assert.Same(ours, ComErrors.Translate("x", ours));
    }

    [Theory]
    [InlineData(unchecked((int)0x80040201), true)]
    [InlineData(unchecked((int)0x80010108), true)]
    [InlineData(unchecked((int)0x80004005), false)]
    public void DeadElementCodes(int hresult, bool dead) => Assert.Equal(dead, ComErrors.IsDeadElement(new COMException("x", hresult)));

    [Fact]
    public void TimeoutsIncludeTheHangBreaker()
    {
        Assert.True(ComErrors.IsTimeout(new COMException("x", unchecked((int)0x80131505))));
        Assert.True(ComErrors.IsTimeout(new UiaTimeoutException("x")));
        Assert.False(ComErrors.IsTimeout(new TimeoutException("x")));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0234` (`DesktopMcp.Desktop.Errors` does not exist).

- [ ] **Step 3: Implement**

`windows/src/DesktopMcp.Desktop/Errors/DesktopException.cs`:

```csharp
namespace DesktopMcp.Desktop.Errors;

/// <summary>
/// A desktop operation failed. Its message is meant for the tool result; COM and WinRT details (HRESULTs) stay inside it
/// (spec §3 boundary rule).
/// </summary>
public class DesktopException : Exception
{
    public DesktopException(string message) : base(message)
    {
    }

    public DesktopException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>A UI Automation call did not finish within its deadline (the hang breaker, Decisions 2).</summary>
public sealed class UiaTimeoutException(string message) : DesktopException(message);

/// <summary>Too many UI Automation calls are still blocked in other applications to start another one.</summary>
public sealed class UiaUnavailableException(string message) : DesktopException(message);
```

`windows/src/DesktopMcp.Desktop/Errors/ComErrors.cs`:

```csharp
using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Errors;

/// <summary>
/// Classifies COM and WinRT failures and turns them into <see cref="DesktopException"/>s at the Desktop boundary
/// (P0 follow-up "COMException translation"). CsWinRT maps some HRESULTs to .NET types (E_ACCESSDENIED to
/// UnauthorizedAccessException, E_NOTIMPL to NotImplementedException, E_NOINTERFACE to InvalidCastException); those are
/// translated too. Argument, cancellation and our own exceptions pass through unchanged.
/// </summary>
public static class ComErrors
{
    // upstream uia/exceptions.py:57-65: the element or its process is gone; retrying will not help.
    private static readonly HashSet<int> DeadElement =
    [
        unchecked((int)0x80040201), // UIA_E_ELEMENTNOTAVAILABLE
        unchecked((int)0x80000013), // RO_E_CLOSED
        unchecked((int)0x80010108), // RPC_E_DISCONNECTED
        unchecked((int)0x80010007), // RPC_E_SERVER_DIED
        unchecked((int)0x80010012), // RPC_E_SERVER_DIED_DNE
        unchecked((int)0x80010006), // RPC_E_CONNECTION_TERMINATED
        unchecked((int)0x800401FD), // CO_E_OBJNOTCONNECTED
        unchecked((int)0x800401FF), // CO_E_RELEASED
    ];

    // upstream uia/exceptions.py:73-74.
    private static readonly HashSet<int> Timeout =
    [
        unchecked((int)0x80131505), // UIA_E_TIMEOUT
        unchecked((int)0x8001011F), // RPC_E_TIMEOUT
    ];

    /// <summary>The element no longer exists (upstream UIADeadElementError).</summary>
    public static bool IsDeadElement(Exception error) => error is COMException && DeadElement.Contains(error.HResult);

    /// <summary>UIA's own timeout (UIA_E_TIMEOUT, RPC_E_TIMEOUT) or the hang breaker's deadline.</summary>
    public static bool IsTimeout(Exception error) =>
        error is UiaTimeoutException || (error is COMException && Timeout.Contains(error.HResult));

    /// <summary>True for the COM/WinRT failures <see cref="Translate"/> wraps.</summary>
    public static bool ShouldTranslate(Exception error) =>
        error is COMException or UnauthorizedAccessException or NotImplementedException or InvalidCastException
        && error.HResult < 0;

    /// <summary>
    /// "&lt;operation&gt; failed: &lt;message&gt; (HRESULT 0x...)", or "&lt;operation&gt; failed (HRESULT 0x...)" when the
    /// exception has no message; other exceptions are returned unchanged.
    /// </summary>
    public static Exception Translate(string operation, Exception error)
    {
        if (!ShouldTranslate(error)) return error;
        var hresult = $"HRESULT 0x{error.HResult:X8}";
        var message = error.Message.Trim();
        return new DesktopException(message.Length == 0 ? $"{operation} failed ({hresult})" : $"{operation} failed: {message} ({hresult})", error);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~ComErrorsTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Errors windows/tests/DesktopMcp.Desktop.Tests/Errors
git commit -m "Translate COM and WinRT failures into DesktopException at the Desktop boundary" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: AppRuntime follow-ups

Closes the P0 `AppRuntime` follow-ups: a real race test for concurrent first calls (through an injected initializer), the failing call named in the install hint, the "or later" wording, a comment on the architecture choice, a label on the `PackageFullName` fallback, and a check of the downloads link.

**Files:**
- Replace: `windows/src/DesktopMcp.Desktop/AppRuntime.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/AppRuntimeInitializerTests.cs`

**Interfaces:**
- Consumes: `Imaging.Win2DStaticActivation.Install` (P0b).
- Produces: `AppRuntime.EnsureInitialized()` and `AppRuntime.PackageFullName` (unchanged signatures); internal `AppRuntime.DownloadsPage`, `AppRuntime.InstallHint(string call, int hresult)`; internal `sealed class AppRuntimeInitializer(Func<bool> osSupported, Action installActivation, Func<string> addDependency)` with `EnsureInitialized()` and `string? PackageFullName`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/AppRuntimeInitializerTests.cs`:

```csharp
namespace DesktopMcp.Desktop.Tests;

public class AppRuntimeInitializerTests
{
    [Fact]
    public void ConcurrentFirstCallsAddTheDependencyOnce()
    {
        var calls = 0;
        var initializer = new AppRuntimeInitializer(() => true, () => { }, () =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(50);
            return "Microsoft.WindowsAppRuntime.2_2.5.1.0_x64__8wekyb3d8bbwe";
        });
        using var start = new Barrier(8);
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            initializer.EnsureInitialized();
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, calls);
        Assert.Equal("Microsoft.WindowsAppRuntime.2_2.5.1.0_x64__8wekyb3d8bbwe", initializer.PackageFullName);
    }

    [Fact]
    public void AFailedAttemptIsRetriedByTheNextCall()
    {
        var attempts = 0;
        var initializer = new AppRuntimeInitializer(() => true, () => { }, () =>
            ++attempts == 1 ? throw new InvalidOperationException("missing") : "full name");
        Assert.Throws<InvalidOperationException>(initializer.EnsureInitialized);
        initializer.EnsureInitialized();
        Assert.Equal(("full name", 2), (initializer.PackageFullName, attempts));
    }

    [Fact]
    public void AnOldWindowsFailsBeforeAnyCall()
    {
        var called = false;
        var initializer = new AppRuntimeInitializer(() => false, () => called = true, () => { called = true; return ""; });
        var error = Assert.Throws<InvalidOperationException>(initializer.EnsureInitialized);
        Assert.Contains("Windows 11 22H2 (build 22621) or later", error.Message);
        Assert.False(called);
    }

    [Fact]
    public void TheInstallHintNamesTheFailingCallAndTheMinimumVersion()
    {
        var hint = AppRuntime.InstallHint("AddPackageDependency", unchecked((int)0x80670016));
        Assert.Contains(" or later (Microsoft.WindowsAppRuntime.", hint);
        Assert.Contains("AddPackageDependency failed with HRESULT 0x80670016", hint);
        Assert.EndsWith($"from {AppRuntime.DownloadsPage}.", hint);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0246` (`AppRuntimeInitializer`) and `CS0117` (`InstallHint`, `DownloadsPage`).

- [ ] **Step 3: Implement**

Replace `windows/src/DesktopMcp.Desktop/AppRuntime.cs` with:

```csharp
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.Packaging.Appx;
using Framework = Microsoft.WindowsAppSDK.Runtime.Packages.Framework;
using Release = Microsoft.WindowsAppSDK.Release;
using RuntimeVersion = Microsoft.WindowsAppSDK.Runtime.Version;

namespace DesktopMcp.Desktop;

/// <summary>
/// Brings the framework-dependent Windows App SDK runtime into this unpackaged process through the OS
/// Dynamic Dependencies API (kernelbase, Windows 11 22H2+), without the SDK's bootstrap DLL.
/// </summary>
public static class AppRuntime
{
    /// <summary>The Windows App SDK downloads page, named in the install hint (checked in P2 Task 6).</summary>
    internal const string DownloadsPage = "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads";

    private static readonly AppRuntimeInitializer Default = new(
        osSupported: () => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621),
        installActivation: Imaging.Win2DStaticActivation.Install,
        addDependency: AddFrameworkDependency);

    /// <summary>
    /// The full name of the Windows App SDK framework package resolved into this process (null before initialisation).
    /// When the OS returns no full name it is the family name with "(family name; no full name returned)" appended.
    /// </summary>
    public static string? PackageFullName => Default.PackageFullName;

    /// <summary>
    /// Adds the Windows App SDK framework package to this process's package graph (idempotent, thread-safe).
    /// Throws <see cref="InvalidOperationException"/> with an install hint when the runtime is missing.
    /// </summary>
    public static void EnsureInitialized() => Default.EnsureInitialized();

    /// <summary>The install hint for a failed <paramref name="call"/>; it names the minimum version "or later".</summary>
    internal static string InstallHint(string call, int hresult) =>
        $"The Windows App SDK runtime {RuntimeVersion.DotQuadString} or later ({Framework.PackageFamilyName}, {Release.Major}.{Release.Minor}, " +
        $"{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}) is not installed: {call} failed with HRESULT 0x{hresult:X8}. " +
        $"Install the Windows App SDK {Release.Major}.{Release.Minor} runtime from {DownloadsPage}.";

    private static unsafe string AddFrameworkDependency()
    {
        HRESULT hresult;
        var call = "TryCreatePackageDependency";
        PWSTR dependencyId = default, fullName = default;
        try
        {
            var minVersion = default(PACKAGE_VERSION);
            minVersion.Version = RuntimeVersion.UInt64;
            hresult = PInvoke.TryCreatePackageDependency(
                default, Framework.PackageFamilyName, minVersion, CurrentArchitecture(),
                PackageDependencyLifetimeKind.PackageDependencyLifetimeKind_Process, null,
                CreatePackageDependencyOptions.CreatePackageDependencyOptions_None, out dependencyId);
            if (hresult.Succeeded)
            {
                call = "AddPackageDependency";
                PACKAGEDEPENDENCY_CONTEXT context;
                hresult = PInvoke.AddPackageDependency(dependencyId, (int)PInvoke.PACKAGE_DEPENDENCY_RANK_DEFAULT,
                    AddPackageDependencyOptions.AddPackageDependencyOptions_None, &context, &fullName);
            }
            if (hresult.Succeeded)
                return fullName.ToString() ?? $"{Framework.PackageFamilyName} (family name; no full name returned)";
        }
        catch (EntryPointNotFoundException e)
        {
            throw new InvalidOperationException(
                $"The Windows App SDK runtime needs Windows 11 22H2 (build 22621) or later; this Windows has no Dynamic Dependencies API ({e.Message}).", e);
        }
        finally
        {
            var heap = PInvoke.GetProcessHeap();
            if (fullName.Value != null) PInvoke.HeapFree(heap, 0, fullName);
            if (dependencyId.Value != null) PInvoke.HeapFree(heap, 0, dependencyId);
        }
        throw new InvalidOperationException(InstallHint(call, hresult.Value));
    }

    /// <summary>
    /// The framework package to depend on: the one built for this process's architecture, not the machine's. An x64
    /// process on an ARM64 machine (emulated) therefore asks for the x64 runtime, which Windows App SDK installs on ARM64
    /// for that purpose; an x86 process on x64 asks for x86. ARM64EC and other architectures have no runtime package.
    /// </summary>
    private static PackageDependencyProcessorArchitectures CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => PackageDependencyProcessorArchitectures.PackageDependencyProcessorArchitectures_X64,
        Architecture.Arm64 => PackageDependencyProcessorArchitectures.PackageDependencyProcessorArchitectures_Arm64,
        Architecture.X86 => PackageDependencyProcessorArchitectures.PackageDependencyProcessorArchitectures_X86,
        var other => throw new PlatformNotSupportedException($"The Windows App SDK runtime has no {other} build."),
    };
}

/// <summary>The once-only initialisation behind <see cref="AppRuntime"/>, with its OS calls injected for tests.</summary>
internal sealed class AppRuntimeInitializer(Func<bool> osSupported, Action installActivation, Func<string> addDependency)
{
    private readonly Lock _gate = new();
    private volatile bool _initialized;

    public string? PackageFullName { get; private set; }

    public void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_gate)
        {
            if (_initialized) return;
            // The Dynamic Dependencies API in kernelbase arrived in Windows 11 22H2; check before binding to it.
            if (!osSupported())
                throw new InvalidOperationException(
                    $"The Windows App SDK runtime needs Windows 11 22H2 (build 22621) or later; this is Windows {Environment.OSVersion.Version}.");
            // NativeAOT publishes link Win2D statically: route its activation before any Win2D type is used.
            installActivation();
            PackageFullName = addDependency();
            _initialized = true;
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~AppRuntimeInitializerTests"`
Expected: `Passed!` with `Failed: 0` (4 tests).

Run (interactive session): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~AppRuntimeTests"`
Expected: `Passed!` with `Failed: 0` (the P0b tests against the installed runtime still pass).

- [ ] **Step 5: Check the downloads link**

Run: `(Invoke-WebRequest -Method Head -Uri https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads -MaximumRedirection 5).StatusCode`
Expected: `200`. If the page moved, set `DownloadsPage` to the new URL that lists the 2.5 runtime installers, rerun Step 4, and note it in the commit message.

- [ ] **Step 6: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/AppRuntime.cs windows/tests/DesktopMcp.Desktop.Tests/AppRuntimeInitializerTests.cs
git commit -m "Name the failing Dynamic Dependencies call in the install hint and test concurrent initialisation" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: DispatcherQueueThread follow-ups

Closes the P0 dispatcher follow-ups: `Start()` no longer leaks the controller when the queue rejects work and no longer waits forever, `DisposeAsync` resumes off the dying thread, and a failure to create the thread is a `DesktopException`. The main-STA question is Decisions 21.

**Files:**
- Replace: `windows/src/DesktopMcp.Desktop/Dispatching/DispatcherQueueThread.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/DispatcherStartupTests.cs`; modify `windows/tests/DesktopMcp.Desktop.Tests/DispatcherQueueThreadTests.cs` (Category=Desktop)

**Interfaces:**
- Consumes: `ComErrors`, `DesktopException` (Task 5).
- Produces: `DispatcherQueueThread.Start()` (unchanged signature; throws `DesktopException` on a failed start); internal `static int WaitForThreadId(Func<Action, bool> tryEnqueue, Func<Task> shutdown, TimeSpan timeout)` and `static readonly TimeSpan StartTimeout` (5 s).

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/DispatcherStartupTests.cs`:

```csharp
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Tests;

public class DispatcherStartupTests
{
    [Fact]
    public void ReturnsTheIdOfTheThreadThatRanTheWork()
    {
        var shutdowns = 0;
        var id = DispatcherQueueThread.WaitForThreadId(work => { work(); return true; }, () => { shutdowns++; return Task.CompletedTask; },
            TimeSpan.FromSeconds(5));
        Assert.Equal((Environment.CurrentManagedThreadId, 0), (id, shutdowns));
    }

    [Fact]
    public void ARejectedWorkItemShutsTheQueueDown()
    {
        var shutdowns = 0;
        var error = Assert.Throws<DesktopException>(() => DispatcherQueueThread.WaitForThreadId(
            _ => false, () => { shutdowns++; return Task.CompletedTask; }, TimeSpan.FromSeconds(5)));
        Assert.Equal(("The new DispatcherQueue rejected work.", 1), (error.Message, shutdowns));
    }

    [Fact]
    public void AThreadThatNeverRunsTimesOutAndShutsTheQueueDown()
    {
        var shutdowns = 0;
        var error = Assert.Throws<DesktopException>(() => DispatcherQueueThread.WaitForThreadId(
            _ => true, () => { shutdowns++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(("The dispatcher thread did not start within 0.1 s.", 1), (error.Message, shutdowns));
    }

    [Fact]
    public void AFailingShutdownDoesNotHideTheStartupFailure() =>
        Assert.Equal("The new DispatcherQueue rejected work.",
            Assert.Throws<DesktopException>(() => DispatcherQueueThread.WaitForThreadId(
                _ => false, () => Task.FromException(new InvalidOperationException("dead")), TimeSpan.FromSeconds(1))).Message);
}
```

In `windows/tests/DesktopMcp.Desktop.Tests/DispatcherQueueThreadTests.cs`, add before `public async Task InvokeAfterDisposeThrows()`'s `[Fact]`:

```csharp
    [Fact]
    public async Task DisposeResumesOffTheDispatcherThread()
    {
        var dispatcher = DispatcherQueueThread.Start();
        await dispatcher.DisposeAsync();
        Assert.NotEqual(dispatcher.ManagedThreadId, Environment.CurrentManagedThreadId);
    }

```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0117` (`DispatcherQueueThread` has no `WaitForThreadId`).

- [ ] **Step 3: Implement**

Replace `windows/src/DesktopMcp.Desktop/Dispatching/DispatcherQueueThread.cs` with:

```csharp
using DesktopMcp.Desktop.Errors;
using Microsoft.UI.Dispatching;

namespace DesktopMcp.Desktop.Dispatching;

/// <summary>
/// <see cref="IDesktopDispatcher"/> on a dedicated Windows App SDK (Microsoft.UI.Dispatching) DispatcherQueue thread.
/// The thread is the process's main STA (APTTYPE_MAINSTA) because nothing else in the process is STA first; P2 does not
/// start it (Decisions 21), so no later phase has to share the main STA with it.
/// </summary>
public sealed class DispatcherQueueThread : IDesktopDispatcher
{
    /// <summary>How long <see cref="Start"/> waits for the new thread to run its first work item.</summary>
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    private readonly DispatcherQueueController _controller;
    private readonly DispatcherQueue _queue;
    private int _disposed;

    private DispatcherQueueThread(DispatcherQueueController controller, int threadId)
    {
        _controller = controller;
        _queue = controller.DispatcherQueue;
        ManagedThreadId = threadId;
    }

    /// <summary>
    /// Starts a dedicated Windows App SDK DispatcherQueue thread. Initialises the Windows App SDK runtime first
    /// (throws <see cref="InvalidOperationException"/> with an install hint when it is missing); a thread that cannot be
    /// created or does not run work within <see cref="StartTimeout"/> gives a <see cref="DesktopException"/>.
    /// </summary>
    public static DispatcherQueueThread Start()
    {
        AppRuntime.EnsureInitialized();
        DispatcherQueueController controller;
        try
        {
            controller = DispatcherQueueController.CreateOnDedicatedThread();
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate("Starting the dispatcher thread", e);
        }
        var threadId = WaitForThreadId(
            work => controller.DispatcherQueue.TryEnqueue(() => work()),
            () => controller.ShutdownQueueAsync().AsTask(),
            StartTimeout);
        return new DispatcherQueueThread(controller, threadId);
    }

    /// <summary>
    /// Runs one work item on the new thread to learn its id. When the queue rejects it or it does not run in time, the
    /// queue is shut down (so the controller and its thread are not leaked) and a <see cref="DesktopException"/> is thrown.
    /// </summary>
    internal static int WaitForThreadId(Func<Action, bool> tryEnqueue, Func<Task> shutdown, TimeSpan timeout)
    {
        var threadId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? failure = null;
        if (!tryEnqueue(() => threadId.TrySetResult(Environment.CurrentManagedThreadId)))
            failure = "The new DispatcherQueue rejected work.";
        else if (!threadId.Task.Wait(timeout))
            failure = $"The dispatcher thread did not start within {timeout.TotalSeconds:0.#} s.";
        if (failure is null) return threadId.Task.Result;
        // Best effort: the queue may already be unusable; the shutdown must not hide the original failure.
        try { shutdown().Wait(timeout); }
        catch (AggregateException) { }
        throw new DesktopException(failure);
    }

    public int ManagedThreadId { get; }

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _queue.TryEnqueue(() =>
        {
            try { completion.SetResult(func()); }
            catch (Exception e) { completion.SetException(e); }
        });
        if (!queued) throw new ObjectDisposedException(nameof(DispatcherQueueThread));
        return completion.Task;
    }

    public Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick) => InvokeAsync<IDisposable>(() =>
    {
        var timer = _queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) => tick();
        timer.Start();
        return new TimerHandle(this, timer);
    });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // The IAsyncAction completes on the dying dispatcher thread; ConfigureAwait(false) moves the caller's
        // continuation off it.
        await _controller.ShutdownQueueAsync().AsTask().ConfigureAwait(false);
    }

    private sealed class TimerHandle(DispatcherQueueThread owner, DispatcherQueueTimer timer) : IDisposable
    {
        public void Dispose()
        {
            if (owner.HasThreadAccess) { timer.Stop(); return; }
            if (Volatile.Read(ref owner._disposed) == 0) owner._queue.TryEnqueue(() => timer.Stop());  // a lambda: CsWinRT would QI the timer RCW for a bare timer.Stop method group
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~DispatcherStartupTests"`
Expected: `Passed!` with `Failed: 0` (4 tests).

Run (interactive session): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~DispatcherQueueThreadTests"`
Expected: `Passed!` with `Failed: 0` (5 tests). The timer test is timing-dependent; if only it fails, rerun once and record both runs.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Dispatching/DispatcherQueueThread.cs windows/tests/DesktopMcp.Desktop.Tests/DispatcherStartupTests.cs windows/tests/DesktopMcp.Desktop.Tests/DispatcherQueueThreadTests.cs
git commit -m "Bound the dispatcher start, shut a failed queue down and leave the dying thread on dispose" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Displays and the DisplayInventory tool

upstream `uia/core.py:659-828` (GetDisplays) and `tools/display.py:26-61` (DisplayInventory). Displays come from Win32 (Decisions 15); a Desktop test records the DisplayArea evaluation the P0 follow-up asks for. DisplayInventory needs neither COM nor the Windows App SDK runtime.

**Files:**
- Modify: `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (append)
- Create: `windows/src/DesktopMcp.Desktop/Displays/Win32Displays.cs`
- Create: `windows/src/DesktopMcp.Server/Tools/DisplayInventoryTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolResults.cs`, `ToolServices.cs`, `ToolRegistry.cs`
- Create: `windows/tests/DesktopMcp.Desktop.Tests/TestDpi.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Displays/Win32DisplaysTests.cs` (one class Category=Desktop), `windows/tests/DesktopMcp.Server.Tests/Tools/DisplayInventoryToolTests.cs`

**Interfaces:**
- Consumes: `DisplayInfo`, `ScreenRect`, `PyText.Round`, `PyText.FloatRepr` (Task 2).
- Produces:
  - `public interface DesktopMcp.Desktop.Displays.IDisplayProvider { IReadOnlyList<DisplayInfo> GetDisplays(); ScreenRect GetVirtualScreen(); }` and `public sealed class Win32Displays : IDisplayProvider` (internal static `EffectiveDpi(HMONITOR)`, `DpiMetadata(int)`).
  - `ToolResults.Json(string json)`; `ToolServices.Displays` (`IDisplayProvider`); `DisplayInventoryTool` with internal `static string Write(IReadOnlyList<DisplayInfo>)`.
  - Test helper `TestDpi.EnsurePerMonitorV2()` (makes the test process PerMonitorV2 like the exe).

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/TestDpi.cs`:

```csharp
using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// Makes the test process PerMonitorV2 DPI aware, like desktop-mcp.exe (app.manifest), so Win32, UIA and capture report
/// physical pixels. The first call wins; later calls and a host that already set the awareness are no-ops.
/// </summary>
internal static partial class TestDpi
{
    private static readonly Lazy<bool> Applied = new(() => SetProcessDpiAwarenessContext(-4));

    public static void EnsurePerMonitorV2() => _ = Applied.Value;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint context);
}
```

`windows/tests/DesktopMcp.Desktop.Tests/Displays/Win32DisplaysTests.cs`:

```csharp
using DesktopMcp.Desktop.Displays;
using Microsoft.UI.Windowing;

namespace DesktopMcp.Desktop.Tests.Displays;

public class Win32DisplaysTests
{
    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(144, 1.5)]
    [InlineData(120, 1.25)]
    [InlineData(100, 1.041667)]
    public void ScaleIsTheDpiOver96RoundedToSixDigits(int dpi, double scale)
    {
        var (effective, factor) = Win32Displays.DpiMetadata(dpi);
        Assert.Equal((dpi, scale), (effective, factor));
    }
}

[Trait("Category", "Desktop")]
public class Win32DisplaysDesktopTests
{
    public Win32DisplaysDesktopTests() => TestDpi.EnsurePerMonitorV2();

    [Fact]
    public void ListsThePrimaryDisplayInsideTheVirtualScreen()
    {
        var provider = new Win32Displays();
        var displays = provider.GetDisplays();
        var screen = provider.GetVirtualScreen();
        var primary = Assert.Single(displays, d => d.Primary);
        Assert.Equal(displays.Select(d => d.Index).Order(), displays.Select(d => d.Index));
        Assert.All(displays, d => Assert.Equal(d.Bounds, d.Bounds.Intersect(screen)));
        Assert.StartsWith(@"\\.\DISPLAY", primary.DeviceName);
        Assert.NotNull(primary.EffectiveDpi);
    }

    /// <summary>
    /// The DisplayArea evaluation (P0 follow-up, Decisions 15): the Windows App SDK reports the same bounds and work areas
    /// as GetMonitorInfo for a PerMonitorV2 process.
    /// </summary>
    [Fact]
    public void DisplayAreaAgreesWithGetMonitorInfo()
    {
        AppRuntime.EnsureInitialized();
        var win32 = new Win32Displays().GetDisplays();
        var areas = DisplayArea.FindAll();
        Assert.Equal(win32.Count, areas.Count);
        for (var i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            var match = Assert.Single(win32, d => d.Bounds.Left == area.OuterBounds.X && d.Bounds.Top == area.OuterBounds.Y);
            Assert.Equal((match.Bounds.Width, match.Bounds.Height), (area.OuterBounds.Width, area.OuterBounds.Height));
            Assert.Equal(match.WorkArea, new Core.Snapshots.ScreenRect(area.WorkArea.X, area.WorkArea.Y,
                area.WorkArea.X + area.WorkArea.Width, area.WorkArea.Y + area.WorkArea.Height));
            Assert.Equal(match.Primary, area.IsPrimary);
        }
    }
}
```

`windows/tests/DesktopMcp.Server.Tests/Tools/DisplayInventoryToolTests.cs`:

```csharp
using System.Text.Json;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public class DisplayInventoryToolTests
{
    private sealed class FakeDisplays(params DisplayInfo[] displays) : IDisplayProvider
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => displays;
        public ScreenRect GetVirtualScreen() => new(0, 0, 1, 1);
    }

    private const string Expected =
        """[{"index":0,"device":"\\\\.\\DISPLAY1","primary":true,"bounds":{"left":0,"top":0,"right":2560,"bottom":1600,"width":2560,"height":1600},"work_area":{"left":0,"top":0,"right":2560,"bottom":1520,"width":2560,"height":1520},"resolution":"2560x1600","orientation":"landscape","effective_dpi":144,"scale":1.5},{"index":1,"device":"DISPLAY1","primary":false,"bounds":{"left":-1080,"top":0,"right":0,"bottom":1920,"width":1080,"height":1920},"work_area":null,"resolution":"1080x1920","orientation":"portrait","effective_dpi":96,"scale":1.0}]""";

    [Fact]
    public async Task ReturnsTheDisplaysAsJsonTextAndStructuredContent()
    {
        var services = ToolServices.CreateDefault() with
        {
            Displays = new FakeDisplays(
                new DisplayInfo(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1600), true, new ScreenRect(0, 0, 2560, 1520), 144, 1.5, "landscape"),
                new DisplayInfo(1, "DISPLAY1", new ScreenRect(-1080, 0, 0, 1920), false, null, 96, 1.0, "portrait")),
        };
        var result = await ToolCalls.CallAsync(ToolCalls.Create("DisplayInventory", services), "{}");

        Assert.Equal(Expected, ToolCalls.OkText(result));
        var list = result.StructuredContent!.Value.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(1.0, list[1].GetProperty("scale").GetDouble());
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build DesktopMcp.slnx`
Expected: FAIL with `CS0234` (`DesktopMcp.Desktop.Displays` does not exist).

- [ ] **Step 3: Read the displays**

Append to `windows/src/DesktopMcp.Desktop/NativeMethods.txt`:

```text
// Displays (DisplayInventory, display selection; upstream uia/core.py:599-828)
EnumDisplayMonitors
GetMonitorInfo
MONITORINFOEXW
MONITORINFOF_PRIMARY
EnumDisplayDevices
DISPLAY_DEVICE_STATE_FLAGS
EnumDisplaySettings
ENUM_DISPLAY_SETTINGS_MODE
GetDpiForMonitor
GetSystemMetrics
```

(List the enums `DISPLAY_DEVICE_STATE_FLAGS` and `ENUM_DISPLAY_SETTINGS_MODE`, not their constants: CsWin32 warns about a constant that belongs to an enum, and warnings fail the build.)

`windows/src/DesktopMcp.Desktop/Displays/Win32Displays.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopMcp.Desktop.Displays;

/// <summary>The active displays and the virtual screen, in physical pixels (the exe is PerMonitorV2 DPI aware).</summary>
public interface IDisplayProvider
{
    IReadOnlyList<DisplayInfo> GetDisplays();

    /// <summary>upstream desktop/service.py:1251-1260 get_screen_box (SM_XVIRTUALSCREEN and friends).</summary>
    ScreenRect GetVirtualScreen();
}

/// <summary>
/// upstream uia/core.py:659-828 GetDisplays: EnumDisplayMonitors, GetMonitorInfo, GetDpiForMonitor (falling back to the
/// system DPI) and EnumDisplaySettings. Microsoft.UI.Windowing.DisplayArea was evaluated and not adopted (Decisions 15).
/// Needs no Windows App SDK runtime and no COM.
/// </summary>
public sealed unsafe class Win32Displays : IDisplayProvider
{
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var activeIndices = ActiveDisplayIndices();
        var monitors = new List<(HMONITOR Monitor, RECT Rect)>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            PInvoke.EnumDisplayMonitors(default, (RECT*)null, &OnMonitor, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        var displays = new List<DisplayInfo>();
        foreach (var (monitor, enumRect) in monitors)
        {
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            int index;
            string deviceName;
            ScreenRect bounds;
            ScreenRect? workArea;
            bool primary;
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                bounds = ToRect(info.monitorInfo.rcMonitor);
                workArea = ToRect(info.monitorInfo.rcWork);
                deviceName = info.szDevice.ToString();
                index = activeIndices.TryGetValue(deviceName.ToUpperInvariant(), out var active) ? active : NextFallbackIndex(activeIndices, displays);
                primary = (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0;
            }
            else
            {
                bounds = ToRect(enumRect);
                workArea = null;
                index = NextFallbackIndex(activeIndices, displays);
                deviceName = $"DISPLAY{index}";
                primary = false;
            }
            var (dpi, scale) = EffectiveDpi(monitor);
            displays.Add(new DisplayInfo(index, deviceName, bounds, primary, workArea, dpi, scale, Orientation(deviceName, bounds)));
        }
        return displays.OrderBy(display => display.Index).ToArray();
    }

    public ScreenRect GetVirtualScreen()
    {
        var left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        return new ScreenRect(left, top,
            left + PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
            top + PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));
    }

    /// <summary>upstream uia/core.py:685-705: the monitor's effective DPI, else the system DPI, with scale = round(dpi / 96, 6).</summary>
    internal static (int? Dpi, double? Scale) EffectiveDpi(HMONITOR monitor)
    {
        uint dpiX, dpiY;
        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY).Succeeded && dpiX > 0)
            return DpiMetadata((int)dpiX);
        var system = (int)PInvoke.GetDpiForSystem();
        return system > 0 ? DpiMetadata(system) : (null, null);
    }

    internal static (int? Dpi, double? Scale) DpiMetadata(int dpi) => (dpi, PyText.Round(dpi / 96.0, 6));

    // upstream uia/core.py:731-748: \\.\DISPLAYn names attached to the desktop, numbered in enumeration order.
    private static Dictionary<string, int> ActiveDisplayIndices()
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var device = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        for (uint i = 0; PInvoke.EnumDisplayDevices((string?)null, i, ref device, 0); i++)
        {
            if ((device.StateFlags & DISPLAY_DEVICE_STATE_FLAGS.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                indices[device.DeviceName.ToString().ToUpperInvariant()] = indices.Count;
            device = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        }
        return indices;
    }

    // upstream uia/core.py:766-771.
    private static int NextFallbackIndex(Dictionary<string, int> activeIndices, List<DisplayInfo> displays)
    {
        var used = activeIndices.Values.Concat(displays.Select(d => d.Index)).ToHashSet();
        var index = 0;
        while (used.Contains(index)) index++;
        return index;
    }

    // upstream uia/core.py:708-728.
    private static string Orientation(string deviceName, ScreenRect bounds)
    {
        var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
        if (PInvoke.EnumDisplaySettings(deviceName, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode)
            && mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
            return mode.dmPelsWidth >= mode.dmPelsHeight ? "landscape" : "portrait";
        return bounds.Width >= bounds.Height ? "landscape" : "portrait";
    }

    private static ScreenRect ToRect(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnMonitor(HMONITOR monitor, HDC dc, RECT* rect, LPARAM data)
    {
        ((List<(HMONITOR, RECT)>)GCHandle.FromIntPtr(data).Target!).Add((monitor, *rect));
        return true;
    }
}
```

- [ ] **Step 4: Add the tool**

`windows/src/DesktopMcp.Server/Tools/DisplayInventoryTool.cs`:

```csharp
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Displays;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/display.py:26-61: the display list as JSON text and as structuredContent {"result": [...]}.</summary>
public sealed class DisplayInventoryTool(Tool protocolTool, IDisplayProvider displays) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var list = await Task.Run(displays.GetDisplays, cancellationToken).ConfigureAwait(false);
        return ToolResults.Json(Write(list));
    }

    /// <summary>
    /// upstream tools/display.py:13-23 and 49-61, key order kept; compact JSON, scale written as Python prints a float
    /// (Decisions 15).
    /// </summary>
    internal static string Write(IReadOnlyList<DisplayInfo> displays)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (var display in displays)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", display.Index);
                writer.WriteString("device", display.DeviceName);
                writer.WriteBoolean("primary", display.Primary);
                WriteRect(writer, "bounds", display.Bounds);
                if (display.WorkArea is { } workArea) WriteRect(writer, "work_area", workArea);
                else writer.WriteNull("work_area");
                writer.WriteString("resolution", $"{display.Bounds.Width}x{display.Bounds.Height}");
                writer.WriteString("orientation", display.Orientation);
                if (display.EffectiveDpi is { } dpi) writer.WriteNumber("effective_dpi", dpi);
                else writer.WriteNull("effective_dpi");
                writer.WritePropertyName("scale");
                if (display.Scale is { } scale) writer.WriteRawValue(PyText.FloatRepr(scale));
                else writer.WriteNullValue();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteRect(Utf8JsonWriter writer, string name, ScreenRect rect)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("left", rect.Left);
        writer.WriteNumber("top", rect.Top);
        writer.WriteNumber("right", rect.Right);
        writer.WriteNumber("bottom", rect.Bottom);
        writer.WriteNumber("width", rect.Width);
        writer.WriteNumber("height", rect.Height);
        writer.WriteEndObject();
    }
}
```

In `windows/src/DesktopMcp.Server/Tools/ToolResults.cs`, add before `public static CallToolResult Error(`:

```csharp
    /// <summary>A JSON result (spec §4): the JSON as text, plus structuredContent {"result": json}.</summary>
    public static CallToolResult Json(string json)
    {
        using var document = JsonDocument.Parse($"{{\"result\":{json}}}");
        return new()
        {
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = document.RootElement.Clone(),
        };
    }

```

Replace `windows/src/DesktopMcp.Server/Tools/ToolServices.cs` with:

```csharp
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Core.Processes;
using DesktopMcp.Core.Scrape;
using DesktopMcp.Core.WindowsRegistry;
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Toasts;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public required PowerShellRunner PowerShell { get; init; }
    public required FileSystemService FileSystem { get; init; }
    public required RegistryService Registry { get; init; }
    public required ProcessService Processes { get; init; }
    public required IClipboard Clipboard { get; init; }
    public required IToastSender Toasts { get; init; }
    public required ScrapeClient Scrape { get; init; }
    public required IDisplayProvider Displays { get; init; }

    public static ToolServices CreateDefault() => new()
    {
        PowerShell = new PowerShellRunner(),
        FileSystem = new FileSystemService(),
        Registry = new RegistryService(),
        Processes = new ProcessService(),
        Clipboard = new SystemClipboard(),
        Toasts = new ToastSender(),
        Scrape = new ScrapeClient(),
        Displays = new Win32Displays(),
    };
}
```

In `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs`, add after the `"Scrape"` arm:

```csharp
        "DisplayInventory" => new DisplayInventoryTool(protocolTool, services.Displays),
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for the three projects, including `DisplayInventoryToolTests` and `Win32DisplaysTests.ScaleIsTheDpiOver96RoundedToSixDigits`.

Run (interactive session): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~Win32DisplaysDesktopTests"`
Expected: `Passed!` with `Failed: 0` (2 tests). `DisplayAreaAgreesWithGetMonitorInfo` is the DisplayArea evaluation; record its outcome (and the monitor count) for Task 19. If it fails on a multi-monitor or mixed-DPI setup, keep the Win32 path, record the difference in Decisions 15's spec note, and do not change the test's expectation without the user.

- [ ] **Step 6: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/NativeMethods.txt windows/src/DesktopMcp.Desktop/Displays windows/src/DesktopMcp.Server/Tools windows/tests/DesktopMcp.Desktop.Tests/TestDpi.cs windows/tests/DesktopMcp.Desktop.Tests/Displays windows/tests/DesktopMcp.Server.Tests/Tools/DisplayInventoryToolTests.cs
git commit -m "Add DisplayInventory on Win32 displays and record the DisplayArea evaluation" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: Virtual desktops

upstream `vdm/core.py`: the documented `IVirtualDesktopManager` decides which windows are on the current desktop; the undocumented `IVirtualDesktopManagerInternal` lists the desktops, named from the registry or `Desktop N` (spec §6, Decisions 19). Both are bounded and fail open.

**Files:**
- Modify: `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (append)
- Create: `windows/src/DesktopMcp.Desktop/VirtualDesktops/VirtualDesktopInterop.cs`, `VirtualDesktopService.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/VirtualDesktops/VirtualDesktopServiceTests.cs` (one class Category=Desktop)

**Interfaces:**
- Consumes: `VirtualDesktopInfo` (Task 2).
- Produces:
  - `public interface DesktopMcp.Desktop.VirtualDesktops.IVirtualDesktops { IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows); (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops(); }`.
  - `public sealed class VirtualDesktopService : IVirtualDesktops` (`static readonly TimeSpan CallTimeout` = 2 s; internal ctor `(IVirtualDesktopSource, Func<Guid, string?> registryName, TimeSpan timeout)`; internal `static string Format(Guid)`).
  - internal `interface IVirtualDesktopSource { bool IsOnCurrentDesktop(long); IReadOnlyList<Guid?> DesktopIds(); Guid CurrentDesktopId(); }`, `ShellVirtualDesktops`, `VirtualDesktopIds`, and the `[GeneratedComInterface]` declarations `IVirtualDesktopManagerInternal22621`, `IVirtualDesktopManagerInternal26100`, `IVirtualDesktop`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/VirtualDesktops/VirtualDesktopServiceTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Tests.VirtualDesktops;

public class VirtualDesktopServiceTests
{
    private static readonly Guid First = new("033294df-1cac-47b0-a62e-d3e53b23f4bc");
    private static readonly Guid Second = new("c4e16f48-2232-4ef9-8671-d901d748ad83");

    private sealed class FakeSource : IVirtualDesktopSource
    {
        public Func<long, bool> OnCurrent { get; init; } = _ => true;
        public Func<IReadOnlyList<Guid?>> Ids { get; init; } = () => [First, Second];
        public Func<Guid> Current { get; init; } = () => Second;
        public bool IsOnCurrentDesktop(long window) => OnCurrent(window);
        public IReadOnlyList<Guid?> DesktopIds() => Ids();
        public Guid CurrentDesktopId() => Current();
    }

    private static VirtualDesktopService Service(FakeSource source, Func<Guid, string?>? names = null, int timeoutMs = 2000) =>
        new(source, names ?? (_ => null), TimeSpan.FromMilliseconds(timeoutMs));

    [Fact]
    public void NamesComeFromTheRegistryElseDesktopN()
    {
        var (active, all) = Service(new FakeSource(), id => id == Second ? "Work" : null).GetDesktops();
        Assert.Equal(
            [new VirtualDesktopInfo("{033294DF-1CAC-47B0-A62E-D3E53B23F4BC}", "Desktop 1"), new VirtualDesktopInfo("{C4E16F48-2232-4EF9-8671-D901D748AD83}", "Work")],
            all);
        Assert.Equal("Work", active.Name);
    }

    [Fact]
    public void AnUnreadableDesktopIsSkippedButKeepsItsNumber()
    {
        var (_, all) = Service(new FakeSource { Ids = () => [null, Second] }).GetDesktops();
        Assert.Equal("Desktop 2", Assert.Single(all).Name);
    }

    [Fact]
    public void ACurrentDesktopMissingFromTheListIsUnknown()
    {
        var (active, _) = Service(new FakeSource { Current = () => Guid.Empty }).GetDesktops();
        Assert.Equal(new VirtualDesktopInfo("{00000000-0000-0000-0000-000000000000}", "Unknown"), active);
    }

    [Fact]
    public void FailuresAndHangsFallBackToTheDefaultDesktop()
    {
        var failing = Service(new FakeSource { Ids = () => throw new InvalidOperationException("no shell") }).GetDesktops();
        Assert.Equal(VirtualDesktopInfo.Default, failing.Active);
        Assert.Equal([VirtualDesktopInfo.Default], failing.All);
        var hanging = Service(new FakeSource { Ids = () => { Thread.Sleep(5000); return []; } }, timeoutMs: 50).GetDesktops();
        Assert.Equal(VirtualDesktopInfo.Default, hanging.Active);
    }

    [Fact]
    public void WindowFilterKeepsOrderAndFailsOpen()
    {
        var source = new FakeSource { OnCurrent = window => window == 3 ? throw new InvalidOperationException("bad window") : window != 2 };
        Assert.Equal([1L, 3L], Service(source).OnCurrentDesktop([1, 2, 3]));
    }
}

[Trait("Category", "Desktop")]
public class ShellVirtualDesktopsTests
{
    [Fact]
    public void TheCurrentDesktopIsOneOfTheListedDesktops()
    {
        var (active, all) = new VirtualDesktopService().GetDesktops();
        Assert.NotEqual(VirtualDesktopInfo.Default, active);
        Assert.Contains(active, all);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0234` (`DesktopMcp.Desktop.VirtualDesktops` does not exist).

- [ ] **Step 3: Implement**

Append to `windows/src/DesktopMcp.Desktop/NativeMethods.txt`:

```text
// Virtual desktops (documented manager; the internal one is declared by hand in VirtualDesktops/)
VirtualDesktopManager
IVirtualDesktopManager
IServiceProvider
IObjectArray
CoCreateInstance
```

`windows/src/DesktopMcp.Desktop/VirtualDesktops/VirtualDesktopInterop.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DesktopMcp.Desktop.VirtualDesktops;

// The undocumented shell interfaces upstream uses for desktop names (vdm/core.py:67-367), declared by hand because they
// are not in the Windows SDK metadata (spec §6). Only the slots up to GetDesktops are called; the earlier ones are
// declared so the vtable order is right. Their IIDs change between Windows builds, so the manager has one declaration
// per supported build. IVirtualDesktop is the same on 22621 and 26100.

internal static class VirtualDesktopIds
{
    public static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid VirtualDesktopManagerInternalService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    /// <summary>upstream vdm/core.py:137-148; Windows 10 (19041) is not supported (spec §2).</summary>
    public static Guid ManagerInternal(int build) => build >= 26100
        ? typeof(IVirtualDesktopManagerInternal26100).GUID
        : typeof(IVirtualDesktopManagerInternal22621).GUID;
}

[GeneratedComInterface, Guid("A3175F2D-239C-4BD2-8AA0-EEBA8B0B138E")]
internal unsafe partial interface IVirtualDesktopManagerInternal22621
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int MoveViewToDesktop(void* view, void* desktop);
    [PreserveSig] int CanViewMoveDesktops(void* view, int* canMove);
    [PreserveSig] int GetCurrentDesktop(out void* desktop);
    [PreserveSig] int GetDesktops(out void* desktops);
}

[GeneratedComInterface, Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal unsafe partial interface IVirtualDesktopManagerInternal26100
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int MoveViewToDesktop(void* view, void* desktop);
    [PreserveSig] int CanViewMoveDesktops(void* view, int* canMove);
    [PreserveSig] int GetCurrentDesktop(out void* desktop);
    [PreserveSig] int GetDesktops(out void* desktops);
}

[GeneratedComInterface, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal unsafe partial interface IVirtualDesktop
{
    [PreserveSig] int IsViewVisible(void* view, int* visible);
    [PreserveSig] int GetID(out Guid id);
}
```

`windows/src/DesktopMcp.Desktop/VirtualDesktops/VirtualDesktopService.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using DesktopMcp.Core.Snapshots;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using ComServiceProvider = Windows.Win32.System.Com.IServiceProvider;

namespace DesktopMcp.Desktop.VirtualDesktops;

/// <summary>Virtual desktops: which windows are on the current one, and the desktop names the Snapshot text lists.</summary>
public interface IVirtualDesktops
{
    /// <summary>The windows on the current virtual desktop, in the given order; a window that cannot be checked is kept.</summary>
    IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows);

    /// <summary>The current desktop and all desktops; <see cref="VirtualDesktopInfo.Default"/> when they cannot be read.</summary>
    (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops();
}

/// <summary>The shell calls behind <see cref="VirtualDesktopService"/>; each may throw.</summary>
internal interface IVirtualDesktopSource
{
    bool IsOnCurrentDesktop(long window);

    /// <summary>The desktop ids in order; null where a desktop's id could not be read.</summary>
    IReadOnlyList<Guid?> DesktopIds();

    Guid CurrentDesktopId();
}

/// <summary>
/// upstream vdm/core.py:370-689. The documented IVirtualDesktopManager filters windows; the undocumented
/// IVirtualDesktopManagerInternal lists desktops, named from the registry or "Desktop N" (spec §6). Every call goes to
/// Explorer, so each is bounded by <see cref="CallTimeout"/> and fails open (spec §12: names only affect the report).
/// </summary>
public sealed class VirtualDesktopService : IVirtualDesktops
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private const string DesktopsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops";

    private readonly IVirtualDesktopSource _source;
    private readonly Func<Guid, string?> _registryName;
    private readonly TimeSpan _timeout;

    public VirtualDesktopService() : this(new ShellVirtualDesktops(), ReadRegistryName, CallTimeout)
    {
    }

    internal VirtualDesktopService(IVirtualDesktopSource source, Func<Guid, string?> registryName, TimeSpan timeout)
    {
        _source = source;
        _registryName = registryName;
        _timeout = timeout;
    }

    public IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows) =>
        Bounded(() => windows.Where(window =>
        {
            try { return _source.IsOnCurrentDesktop(window); }
            catch (Exception) { return true; } // upstream vdm/core.py:415-420: fail open
        }).ToArray(), windows);

    public (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops() =>
        Bounded(ReadDesktops, (VirtualDesktopInfo.Default, [VirtualDesktopInfo.Default]));

    /// <summary>The id as upstream prints it: str() of a comtypes GUID, "{...}" upper case.</summary>
    internal static string Format(Guid id) => id.ToString("B").ToUpperInvariant();

    // upstream vdm/core.py:621-689.
    private (VirtualDesktopInfo, IReadOnlyList<VirtualDesktopInfo>) ReadDesktops()
    {
        var ids = _source.DesktopIds();
        var all = new List<VirtualDesktopInfo>();
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] is not { } id) continue;
            var name = _registryName(id);
            all.Add(new VirtualDesktopInfo(Format(id), string.IsNullOrEmpty(name) ? $"Desktop {i + 1}" : name));
        }
        var current = Format(_source.CurrentDesktopId());
        var active = all.FirstOrDefault(desktop => desktop.Id == current) ?? new VirtualDesktopInfo(current, "Unknown");
        return (active, all);
    }

    // upstream vdm/core.py:434-447.
    private static string? ReadRegistryName(Guid id)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{DesktopsKey}\{Format(id)}");
        return key?.GetValue("Name") as string;
    }

    private T Bounded<T>(Func<T> read, T fallback)
    {
        try
        {
            var task = Task.Run(read);
            return task.Wait(_timeout) ? task.Result : fallback;
        }
        catch (AggregateException)
        {
            return fallback;
        }
    }
}

/// <summary>The real shell calls (spec §6). COM objects are created on the calling thread-pool (MTA) thread.</summary>
internal sealed unsafe class ShellVirtualDesktops : IVirtualDesktopSource
{
    private readonly Lazy<IVirtualDesktopManager> _manager =
        new(() => VirtualDesktopManager.CreateInstance<IVirtualDesktopManager>(), LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsOnCurrentDesktop(long window)
    {
        _manager.Value.IsWindowOnCurrentVirtualDesktop((HWND)(nint)window, out var onCurrent);
        return onCurrent;
    }

    public IReadOnlyList<Guid?> DesktopIds() => WithInternalManager((current, desktops) =>
    {
        Marshal.ThrowExceptionForHR(desktops(out var raw));
        var array = ComInterfaceMarshaller<IObjectArray>.ConvertToManaged(raw)
            ?? throw new InvalidOperationException("GetDesktops returned no array.");
        ComInterfaceMarshaller<IObjectArray>.Free(raw);
        array.GetCount(out var count);
        var iid = typeof(IVirtualDesktop).GUID;
        var ids = new List<Guid?>();
        for (uint i = 0; i < count; i++)
        {
            try
            {
                array.GetAt(i, &iid, out var item);
                ids.Add(ReadId(item));
            }
            catch (Exception)
            {
                ids.Add(null); // upstream vdm/core.py:659-661: a desktop that cannot be read is skipped
            }
        }
        return ids;
    });

    public Guid CurrentDesktopId() => WithInternalManager((current, _) =>
    {
        Marshal.ThrowExceptionForHR(current(out var raw));
        return ReadId(raw);
    });

    private delegate int GetObject(out void* result);

    private static Guid ReadId(void* raw)
    {
        var desktop = ComInterfaceMarshaller<IVirtualDesktop>.ConvertToManaged(raw)
            ?? throw new InvalidOperationException("The virtual desktop is null.");
        ComInterfaceMarshaller<IVirtualDesktop>.Free(raw);
        Marshal.ThrowExceptionForHR(desktop.GetID(out var id));
        return id;
    }

    // upstream vdm/core.py:394-406: ImmersiveShell's IServiceProvider hands out the internal manager.
    private static T WithInternalManager<T>(Func<GetObject, GetObject, T> use)
    {
        var shell = VirtualDesktopIds.ImmersiveShell;
        PInvoke.CoCreateInstance<ComServiceProvider>(shell, null, CLSCTX.CLSCTX_LOCAL_SERVER, out var provider).ThrowOnFailure();
        var service = VirtualDesktopIds.VirtualDesktopManagerInternalService;
        var build = Environment.OSVersion.Version.Build;
        var iid = VirtualDesktopIds.ManagerInternal(build);
        provider.QueryService(&service, &iid, out var raw);
        if (build >= 26100)
        {
            var manager = ComInterfaceMarshaller<IVirtualDesktopManagerInternal26100>.ConvertToManaged(raw)!;
            ComInterfaceMarshaller<IVirtualDesktopManagerInternal26100>.Free(raw);
            return use(manager.GetCurrentDesktop, manager.GetDesktops);
        }
        var older = ComInterfaceMarshaller<IVirtualDesktopManagerInternal22621>.ConvertToManaged(raw)!;
        ComInterfaceMarshaller<IVirtualDesktopManagerInternal22621>.Free(raw);
        return use(older.GetCurrentDesktop, older.GetDesktops);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~VirtualDesktopServiceTests"`
Expected: `Passed!` with `Failed: 0` (5 tests).

Run (interactive session): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~ShellVirtualDesktopsTests"`
Expected: `Passed!` with `Failed: 0`: the hand-declared interfaces reach Explorer on this build. If it fails on a build other than 22621 or 26100+, record the build number for Task 19; the snapshot still works with `Default Desktop`.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/NativeMethods.txt windows/src/DesktopMcp.Desktop/VirtualDesktops windows/tests/DesktopMcp.Desktop.Tests/VirtualDesktops
git commit -m "Read virtual desktops through the shell managers, bounded and failing open" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: UIA worker and hang breaker

The P0 follow-up "UIA hang breaker" (Decisions 2): every UIA job runs on one MTA thread under a deadline; a job that misses it costs only that thread, and the window and process that hung are skipped for 90 s. Pure threading code, tested with fake sessions and a manual clock.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Uia/UiaWorker.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Uia/UiaWorkerTests.cs`

**Interfaces:**
- Consumes: `UiaTimeoutException`, `UiaUnavailableException` (Task 5).
- Produces:
  - `internal sealed class UiaWorker<TSession> : IDisposable where TSession : class`: ctor `(Func<TSession> createSession)`, `const int MaxStuckWorkers = 4`, `int StuckWorkers`, `Task<T> RunAsync<T>(Func<TSession, T> job, TimeSpan deadline, CancellationToken)`.
  - `internal sealed class HangBlockList(TimeProvider time, TimeSpan duration)`: `static readonly TimeSpan DefaultDuration` (90 s), `void Block(long window, int processId)`, `bool IsBlocked(long window, int processId)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/Uia/UiaWorkerTests.cs`:

```csharp
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tests.Uia;

public class UiaWorkerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private sealed class Session
    {
        public int ThreadId { get; } = Environment.CurrentManagedThreadId;
    }

    [Fact]
    public async Task JobsRunOnOneMtaThreadThatOwnsTheSession()
    {
        using var worker = new UiaWorker<Session>(() => new Session());
        var first = await worker.RunAsync(s => (s.ThreadId, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()), Generous, default);
        var second = await worker.RunAsync(s => s.ThreadId, Generous, default);
        Assert.Equal(first.Item1, first.Item2);
        Assert.Equal(ApartmentState.MTA, first.Item3);
        Assert.Equal(first.Item1, second);
        Assert.NotEqual(Environment.CurrentManagedThreadId, second);
    }

    [Fact]
    public async Task JobExceptionsReachTheCaller()
    {
        using var worker = new UiaWorker<Session>(() => new Session());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync<int>(_ => throw new InvalidOperationException("boom"), Generous, default));
        Assert.Equal("boom", error.Message);
        Assert.Equal(1, await worker.RunAsync(_ => 1, Generous, default));
    }

    [Fact]
    public async Task AHungJobTimesOutAndTheNextJobGetsAFreshThreadAndSession()
    {
        using var release = new ManualResetEventSlim();
        var sessions = 0;
        using var worker = new UiaWorker<Session>(() => { Interlocked.Increment(ref sessions); return new Session(); });
        var hungThread = await worker.RunAsync(s => s.ThreadId, Generous, default);

        var error = await Assert.ThrowsAsync<UiaTimeoutException>(() => worker.RunAsync(_ => release.Wait(Generous), TimeSpan.FromMilliseconds(100), default));
        Assert.Equal("UI Automation did not answer within 0.1 s.", error.Message);
        Assert.Equal(1, worker.StuckWorkers);

        var next = await worker.RunAsync(s => s.ThreadId, Generous, default);
        Assert.NotEqual(hungThread, next);
        Assert.Equal(2, sessions);

        release.Set();
        await WaitUntil(() => worker.StuckWorkers == 0);
    }

    [Fact]
    public async Task TooManyStuckThreadsFailFast()
    {
        using var release = new ManualResetEventSlim();
        using var worker = new UiaWorker<Session>(() => new Session());
        for (var i = 0; i < UiaWorker<Session>.MaxStuckWorkers; i++)
            await Assert.ThrowsAsync<UiaTimeoutException>(() => worker.RunAsync(_ => release.Wait(Generous), TimeSpan.FromMilliseconds(50), default));

        var ran = false;
        await Assert.ThrowsAsync<UiaUnavailableException>(() => worker.RunAsync(_ => ran = true, Generous, default));
        Assert.False(ran);

        release.Set();
        await WaitUntil(() => worker.StuckWorkers == 0);
        Assert.True(await worker.RunAsync(_ => true, Generous, default));
    }

    [Fact]
    public async Task CancellationStopsTheWaitAndTheNextJobQueuesBehind()
    {
        using var release = new ManualResetEventSlim();
        using var worker = new UiaWorker<Session>(() => new Session());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(_ => release.Wait(Generous), Generous, cancel.Token));
        var next = worker.RunAsync(_ => 2, Generous, default);
        release.Set();
        Assert.Equal(2, await next);
        Assert.Equal(0, worker.StuckWorkers);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}

public class HangBlockListTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void BlocksTheWindowAndItsProcessForTheDuration()
    {
        var time = new ManualTime();
        var blocks = new HangBlockList(time, TimeSpan.FromSeconds(90));
        blocks.Block(window: 0x1234, processId: 42);

        Assert.True(blocks.IsBlocked(0x1234, 0));
        Assert.True(blocks.IsBlocked(0x9999, 42));
        Assert.False(blocks.IsBlocked(0x9999, 7));

        time.Now += TimeSpan.FromSeconds(91);
        Assert.False(blocks.IsBlocked(0x1234, 42));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0246` for `UiaWorker<>` and `HangBlockList`.

- [ ] **Step 3: Implement**

`windows/src/DesktopMcp.Desktop/Uia/UiaWorker.cs`:

```csharp
using System.Collections.Concurrent;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Uia;

/// <summary>
/// The UIA hang breaker (P0 follow-up, Decisions 2). Jobs run one at a time on a dedicated MTA thread that owns a
/// session (the UIA client). A job that misses its deadline gives a <see cref="UiaTimeoutException"/>; its thread is
/// abandoned (it stays blocked in the hung application until the call returns) and the next job gets a new thread
/// and a new session. When <see cref="MaxStuckWorkers"/> abandoned threads are still blocked, jobs fail at once with
/// <see cref="UiaUnavailableException"/>.
/// </summary>
internal sealed class UiaWorker<TSession> : IDisposable where TSession : class
{
    public const int MaxStuckWorkers = 4;

    private readonly Func<TSession> _createSession;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Worker? _worker;
    private int _stuck;

    public UiaWorker(Func<TSession> createSession) => _createSession = createSession;

    /// <summary>Abandoned threads whose job has not returned yet.</summary>
    public int StuckWorkers => Volatile.Read(ref _stuck);

    /// <summary>
    /// Runs <paramref name="job"/> on the worker thread. Cancellation stops the wait, not the job: the job finishes on
    /// the worker, and the next job queues behind it.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<TSession, T> job, TimeSpan deadline, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (StuckWorkers >= MaxStuckWorkers)
                throw new UiaUnavailableException(
                    $"UI Automation is not responding: {StuckWorkers} calls are still blocked in other applications.");
            var worker = _worker ??= new Worker(this);
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            worker.Post(() =>
            {
                try { completion.TrySetResult(job(worker.Session)); }
                catch (Exception e) { completion.TrySetException(e); }
            });
            try
            {
                return await completion.Task.WaitAsync(deadline, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Interlocked.Increment(ref _stuck);
                worker.Abandon();
                _worker = null;
                throw new UiaTimeoutException($"UI Automation did not answer within {deadline.TotalSeconds:0.#} s.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _worker?.Stop();

    private sealed class Worker
    {
        private readonly UiaWorker<TSession> _owner;
        private readonly BlockingCollection<Action> _queue = new();
        private TSession? _session;
        private volatile bool _abandoned;

        public Worker(UiaWorker<TSession> owner)
        {
            _owner = owner;
            var thread = new Thread(Run) { IsBackground = true, Name = "desktop-mcp UIA" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        /// <summary>The session, created on this thread by the first job that needs it.</summary>
        public TSession Session => _session ??= _owner._createSession();

        public void Post(Action work) => _queue.Add(work);

        public void Abandon()
        {
            _abandoned = true;
            _queue.CompleteAdding();
        }

        public void Stop() => _queue.CompleteAdding();

        private void Run()
        {
            try
            {
                foreach (var work in _queue.GetConsumingEnumerable()) work();
            }
            finally
            {
                if (_abandoned) Interlocked.Decrement(ref _owner._stuck);
            }
        }
    }
}

/// <summary>
/// Windows and processes that made a UIA call time out are skipped for <c>duration</c> (about 90 s, Decisions 2), so a
/// hung application costs one deadline, not one per snapshot.
/// </summary>
internal sealed class HangBlockList(TimeProvider time, TimeSpan duration)
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(90);

    private readonly Lock _gate = new();
    private readonly Dictionary<long, DateTimeOffset> _windows = [];
    private readonly Dictionary<int, DateTimeOffset> _processes = [];

    public void Block(long window, int processId)
    {
        var until = time.GetUtcNow() + duration;
        lock (_gate)
        {
            _windows[window] = until;
            if (processId > 0) _processes[processId] = until;
        }
    }

    public bool IsBlocked(long window, int processId)
    {
        var now = time.GetUtcNow();
        lock (_gate)
            return Active(_windows, window, now) || (processId > 0 && Active(_processes, processId, now));
    }

    private static bool Active<TKey>(Dictionary<TKey, DateTimeOffset> entries, TKey key, DateTimeOffset now) where TKey : notnull
    {
        if (!entries.TryGetValue(key, out var until)) return false;
        if (until > now) return true;
        entries.Remove(key);
        return false;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~UiaWorkerTests|FullyQualifiedName~HangBlockListTests"`
Expected: `Passed!` with `Failed: 0` (6 tests, under 2 s).

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Uia/UiaWorker.cs windows/tests/DesktopMcp.Desktop.Tests/Uia/UiaWorkerTests.cs
git commit -m "Run UIA on a replaceable MTA worker with deadlines and block hung windows for 90 s" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: UIA tree source and the test window

The UIA wrapper of spec §6: element properties read once from the cache request into a plain record (every BSTR freed, every VARIANT disposed), children one level at a time with `BuildUpdatedCache` at `TreeScope_Children`, the window-list probe, and the visible words of text controls through TextPattern (`uia/controls.py:1240-1294`, `uia/patterns.py:1578-1599`). The tree walker (Task 12) sees only the `ITreeSource` seam. Desktop tests read a window the test creates.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Uia/UiaValues.cs`, `UiaTreeSource.cs`
- Create: `windows/src/DesktopMcp.Desktop/Tree/TreeSource.cs`
- Create: `windows/tests/DesktopMcp.Desktop.Tests/TestWindow.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Uia/UiaTreeSourceTests.cs` (one class Category=Desktop)

**Interfaces:**
- Consumes: `UiaSession` (Task 1), `ScreenRect` (Task 2), `ComErrors` (Task 5), `UiaWorker<UiaSession>` (Task 10, in the Desktop tests), `TestDpi` (Task 8).
- Produces:
  - `internal static unsafe class UiaValues`: `string Take(BSTR)`, `ScreenRect Rect(RECT)`, `int? CachedInt(...)`, `double? CachedDouble(...)`, `bool? CachedBool(...)`, `string? CachedString(...)` (each `(IUIAutomationElement, UIA_PROPERTY_ID)`), `string? CachedRuntimeId(IUIAutomationElement)`, `int[] ReadInts(SAFEARRAY*)`, `double[] ReadDoubles(SAFEARRAY*)`, `void Release(object?)`.
  - namespace `DesktopMcp.Desktop.Tree`: `internal sealed record UiaElementData` (init properties `Name`, `AutomationId`, `LocalizedControlType`, `AcceleratorKey`, `ClassName`, `ControlType`, `RuntimeId`, `IsOffscreen`, `IsControlElement` (default true), `IsEnabled` (default true), `HasKeyboardFocus`, `IsKeyboardFocusable`, `IsPassword`, `Bounds`, `LegacyRole`, `LegacyValue`, `LegacyDefaultAction`, `HorizontallyScrollable`, `VerticallyScrollable`, `HorizontalScrollPercent`, `VerticalScrollPercent`, `ExpandCollapseState`, `ToggleState`, `WindowIsModal`, `RangeValue`, `RangeMinimum`, `RangeMaximum`, `object? Handle`); `internal readonly record struct WordBox(string Word, ScreenRect Box)`; `internal interface ITreeSource { IReadOnlyList<UiaElementData> GetChildren(UiaElementData); IReadOnlyList<WordBox> GetWords(UiaElementData, int maxWords); void Release(UiaElementData); }`.
  - namespace `DesktopMcp.Desktop.Uia`: `internal sealed record WindowProbe(string Name, int ControlType, string ClassName, ScreenRect Bounds, int ProcessId, bool HasWindowPattern, bool CanMinimize, bool CanMaximize, bool HasChildren)`; `internal sealed unsafe class UiaTreeSource(UiaSession) : ITreeSource` with `const int MaxChildren = 10_000`, `const int MaxWordSteps = 10_000`, `UiaElementData ReadWindow(long window)`, `WindowProbe ProbeWindow(long window)`, internal static `ShrinkToFontSize(ScreenRect, double points, int dpi)`, `ToRects(double[])`, `Read(IUIAutomationElement)`.
  - Test helper `TestWindow` (`Title`, `Handle`, `ButtonText` = "Press me", `EditText` = "hello world"; `IDisposable`).

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/TestWindow.cs`:

```csharp
using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// A top-level window the desktop tests own, so they never read or touch the user's applications: a dialog-class
/// window with minimize and maximize boxes, a "Press me" button and an edit holding "hello world". It runs its own
/// message loop on an STA thread and is destroyed on Dispose.
/// </summary>
internal sealed partial class TestWindow : IDisposable
{
    public const string ButtonText = "Press me";
    public const string EditText = "hello world";

    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const uint WsExClientEdge = 0x00000200;
    private const uint WmApp = 0x8000;

    private readonly Thread _thread;
    private readonly TaskCompletionSource<(nint Window, uint ThreadId)> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestWindow(int left = 120, int top = 120, int width = 420, int height = 260)
    {
        TestDpi.EnsurePerMonitorV2();
        Title = $"desktop-mcp test {Guid.NewGuid():N}";
        _thread = new Thread(() => Run(left, top, width, height)) { IsBackground = true, Name = "test window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        var (window, threadId) = _created.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Handle = window;
        ThreadId = threadId;
    }

    public string Title { get; }
    public nint Handle { get; }
    private uint ThreadId { get; }

    public void Dispose()
    {
        PostThreadMessageW(ThreadId, WmApp, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(10));
    }

    private void Run(int left, int top, int width, int height)
    {
        try
        {
            var module = GetModuleHandleW(null);
            var window = CreateWindowExW(0, "#32770", Title, WsOverlappedWindow | WsVisible, left, top, width, height, 0, 0, module, 0);
            if (window == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
            CreateWindowExW(0, "BUTTON", ButtonText, WsChild | WsVisible, 20, 20, 120, 32, window, 1, module, 0);
            CreateWindowExW(WsExClientEdge, "EDIT", EditText, WsChild | WsVisible, 20, 70, 300, 28, window, 2, module, 0);
            _created.SetResult((window, GetCurrentThreadId()));
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == WmApp && message.Window == 0) break;
                TranslateMessage(in message);
                DispatchMessageW(in message);
            }
            DestroyWindow(window);
        }
        catch (Exception e)
        {
            _created.TrySetException(e);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Msg message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in Msg message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);
}
```

`windows/tests/DesktopMcp.Desktop.Tests/Uia/UiaTreeSourceTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tests.Uia;

public class UiaTreeSourceTests
{
    [Fact]
    public void ShrinksALineBoxToTheFontHeightOnItsBottomEdge()
    {
        var line = new ScreenRect(10, 100, 60, 130);
        Assert.Equal(new ScreenRect(10, 114, 60, 130), UiaTreeSource.ShrinkToFontSize(line, 12, 96));
        Assert.Equal(line, UiaTreeSource.ShrinkToFontSize(line, 40, 96));
        Assert.Equal(line, UiaTreeSource.ShrinkToFontSize(line, 0, 96));
    }

    [Fact]
    public void TextRangeRectanglesTruncateLikePythonInt() =>
        Assert.Equal([new ScreenRect(10, 20, 40, 32), new ScreenRect(-3, 0, 2, 9)],
            UiaTreeSource.ToRects([10.9, 20.2, 30.7, 12.9, -3.5, 0.4, 5.9, 9.1]));
}

[Trait("Category", "Desktop")]
public sealed class UiaTreeSourceDesktopTests : IDisposable
{
    private readonly TestWindow _window = new();
    private readonly UiaWorker<UiaSession> _worker = new(UiaSession.Create);

    public void Dispose()
    {
        _worker.Dispose();
        _window.Dispose();
    }

    private Task<T> Run<T>(Func<UiaTreeSource, T> job) =>
        _worker.RunAsync(session => job(new UiaTreeSource(session)), TimeSpan.FromSeconds(20), default);

    [Fact]
    public async Task ProbesTheTestWindow()
    {
        var probe = await Run(source => source.ProbeWindow(_window.Handle));
        Assert.Equal(_window.Title, probe.Name);
        Assert.Equal(50032, probe.ControlType); // UIA_WindowControlTypeId
        Assert.Equal(Environment.ProcessId, probe.ProcessId);
        Assert.True(probe.HasWindowPattern && probe.CanMinimize && probe.CanMaximize);
        Assert.True(probe.Bounds.Width > 0 && probe.Bounds.Height > 0);
    }

    [Fact]
    public async Task ReadsTheWindowAndItsCachedChildren()
    {
        var (window, children) = await Run(source =>
        {
            var root = source.ReadWindow(_window.Handle);
            return (root, source.GetChildren(root));
        });
        Assert.Equal(_window.Title, window.Name);
        Assert.NotNull(window.RuntimeId);
        var button = Assert.Single(children, c => c.Name == TestWindow.ButtonText);
        Assert.Equal(50000, button.ControlType); // UIA_ButtonControlTypeId
        Assert.Equal(0x2B, button.LegacyRole);    // ROLE_SYSTEM_PUSHBUTTON
        var edit = Assert.Single(children, c => c.ControlType == 50004); // UIA_EditControlTypeId
        Assert.Equal(TestWindow.EditText, edit.LegacyValue);
    }

    [Fact]
    public async Task ReadsTheVisibleWordsOfTheEdit()
    {
        var words = await Run(source =>
        {
            var root = source.ReadWindow(_window.Handle);
            var edit = source.GetChildren(root).Single(c => c.ControlType == 50004);
            return source.GetWords(edit, maxWords: 100);
        });
        Assert.Equal(["hello", "world"], words.Select(w => w.Word));
        Assert.All(words, w => Assert.True(w.Box.Width > 0 && w.Box.Height > 0));
    }

    [Fact]
    public async Task StopsAtTheWordLimit()
    {
        var words = await Run(source =>
        {
            var root = source.ReadWindow(_window.Handle);
            return source.GetWords(source.GetChildren(root).Single(c => c.ControlType == 50004), maxWords: 1);
        });
        Assert.Equal("hello", Assert.Single(words).Word);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0234` (`DesktopMcp.Desktop.Tree` does not exist) and `CS0246` (`UiaTreeSource`).

- [ ] **Step 3: Implement**

`windows/src/DesktopMcp.Desktop/Uia/UiaValues.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using DesktopMcp.Core.Snapshots;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Desktop.Uia;

/// <summary>
/// Turns UIA's BSTRs, VARIANTs and SAFEARRAYs into plain values and frees them (spec §6: every BSTR is freed and every
/// ComVariant disposed before the wrapper returns).
/// </summary>
internal static unsafe class UiaValues
{
    /// <summary>Copies a BSTR the callee allocated and frees it; null becomes "".</summary>
    public static string Take(BSTR value)
    {
        try { return value.ToString() ?? ""; }
        finally { PInvoke.SysFreeString(value); }
    }

    public static ScreenRect Rect(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);

    /// <summary>A cached VT_I4 property (enums such as the LegacyIAccessible role and the toggle state), else null.</summary>
    public static int? CachedInt(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        using var value = element.GetCachedPropertyValue(property);
        return value.VarType == VarEnum.VT_I4 ? value.As<int>() : null;
    }

    /// <summary>A cached VT_R8 property (scroll percentages, range values), else null.</summary>
    public static double? CachedDouble(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        using var value = element.GetCachedPropertyValue(property);
        return value.VarType switch
        {
            VarEnum.VT_R8 => value.As<double>(),
            VarEnum.VT_I4 => value.As<int>(),
            _ => null,
        };
    }

    /// <summary>A cached VT_BOOL property, else null.</summary>
    public static bool? CachedBool(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        using var value = element.GetCachedPropertyValue(property);
        return value.VarType == VarEnum.VT_BOOL ? value.As<bool>() : null;
    }

    /// <summary>A cached VT_BSTR property, else null (the "not supported" sentinel is a VT_UNKNOWN).</summary>
    public static string? CachedString(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        using var value = element.GetCachedPropertyValue(property);
        return value.VarType == VarEnum.VT_BSTR ? value.As<string>() : null;
    }

    /// <summary>The cached RuntimeId as "a.b.c" (the cycle-guard key), or null when the provider gave none.</summary>
    public static string? CachedRuntimeId(IUIAutomationElement element)
    {
        using var value = element.GetCachedPropertyValue(UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId);
        if (value.VarType != (VarEnum.VT_ARRAY | VarEnum.VT_I4)) return null;
        var parts = ReadInts((SAFEARRAY*)value.GetRawDataRef<nint>());
        return parts.Length == 0 ? null : string.Join('.', parts);
    }

    /// <summary>Reads a one-dimensional SAFEARRAY of VT_I4 without taking ownership.</summary>
    public static int[] ReadInts(SAFEARRAY* array) => Read<int>(array);

    /// <summary>Reads a one-dimensional SAFEARRAY of VT_R8 without taking ownership.</summary>
    public static double[] ReadDoubles(SAFEARRAY* array) => Read<double>(array);

    private static T[] Read<T>(SAFEARRAY* array) where T : unmanaged
    {
        if (array is null) return [];
        PInvoke.SafeArrayGetLBound(array, 1, out var lower).ThrowOnFailure();
        PInvoke.SafeArrayGetUBound(array, 1, out var upper).ThrowOnFailure();
        var count = upper - lower + 1;
        if (count <= 0) return [];
        void* data;
        PInvoke.SafeArrayAccessData(array, &data).ThrowOnFailure();
        try { return new ReadOnlySpan<T>(data, count).ToArray(); }
        finally { PInvoke.SafeArrayUnaccessData(array); }
    }

    /// <summary>Returns a COM object's reference now instead of at the next GC (the walk creates many elements).</summary>
    public static void Release(object? comObject)
    {
        if (comObject is ComObject wrapper) wrapper.FinalRelease();
    }
}
```

`windows/src/DesktopMcp.Desktop/Tree/TreeSource.cs`:

```csharp
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Desktop.Tree;

/// <summary>
/// One UIA element's cached properties, read once into plain values (spec §6). <see cref="Handle"/> is the COM element
/// the source needs for children and words; tests leave it null.
/// </summary>
internal sealed record UiaElementData
{
    public string Name { get; init; } = "";
    public string AutomationId { get; init; } = "";
    public string LocalizedControlType { get; init; } = "";
    public string AcceleratorKey { get; init; } = "";
    public string ClassName { get; init; } = "";
    public int ControlType { get; init; }

    /// <summary>The RuntimeId as "a.b.c", or null when the provider gave none.</summary>
    public string? RuntimeId { get; init; }

    public bool IsOffscreen { get; init; }
    public bool IsControlElement { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
    public bool HasKeyboardFocus { get; init; }
    public bool IsKeyboardFocusable { get; init; }
    public bool IsPassword { get; init; }
    public ScreenRect Bounds { get; init; }
    public int? LegacyRole { get; init; }
    public string? LegacyValue { get; init; }
    public string? LegacyDefaultAction { get; init; }
    public bool HorizontallyScrollable { get; init; }
    public bool VerticallyScrollable { get; init; }
    public double? HorizontalScrollPercent { get; init; }
    public double? VerticalScrollPercent { get; init; }
    public int? ExpandCollapseState { get; init; }
    public int? ToggleState { get; init; }
    public bool? WindowIsModal { get; init; }
    public double? RangeValue { get; init; }
    public double? RangeMinimum { get; init; }
    public double? RangeMaximum { get; init; }
    public object? Handle { get; init; }
}

/// <summary>A word of a text control and one of its line boxes (upstream uia/controls.py:1267-1294).</summary>
internal readonly record struct WordBox(string Word, ScreenRect Box);

/// <summary>What the tree walk reads beyond an element's cached properties; the walker's only external dependency.</summary>
internal interface ITreeSource
{
    /// <summary>The element's children in the control view with their properties cached; empty when it is gone.</summary>
    IReadOnlyList<UiaElementData> GetChildren(UiaElementData parent);

    /// <summary>Up to <paramref name="maxWords"/> visible words with their boxes; empty without a TextPattern.</summary>
    IReadOnlyList<WordBox> GetWords(UiaElementData element, int maxWords);

    /// <summary>The walk is done with the element: its COM reference can go.</summary>
    void Release(UiaElementData element);
}
```

`windows/src/DesktopMcp.Desktop/Uia/UiaTreeSource.cs`:

```csharp
using System.Runtime.InteropServices.Marshalling;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Desktop.Uia;

/// <summary>What the window list needs from a top-level window (upstream desktop/service.py:1054-1066 and 1192-1245).</summary>
internal sealed record WindowProbe(
    string Name, int ControlType, string ClassName, ScreenRect Bounds, int ProcessId,
    bool HasWindowPattern, bool CanMinimize, bool CanMaximize, bool HasChildren);

/// <summary>
/// <see cref="ITreeSource"/> over a <see cref="UiaSession"/>. Runs only on the UIA worker thread that owns the session.
/// </summary>
internal sealed unsafe class UiaTreeSource(UiaSession session) : ITreeSource
{
    /// <summary>The most children read from one element; the walk visits the last ones first (Decisions 9).</summary>
    public const int MaxChildren = 10_000;

    /// <summary>The most word moves through one control's text, so a provider that never advances cannot spin forever.</summary>
    public const int MaxWordSteps = 10_000;

    /// <summary>The top-level window with the traversal properties cached.</summary>
    public UiaElementData ReadWindow(long window) =>
        Read(session.Automation.ElementFromHandleBuildCache((HWND)(nint)window, session.ElementRequest));

    /// <summary>The window-list properties of a top-level window.</summary>
    public WindowProbe ProbeWindow(long window)
    {
        var element = session.Automation.ElementFromHandleBuildCache((HWND)(nint)window, session.ProbeRequest);
        try
        {
            var name = UiaValues.Take(element.get_CachedName());
            // upstream desktop/service.py:1063-1066: only a nameless window is checked for children.
            var hasChildren = name.Trim().Length > 0 || session.ControlView.GetFirstChildElement(element) is not null;
            return new WindowProbe(
                name,
                (int)element.get_CachedControlType(),
                UiaValues.Take(element.get_CachedClassName()),
                UiaValues.Rect(element.get_CachedBoundingRectangle()),
                element.get_CachedProcessId(),
                UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_IsWindowPatternAvailablePropertyId) ?? false,
                UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_WindowCanMinimizePropertyId) ?? false,
                UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_WindowCanMaximizePropertyId) ?? false,
                hasChildren);
        }
        finally
        {
            UiaValues.Release(element);
        }
    }

    public IReadOnlyList<UiaElementData> GetChildren(UiaElementData parent)
    {
        var element = Element(parent);
        try
        {
            var children = element.BuildUpdatedCache(session.ChildrenRequest).GetCachedChildren();
            if (children is null) return [];
            var count = children.get_Length();
            var result = new List<UiaElementData>(Math.Min(count, MaxChildren));
            for (var i = Math.Max(0, count - MaxChildren); i < count; i++) result.Add(Read(children.GetElement(i)));
            return result;
        }
        catch (Exception e) when (ComErrors.IsDeadElement(e))
        {
            return [];
        }
        catch (Exception e) when (!ComErrors.IsTimeout(e))
        {
            // upstream tree/cache_utils.py:201-213: fall back to walking the control view one sibling at a time.
            try { return WalkChildren(element); }
            catch (Exception fallback) when (ComErrors.IsDeadElement(fallback)) { return []; }
        }
    }

    // upstream uia/controls.py:1240-1294 GetAllWordBoundingBoxes and _iter_word_ranges.
    public IReadOnlyList<WordBox> GetWords(UiaElementData element, int maxWords)
    {
        var words = new List<WordBox>();
        if (maxWords <= 0) return words;
        var iid = typeof(IUIAutomationTextPattern).GUID;
        Element(element).GetCurrentPatternAs(UIA_PATTERN_ID.UIA_TextPatternId, &iid, out var raw);
        if (raw is null) return words;
        var pattern = ComInterfaceMarshaller<IUIAutomationTextPattern>.ConvertToManaged(raw)!;
        ComInterfaceMarshaller<IUIAutomationTextPattern>.Free(raw);

        var dpi = (int)PInvoke.GetDpiForSystem();
        if (dpi <= 0) dpi = 96;
        var uniformSize = FontSize(pattern.get_DocumentRange());
        var ranges = pattern.GetVisibleRanges();
        var steps = 0;
        for (var r = 0; ranges is not null && r < ranges.get_Length(); r++)
        {
            var visible = ranges.GetElement(r);
            var word = visible.Clone();
            word.ExpandToEnclosingUnit(TextUnit.TextUnit_Word);
            while (word.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, visible,
                       TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) < 0)
            {
                if (++steps > MaxWordSteps || words.Count >= maxWords) return words;
                var text = UiaValues.Take(word.GetText(-1)).Trim();
                if (text.Length > 0)
                {
                    var size = uniformSize ?? FontSize(word);
                    foreach (var box in BoundingRectangles(word))
                        words.Add(new WordBox(text, size is { } points ? ShrinkToFontSize(box, points, dpi) : box));
                }
                if (word.Move(TextUnit.TextUnit_Word, 1) == 0) break;
            }
        }
        return words;
    }

    public void Release(UiaElementData element) => UiaValues.Release(element.Handle);

    /// <summary>upstream uia/controls.py:1396-1411: the line box shrunk to the font's pixel height, kept on its bottom edge.</summary>
    internal static ScreenRect ShrinkToFontSize(ScreenRect box, double points, int dpi)
    {
        var pixels = points * dpi / 72.0;
        if (pixels <= 0 || pixels >= box.Height) return box;
        return box with { Top = (int)Math.Round(box.Bottom - pixels, MidpointRounding.ToEven) };
    }

    /// <summary>upstream uia/patterns.py:1578-1599: left, top, width, height doubles, each truncated to int.</summary>
    internal static IReadOnlyList<ScreenRect> ToRects(double[] values)
    {
        var rects = new List<ScreenRect>(values.Length / 4);
        for (var i = 0; i + 3 < values.Length; i += 4)
        {
            var left = (int)values[i];
            var top = (int)values[i + 1];
            rects.Add(new ScreenRect(left, top, left + (int)values[i + 2], top + (int)values[i + 3]));
        }
        return rects;
    }

    private static IReadOnlyList<ScreenRect> BoundingRectangles(IUIAutomationTextRange range)
    {
        var array = range.GetBoundingRectangles();
        try { return ToRects(UiaValues.ReadDoubles(array)); }
        finally { if (array is not null) PInvoke.SafeArrayDestroy(array); }
    }

    private static double? FontSize(IUIAutomationTextRange range)
    {
        using var value = range.GetAttributeValue(UIA_TEXTATTRIBUTE_ID.UIA_FontSizeAttributeId);
        return value.VarType switch
        {
            System.Runtime.InteropServices.VarEnum.VT_R8 => value.As<double>(),
            System.Runtime.InteropServices.VarEnum.VT_I4 => value.As<int>(),
            _ => null, // mixed or not supported
        };
    }

    private IReadOnlyList<UiaElementData> WalkChildren(IUIAutomationElement element)
    {
        var result = new List<UiaElementData>();
        var child = session.ControlView.GetFirstChildElementBuildCache(element, session.ElementRequest);
        while (child is not null && result.Count < MaxChildren)
        {
            result.Add(Read(child));
            child = session.ControlView.GetNextSiblingElementBuildCache(child, session.ElementRequest);
        }
        return result;
    }

    private static IUIAutomationElement Element(UiaElementData data) =>
        data.Handle as IUIAutomationElement ?? throw new InvalidOperationException("The element has no UIA handle.");

    // upstream tree/service.py reads these through Cached* accessors and GetCachedPropertyValue.
    internal static UiaElementData Read(IUIAutomationElement element) => new()
    {
        Name = UiaValues.Take(element.get_CachedName()),
        AutomationId = UiaValues.Take(element.get_CachedAutomationId()),
        LocalizedControlType = UiaValues.Take(element.get_CachedLocalizedControlType()),
        AcceleratorKey = UiaValues.Take(element.get_CachedAcceleratorKey()),
        ClassName = UiaValues.Take(element.get_CachedClassName()),
        ControlType = (int)element.get_CachedControlType(),
        RuntimeId = UiaValues.CachedRuntimeId(element),
        IsOffscreen = element.get_CachedIsOffscreen(),
        IsControlElement = element.get_CachedIsControlElement(),
        IsEnabled = element.get_CachedIsEnabled(),
        HasKeyboardFocus = element.get_CachedHasKeyboardFocus(),
        IsKeyboardFocusable = element.get_CachedIsKeyboardFocusable(),
        IsPassword = element.get_CachedIsPassword(),
        Bounds = UiaValues.Rect(element.get_CachedBoundingRectangle()),
        LegacyRole = UiaValues.CachedInt(element, UIA_PROPERTY_ID.UIA_LegacyIAccessibleRolePropertyId),
        LegacyValue = UiaValues.CachedString(element, UIA_PROPERTY_ID.UIA_LegacyIAccessibleValuePropertyId),
        LegacyDefaultAction = UiaValues.CachedString(element, UIA_PROPERTY_ID.UIA_LegacyIAccessibleDefaultActionPropertyId),
        HorizontallyScrollable = UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_ScrollHorizontallyScrollablePropertyId) ?? false,
        VerticallyScrollable = UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_ScrollVerticallyScrollablePropertyId) ?? false,
        HorizontalScrollPercent = UiaValues.CachedDouble(element, UIA_PROPERTY_ID.UIA_ScrollHorizontalScrollPercentPropertyId),
        VerticalScrollPercent = UiaValues.CachedDouble(element, UIA_PROPERTY_ID.UIA_ScrollVerticalScrollPercentPropertyId),
        ExpandCollapseState = UiaValues.CachedInt(element, UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId),
        ToggleState = UiaValues.CachedInt(element, UIA_PROPERTY_ID.UIA_ToggleToggleStatePropertyId),
        WindowIsModal = UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_WindowIsModalPropertyId),
        RangeValue = UiaValues.CachedDouble(element, UIA_PROPERTY_ID.UIA_RangeValueValuePropertyId),
        RangeMinimum = UiaValues.CachedDouble(element, UIA_PROPERTY_ID.UIA_RangeValueMinimumPropertyId),
        RangeMaximum = UiaValues.CachedDouble(element, UIA_PROPERTY_ID.UIA_RangeValueMaximumPropertyId),
        Handle = element,
    };
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~UiaTreeSourceTests"`
Expected: `Passed!` with `Failed: 0` (2 tests).

Run (interactive session; a small "desktop-mcp test" window appears for a moment): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~UiaTreeSourceDesktopTests"`
Expected: `Passed!` with `Failed: 0` (4 tests): the probe, the cached children with the button's role 0x2B and the edit's value, and the words `hello` and `world` read through TextPattern.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Uia/UiaValues.cs windows/src/DesktopMcp.Desktop/Uia/UiaTreeSource.cs windows/src/DesktopMcp.Desktop/Tree/TreeSource.cs windows/tests/DesktopMcp.Desktop.Tests/TestWindow.cs windows/tests/DesktopMcp.Desktop.Tests/Uia/UiaTreeSourceTests.cs
git commit -m "Read UIA elements, children and words into plain records through the cache request" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 12: Tree classification, budget and the iterative walker

upstream `tree/service.py:446-909` (tree_traversal), `tree/config.py`, `tree/budget.py` and `tree/views.py:157-169`, without recursion (Decisions 9). The walker classifies each element (scrollable, interactive, structural, word elements, modal reset, cycle guard), fills the window's interactive and scrollable lists in upstream's right-to-left visit order, and returns the window's semantic node in document order. It depends only on `ITreeSource`, so all of this is tested with in-memory trees.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Tree/TreeConfig.cs`, `ElementBudget.cs`, `TreeWalker.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Tree/ElementBudgetTests.cs`, `TreeWalkerTests.cs`

**Interfaces:**
- Consumes: `UiaElementData`, `WordBox`, `ITreeSource` (Task 11); `UiElement`, `UiMetadata`, `SemanticNode`, `SemanticKind`, `SemanticTree.ReverseChildren`, `ScreenRect`, `PyText.FloatRepr/Round/Capitalize` (Task 2); `ComErrors.IsTimeout` (Task 5).
- Produces (namespace `DesktopMcp.Desktop.Tree`, internal):
  - `static class TreeConfig`: control type constants (`Button` 50000, `CheckBox`, `ComboBox`, `Edit`, `Image`, `ListItem`, `RadioButton`, `Slider`, `TabItem`, `Group`, `DataItem`, `Document`, `Window` 50032, `Pane` 50033), `string ControlTypeName(int)`, the sets `InteractiveControlTypes`, `DocumentControlTypes`, `StructuralControlTypes`, `InformativeControlTypes`, `InteractiveRoles`, `DefaultActions`, `FocusableControlTypes`, `bool IsBrowser(string? processName)`, `string WindowName(string name, string className)`, `string Title(string)`.
  - `sealed class ElementBudget(int limit, int maxVisits)` and `ElementBudget(int limit)` (50 visits per element): `Limit`, `Count`, `Truncated`, `Exhausted`, `Remaining`, `bool TryConsume(int amount = 1)`, `bool Visit()`, `const int VisitsPerElement = 50`.
  - `sealed record WindowContext(string WindowName, bool IsBrowser, ScreenRect WindowBox, ScreenRect ScreenBox)`; `sealed class WindowWalk(SemanticNode window)` with `List<UiElement> Interactive`, `List<UiElement> Scrollable`, `SemanticNode Window`.
  - `sealed class TreeWalker(ITreeSource source, ElementBudget budget, Func<int> nextId, int maxDepth = 256)`: `const int MaxDepth = 256`, `WindowWalk Walk(UiaElementData root, WindowContext context)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/Tree/ElementBudgetTests.cs`:

```csharp
using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Tests.Tree;

public class ElementBudgetTests
{
    [Fact]
    public void ReachingTheLimitTruncatesAndFurtherConsumesFail()
    {
        var budget = new ElementBudget(2);
        Assert.True(budget.TryConsume());
        Assert.False(budget.Truncated);
        Assert.True(budget.TryConsume());
        Assert.True(budget.Truncated && budget.Exhausted);
        Assert.False(budget.TryConsume());
        Assert.Equal(2, budget.Count);
    }

    [Fact]
    public void AnOversizedAmountIsCappedAtTheLimit()
    {
        var budget = new ElementBudget(5);
        Assert.False(budget.TryConsume(9));
        Assert.Equal((5, true), (budget.Count, budget.Truncated));
    }

    [Fact]
    public void TheLimitIsAtLeastOneAndVisitsAreCappedAt50PerElement()
    {
        var budget = new ElementBudget(0);
        Assert.Equal(1, budget.Limit);
        for (var i = 0; i < 49; i++) Assert.True(budget.Visit());
        Assert.False(budget.Exhausted);
        Assert.True(budget.Visit());
        Assert.True(budget.Exhausted && budget.Truncated);
        Assert.False(budget.Visit());
    }
}
```

`windows/tests/DesktopMcp.Desktop.Tests/Tree/TreeWalkerTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Tests.Tree;

public class TreeWalkerTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);
    private static readonly WindowContext App = new("App", IsBrowser: false, new ScreenRect(0, 0, 800, 600), Screen);

    /// <summary>An in-memory UIA tree; children and words are keyed by element instance.</summary>
    private sealed class FakeSource : ITreeSource
    {
        public Dictionary<UiaElementData, UiaElementData[]> Children { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<UiaElementData, WordBox[]> Words { get; } = new(ReferenceEqualityComparer.Instance);
        public int Released { get; private set; }
        public int Fetched { get; private set; }

        public IReadOnlyList<UiaElementData> GetChildren(UiaElementData parent)
        {
            var children = Children.GetValueOrDefault(parent, []);
            Fetched += children.Length;
            return children;
        }

        public IReadOnlyList<WordBox> GetWords(UiaElementData element, int maxWords) => Words.GetValueOrDefault(element, []).Take(maxWords).ToArray();

        public void Release(UiaElementData element) => Released++;

        public UiaElementData Add(UiaElementData parent, params UiaElementData[] children)
        {
            Children[parent] = children;
            return parent;
        }
    }

    private static int _ids;

    private static UiaElementData Window(string name = "App") => new() { Name = name, ControlType = TreeConfig.Window, LocalizedControlType = "window", Bounds = new ScreenRect(0, 0, 800, 600), RuntimeId = $"w{++_ids}" };

    private static UiaElementData Button(string name, int x = 10) => new()
    {
        Name = name, ControlType = TreeConfig.Button, LocalizedControlType = "button", LegacyRole = 0x2B,
        Bounds = new ScreenRect(x, 10, x + 50, 30), RuntimeId = $"b{++_ids}",
    };

    private static UiaElementData Pane(string name, bool scrollable = false) => new()
    {
        Name = name, ControlType = TreeConfig.Pane, LocalizedControlType = "pane", Bounds = new ScreenRect(0, 0, 400, 300),
        VerticallyScrollable = scrollable, VerticalScrollPercent = 33.333, RuntimeId = $"p{++_ids}",
    };

    private static (WindowWalk Walk, ElementBudget Budget) Walk(FakeSource source, UiaElementData root, int limit = 500, WindowContext? context = null, int maxDepth = TreeWalker.MaxDepth)
    {
        var budget = new ElementBudget(limit);
        var next = 0;
        return (new TreeWalker(source, budget, () => next++, maxDepth).Walk(root, context ?? App), budget);
    }

    [Fact]
    public void ChildrenAreNumberedRightToLeftButTheTreeKeepsDocumentOrder()
    {
        var source = new FakeSource();
        var root = source.Add(Window(), Button("A", 10), Button("B", 100));
        var (walk, _) = Walk(source, root);
        Assert.Equal(["B", "A"], walk.Interactive.Select(e => e.Name));
        Assert.Equal(["A", "B"], walk.Window.Children.Select(c => c.Name));
        Assert.Equal(new ScreenPoint(35, 20), walk.Interactive[1].Center);
        Assert.Equal(3, source.Released);
    }

    [Fact]
    public void ElementsThatAreHiddenDisabledEmptyOrNotInteractiveAreLeftOut()
    {
        var source = new FakeSource();
        var root = source.Add(Window(),
            Button("Offscreen") with { IsOffscreen = true },
            Button("Disabled") with { IsEnabled = false },
            Button("Flat") with { Bounds = new ScreenRect(10, 10, 60, 10) },
            Button("No role") with { LegacyRole = 0x0A },
            Button("   "),
            Button("Kept"));
        Assert.Equal(["Kept"], Walk(source, root).Walk.Interactive.Select(e => e.Name));
    }

    [Fact]
    public void AnOffscreenEditIsStillListedWithItsValue()
    {
        var source = new FakeSource();
        var edit = new UiaElementData
        {
            Name = "Search", ControlType = TreeConfig.Edit, LocalizedControlType = "edit", IsOffscreen = true, LegacyRole = 0x2A,
            LegacyValue = "  query ", Bounds = new ScreenRect(0, 0, 100, 20), HasKeyboardFocus = true,
        };
        var element = Assert.Single(Walk(source, source.Add(Window(), edit)).Walk.Interactive);
        Assert.Equal(new UiMetadata { HasFocus = true, Value = "query" }, element.Metadata);
    }

    [Fact]
    public void ScrollablePanesAreListedAtTheirCentreAndDoNotBecomeStructuralNodes()
    {
        var source = new FakeSource();
        var pane = source.Add(Pane("", scrollable: true) with { AutomationId = "", LocalizedControlType = "PANE" }, Button("Inside"));
        var (walk, _) = Walk(source, source.Add(Window(), pane));
        var scrollable = Assert.Single(walk.Scrollable);
        Assert.Equal(("Pane", new ScreenPoint(200, 150)), (scrollable.Name, scrollable.Center));
        Assert.Equal(new UiMetadata { VerticallyScrollable = true, VerticalScrollPercent = 33.33 }, scrollable.Metadata);
        // The pane's children hang off the window, not the pane, and the window's children come back in reverse visit order.
        Assert.Equal([SemanticKind.Interactive, SemanticKind.Scrollable], walk.Window.Children.Select(c => c.Kind));
    }

    [Fact]
    public void NamedContainersGroupTheirChildren()
    {
        var source = new FakeSource();
        var toolbar = source.Add(new UiaElementData { Name = "Tools", ControlType = 50021, LocalizedControlType = "tool bar", Bounds = new ScreenRect(0, 0, 10, 10) }, Button("Save"));
        var unnamed = source.Add(Pane(""), Button("Open"));
        var (walk, _) = Walk(source, source.Add(Window(), toolbar, unnamed));
        Assert.Equal(["Tools", "Open"], walk.Window.Children.Select(c => c.Name));
        Assert.Equal("Save", Assert.Single(walk.Window.Children[0].Children).Name);
    }

    [Fact]
    public void AModalDialogDiscardsTheElementsFoundBeforeIt()
    {
        var source = new FakeSource();
        var dialog = source.Add(Window("Confirm") with { WindowIsModal = true }, Button("Yes"));
        // Visited right to left: "Behind" first, then the dialog.
        var (walk, _) = Walk(source, source.Add(Window(), dialog, Button("Behind")));
        Assert.Equal(["Yes"], walk.Interactive.Select(e => e.Name));
    }

    [Fact]
    public void ABranchRepeatingAnAncestorsRuntimeIdIsCut()
    {
        var source = new FakeSource();
        var root = Window();
        var pane = Pane("Loop");
        source.Add(root, pane);
        source.Add(pane, Button("OK"), root with { Name = "Again" });
        var (walk, _) = Walk(source, root);
        Assert.Equal(["OK"], walk.Interactive.Select(e => e.Name));
    }

    [Fact]
    public void ElementsBelowTheDepthCapAreNotVisited()
    {
        var source = new FakeSource();
        var root = Window();
        var current = root;
        for (var depth = 1; depth <= 10; depth++)
        {
            var next = depth == 10 ? Button("Deep") : Pane("");
            source.Add(current, next);
            current = next;
        }
        Assert.Empty(Walk(source, root, maxDepth: 5).Walk.Interactive);
        Assert.Single(Walk(source, root, maxDepth: 10).Walk.Interactive);
    }

    [Fact]
    public void AVeryDeepTreeDoesNotOverflowTheStack()
    {
        var source = new FakeSource();
        var root = Window();
        var current = root;
        for (var depth = 0; depth < 100_000; depth++)
        {
            var next = Pane("");
            source.Add(current, next);
            current = next;
        }
        var (walk, _) = Walk(source, root, maxDepth: int.MaxValue);
        Assert.Empty(walk.Interactive);
    }

    [Fact]
    public void TheBudgetStopsTheWalkAndMarksItTruncated()
    {
        var source = new FakeSource();
        var root = source.Add(Window(), Enumerable.Range(0, 5).Select(i => Button($"B{i}", i * 60)).ToArray());
        var (walk, budget) = Walk(source, root, limit: 2);
        Assert.Equal(["B4", "B3"], walk.Interactive.Select(e => e.Name));
        Assert.True(budget.Truncated);
        Assert.Equal(source.Fetched + 1, source.Released);
    }

    [Fact]
    public void TheVisitCapStopsATreeWithoutOutputElements()
    {
        var source = new FakeSource();
        var root = source.Add(Window(), Enumerable.Range(0, 100).Select(_ => Pane("")).ToArray());
        var budget = new ElementBudget(limit: 500, maxVisits: 10);
        var next = 0;
        new TreeWalker(source, budget, () => next++).Walk(root, App);
        Assert.True(budget.Truncated);
        Assert.True(budget.Exhausted);
    }

    [Fact]
    public void TextControlsAddOneWordElementPerWordBox()
    {
        var source = new FakeSource();
        var edit = new UiaElementData
        {
            Name = "Body", ControlType = TreeConfig.Edit, LocalizedControlType = "edit", LegacyRole = 0x2A, Bounds = new ScreenRect(0, 0, 300, 40),
        };
        source.Words[edit] = [new WordBox("hello", new ScreenRect(5, 5, 40, 20)), new WordBox("away", new ScreenRect(900, 5, 950, 20)), new WordBox("world", new ScreenRect(45, 5, 90, 20))];
        var (walk, _) = Walk(source, source.Add(Window(), edit));
        Assert.Equal(["Body", "hello", "world"], walk.Interactive.Select(e => e.Name));
        Assert.Equal("Word", walk.Interactive[1].ControlType);
        Assert.Equal(["Body", "hello", "world"], walk.Window.Children.Select(c => c.Name).Reverse());
    }

    [Fact]
    public void PasswordFieldsHideTheirWords()
    {
        var source = new FakeSource();
        var password = new UiaElementData
        {
            Name = "Password", ControlType = TreeConfig.Edit, LocalizedControlType = "edit", LegacyRole = 0x2A, IsPassword = true,
            Bounds = new ScreenRect(0, 0, 300, 40),
        };
        source.Words[password] = [new WordBox("secret", new ScreenRect(5, 5, 40, 20))];
        var element = Assert.Single(Walk(source, source.Add(Window(), password)).Walk.Interactive);
        Assert.True(element.Metadata.IsPassword);
    }

    [Fact]
    public void ControlStateMetadata()
    {
        var source = new FakeSource();
        var toggle = Button("Bold") with { ToggleState = 1, AcceleratorKey = "Ctrl+B" };
        var combo = new UiaElementData
        {
            Name = "Font", ControlType = TreeConfig.ComboBox, LocalizedControlType = "combo box", LegacyRole = 0x2E, LegacyValue = "",
            ExpandCollapseState = 0, Bounds = new ScreenRect(0, 50, 100, 70),
        };
        var slider = new UiaElementData
        {
            Name = "Zoom", ControlType = TreeConfig.Slider, LocalizedControlType = "slider", LegacyRole = 0x33,
            RangeValue = 100, RangeMinimum = 0, RangeMaximum = 225.005, Bounds = new ScreenRect(0, 80, 100, 90),
        };
        var elements = Walk(source, source.Add(Window(), slider, combo, toggle)).Walk.Interactive.ToDictionary(e => e.Name, e => e.Metadata);
        Assert.Equal(new UiMetadata { ToggleState = "on", Shortcut = "Ctrl+B" }, elements["Bold"]);
        Assert.Equal(new UiMetadata { Value = "(empty)", ExpandCollapseState = "collapsed" }, elements["Font"]);
        Assert.Equal(new UiMetadata { Value = "100.0", Minimum = "0.0", Maximum = "225.0" }, elements["Zoom"]);
    }

    [Fact]
    public void BrowserGroupsWithAnExpandCollapseStateAreInteractive()
    {
        var source = new FakeSource();
        var group = new UiaElementData
        {
            Name = "Menu", ControlType = TreeConfig.Group, LocalizedControlType = "group", ExpandCollapseState = 3, Bounds = new ScreenRect(0, 0, 10, 10),
        };
        Assert.Empty(Walk(source, source.Add(Window(), group)).Walk.Interactive);
        Assert.Single(Walk(source, source.Add(Window(), group), context: App with { IsBrowser = true }).Walk.Interactive);
    }

    [Fact]
    public void BoxesAreClippedToTheWindowAndTheScreen()
    {
        var source = new FakeSource();
        var element = Assert.Single(Walk(source, source.Add(Window(), Button("Edge") with { Bounds = new ScreenRect(780, 10, 900, 30) })).Walk.Interactive);
        Assert.Equal((new ScreenRect(780, 10, 800, 30), new ScreenPoint(790, 20)), (element.Box, element.Center));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0246` for `TreeConfig`, `ElementBudget`, `TreeWalker`, `WindowContext` and `WindowWalk`.

- [ ] **Step 3: Implement**

`windows/src/DesktopMcp.Desktop/Tree/TreeConfig.cs`:

```csharp
namespace DesktopMcp.Desktop.Tree;

/// <summary>upstream tree/config.py, desktop/config.py and the classification constants of tree/service.py (spec §6).</summary>
internal static class TreeConfig
{
    public const int Button = 50000;
    public const int CheckBox = 50002;
    public const int ComboBox = 50003;
    public const int Edit = 50004;
    public const int Image = 50006;
    public const int ListItem = 50007;
    public const int RadioButton = 50013;
    public const int Slider = 50015;
    public const int TabItem = 50019;
    public const int Group = 50026;
    public const int DataItem = 50029;
    public const int Document = 50030;
    public const int Window = 50032;
    public const int Pane = 50033;

    // UIA_*ControlTypeId 50000-50040 in order; upstream names them "<Name>Control" (uia/enums.py ControlTypeNames).
    private static readonly string[] Names =
    [
        "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image", "ListItem", "List", "Menu", "MenuBar",
        "MenuItem", "ProgressBar", "RadioButton", "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem", "Text",
        "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid", "DataItem", "Document",
        "SplitButton", "Window", "Pane", "Header", "HeaderItem", "Table", "TitleBar", "Separator", "SemanticZoom", "AppBar",
    ];

    /// <summary>upstream uia/controls.py:521-526 CachedControlTypeName.</summary>
    public static string ControlTypeName(int controlType) =>
        controlType is >= 50000 and <= 50040 ? Names[controlType - 50000] + "Control" : "Unknown";

    /// <summary>upstream tree/config.py:3-35 (TextBoxControl never matches a real control type; kept as upstream has it).</summary>
    public static readonly IReadOnlySet<string> InteractiveControlTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ButtonControl", "ListItemControl", "MenuItemControl", "EditControl", "CheckBoxControl", "RadioButtonControl",
        "ComboBoxControl", "HyperlinkControl", "SplitButtonControl", "TabItemControl", "TreeItemControl", "DataItemControl",
        "HeaderItemControl", "TextBoxControl", "SpinnerControl", "SliderControl", "ScrollBarControl",
    };

    /// <summary>upstream tree/config.py:73.</summary>
    public static readonly IReadOnlySet<string> DocumentControlTypes = new HashSet<string>(StringComparer.Ordinal) { "DocumentControl" };

    /// <summary>upstream tree/config.py:75-82.</summary>
    public static readonly IReadOnlySet<string> StructuralControlTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "PaneControl", "GroupControl", "CustomControl", "ToolBarControl", "TabControl", "MenuBarControl",
    };

    /// <summary>upstream tree/config.py:84-96.</summary>
    public static readonly IReadOnlySet<string> InformativeControlTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "TextControl", "ImageControl", "StatusBarControl",
    };

    /// <summary>upstream tree/config.py:37-71 INTERACTIVE_ROLES, as MSAA ROLE_SYSTEM_* values (uia/enums.py:584-656).</summary>
    public static readonly IReadOnlySet<int> InteractiveRoles = new HashSet<int>
    {
        0x2B, 0x3E, 0x38, 0x39, 0x3A, 0x40, // PushButton, SplitButton, ButtonDropDown, ButtonMenu, ButtonDropDownGrid, OutlineButton
        0x1E,                               // Link
        0x2A, 0x3F, 0x32, 0x2E, 0x2F, 0x2C, 0x2D, // Text, IpAddress, HotkeyField, ComboBox, DropList, CheckButton, RadioButton
        0x0C, 0x22, 0x25,                   // MenuItem, ListItem, PageTab
        0x24,                               // OutlineItem
        0x33, 0x34, 0x31, 0x03, 0x04,       // Slider, SpinButton, Dial, ScrollBar, Grip
        0x19, 0x1A, 0x1D,                   // ColumnHeader, RowHeader, Cell
    };

    /// <summary>upstream tree/config.py:98, compared after Python's str.title().</summary>
    public static readonly IReadOnlySet<string> DefaultActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "Click", "Press", "Jump", "Check", "Uncheck", "Double Click",
    };

    /// <summary>upstream tree/service.py:551: control types treated as keyboard focusable without asking.</summary>
    public static readonly IReadOnlySet<string> FocusableControlTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "EditControl", "ButtonControl", "CheckBoxControl", "RadioButtonControl", "TabItemControl", "ListItemControl",
    };

    /// <summary>upstream desktop/config.py:3 and desktop/views.py:8-17.</summary>
    public static bool IsBrowser(string? processName) =>
        processName?.ToLowerInvariant() is "chrome.exe" or "msedge.exe" or "firefox.exe";

    /// <summary>
    /// upstream tree/service.py:911-920 app_name_correction. Upstream compares the window title, which never equals these
    /// class names for the shell windows; the fixed names apply by class name (Decisions 8).
    /// </summary>
    public static string WindowName(string name, string className) => className switch
    {
        "Progman" => "Desktop",
        "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" => "Taskbar",
        "Microsoft.UI.Content.PopupWindowSiteBridge" => "Context Menu",
        _ => name switch
        {
            "Progman" => "Desktop",
            "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" => "Taskbar",
            "Microsoft.UI.Content.PopupWindowSiteBridge" => "Context Menu",
            _ => name,
        },
    };

    /// <summary>Python's str.title() for the default action check: each word capitalised.</summary>
    public static string Title(string value)
    {
        var chars = value.ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (char.IsLetter(chars[i]) && (i == 0 || !char.IsLetter(chars[i - 1]))) chars[i] = char.ToUpperInvariant(chars[i]);
        return new string(chars);
    }
}
```

`windows/src/DesktopMcp.Desktop/Tree/ElementBudget.cs`:

```csharp
namespace DesktopMcp.Desktop.Tree;

/// <summary>
/// upstream tree/budget.py:55-91 TreeElementBudget: how many output elements one capture may collect. This port also
/// counts visited elements (Decisions 9): a tree of output-free containers stops after <c>maxVisits</c>, marked truncated.
/// </summary>
internal sealed class ElementBudget(int limit, int maxVisits)
{
    /// <summary>Visited elements allowed per output element.</summary>
    public const int VisitsPerElement = 50;

    private int _visits;

    public ElementBudget(int limit) : this(limit, (int)Math.Min(int.MaxValue, (long)Math.Max(1, limit) * VisitsPerElement))
    {
    }

    public int Limit { get; } = Math.Max(1, limit);
    public int Count { get; private set; }
    public bool Truncated { get; private set; }

    public bool Exhausted => Count >= Limit || _visits >= maxVisits;

    public int Remaining => Math.Max(0, Limit - Count);

    /// <summary>upstream try_consume: false (and truncated) once the limit is reached; reaching it exactly also truncates.</summary>
    public bool TryConsume(int amount = 1)
    {
        if (amount <= 0) return !Exhausted;
        if (Count >= Limit)
        {
            Truncated = true;
            return false;
        }
        if (amount > Remaining)
        {
            Count = Limit;
            Truncated = true;
            return false;
        }
        Count += amount;
        if (Count >= Limit) Truncated = true;
        return true;
    }

    /// <summary>Counts one visited element; reaching the visit cap marks the capture truncated, like reaching the limit.</summary>
    public bool Visit()
    {
        if (_visits >= maxVisits)
        {
            Truncated = true;
            return false;
        }
        if (++_visits >= maxVisits) Truncated = true;
        return true;
    }
}
```

`windows/src/DesktopMcp.Desktop/Tree/TreeWalker.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Tree;

/// <summary>The window being walked (upstream tree/service.py:922-952).</summary>
internal sealed record WindowContext(string WindowName, bool IsBrowser, ScreenRect WindowBox, ScreenRect ScreenBox);

/// <summary>One window's elements in visit order and its semantic node with children in document order.</summary>
internal sealed class WindowWalk(SemanticNode window)
{
    public List<UiElement> Interactive { get; } = [];
    public List<UiElement> Scrollable { get; } = [];
    public SemanticNode Window { get; } = window;
}

/// <summary>
/// upstream tree/service.py:446-909 tree_traversal without recursion (Decisions 9): an explicit stack, the RuntimeId cycle
/// guard over the current path only, a depth cap and the element budget. Children are visited right to left as upstream
/// does for applications, so interactive elements are numbered in reverse document order; the semantic tree is put back
/// in document order at the end. The browser DOM is P6 (Decisions 4): RootWebArea is walked like any other element.
/// </summary>
internal sealed class TreeWalker(ITreeSource source, ElementBudget budget, Func<int> nextId, int maxDepth = TreeWalker.MaxDepth)
{
    /// <summary>Elements deeper than this below the window are not visited.</summary>
    public const int MaxDepth = 256;

    private readonly record struct Frame(UiaElementData? Node, SemanticNode? Parent, int Depth, string? ExitKey);

    public WindowWalk Walk(UiaElementData root, WindowContext context)
    {
        var walk = new WindowWalk(new SemanticNode(SemanticKind.Window, "Window", context.WindowName));
        var path = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<Frame>();
        stack.Push(new Frame(root, walk.Window, 0, null));
        while (stack.Count > 0)
        {
            var frame = stack.Pop();
            if (frame.Node is not { } node)
            {
                path.Remove(frame.ExitKey!);
                continue;
            }
            if (frame.Depth > 0)
            {
                // upstream tree/service.py:846-850: stop descending once the budget is spent.
                if (budget.Exhausted)
                {
                    source.Release(node);
                    ReleaseAll(stack);
                    break;
                }
                // upstream tree/service.py:862-878: a modal dialog discards the window's elements found so far.
                if (node.ControlType == TreeConfig.Window && !node.IsOffscreen && node.WindowIsModal == true) walk.Interactive.Clear();
            }
            Visit(node, frame, walk, context, path, stack);
        }
        SemanticTree.ReverseChildren(walk.Window);
        return walk;
    }

    private void Visit(UiaElementData node, Frame frame, WindowWalk walk, WindowContext context, HashSet<string> path, Stack<Frame> stack)
    {
        try
        {
            if (frame.Depth > maxDepth || !budget.Visit()) return;
            // upstream tree/service.py:464-481: a provider can repeat an ancestor's RuntimeId; cut that branch.
            if (node.RuntimeId is { } runtimeId && path.Contains(runtimeId)) return;

            var typeName = TreeConfig.ControlTypeName(node.ControlType);
            var semanticAdded = AddScrollable(node, typeName, frame.Parent, walk, context);
            semanticAdded |= AddInteractive(node, typeName, frame.Parent, walk, context);

            // upstream tree/service.py:823-839: named structural containers become tree nodes.
            var semanticParent = frame.Parent;
            if (frame.Parent is not null && !node.IsOffscreen && !semanticAdded && TreeConfig.StructuralControlTypes.Contains(typeName)
                && node.Name.Trim() is { Length: > 0 } structuralName)
            {
                semanticParent = new SemanticNode(SemanticKind.Structural, node.LocalizedControlType, structuralName);
                frame.Parent.Children.Add(semanticParent);
            }

            var children = source.GetChildren(node);
            if (node.RuntimeId is { } key && path.Add(key)) stack.Push(new Frame(null, null, 0, key));
            foreach (var child in children) stack.Push(new Frame(child, semanticParent, frame.Depth + 1, null));
        }
        finally
        {
            source.Release(node);
        }
    }

    // upstream tree/service.py:483-534. The centre is the box centre, not upstream's random point (spec §10).
    private bool AddScrollable(UiaElementData node, string typeName, SemanticNode? parent, WindowWalk walk, WindowContext context)
    {
        if (TreeConfig.InteractiveControlTypes.Contains(typeName) || TreeConfig.InformativeControlTypes.Contains(typeName)
            || node.IsOffscreen || !node.VerticallyScrollable)
            return false;
        var name = node.Name.Trim() is { Length: > 0 } trimmed ? trimmed
            : node.AutomationId.Length > 0 ? node.AutomationId
            : PyText.Capitalize(node.LocalizedControlType) is { Length: > 0 } capitalized ? capitalized
            : "''";
        var metadata = new UiMetadata
        {
            HasFocus = node.HasKeyboardFocus,
            HorizontallyScrollable = node.HorizontallyScrollable,
            HorizontalScrollPercent = node.HorizontallyScrollable ? PyText.Round(node.HorizontalScrollPercent ?? 0, 2) : 0,
            VerticallyScrollable = true,
            VerticalScrollPercent = PyText.Round(node.VerticalScrollPercent ?? 0, 2),
        };
        budget.TryConsume();
        var element = new UiElement(nextId(), name, node.LocalizedControlType, context.WindowName, node.Bounds, node.Bounds.Center, metadata);
        walk.Scrollable.Add(element);
        if (parent is null) return false;
        parent.Children.Add(Semantic(SemanticKind.Scrollable, element));
        return true;
    }

    // upstream tree/service.py:536-792.
    private bool AddInteractive(UiaElementData node, string typeName, SemanticNode? parent, WindowWalk walk, WindowContext context)
    {
        var area = (long)node.Bounds.Width * node.Bounds.Height;
        var visible = area > 0
            && (!node.IsOffscreen || typeName == "EditControl" || (typeName == "ListItemControl" && context.IsBrowser))
            && node.IsControlElement;
        if (!visible || !node.IsEnabled) return false;
        var focusable = TreeConfig.FocusableControlTypes.Contains(typeName) || node.IsKeyboardFocusable;
        if (!IsInteractive(node, typeName, focusable, context.IsBrowser)) return false;

        var (metadata, words) = Metadata(node, typeName);
        var box = ClipToWindow(context, node.Bounds);
        var added = false;
        var name = node.Name.Trim();
        if (name.Length > 0)
        {
            budget.TryConsume();
            var element = new UiElement(nextId(), name, node.LocalizedControlType, context.WindowName, box, box.Center, metadata);
            walk.Interactive.Add(element);
            if (parent is not null)
            {
                parent.Children.Add(Semantic(SemanticKind.Interactive, element));
                added = true;
            }
        }
        // upstream tree/service.py:320-348: one "Word" element per word box, each consuming budget.
        foreach (var word in words)
        {
            if (!budget.TryConsume()) break;
            var wordBox = ClipToWindow(context, word.Box);
            if (wordBox.Width <= 0 || wordBox.Height <= 0) continue;
            var element = new UiElement(nextId(), word.Word, "Word", context.WindowName, wordBox, wordBox.Center, UiMetadata.Empty);
            walk.Interactive.Add(element);
            parent?.Children.Add(Semantic(SemanticKind.Interactive, element));
        }
        return added;
    }

    // upstream tree/service.py:558-609.
    private static bool IsInteractive(UiaElementData node, string typeName, bool focusable, bool isBrowser)
    {
        var interactive = false;
        var roleInteractive = node.LegacyRole is { } role && TreeConfig.InteractiveRoles.Contains(role);
        if (isBrowser && typeName == "DataItemControl" && !focusable)
        {
            interactive = false;
        }
        else if (!isBrowser && typeName == "ImageControl" && focusable)
        {
            interactive = true;
        }
        else if (TreeConfig.InteractiveControlTypes.Contains(typeName) || TreeConfig.DocumentControlTypes.Contains(typeName))
        {
            var isImage = typeName == "ImageControl" && (node.LocalizedControlType == "graphic" || !focusable);
            interactive = roleInteractive && (!isImage || focusable);
        }
        else if (typeName == "GroupControl" && isBrowser)
        {
            // upstream compares the cached ExpandCollapseState with the enum, so any of its four values counts.
            if (node.ExpandCollapseState is >= 0 and <= 3) interactive = true;
            var defaultAction = node.LegacyDefaultAction is { Length: > 0 } action && TreeConfig.DefaultActions.Contains(TreeConfig.Title(action));
            if (roleInteractive && (defaultAction || focusable)) interactive = true;
        }
        return interactive || typeName == "DocumentControl";
    }

    // upstream tree/service.py:617-750. Only the metadata the tree text renders is read (Decisions 6).
    private (UiMetadata Metadata, IReadOnlyList<WordBox> Words) Metadata(UiaElementData node, string typeName)
    {
        var metadata = new UiMetadata
        {
            HasFocus = node.HasKeyboardFocus,
            Shortcut = node.AcceleratorKey.Length > 0 ? node.AcceleratorKey : null,
        };
        IReadOnlyList<WordBox> words = [];
        if (typeName is "ButtonControl" or "CheckBoxControl")
            metadata = metadata with { ToggleState = node.ToggleState switch { 1 => "on", 0 => "off", _ => null } };
        if (typeName is "EditControl" or "DocumentControl" or "ImageControl")
        {
            metadata = metadata with { IsPassword = node.IsPassword, Value = LegacyValue(node) };
            if (!node.IsPassword) words = Words(node);
        }
        if (typeName == "ComboBoxControl")
        {
            metadata = metadata with
            {
                Value = LegacyValue(node),
                ExpandCollapseState = node.ExpandCollapseState switch
                {
                    0 => "collapsed",
                    1 => "expanded",
                    2 => "partially expanded",
                    _ => null,
                },
            };
        }
        if (typeName == "SliderControl")
        {
            metadata = metadata with
            {
                Value = Float(node.RangeValue),
                Minimum = Float(node.RangeMinimum),
                Maximum = Float(node.RangeMaximum),
            };
        }
        return (metadata, words);
    }

    private IReadOnlyList<WordBox> Words(UiaElementData node)
    {
        try
        {
            return source.GetWords(node, budget.Remaining + 1);
        }
        catch (Exception e) when (e is not OperationCanceledException && !ComErrors.IsTimeout(e))
        {
            return []; // upstream tree/service.py:654-663 ignores any failure here
        }
    }

    private static string LegacyValue(UiaElementData node) =>
        string.IsNullOrEmpty(node.LegacyValue) ? "(empty)" : node.LegacyValue.Trim();

    private static string? Float(double? value) => value is { } number ? PyText.FloatRepr(PyText.Round(number, 2)) : null;

    // upstream tree/service.py:307-318 iou_bounding_box: the element clipped to its window and the screen, else empty.
    private static ScreenRect ClipToWindow(WindowContext context, ScreenRect box)
    {
        var clipped = box.Intersect(context.WindowBox).Intersect(context.ScreenBox);
        return clipped.Right > clipped.Left && clipped.Bottom > clipped.Top ? clipped : default;
    }

    private static SemanticNode Semantic(SemanticKind kind, UiElement element) => new(kind, element.ControlType, element.Name)
    {
        Box = element.Box,
        Center = element.Center,
        ElementId = element.Id,
        Metadata = element.Metadata,
    };

    private void ReleaseAll(Stack<Frame> stack)
    {
        foreach (var frame in stack)
            if (frame.Node is { } pending) source.Release(pending);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~ElementBudgetTests|FullyQualifiedName~TreeWalkerTests"`
Expected: `Passed!` with `Failed: 0` (19 tests; the 100,000-deep tree finishes without a stack overflow).

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Tree/TreeConfig.cs windows/src/DesktopMcp.Desktop/Tree/ElementBudget.cs windows/src/DesktopMcp.Desktop/Tree/TreeWalker.cs windows/tests/DesktopMcp.Desktop.Tests/Tree
git commit -m "Port the tree classification and walk it iteratively under the element budget" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 13: Window catalog and tree service

upstream `desktop/service.py:1054-1245` (which windows exist, which are applications, which has focus) and `tree/service.py:113-305` (which windows are walked, in what order, with what retries). Win32 decides as much as it can before any UIA call; each UIA call goes through the hang breaker (Decisions 2, 3, 8, 23, 25, 28).

**Files:**
- Modify: `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (append)
- Create: `windows/src/DesktopMcp.Desktop/Tree/WindowSystem.cs`, `WindowCatalog.cs`, `TreeService.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Tree/WindowCatalogTests.cs`, `TreeServiceTests.cs` (one class Category=Desktop)

**Interfaces:**
- Consumes: `UiaWorker<UiaSession>`, `HangBlockList` (Task 10); `UiaTreeSource`, `WindowProbe` (Task 11); `TreeWalker`, `ElementBudget`, `WindowWalk`, `WindowContext`, `TreeConfig` (Task 12); `IVirtualDesktops` (Task 9); `SnapshotSettings` (Task 3); `ComErrors`, `UiaUnavailableException` (Task 5); `Win32Displays` and `TestWindow` (Tasks 8, 11, in the Desktop tests).
- Produces (namespace `DesktopMcp.Desktop.Tree`, internal):
  - `interface IWindowSystem` (`EnumerateTopLevel`, `IsWindow`, `IsVisible`, `IsMinimized`, `IsMaximized`, `GetProcessId`, `GetClassName`, `FindWindow`, `GetForegroundWindow`, `GetRoot`, `GetProcessName`) and `sealed class Win32WindowSystem : IWindowSystem`.
  - `sealed record WindowScan(IReadOnlyList<long> Candidates, IReadOnlyList<WindowInfo> Windows, WindowInfo? Active)` with `static Empty`; `interface IWindowScanner { Task<WindowScan> ScanAsync(CancellationToken); }`; `interface IWindowProbe { Task<WindowProbe> ProbeAsync(long window, CancellationToken); }`; `sealed class UiaWindowProbe(UiaWorker<UiaSession>) : IWindowProbe` (deadline 5 s); `sealed class WindowCatalog(IWindowSystem, IWindowProbe, IVirtualDesktops, HangBlockList, int ownProcessId, Func<TimeSpan, CancellationToken, Task> delay) : IWindowScanner` (`ActiveWindowAttempts` = 3, `ActiveWindowRetryDelay` = 100 ms, internal `Candidates()`).
  - `interface ITreeCapture { Task<TreeSnapshot> CaptureAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, CancellationToken); }`; `interface IWindowWalker { Task<WindowWalk> WalkAsync(long window, bool isBrowser, ScreenRect screen, ElementBudget budget, Func<int> nextId, CancellationToken); }`; `sealed class UiaWindowWalker(UiaWorker<UiaSession>) : IWindowWalker` (deadline 20 s); `sealed class TreeService(IWindowSystem, IWindowWalker, HangBlockList, Func<string, string?> environment, Func<TimeSpan, CancellationToken, Task> delay) : ITreeCapture` (`RetryDelays` 0.5, 1, 2 s; internal `static TreeSnapshot Assemble(IReadOnlyList<WindowWalk>, ElementBudget)`).
  - Test helper `FakeWindowSystem` (in `WindowCatalogTests.cs`, shared with `TreeServiceTests`).

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/Tree/WindowCatalogTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Tests.Tree;

internal sealed class FakeWindowSystem : IWindowSystem
{
    public List<long> TopLevel { get; } = [];
    public HashSet<long> Hidden { get; } = [];
    public HashSet<long> Minimized { get; } = [];
    public Dictionary<long, int> Processes { get; } = [];
    public Dictionary<int, string> ProcessNames { get; } = [];
    public Dictionary<long, string> Classes { get; } = [];
    public Dictionary<long, long> Roots { get; } = [];
    public Queue<long> Foreground { get; } = new();

    public IReadOnlyList<long> EnumerateTopLevel() => TopLevel;
    public bool IsWindow(long window) => true;
    public bool IsVisible(long window) => !Hidden.Contains(window);
    public bool IsMinimized(long window) => Minimized.Contains(window);
    public bool IsMaximized(long window) => false;
    public int GetProcessId(long window) => Processes.GetValueOrDefault(window, 100);
    public string GetClassName(long window) => Classes.GetValueOrDefault(window, "AppWindow");
    public long FindWindow(string className) => Classes.FirstOrDefault(c => c.Value == className).Key;
    public long GetForegroundWindow() => Foreground.Count > 0 ? Foreground.Dequeue() : 0;
    public long GetRoot(long window) => Roots.GetValueOrDefault(window, window);
    public string? GetProcessName(int processId) => ProcessNames.GetValueOrDefault(processId);
}

public class WindowCatalogTests
{
    private sealed class FakeProbe : IWindowProbe
    {
        public Dictionary<long, Func<WindowProbe>> Windows { get; } = [];
        public List<long> Probed { get; } = [];

        public Task<WindowProbe> ProbeAsync(long window, CancellationToken cancellationToken)
        {
            Probed.Add(window);
            return Task.FromResult(Windows.TryGetValue(window, out var probe) ? probe() : throw new InvalidOperationException("gone"));
        }
    }

    private sealed class AllOnCurrentDesktop(params long[] elsewhere) : IVirtualDesktops
    {
        public IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows) => windows.Where(w => !elsewhere.Contains(w)).ToArray();
        public (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops() => (VirtualDesktopInfo.Default, [VirtualDesktopInfo.Default]);
    }

    private sealed class ManualTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    }

    private static WindowProbe App(string name, int processId = 100) =>
        new(name, TreeConfig.Window, "AppWindow", new ScreenRect(0, 0, 800, 600), processId, true, true, true, true);

    private readonly FakeWindowSystem _system = new();
    private readonly FakeProbe _probe = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly HangBlockList _blocks = new(new ManualTime(), HangBlockList.DefaultDuration);

    private WindowCatalog Catalog(IVirtualDesktops? desktops = null) =>
        new(_system, _probe, desktops ?? new AllOnCurrentDesktop(), _blocks, ownProcessId: 7, (delay, _) => { _delays.Add(delay); return Task.CompletedTask; });

    [Fact]
    public void CandidatesAreVisibleForeignWindowsOnThisDesktopThenTheShellWindows()
    {
        _system.TopLevel.AddRange([1, 2, 3, 4, 5]);
        _system.Hidden.Add(2);
        _system.Processes[3] = 7;              // this server's own window
        _system.Classes[5] = "Shell_TrayWnd";  // already listed: not added twice
        _system.Classes[90] = "Progman";
        Assert.Equal([1L, 5L, 90L], Catalog(new AllOnCurrentDesktop(4)).Candidates());
    }

    [Fact]
    public async Task OnlyApplicationWindowsAreListedWithTheirZOrderDepth()
    {
        _system.TopLevel.AddRange([1, 2, 3, 4, 5, 6, 7]);
        _system.Minimized.Add(6);
        _system.ProcessNames[200] = "msedge.exe";
        _probe.Windows[1] = () => App("Notepad");
        _probe.Windows[2] = () => App("NVIDIA GeForce Overlay");
        _probe.Windows[3] = () => App("") with { HasChildren = false };
        _probe.Windows[4] = () => App("Dialog") with { CanMaximize = false };
        _probe.Windows[5] = () => App("Empty") with { Bounds = new ScreenRect(0, 0, 0, 0) };
        _probe.Windows[6] = () => App("Minimized") with { Bounds = new ScreenRect(0, 0, 0, 0) };
        _probe.Windows[7] = () => App("Edge", processId: 200) with { ControlType = TreeConfig.Pane };

        var scan = await Catalog().ScanAsync(default);

        Assert.Equal(["Notepad", "Minimized", "Edge"], scan.Windows.Select(w => w.Name));
        Assert.Equal([0, 5, 6], scan.Windows.Select(w => w.Depth));
        Assert.Equal(WindowStatus.Minimized, scan.Windows[1].Status);
        Assert.True(scan.Windows[2].IsBrowser);
        Assert.Equal(7, scan.Candidates.Count);
    }

    [Fact]
    public async Task AProbeTimeoutBlocksTheWindowForLaterScans()
    {
        _system.TopLevel.Add(1);
        _probe.Windows[1] = () => throw new UiaTimeoutException("hung");
        await Catalog().ScanAsync(default);
        await Catalog().ScanAsync(default);
        Assert.Equal([1L], _probe.Probed);
        Assert.True(_blocks.IsBlocked(1, 100));
    }

    [Fact]
    public async Task TheFocusedWindowIsTheForegroundWindowsRoot()
    {
        _system.TopLevel.AddRange([1, 2]);
        _probe.Windows[1] = () => App("Notepad");
        _probe.Windows[2] = () => App("Paint");
        _system.Roots[55] = 2;
        _system.Foreground.Enqueue(55);
        var scan = await Catalog().ScanAsync(default);
        Assert.Same(scan.Windows[1], scan.Active);
    }

    [Fact]
    public async Task AFocusedWindowOutsideTheListIsDescribedWithDepthZero()
    {
        _system.TopLevel.Add(1);
        _probe.Windows[1] = () => App("Notepad");
        _probe.Windows[9] = () => App("Tool") with { CanMaximize = false };
        _system.Foreground.Enqueue(9);
        var active = (await Catalog().ScanAsync(default)).Active;
        Assert.Equal(("Tool", 0, 9L), (active!.Name, active.Depth, active.Handle));
    }

    [Fact]
    public async Task TheDesktopIsNeverTheFocusedWindow()
    {
        _system.Classes[90] = "Progman";
        _system.Foreground.Enqueue(90);
        Assert.Null((await Catalog().ScanAsync(default)).Active);
    }

    [Fact]
    public async Task NoForegroundWindowIsRetriedThreeTimes()
    {
        Assert.Null((await Catalog().ScanAsync(default)).Active);
        Assert.Equal([WindowCatalog.ActiveWindowRetryDelay, WindowCatalog.ActiveWindowRetryDelay, WindowCatalog.ActiveWindowRetryDelay], _delays);
    }
}
```

`windows/tests/DesktopMcp.Desktop.Tests/Tree/TreeServiceTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Tests.Tree;

public class TreeServiceTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);

    private sealed class ManualTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Each window yields one button named after it, unless a failure is queued for it.</summary>
    private sealed class FakeWalker : IWindowWalker
    {
        public List<long> Walked { get; } = [];
        public Dictionary<long, Queue<Exception>> Failures { get; } = [];

        public Task<WindowWalk> WalkAsync(long window, bool isBrowser, ScreenRect screen, ElementBudget budget, Func<int> nextId, CancellationToken cancellationToken)
        {
            Walked.Add(window);
            if (Failures.TryGetValue(window, out var failures) && failures.Count > 0) throw failures.Dequeue();
            var walk = new WindowWalk(new SemanticNode(SemanticKind.Window, "Window", $"W{window}"));
            budget.TryConsume();
            var element = new UiElement(nextId(), $"B{window}{(isBrowser ? " (browser)" : "")}", "button", $"W{window}", new ScreenRect(0, 0, 10, 10), new ScreenPoint(5, 5), UiMetadata.Empty);
            walk.Interactive.Add(element);
            walk.Window.Children.Add(new SemanticNode(SemanticKind.Interactive, "button", element.Name) { ElementId = element.Id });
            return Task.FromResult(walk);
        }
    }

    private readonly FakeWindowSystem _system = new();
    private readonly FakeWalker _walker = new();
    private readonly HangBlockList _blocks = new(new ManualTime(), HangBlockList.DefaultDuration);
    private readonly Dictionary<string, string> _environment = [];
    private readonly List<TimeSpan> _delays = [];

    private TreeService Service() => new(_system, _walker, _blocks, name => _environment.GetValueOrDefault(name),
        (delay, _) => { _delays.Add(delay); return Task.CompletedTask; });

    [Fact]
    public async Task TheActiveWindowIsWalkedFirstAndLabelsFollowTheWalkOrder()
    {
        var tree = await Service().CaptureAsync(2, [1, 3], Screen, default);
        Assert.Equal([2L, 1L, 3L], _walker.Walked);
        Assert.Equal(["B2", "B1", "B3"], tree.Interactive.Select(e => e.Name));
        Assert.Equal([0, 1, 2], tree.Interactive.Select(e => e.Id));
        Assert.Equal(["W2", "W1", "W3"], tree.Root!.Children.Select(c => c.Name));
    }

    [Fact]
    public async Task ExcludedBlockedAndDesktopWindowsAreSkippedBeforeAnyUiaCall()
    {
        _environment[SnapshotSettings.ExcludeProcessesVariable] = "Secret.exe";
        _system.Processes[1] = 300;
        _system.ProcessNames[300] = "secret.EXE";
        _system.Classes[3] = "Progman";
        _system.Processes[4] = 400;
        _blocks.Block(99, 400);
        _system.ProcessNames[100] = "chrome.exe";

        var tree = await Service().CaptureAsync(2, [1, 3, 4], Screen, default);

        Assert.Equal([2L], _walker.Walked);
        Assert.Equal("B2 (browser)", Assert.Single(tree.Interactive).Name);
    }

    [Fact]
    public async Task TheDesktopIsWalkedWhenNothingIsFocused()
    {
        _system.Classes[3] = "Progman";
        await Service().CaptureAsync(null, [3], Screen, default);
        Assert.Equal([3L], _walker.Walked);
    }

    [Fact]
    public async Task FailuresAreRetriedAfterHalfASecondThenOneSecond()
    {
        _walker.Failures[1] = new Queue<Exception>([new InvalidOperationException("busy"), new InvalidOperationException("busy")]);
        var tree = await Service().CaptureAsync(null, [1], Screen, default);
        Assert.Equal([TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)], _delays);
        Assert.Single(tree.Interactive);
    }

    [Fact]
    public async Task AWindowThatKeepsFailingIsLeftOutAfterThreeRetries()
    {
        _walker.Failures[1] = new Queue<Exception>(Enumerable.Range(0, 4).Select(_ => new InvalidOperationException("busy")));
        var tree = await Service().CaptureAsync(null, [1, 2], Screen, default);
        Assert.Equal([1L, 1L, 1L, 1L, 2L], _walker.Walked);
        Assert.Equal(["B2"], tree.Interactive.Select(e => e.Name));
    }

    [Fact]
    public async Task ATimeoutBlocksTheWindowWithoutRetrying()
    {
        _walker.Failures[1] = new Queue<Exception>([new UiaTimeoutException("hung")]);
        await Service().CaptureAsync(null, [1], Screen, default);
        Assert.Equal([1L], _walker.Walked);
        Assert.Empty(_delays);
        Assert.True(_blocks.IsBlocked(1, 100));
    }

    [Fact]
    public async Task TheBudgetStopsTheCaptureAndTheTreeIsMarkedTruncated()
    {
        _environment[SnapshotSettings.MaxTreeElementsVariable] = "2";
        var tree = await Service().CaptureAsync(null, [1, 2, 3], Screen, default);
        Assert.Equal([1L, 2L], _walker.Walked);
        Assert.True(tree.Truncated);
        Assert.Equal(2, tree.ElementLimit);
    }

    [Fact]
    public void AssembleDropsNodesOfDiscardedElementsAndEmptyContainers()
    {
        var walk = new WindowWalk(new SemanticNode(SemanticKind.Window, "Window", "App"));
        var kept = new UiElement(1, "Kept", "button", "App", default, default, UiMetadata.Empty);
        walk.Interactive.Add(kept);
        var group = new SemanticNode(SemanticKind.Structural, "group", "Old");
        group.Children.Add(new SemanticNode(SemanticKind.Interactive, "button", "Discarded") { ElementId = 0 });
        walk.Window.Children.AddRange([group, new SemanticNode(SemanticKind.Interactive, "button", "Kept") { ElementId = 1 }]);

        var tree = TreeService.Assemble([walk], new ElementBudget(500));

        Assert.Equal(["Kept"], Assert.Single(tree.Root!.Children).Children.Select(c => c.Name));
        Assert.False(tree.Truncated);
    }
}

[Trait("Category", "Desktop")]
public sealed class WindowCatalogDesktopTests : IDisposable
{
    private readonly TestWindow _window = new();
    private readonly UiaWorker<UiaSession> _worker = new(UiaSession.Create);
    private readonly HangBlockList _blocks = new(TimeProvider.System, HangBlockList.DefaultDuration);

    public void Dispose()
    {
        _worker.Dispose();
        _window.Dispose();
    }

    [Fact]
    public async Task ListsTheTestWindowAsANormalApplicationWindow()
    {
        // ownProcessId -1: the test window lives in this process, which the server would otherwise skip.
        var catalog = new WindowCatalog(new Win32WindowSystem(), new UiaWindowProbe(_worker), new VirtualDesktopService(), _blocks, -1, Task.Delay);
        var scan = await catalog.ScanAsync(default);
        var window = Assert.Single(scan.Windows, w => w.Handle == _window.Handle);
        Assert.Equal((_window.Title, WindowStatus.Normal, Environment.ProcessId), (window.Name, window.Status, window.ProcessId));
        Assert.Equal(scan.Candidates.ToList().IndexOf(_window.Handle), window.Depth);
    }

    [Fact]
    public async Task WalksTheTestWindowIntoTheTree()
    {
        var service = new TreeService(new Win32WindowSystem(), new UiaWindowWalker(_worker), _blocks, _ => null, Task.Delay);
        var tree = await service.CaptureAsync(null, [_window.Handle], new Win32Displays().GetVirtualScreen(), default);
        var window = Assert.Single(tree.Root!.Children);
        Assert.Equal(_window.Title, window.Name);
        Assert.Contains(tree.Interactive, e => e.Name == TestWindow.ButtonText); // ControlType is the localized name ("button", "단추", ...)
        Assert.Contains(tree.Interactive, e => e.ControlType == "Word" && e.Name == "world");
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0246` for `IWindowSystem`, `WindowCatalog`, `TreeService` and the other new types.

- [ ] **Step 3: Implement**

Append to `windows/src/DesktopMcp.Desktop/NativeMethods.txt`:

```text
// Top-level windows (upstream desktop/service.py:1068-1245)
EnumWindows
IsWindow
IsWindowVisible
IsIconic
IsZoomed
GetWindowThreadProcessId
GetClassName
FindWindow
GetAncestor
OpenProcess
QueryFullProcessImageName
CloseHandle
```

`windows/src/DesktopMcp.Desktop/Tree/WindowSystem.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopMcp.Desktop.Tree;

/// <summary>The Win32 window queries the window list needs (no UIA, so they never enter a hung provider).</summary>
internal interface IWindowSystem
{
    /// <summary>Top-level windows in z-order, topmost first.</summary>
    IReadOnlyList<long> EnumerateTopLevel();

    bool IsWindow(long window);
    bool IsVisible(long window);
    bool IsMinimized(long window);
    bool IsMaximized(long window);

    /// <summary>The owning process id, or 0 when it cannot be read.</summary>
    int GetProcessId(long window);

    string GetClassName(long window);

    /// <summary>The first top-level window of the class, or 0.</summary>
    long FindWindow(string className);

    long GetForegroundWindow();

    /// <summary>The top-level window that contains <paramref name="window"/> (GetAncestor GA_ROOT), or the window itself.</summary>
    long GetRoot(long window);

    /// <summary>The executable's file name ("chrome.exe"), or null.</summary>
    string? GetProcessName(int processId);
}

internal sealed unsafe class Win32WindowSystem : IWindowSystem
{
    public IReadOnlyList<long> EnumerateTopLevel()
    {
        var windows = new List<long>();
        var handle = GCHandle.Alloc(windows);
        try
        {
            PInvoke.EnumWindows(&OnWindow, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return windows;
    }

    public bool IsWindow(long window) => PInvoke.IsWindow(Hwnd(window));
    public bool IsVisible(long window) => PInvoke.IsWindowVisible(Hwnd(window));
    public bool IsMinimized(long window) => PInvoke.IsIconic(Hwnd(window));
    public bool IsMaximized(long window) => PInvoke.IsZoomed(Hwnd(window));

    public int GetProcessId(long window)
    {
        uint processId = 0;
        PInvoke.GetWindowThreadProcessId(Hwnd(window), &processId);
        return (int)processId;
    }

    public string GetClassName(long window)
    {
        var buffer = stackalloc char[257];
        var length = PInvoke.GetClassName(Hwnd(window), buffer, 257);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    public long FindWindow(string className) => (nint)PInvoke.FindWindow(className, null);

    public long GetForegroundWindow() => (nint)PInvoke.GetForegroundWindow();

    public long GetRoot(long window)
    {
        var root = PInvoke.GetAncestor(Hwnd(window), GET_ANCESTOR_FLAGS.GA_ROOT);
        return root.IsNull ? window : (nint)root;
    }

    // upstream tree/service.py:50-68 resolves HWND -> PID -> name with psutil; QueryFullProcessImageName works for
    // elevated processes too (limited information access), and the process list is the fallback.
    public string? GetProcessName(int processId)
    {
        if (processId <= 0) return null;
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (!process.IsNull)
        {
            try
            {
                var buffer = stackalloc char[1024];
                uint size = 1024;
                if (PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &size))
                    return Path.GetFileName(new string(buffer, 0, (int)size));
            }
            finally
            {
                PInvoke.CloseHandle(process);
            }
        }
        try
        {
            using var fallback = System.Diagnostics.Process.GetProcessById(processId);
            return fallback.ProcessName + ".exe";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static HWND Hwnd(long window) => (HWND)(nint)window;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnWindow(HWND window, LPARAM data)
    {
        ((List<long>)GCHandle.FromIntPtr(data).Target!).Add((nint)window);
        return true;
    }
}
```

`windows/src/DesktopMcp.Desktop/Tree/WindowCatalog.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Tree;

/// <summary>The window scan: every walk candidate in z-order, the application windows among them, the focused window.</summary>
internal sealed record WindowScan(IReadOnlyList<long> Candidates, IReadOnlyList<WindowInfo> Windows, WindowInfo? Active)
{
    public static WindowScan Empty { get; } = new([], [], null);
}

internal interface IWindowScanner
{
    Task<WindowScan> ScanAsync(CancellationToken cancellationToken);
}

/// <summary>Reads a top-level window's UIA properties; may throw, and times out through the hang breaker.</summary>
internal interface IWindowProbe
{
    Task<WindowProbe> ProbeAsync(long window, CancellationToken cancellationToken);
}

internal sealed class UiaWindowProbe(UiaWorker<UiaSession> worker) : IWindowProbe
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public Task<WindowProbe> ProbeAsync(long window, CancellationToken cancellationToken) =>
        worker.RunAsync(session => new UiaTreeSource(session).ProbeWindow(window), Deadline, cancellationToken);
}

/// <summary>
/// upstream desktop/service.py:1068-1245: get_controls_handles, get_windows and get_active_window. Windows are taken in
/// z-order and Depth is the index in that order (Decisions 3); the focused window comes from Win32 (Decisions 25).
/// </summary>
internal sealed class WindowCatalog(
    IWindowSystem windows, IWindowProbe probe, IVirtualDesktops desktops, HangBlockList blocks, int ownProcessId,
    Func<TimeSpan, CancellationToken, Task> delay) : IWindowScanner
{
    /// <summary>upstream desktop/service.py:1112-1113.</summary>
    public const int ActiveWindowAttempts = 3;
    public static readonly TimeSpan ActiveWindowRetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly string[] ShellClasses = ["Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    public async Task<WindowScan> ScanAsync(CancellationToken cancellationToken)
    {
        var candidates = Candidates();
        var list = new List<WindowInfo>();
        for (var depth = 0; depth < candidates.Count; depth++)
        {
            if (await DescribeAsync(candidates[depth], depth, applicationOnly: true, cancellationToken).ConfigureAwait(false) is { } window)
                list.Add(window);
        }
        var active = await ActiveAsync(list, cancellationToken).ConfigureAwait(false);
        return new WindowScan(candidates, list, active);
    }

    // upstream desktop/service.py:1068-1105: visible windows on this virtual desktop that this server does not own, then
    // the desktop and taskbar windows.
    internal IReadOnlyList<long> Candidates()
    {
        var visible = windows.EnumerateTopLevel()
            .Where(window => windows.IsWindow(window) && windows.IsVisible(window) && windows.GetProcessId(window) != ownProcessId)
            .ToList();
        var result = desktops.OnCurrentDesktop(visible).ToList();
        foreach (var className in ShellClasses)
        {
            var shell = windows.FindWindow(className);
            if (shell != 0 && !result.Contains(shell)) result.Add(shell);
        }
        return result;
    }

    // upstream desktop/service.py:1054-1066 and 1192-1245. A window whose probe fails is skipped (upstream drops the
    // whole list on most errors).
    private async Task<WindowInfo?> DescribeAsync(long window, int depth, bool applicationOnly, CancellationToken cancellationToken)
    {
        var processId = windows.GetProcessId(window);
        if (blocks.IsBlocked(window, processId)) return null;
        WindowProbe described;
        try
        {
            described = await probe.ProbeAsync(window, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (ComErrors.IsTimeout(e))
        {
            blocks.Block(window, processId);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException and not UiaUnavailableException)
        {
            return null;
        }
        var status = Status(window);
        if (applicationOnly)
        {
            var name = described.Name.Trim();
            if (name.Contains("Overlay", StringComparison.Ordinal) || (name.Length == 0 && !described.HasChildren)) return null;
            if (described.ControlType is not (TreeConfig.Window or TreeConfig.Pane)) return null;
            if (!described.HasWindowPattern || !(described.CanMinimize && described.CanMaximize)) return null;
            if (described.Bounds.IsEmpty && status != WindowStatus.Minimized) return null;
        }
        return new WindowInfo(described.Name, depth, status, described.Bounds, window, described.ProcessId,
            TreeConfig.IsBrowser(windows.GetProcessName(described.ProcessId)));
    }

    // upstream desktop/service.py:1115-1172 get_active_window.
    private async Task<WindowInfo?> ActiveAsync(List<WindowInfo> list, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ActiveWindowAttempts; attempt++)
        {
            var foreground = windows.GetForegroundWindow();
            if (foreground == 0)
            {
                await delay(ActiveWindowRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var root = windows.GetRoot(foreground);
            if (windows.GetClassName(root) == "Progman") return null;
            return list.FirstOrDefault(window => window.Handle == root)
                ?? await DescribeAsync(root, depth: 0, applicationOnly: false, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    // upstream desktop/service.py:317-325 get_window_status.
    private WindowStatus Status(long window) =>
        windows.IsMinimized(window) ? WindowStatus.Minimized
        : windows.IsMaximized(window) ? WindowStatus.Maximized
        : windows.IsVisible(window) ? WindowStatus.Normal
        : WindowStatus.Hidden;
}
```

`windows/src/DesktopMcp.Desktop/Tree/TreeService.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tree;

internal interface ITreeCapture
{
    /// <summary>Walks the active window first, then <paramref name="others"/>, into one tree (upstream tree/service.py:113-224).</summary>
    Task<TreeSnapshot> CaptureAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, CancellationToken cancellationToken);
}

/// <summary>Walks one window; throws on failure and gives a timeout exception when the hang breaker fires.</summary>
internal interface IWindowWalker
{
    Task<WindowWalk> WalkAsync(long window, bool isBrowser, ScreenRect screen, ElementBudget budget, Func<int> nextId, CancellationToken cancellationToken);
}

internal sealed class UiaWindowWalker(UiaWorker<UiaSession> worker) : IWindowWalker
{
    /// <summary>The hang breaker's deadline for one window walk (Decisions 2).</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public Task<WindowWalk> WalkAsync(
        long window, bool isBrowser, ScreenRect screen, ElementBudget budget, Func<int> nextId, CancellationToken cancellationToken) =>
        worker.RunAsync(session =>
        {
            var source = new UiaTreeSource(session);
            var root = source.ReadWindow(window);
            var name = TreeConfig.WindowName(root.Name.Trim(), root.ClassName);
            return new TreeWalker(source, budget, nextId).Walk(root, new WindowContext(name, isBrowser, root.Bounds, screen));
        }, Deadline, cancellationToken);
}

/// <summary>
/// upstream tree/service.py:113-305: excluded processes are skipped before any UIA call, each window is retried three
/// times after 0.5, 1 and 2 s (Decisions 2: a timeout blocks the window instead), and the budget stops the capture.
/// </summary>
internal sealed class TreeService(
    IWindowSystem windows, IWindowWalker walker, HangBlockList blocks, Func<string, string?> environment,
    Func<TimeSpan, CancellationToken, Task> delay) : ITreeCapture
{
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    public async Task<TreeSnapshot> CaptureAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, CancellationToken cancellationToken)
    {
        var budget = new ElementBudget(SnapshotSettings.MaxTreeElements(environment(SnapshotSettings.MaxTreeElementsVariable)));
        var excluded = SnapshotSettings.ExcludedProcesses(environment(SnapshotSettings.ExcludeProcessesVariable));
        var targets = new List<(long Window, int ProcessId, bool IsBrowser)>();
        foreach (var window in active is { } first ? [first, .. others] : others)
        {
            var processId = windows.GetProcessId(window);
            var processName = windows.GetProcessName(processId);
            if (processName is not null && excluded.Contains(processName.ToLowerInvariant())) continue;
            if (blocks.IsBlocked(window, processId)) continue;
            // upstream tree/service.py:259-260: with a focused window, the desktop icons (Progman) are not walked.
            if (active is not null && windows.GetClassName(window) == "Progman") continue;
            targets.Add((window, processId, TreeConfig.IsBrowser(processName)));
        }

        var nextId = 0;
        var walks = new List<WindowWalk>();
        foreach (var (window, processId, isBrowser) in targets)
        {
            if (budget.Exhausted) break;
            for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
            {
                if (attempt > 0) await delay(RetryDelays[attempt - 1], cancellationToken).ConfigureAwait(false);
                try
                {
                    walks.Add(await walker.WalkAsync(window, isBrowser, screen, budget, () => nextId++, cancellationToken).ConfigureAwait(false));
                    break;
                }
                catch (Exception e) when (ComErrors.IsTimeout(e))
                {
                    blocks.Block(window, processId);
                    break;
                }
                catch (Exception e) when (e is not OperationCanceledException and not UiaUnavailableException)
                {
                    // upstream tree/service.py:286-303: retried, and after the last retry the window is left out.
                }
            }
        }
        return Assemble(walks, budget);
    }

    /// <summary>upstream tree/service.py:181-224: the desktop root over the windows, then the semantic tree cleaned up.</summary>
    internal static TreeSnapshot Assemble(IReadOnlyList<WindowWalk> walks, ElementBudget budget)
    {
        var interactive = walks.SelectMany(walk => walk.Interactive).ToArray();
        var scrollable = walks.SelectMany(walk => walk.Scrollable).ToArray();
        var root = new SemanticNode(SemanticKind.Desktop, "Desktop", "Desktop");
        root.Children.AddRange(walks.Select(walk => walk.Window));
        SemanticTree.DropElements(root, interactive.Select(e => e.Id).Concat(scrollable.Select(e => e.Id)).ToHashSet());
        SemanticTree.PruneStructural(root);
        return new TreeSnapshot(interactive, scrollable, root, budget.Truncated, budget.Limit);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~WindowCatalogTests|FullyQualifiedName~TreeServiceTests"`
Expected: `Passed!` with `Failed: 0` (15 tests).

Run (interactive session): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~WindowCatalogDesktopTests"`
Expected: `Passed!` with `Failed: 0` (2 tests): the test window is listed as a Normal application window with its z-order depth, and its walk yields the "Press me" button and the "world" word element. (The button's control type is UIA's localized name, so the test matches names only.)

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/NativeMethods.txt windows/src/DesktopMcp.Desktop/Tree windows/tests/DesktopMcp.Desktop.Tests/Tree
git commit -m "List application windows in z-order and walk them into one tree with retries and blocking" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 14: Win2D renderer follow-ups, cursor marker and grid

Closes the P0 `Win2DRenderer` follow-ups and adds what upstream's annotated screenshot draws (`desktop/service.py:1385-1486`): the reference grid, palette-coloured boxes with index labels, and the cursor marker (Decisions 12, 13). The entry point initialises the Windows App SDK runtime itself (the P0 follow-up for Win2D entry points), so the probe's imaging step no longer calls `AppRuntime.EnsureInitialized()` first; the AOT activation check in `build-release.ps1` then proves the entry point is enough under NativeAOT.

**Files:**
- Replace: `windows/src/DesktopMcp.Desktop/Imaging/IImageRenderer.cs`, `Win2DRenderer.cs`
- Create: `windows/src/DesktopMcp.Desktop/Imaging/AnnotationLayout.cs`
- Modify: `windows/probes/DesktopMcp.Probe/Program.cs`
- Test: replace `windows/tests/DesktopMcp.Desktop.Tests/Win2DRendererTests.cs` (one class Category=Desktop)

**Interfaces:**
- Consumes: `CapturedImage`, `PixelRect`, `Annotation` (P0), `AppRuntime.EnsureInitialized` (Task 6), `ComErrors` (Task 5).
- Produces (namespace `DesktopMcp.Desktop.Imaging`):
  - `readonly record struct PixelPoint(int X, int Y)`; `readonly record struct GridLines(int Columns, int Rows)`; `sealed record RenderOptions(double Scale, IReadOnlyList<Annotation> Annotations, PixelPoint? Cursor = null, GridLines? Grid = null)` with `static RenderOptions Plain(double scale)`.
  - `interface IImageRenderer { Task<byte[]> RenderPngAsync(CapturedImage image, RenderOptions options); }` (replaces the `(image, scale, annotations)` overload).
  - `Win2DRenderer` with internal ctor `(Action ensureRuntime)`; `Palette` unchanged.
  - internal `static class AnnotationLayout`: `OutputSize(int, int, double)`, `PixelRect? ScaleBox(PixelRect box, int imageWidth, int imageHeight, double scale, int outWidth, int outHeight)`, `PixelRect PlaceLabel(PixelRect box, int labelWidth, int labelHeight, int outWidth, int outHeight)`, `bool UseDarkText((byte R, byte G, byte B))`, `IReadOnlyList<int> GridPositions(int count, int size)`, `const int LabelGap = 2`.

- [ ] **Step 1: Write the failing tests**

Replace `windows/tests/DesktopMcp.Desktop.Tests/Win2DRendererTests.cs` with:

```csharp
using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Tests;

public class AnnotationLayoutTests
{
    [Theory]
    [InlineData(2560, 1600, 0.675, 1728, 1080)]
    [InlineData(1000, 3, 0.1, 100, 1)]
    public void OutputSizeTruncatesLikePil(int width, int height, double scale, int outWidth, int outHeight) =>
        Assert.Equal((outWidth, outHeight), AnnotationLayout.OutputSize(width, height, scale));

    [Fact]
    public void BoxesAreClampedToTheImageThenScaled()
    {
        Assert.Equal(new PixelRect(0, 5, 50, 25), AnnotationLayout.ScaleBox(new PixelRect(-20, 10, 100, 50), 200, 100, 0.5, 100, 50));
        Assert.Null(AnnotationLayout.ScaleBox(new PixelRect(250, 10, 300, 50), 200, 100, 0.5, 100, 50));
        // A box that scales to nothing keeps one pixel.
        Assert.Equal(new PixelRect(10, 10, 11, 11), AnnotationLayout.ScaleBox(new PixelRect(100, 100, 101, 101), 200, 200, 0.1, 20, 20));
    }

    [Fact]
    public void LabelsSitAboveTheBoxRightAlignedOrBelowWhenThereIsNoRoom()
    {
        Assert.Equal(new PixelRect(80, 22, 100, 38), AnnotationLayout.PlaceLabel(new PixelRect(40, 40, 100, 80), 20, 16, 400, 300));
        Assert.Equal(new PixelRect(80, 82, 100, 98), AnnotationLayout.PlaceLabel(new PixelRect(40, 5, 100, 80), 20, 16, 400, 300));
        Assert.Equal(new PixelRect(0, 22, 20, 38), AnnotationLayout.PlaceLabel(new PixelRect(0, 40, 10, 80), 20, 16, 400, 300));
        Assert.Equal(new PixelRect(380, 284, 400, 300), AnnotationLayout.PlaceLabel(new PixelRect(390, 5, 400, 299), 20, 16, 400, 300));
    }

    [Fact]
    public void LightPaletteColoursGetDarkText()
    {
        Assert.True(AnnotationLayout.UseDarkText((210, 245, 60)));
        Assert.True(AnnotationLayout.UseDarkText((70, 240, 240)));
        Assert.False(AnnotationLayout.UseDarkText((230, 25, 75)));
        Assert.False(AnnotationLayout.UseDarkText((0, 0, 128)));
    }

    [Fact]
    public void GridLinesSplitTheImageEvenly()
    {
        Assert.Equal([33, 66], AnnotationLayout.GridPositions(3, 100));
        Assert.Empty(AnnotationLayout.GridPositions(1, 100));
        Assert.Equal(4, AnnotationLayout.GridPositions(int.MaxValue, 5).Count);
    }
}

public class Win2DRendererValidationTests
{
    [Fact]
    public async Task ABufferOfTheWrongLengthIsRejectedBeforeAnyWin2DCall()
    {
        var renderer = new Win2DRenderer(() => throw new InvalidOperationException("must not initialise"));
        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderPngAsync(new CapturedImage(10, 10, new byte[399]), RenderOptions.Plain(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => renderer.RenderPngAsync(new CapturedImage(1, 1, new byte[4]), RenderOptions.Plain(1.5)));
    }
}

[Trait("Category", "Desktop")]
public class Win2DRendererTests
{
    private static readonly (byte R, byte G, byte B, byte A) Gray = (0x80, 0x80, 0x80, 0xFF);

    [Fact]
    public async Task EncodesAPngOfTheSameSize()
    {
        var png = await new Win2DRenderer().RenderPngAsync(Solid(120, 90), RenderOptions.Plain(1.0));
        var decoded = await PngTestDecoder.DecodeAsync(png);
        Assert.Equal((120, 90), (decoded.Width, decoded.Height));
        Assert.Equal(Gray, decoded.Pixel(60, 45));
    }

    [Fact]
    public async Task ScalesDownWithTruncatedSize()
    {
        var png = await new Win2DRenderer().RenderPngAsync(Solid(201, 101), RenderOptions.Plain(0.5));
        var decoded = await PngTestDecoder.DecodeAsync(png);
        Assert.Equal((100, 50), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task TheBoxCoversItsFirstAndLastPixelsAndNothingBeyond()
    {
        var annotations = new[] { new Annotation(new PixelRect(40, 50, 140, 120), "0") };
        var decoded = await PngTestDecoder.DecodeAsync(await new Win2DRenderer().RenderPngAsync(Solid(200, 150), new RenderOptions(1.0, annotations)));
        var colour = Opaque(Win2DRenderer.Palette[0]);
        Assert.Equal(colour, decoded.Pixel(40, 90));
        Assert.Equal(colour, decoded.Pixel(139, 90));
        Assert.Equal(Gray, decoded.Pixel(140, 90));
        Assert.Equal(colour, decoded.Pixel(90, 119));
        Assert.Equal(Gray, decoded.Pixel(90, 120));
        Assert.Equal(Gray, decoded.Pixel(90, 90));
    }

    [Fact]
    public async Task TheLabelIsRightAlignedJustAboveTheBox()
    {
        var annotations = new[] { new Annotation(new PixelRect(40, 60, 140, 120), "7") };
        var decoded = await PngTestDecoder.DecodeAsync(await new Win2DRenderer().RenderPngAsync(Solid(200, 150), new RenderOptions(1.0, annotations)));
        var colour = Opaque(Win2DRenderer.Palette[0]);
        Assert.Equal(colour, decoded.Pixel(139, 57)); // the label's bottom-right corner, 2 px above the box
        Assert.Equal(Gray, decoded.Pixel(139, 58));
    }

    [Fact]
    public async Task BoxesScaleWithTheImage()
    {
        var annotations = new[] { new Annotation(new PixelRect(80, 100, 280, 240), "0") };
        var decoded = await PngTestDecoder.DecodeAsync(await new Win2DRenderer().RenderPngAsync(Solid(400, 300), new RenderOptions(0.5, annotations)));
        Assert.Equal(Opaque(Win2DRenderer.Palette[0]), decoded.Pixel(40, 85));
    }

    [Fact]
    public async Task DrawsTheCursorMarkerAndTheGrid()
    {
        var options = new RenderOptions(1.0, [], new PixelPoint(100, 100), new GridLines(2, 2));
        var decoded = await PngTestDecoder.DecodeAsync(await new Win2DRenderer().RenderPngAsync(Solid(300, 200), options));
        var red = decoded.Pixel(100, 92); // on the vertical bar of the cross
        Assert.True(red.R > 200 && red.G < 60 && red.B < 60, $"not red: {red}");
        var grid = decoded.Pixel(150, 20); // the vertical grid line at 300 * 1 // 2
        Assert.NotEqual(Gray, grid);
    }

    private static CapturedImage Solid(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = Gray.B;
            bgra[i + 1] = Gray.G;
            bgra[i + 2] = Gray.R;
            bgra[i + 3] = Gray.A;
        }
        return new CapturedImage(width, height, bgra);
    }

    private static (byte R, byte G, byte B, byte A) Opaque((byte R, byte G, byte B) c) => (c.R, c.G, c.B, 0xFF);
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0246` for `AnnotationLayout`, `RenderOptions`, `PixelPoint` and `GridLines`.

- [ ] **Step 3: Implement**

Replace `windows/src/DesktopMcp.Desktop/Imaging/IImageRenderer.cs` with:

```csharp
namespace DesktopMcp.Desktop.Imaging;

/// <summary>A point in image pixels of the captured (unscaled) image.</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>Reference grid: <c>Columns</c> by <c>Rows</c> cells (upstream grid_lines, desktop/service.py:1427-1435).</summary>
public readonly record struct GridLines(int Columns, int Rows);

/// <summary>
/// What to draw over a capture. Annotation boxes and the cursor are in captured-image pixels; the renderer scales them
/// with the image (Decisions 12).
/// </summary>
public sealed record RenderOptions(double Scale, IReadOnlyList<Annotation> Annotations, PixelPoint? Cursor = null, GridLines? Grid = null)
{
    public static RenderOptions Plain(double scale) => new(scale, []);
}

public interface IImageRenderer
{
    /// <summary>
    /// Scales the image by <see cref="RenderOptions.Scale"/> (0 &lt; scale ≤ 1, output size truncated like PIL's resize),
    /// draws the grid, the annotations and the cursor marker, and encodes PNG. Initialises the Windows App SDK runtime.
    /// </summary>
    Task<byte[]> RenderPngAsync(CapturedImage image, RenderOptions options);
}
```

`windows/src/DesktopMcp.Desktop/Imaging/AnnotationLayout.cs`:

```csharp
namespace DesktopMcp.Desktop.Imaging;

/// <summary>
/// The geometry of the annotations (upstream desktop/service.py:1385-1486), kept apart from Win2D so it is unit-tested:
/// output size, box clamping and scaling, label placement and label text colour.
/// </summary>
internal static class AnnotationLayout
{
    /// <summary>Space between a label and its box, as upstream (label_y = top - label_height - 2).</summary>
    public const int LabelGap = 2;

    /// <summary>upstream desktop/service.py:258-262: int(width * scale), at least one pixel.</summary>
    public static (int Width, int Height) OutputSize(int width, int height, double scale) =>
        (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));

    /// <summary>
    /// Clamps a box to the captured image (upstream desktop/service.py:1441-1453) and scales it into the output image.
    /// Null when nothing of it is on the image.
    /// </summary>
    public static PixelRect? ScaleBox(PixelRect box, int imageWidth, int imageHeight, double scale, int outWidth, int outHeight)
    {
        var left = Math.Clamp(box.Left, 0, imageWidth);
        var top = Math.Clamp(box.Top, 0, imageHeight);
        var right = Math.Clamp(box.Right, 0, imageWidth);
        var bottom = Math.Clamp(box.Bottom, 0, imageHeight);
        if (right <= left || bottom <= top) return null;
        var scaledLeft = Math.Clamp((int)Math.Round(left * scale), 0, outWidth - 1);
        var scaledTop = Math.Clamp((int)Math.Round(top * scale), 0, outHeight - 1);
        var scaledRight = Math.Clamp((int)Math.Round(right * scale), scaledLeft + 1, outWidth);
        var scaledBottom = Math.Clamp((int)Math.Round(bottom * scale), scaledTop + 1, outHeight);
        return new PixelRect(scaledLeft, scaledTop, scaledRight, scaledBottom);
    }

    /// <summary>
    /// upstream desktop/service.py:1457-1463 and 1412-1415: right-aligned above the box, below it when there is no room
    /// above, then kept inside the image.
    /// </summary>
    public static PixelRect PlaceLabel(PixelRect box, int labelWidth, int labelHeight, int outWidth, int outHeight)
    {
        var x = box.Right - labelWidth;
        var y = box.Top - labelHeight - LabelGap;
        if (y < 0) y = box.Bottom + LabelGap;
        x = Math.Clamp(x, 0, Math.Max(0, outWidth - labelWidth));
        y = Math.Clamp(y, 0, Math.Max(0, outHeight - labelHeight));
        return new PixelRect(x, y, x + labelWidth, y + labelHeight);
    }

    /// <summary>Dark label text on light palette colours (yellow-green, cyan), white text on the others.</summary>
    public static bool UseDarkText((byte R, byte G, byte B) colour) => 0.299 * colour.R + 0.587 * colour.G + 0.114 * colour.B > 150;

    /// <summary>upstream desktop/service.py:1430-1435: line i of n at size * i // n; counts above the size are capped.</summary>
    public static IReadOnlyList<int> GridPositions(int count, int size)
    {
        count = Math.Min(count, size);
        var positions = new List<int>();
        for (var i = 1; i < count; i++) positions.Add((int)((long)size * i / count));
        return positions;
    }
}
```

Replace `windows/src/DesktopMcp.Desktop/Imaging/Win2DRenderer.cs` with:

```csharp
using System.Runtime.InteropServices.WindowsRuntime;
using DesktopMcp.Desktop.Errors;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using Windows.UI;

namespace DesktopMcp.Desktop.Imaging;

/// <summary>
/// Scales, annotates and PNG-encodes screenshots with Win2D (spec §6). The entry point initialises the Windows App SDK
/// runtime itself, which also installs the static Win2D activation (P0 follow-up).
/// </summary>
public sealed class Win2DRenderer : IImageRenderer
{
    private const float Dpi = 96;
    private const float StrokeWidth = 2;
    private const int LabelPadding = 2;
    private const float CursorRadius = 15;

    /// <summary>Box colours; annotation i uses Palette[i % Palette.Count].</summary>
    public static IReadOnlyList<(byte R, byte G, byte B)> Palette { get; } =
    [
        (230, 25, 75), (60, 180, 75), (0, 130, 200), (245, 130, 48), (145, 30, 180), (70, 240, 240),
        (240, 50, 230), (210, 245, 60), (0, 128, 128), (170, 110, 40), (128, 0, 0), (0, 0, 128),
    ];

    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color Black = Color.FromArgb(0xFF, 0, 0, 0);
    private static readonly Color Red = Color.FromArgb(0xFF, 0xFF, 0, 0);
    private static readonly Color Grid = Color.FromArgb(128, 200, 200, 200);

    private readonly Action _ensureRuntime;

    public Win2DRenderer() : this(AppRuntime.EnsureInitialized)
    {
    }

    internal Win2DRenderer(Action ensureRuntime) => _ensureRuntime = ensureRuntime;

    public async Task<byte[]> RenderPngAsync(CapturedImage image, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        if (!(options.Scale > 0 && options.Scale <= 1))
            throw new ArgumentOutOfRangeException(nameof(options), options.Scale, "Scale must be in (0, 1].");
        if (image.Width <= 0 || image.Height <= 0 || image.Bgra.Length != (long)image.Width * image.Height * 4)
            throw new ArgumentException($"A {image.Width}x{image.Height} image needs {(long)image.Width * image.Height * 4} bytes of BGRA, not {image.Bgra.Length}.", nameof(image));
        try
        {
            _ensureRuntime();
            for (var attempt = 0; ; attempt++)
            {
                var device = CanvasDevice.GetSharedDevice();
                try
                {
                    return await RenderAsync(device, image, options).ConfigureAwait(false);
                }
                catch (Exception e) when (attempt == 0 && device.IsDeviceLost(e.HResult))
                {
                    // A lost shared device is replaced by the next GetSharedDevice call; retry once on the new one.
                }
            }
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate("Image rendering", e);
        }
    }

    private static async Task<byte[]> RenderAsync(CanvasDevice device, CapturedImage image, RenderOptions options)
    {
        var (outWidth, outHeight) = AnnotationLayout.OutputSize(image.Width, image.Height, options.Scale);
        using var bitmap = CanvasBitmap.CreateFromBytes(
            device, image.Bgra, image.Width, image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, Dpi, CanvasAlphaMode.Premultiplied);
        using var target = new CanvasRenderTarget(device, outWidth, outHeight, Dpi);
        using (var session = target.CreateDrawingSession())
        {
            session.DrawImage(bitmap, new Rect(0, 0, outWidth, outHeight), new Rect(0, 0, image.Width, image.Height), 1f, CanvasImageInterpolation.HighQualityCubic);
            session.Antialiasing = CanvasAntialiasing.Aliased;
            session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            using var format = new CanvasTextFormat { FontFamily = "Arial", FontSize = 12 };
            if (options.Grid is { } grid) DrawGrid(session, grid, outWidth, outHeight);
            for (var i = 0; i < options.Annotations.Count; i++)
                DrawAnnotation(session, format, options.Annotations[i], Palette[i % Palette.Count], image, options.Scale, outWidth, outHeight);
            if (options.Cursor is { } cursor) DrawCursor(session, format, cursor, options.Scale, outWidth, outHeight);
        }

        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        var size = (uint)stream.Size;
        var buffer = await stream.ReadAsync(new Windows.Storage.Streams.Buffer(size), size, InputStreamOptions.None);
        return buffer.ToArray();
    }

    private static void DrawGrid(CanvasDrawingSession session, GridLines grid, int width, int height)
    {
        foreach (var x in AnnotationLayout.GridPositions(grid.Columns, width)) session.DrawLine(x + 0.5f, 0, x + 0.5f, height, Grid, 1);
        foreach (var y in AnnotationLayout.GridPositions(grid.Rows, height)) session.DrawLine(0, y + 0.5f, width, y + 0.5f, Grid, 1);
    }

    private static void DrawAnnotation(CanvasDrawingSession session, CanvasTextFormat format, Annotation annotation,
        (byte R, byte G, byte B) palette, CapturedImage image, double scale, int outWidth, int outHeight)
    {
        if (AnnotationLayout.ScaleBox(annotation.Box, image.Width, image.Height, scale, outWidth, outHeight) is not { } box) return;
        var colour = Color.FromArgb(0xFF, palette.R, palette.G, palette.B);
        if (box.Width >= 2 * StrokeWidth && box.Height >= 2 * StrokeWidth)
        {
            // The stroke is centred on the path: inset by half its width so it covers the box's first and last pixels.
            const float inset = StrokeWidth / 2;
            session.DrawRectangle(box.Left + inset, box.Top + inset, box.Width - StrokeWidth, box.Height - StrokeWidth, colour, StrokeWidth);
        }
        else
        {
            session.FillRectangle(box.Left, box.Top, box.Width, box.Height, colour);
        }
        using var label = new CanvasTextLayout(session, annotation.Label, format, float.MaxValue, float.MaxValue);
        var (width, height) = LabelSize(label);
        var place = AnnotationLayout.PlaceLabel(box, width, height, outWidth, outHeight);
        DrawLabel(session, label, place, colour, AnnotationLayout.UseDarkText(palette) ? Black : White);
    }

    // upstream desktop/service.py:1470-1484: a red ring with a cross and a "CURSOR" label, at a fixed size in the output.
    private static void DrawCursor(CanvasDrawingSession session, CanvasTextFormat format, PixelPoint cursor, double scale, int outWidth, int outHeight)
    {
        var x = (float)Math.Round(cursor.X * scale);
        var y = (float)Math.Round(cursor.Y * scale);
        session.DrawEllipse(x, y, CursorRadius, CursorRadius, Red, 3);
        session.DrawLine(x - CursorRadius, y, x + CursorRadius, y, Red, 2);
        session.DrawLine(x, y - CursorRadius, x, y + CursorRadius, Red, 2);
        using var label = new CanvasTextLayout(session, "CURSOR", format, float.MaxValue, float.MaxValue);
        var (width, height) = LabelSize(label);
        var left = Math.Clamp((int)(x + CursorRadius), 0, Math.Max(0, outWidth - width));
        var top = Math.Clamp((int)(y - CursorRadius), 0, Math.Max(0, outHeight - height));
        DrawLabel(session, label, new PixelRect(left, top, left + width, top + height), Red, White);
    }

    // upstream desktop/service.py:1408-1420: the text bounds plus 2 px on each side.
    private static (int Width, int Height) LabelSize(CanvasTextLayout layout) =>
        ((int)Math.Ceiling(layout.LayoutBounds.Width) + 2 * LabelPadding, (int)Math.Ceiling(layout.LayoutBounds.Height) + 2 * LabelPadding);

    private static void DrawLabel(CanvasDrawingSession session, CanvasTextLayout layout, PixelRect place, Color background, Color foreground)
    {
        session.FillRectangle(place.Left, place.Top, place.Width, place.Height, background);
        session.DrawTextLayout(layout, place.Left + LabelPadding, place.Top + LabelPadding, foreground);
    }
}
```

- [ ] **Step 4: Update the probe to the new renderer API**

In `windows/probes/DesktopMcp.Probe/Program.cs`, in `ProbeImagingAsync`, delete the line `DesktopMcp.Desktop.AppRuntime.EnsureInitialized();` (the renderer initialises the runtime itself; this is what the AOT activation check now proves) and replace

```csharp
    var png = await new DesktopMcp.Desktop.Imaging.Win2DRenderer().RenderPngAsync(new(width, height, bgra), 0.75, annotations);
```

with

```csharp
    var png = await new DesktopMcp.Desktop.Imaging.Win2DRenderer().RenderPngAsync(new(width, height, bgra),
        new DesktopMcp.Desktop.Imaging.RenderOptions(0.75, annotations, new DesktopMcp.Desktop.Imaging.PixelPoint(960, 540), new DesktopMcp.Desktop.Imaging.GridLines(4, 3)));
```

In `ProbeCaptureAsync`, replace

```csharp
    var png = await new DesktopMcp.Desktop.Imaging.Win2DRenderer().RenderPngAsync(image, 1.0, []);
```

with

```csharp
    var png = await new DesktopMcp.Desktop.Imaging.Win2DRenderer().RenderPngAsync(image, DesktopMcp.Desktop.Imaging.RenderOptions.Plain(1.0));
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~AnnotationLayoutTests|FullyQualifiedName~Win2DRendererValidationTests"`
Expected: `Passed!` with `Failed: 0` (7 tests).

Run (interactive session): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~Win2DRendererTests"`
Expected: `Passed!` with `Failed: 0` (6 tests): truncated output size, the box's right and bottom edges, the label right-aligned 2 px above the box, scaling, the cursor marker and the grid.

Run: `pwsh scripts/build-release.ps1 -Runtimes win-x64`
Expected: as in Task 1 Step 9, with `AOT activation check OK` although the probe no longer initialises the runtime itself.

- [ ] **Step 6: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Imaging windows/probes/DesktopMcp.Probe/Program.cs windows/tests/DesktopMcp.Desktop.Tests/Win2DRendererTests.cs
git commit -m "Render the grid, labelled boxes and cursor marker with Win2D and initialise the runtime in the renderer" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 15: Multi-monitor capture and the capture follow-ups

Spec §6: one Windows.Graphics.Capture item per monitor from its display id, composited for areas that span monitors (Decisions 14). Closes the P0 `GraphicsCaptureService` follow-ups: the late frame and the `FrameArrived` handler, multithread protection through Win2D's device lock, `Pack` internal and tested, `LastBorderSuppressed` off the public API, `GraphicsCaptureAccess.RequestAccessAsync(Borderless)`, the runtime initialised in the entry point, and COM translation. The border check with the user watching is Task 19 Step 6.

**Files:**
- Modify: `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (append)
- Replace: `windows/src/DesktopMcp.Desktop/Capture/IScreenCapture.cs`, `GraphicsCaptureService.cs`
- Create: `windows/src/DesktopMcp.Desktop/Capture/FrameCompositor.cs`
- Modify: `windows/probes/DesktopMcp.Probe/Program.cs`
- Test: replace `windows/tests/DesktopMcp.Desktop.Tests/GraphicsCaptureServiceTests.cs` (one class Category=Desktop)

**Interfaces:**
- Consumes: `ScreenRect` (Task 2), `ComErrors`, `DesktopException` (Task 5), `AppRuntime` (Task 6), `Win32Displays` (Task 8), `RenderOptions` (Task 14).
- Produces:
  - `public interface IScreenCapture { Task<CapturedImage> CaptureAsync(ScreenRect area, IReadOnlyList<ScreenRect> monitors, CancellationToken); }` (replaces `CapturePrimaryMonitorAsync`).
  - `GraphicsCaptureService` with internal ctor `(Action ensureRuntime)`, `static bool IsSupported`, internal `static bool? LastBorderSuppressed`, internal `static string? BorderlessAccess`, internal `static byte[] Pack(byte[], int, int)`, internal `FrameTimeout` (2 s) and `AccessTimeout` (2 s).
  - internal `static class FrameCompositor { CapturedImage Compose(ScreenRect area, IReadOnlyList<(ScreenRect Monitor, CapturedImage Frame)> frames); }`.

- [ ] **Step 1: Write the failing tests**

Replace `windows/tests/DesktopMcp.Desktop.Tests/GraphicsCaptureServiceTests.cs` with:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Capture;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Tests;

public class FrameCompositorTests
{
    private static CapturedImage Solid(int width, int height, byte value) =>
        new(width, height, Enumerable.Repeat(value, width * height * 4).ToArray());

    [Fact]
    public void ASingleFrameCoveringTheAreaIsReturnedAsIs()
    {
        var frame = Solid(4, 2, 7);
        Assert.Same(frame, FrameCompositor.Compose(new ScreenRect(0, 0, 4, 2), [(new ScreenRect(0, 0, 4, 2), frame)]));
    }

    [Fact]
    public void FramesArePlacedAtTheirOffsetAndGapsAreOpaqueBlack()
    {
        // Two 2x2 monitors side by side at x = -2 and x = 2; the area spans both and the 2-pixel gap between them.
        var image = FrameCompositor.Compose(new ScreenRect(-2, 0, 4, 2),
            [(new ScreenRect(-2, 0, 0, 2), Solid(2, 2, 10)), (new ScreenRect(2, 0, 4, 2), Solid(2, 2, 20))]);
        Assert.Equal((6, 2), (image.Width, image.Height));
        byte[] Pixel(int x, int y) => image.Bgra.AsSpan((y * 6 + x) * 4, 4).ToArray();
        Assert.Equal([10, 10, 10, 10], Pixel(1, 1));
        Assert.Equal([0, 0, 0, 255], Pixel(2, 0));
        Assert.Equal([20, 20, 20, 20], Pixel(5, 0));
    }

    [Fact]
    public void ARegionCropsTheFrame()
    {
        var frame = new CapturedImage(3, 1, [1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3]);
        var image = FrameCompositor.Compose(new ScreenRect(11, 0, 13, 1), [(new ScreenRect(10, 0, 13, 1), frame)]);
        Assert.Equal([2, 2, 2, 2, 3, 3, 3, 3], image.Bgra);
    }
}

public class GraphicsCaptureServiceTests
{
    [Fact]
    public void PackRemovesRowPadding()
    {
        // 2x2 pixels in rows padded to 12 bytes.
        byte[] padded = [1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 3, 3, 3, 3, 4, 4, 4, 4, 0, 0, 0, 0];
        Assert.Equal([1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4], GraphicsCaptureService.Pack(padded, 2, 2));
        var packed = new byte[16];
        Assert.Same(packed, GraphicsCaptureService.Pack(packed, 2, 2));
        Assert.Throws<DesktopException>(() => GraphicsCaptureService.Pack(new byte[10], 2, 2));
    }

    [Fact]
    public async Task TheRuntimeIsInitialisedFirstAndItsInstallHintIsNotWrapped()
    {
        var service = new GraphicsCaptureService(() => throw new InvalidOperationException("install the runtime"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CaptureAsync(new ScreenRect(0, 0, 10, 10), [new ScreenRect(0, 0, 10, 10)], default));
        Assert.Equal("install the runtime", error.Message);
    }

    [Fact]
    public async Task AnEmptyAreaIsRejected() =>
        await Assert.ThrowsAsync<ArgumentException>(() => new GraphicsCaptureService(() => { }).CaptureAsync(new ScreenRect(5, 5, 5, 9), [], default));
}

[Trait("Category", "Desktop")]
public class GraphicsCaptureServiceDesktopTests
{
    public GraphicsCaptureServiceDesktopTests() => TestDpi.EnsurePerMonitorV2();

    [Fact]
    public async Task CapturesThePrimaryMonitor()
    {
        Assert.True(GraphicsCaptureService.IsSupported);
        var primary = new Win32Displays().GetDisplays().Single(d => d.Primary).Bounds;
        var image = await new GraphicsCaptureService().CaptureAsync(primary, [primary], CancellationToken.None);

        Assert.Equal((primary.Width, primary.Height), (image.Width, image.Height));
        Assert.Equal(image.Width * image.Height * 4, image.Bgra.Length);
        var nonBlack = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4 * 97)
            if (image.Bgra[i] != 0 || image.Bgra[i + 1] != 0 || image.Bgra[i + 2] != 0) nonBlack++;
        Assert.True(nonBlack > 100, "the capture is entirely black");
    }

    [Fact]
    public async Task CapturesTheWholeVirtualScreenAndARegion()
    {
        var displays = new Win32Displays();
        var monitors = displays.GetDisplays().Select(d => d.Bounds).ToArray();
        var screen = displays.GetVirtualScreen();
        var service = new GraphicsCaptureService();

        var whole = await service.CaptureAsync(screen, monitors, CancellationToken.None);
        Assert.Equal((screen.Width, screen.Height), (whole.Width, whole.Height));

        var region = new ScreenRect(screen.Left + 10, screen.Top + 20, screen.Left + 210, screen.Top + 120);
        var part = await service.CaptureAsync(region, monitors, CancellationToken.None);
        Assert.Equal((200, 100), (part.Width, part.Height));
        Assert.NotNull(GraphicsCaptureService.BorderlessAccess);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0246` (`FrameCompositor`), `CS0122` (`Pack` is inaccessible) and `CS1061` (`CaptureAsync`).

- [ ] **Step 3: Implement**

Append to `windows/src/DesktopMcp.Desktop/NativeMethods.txt`:

```text
// Capture: the monitor behind a display rectangle
MonitorFromRect
```

Replace `windows/src/DesktopMcp.Desktop/Capture/IScreenCapture.cs` with:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Capture;

public interface IScreenCapture
{
    /// <summary>
    /// Captures <paramref name="area"/> (physical virtual-desktop pixels) from the monitors in <paramref name="monitors"/>
    /// that overlap it; pixels no monitor covers are black. Failures are <see cref="Errors.DesktopException"/>s, and a
    /// missing Windows App SDK runtime is an <see cref="InvalidOperationException"/> with the install hint.
    /// </summary>
    Task<CapturedImage> CaptureAsync(ScreenRect area, IReadOnlyList<ScreenRect> monitors, CancellationToken cancellationToken);
}
```

`windows/src/DesktopMcp.Desktop/Capture/FrameCompositor.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Capture;

/// <summary>
/// Places monitor frames into one image of a virtual-desktop rectangle (spec §6: a full virtual-desktop capture
/// composites the monitors it covers). Pixels no frame covers are opaque black, as mss and PIL leave them upstream.
/// </summary>
internal static class FrameCompositor
{
    public static CapturedImage Compose(ScreenRect area, IReadOnlyList<(ScreenRect Monitor, CapturedImage Frame)> frames)
    {
        if (frames is [var (monitor, frame)] && monitor == area && frame.Width == area.Width && frame.Height == area.Height)
            return frame;
        var width = area.Width;
        var height = area.Height;
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;
        foreach (var (bounds, image) in frames)
        {
            // The frame may be smaller than the monitor rectangle (a mode change between the two reads): copy what overlaps.
            var source = new ScreenRect(bounds.Left, bounds.Top, bounds.Left + image.Width, bounds.Top + image.Height);
            if (ScreenRect.Clip(source, area) is not { } overlap) continue;
            var rowBytes = overlap.Width * 4;
            for (var y = overlap.Top; y < overlap.Bottom; y++)
            {
                var from = ((y - source.Top) * image.Width + (overlap.Left - source.Left)) * 4;
                var to = ((y - area.Top) * width + (overlap.Left - area.Left)) * 4;
                Buffer.BlockCopy(image.Bgra, from, pixels, to, rowBytes);
            }
        }
        return new CapturedImage(width, height, pixels);
    }
}
```

Replace `windows/src/DesktopMcp.Desktop/Capture/GraphicsCaptureService.cs` with:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace DesktopMcp.Desktop.Capture;

/// <summary>
/// Screen capture through Windows.Graphics.Capture, one display item per monitor (spec §6). Frames are read through
/// Win2D's shared device; the entry point initialises the Windows App SDK runtime itself (P0 follow-up).
/// </summary>
public sealed class GraphicsCaptureService : IScreenCapture
{
    /// <summary>How long one monitor may take to deliver its first frame.</summary>
    internal static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long the one-time borderless access request may take.</summary>
    internal static readonly TimeSpan AccessTimeout = TimeSpan.FromSeconds(2);

    private readonly Action _ensureRuntime;
    private int _accessRequested;

    public GraphicsCaptureService() : this(AppRuntime.EnsureInitialized)
    {
    }

    internal GraphicsCaptureService(Action ensureRuntime) => _ensureRuntime = ensureRuntime;

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <summary>Diagnostics: whether the last capture could turn the capture border off (null before the first).</summary>
    internal static bool? LastBorderSuppressed { get; private set; }

    /// <summary>Diagnostics: the result of GraphicsCaptureAccess.RequestAccessAsync(Borderless), or why it failed.</summary>
    internal static string? BorderlessAccess { get; private set; }

    public async Task<CapturedImage> CaptureAsync(ScreenRect area, IReadOnlyList<ScreenRect> monitors, CancellationToken cancellationToken)
    {
        if (area.Width <= 0 || area.Height <= 0) throw new ArgumentException($"The capture area {area.ToXyxy()} is empty.", nameof(area));
        try
        {
            _ensureRuntime();
            await RequestBorderlessAccessOnceAsync().ConfigureAwait(false);
            var frames = new List<(ScreenRect, CapturedImage)>();
            foreach (var monitor in monitors)
            {
                if (ScreenRect.Clip(monitor, area) is null) continue;
                frames.Add((monitor, await CaptureMonitorAsync(monitor, cancellationToken).ConfigureAwait(false)));
            }
            return FrameCompositor.Compose(area, frames);
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate("Screen capture", e);
        }
    }

    private static async Task<CapturedImage> CaptureMonitorAsync(ScreenRect bounds, CancellationToken cancellationToken)
    {
        var item = CreateItem(bounds);
        for (var attempt = 0; ; attempt++)
        {
            var device = CanvasDevice.GetSharedDevice();
            try
            {
                return await CaptureFrameAsync(item, device, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt == 0 && device.IsDeviceLost(e.HResult))
            {
                // A lost shared device is replaced by the next GetSharedDevice call; retry once on the new one.
            }
        }
    }

    private static async Task<CapturedImage> CaptureFrameAsync(GraphicsCaptureItem item, CanvasDevice device, CancellationToken cancellationToken)
    {
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        LastBorderSuppressed = TrySuppressBorder(session);

        var firstFrame = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame is not null && !firstFrame.TrySetResult(frame)) frame.Dispose();
            }
            catch (Exception e)
            {
                firstFrame.TrySetException(e);
            }
        }

        pool.FrameArrived += OnFrameArrived;
        var consumed = false;
        try
        {
            session.StartCapture();
            using var frame = await firstFrame.Task.WaitAsync(FrameTimeout, cancellationToken).ConfigureAwait(false);
            consumed = true;
            // The frame pool delivers on its own thread; the device lock keeps Win2D's use of the shared device serial.
            using (device.Lock())
            {
                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
                var width = (int)bitmap.SizeInPixels.Width;
                var height = (int)bitmap.SizeInPixels.Height;
                return new CapturedImage(width, height, Pack(bitmap.GetPixelBytes(), width, height));
            }
        }
        catch (TimeoutException)
        {
            throw new DesktopException($"Screen capture failed: no frame arrived within {FrameTimeout.TotalSeconds:0.#} s.");
        }
        finally
        {
            pool.FrameArrived -= OnFrameArrived;
            // A frame that arrived after the timeout or cancellation is ours to dispose.
            if (!consumed && firstFrame.Task.IsCompletedSuccessfully) firstFrame.Task.Result.Dispose();
        }
    }

    /// <summary>Returns rows packed at Width * 4 bytes, repacking when the source rows are padded.</summary>
    internal static byte[] Pack(byte[] pixels, int width, int height)
    {
        var rowBytes = width * 4;
        if (pixels.Length == rowBytes * height)
            return pixels;
        if (height == 0 || pixels.Length % height != 0 || pixels.Length / height < rowBytes)
            throw new DesktopException($"Screen capture failed: unexpected frame buffer of {pixels.Length} bytes for {width}x{height}.");
        var stride = pixels.Length / height;
        var packed = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * stride, packed, y * rowBytes, rowBytes);
        return packed;
    }

    // spec §6: requested once, where available; unpackaged processes may be refused, which only keeps the border.
    private async Task RequestBorderlessAccessOnceAsync()
    {
        if (Interlocked.Exchange(ref _accessRequested, 1) != 0) return;
        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask()
                .WaitAsync(AccessTimeout).ConfigureAwait(false);
            BorderlessAccess = status.ToString();
        }
        catch (Exception e)
        {
            BorderlessAccess = $"{e.GetType().Name} 0x{e.HResult:X8}";
        }
    }

    private static bool TrySuppressBorder(GraphicsCaptureSession session)
    {
        try
        {
            session.IsBorderRequired = false;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Unpackaged apps may lack the graphicsCaptureWithoutBorder capability; the border then stays.
            return false;
        }
    }

    // spec §6: the capture item comes from the monitor's display id.
    private static GraphicsCaptureItem CreateItem(ScreenRect bounds)
    {
        var rect = new RECT { left = bounds.Left, top = bounds.Top, right = bounds.Right, bottom = bounds.Bottom };
        var monitor = PInvoke.MonitorFromRect(rect, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
        if (monitor.IsNull) throw new DesktopException($"Screen capture failed: no monitor at {bounds.ToXyxy()}.");
        var displayId = Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor(monitor);
        return GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId { Value = displayId.Value })
            ?? throw new DesktopException($"Screen capture failed: Windows.Graphics.Capture could not open display {displayId.Value}.");
    }
}
```

- [ ] **Step 4: Capture the whole virtual screen in the probe**

In `windows/probes/DesktopMcp.Probe/Program.cs`, replace the whole `ProbeCaptureAsync` function with:

```csharp
static async Task<bool> ProbeCaptureAsync(string path)
{
    Console.WriteLine($"capture: supported={DesktopMcp.Desktop.Capture.GraphicsCaptureService.IsSupported}");
    var displays = new DesktopMcp.Desktop.Displays.Win32Displays();
    var monitors = displays.GetDisplays().Select(d => d.Bounds).ToArray();
    var clock = Stopwatch.StartNew();
    // The whole virtual screen: every monitor, composited (the runtime is initialised by the capture itself).
    var image = await new DesktopMcp.Desktop.Capture.GraphicsCaptureService().CaptureAsync(displays.GetVirtualScreen(), monitors, CancellationToken.None);
    var captureMs = clock.ElapsedMilliseconds;
    var png = await new DesktopMcp.Desktop.Imaging.Win2DRenderer().RenderPngAsync(image, DesktopMcp.Desktop.Imaging.RenderOptions.Plain(1.0));
    File.WriteAllBytes(path, png);
    Console.WriteLine($"capture: {monitors.Length} monitor(s), borderSuppressed={DesktopMcp.Desktop.Capture.GraphicsCaptureService.LastBorderSuppressed}, " +
        $"borderlessAccess={DesktopMcp.Desktop.Capture.GraphicsCaptureService.BorderlessAccess}");
    Console.WriteLine($"capture: {image.Width}x{image.Height} in {captureMs} ms, png {png.Length / 1024} KB -> {Path.GetFullPath(path)}");
    return image.Width > 0 && CheckWin2DLinkage("capture");
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~FrameCompositorTests|FullyQualifiedName~GraphicsCaptureServiceTests"`
Expected: `Passed!` with `Failed: 0` (6 tests; the desktop class is excluded by the default filter).

Run (interactive session): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~GraphicsCaptureServiceDesktopTests"`
Expected: `Passed!` with `Failed: 0` (2 tests): the primary monitor, the whole virtual screen and a 200x100 region.

Run:
```powershell
pwsh scripts/build-release.ps1 -Runtimes win-x64
probes/out/desktop-mcp-probe.exe capture $env:TEMP\p2-capture.png
Remove-Item $env:TEMP\p2-capture.png
```
Expected: the release script as in Task 1 Step 9; then `capture: supported=True`, `capture: <n> monitor(s), borderSuppressed=True, borderlessAccess=<Allowed|...>`, `capture: <w>x<h> in <ms> ms, png <n> KB -> ...` where `<w>x<h>` is the virtual screen size, and `capture: nativeAot=True, Microsoft.Graphics.Canvas.dll loaded=False`. Record the `borderlessAccess` value for Task 19.

- [ ] **Step 6: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/NativeMethods.txt windows/src/DesktopMcp.Desktop/Capture windows/probes/DesktopMcp.Probe/Program.cs windows/tests/DesktopMcp.Desktop.Tests/GraphicsCaptureServiceTests.cs
git commit -m "Capture any virtual-desktop rectangle from per-monitor Graphics Capture frames" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 16: Snapshot service

upstream `Desktop.get_state` (`desktop/service.py:87-315`) and the checks of `capture_desktop_state` (`tools/_snapshot_helpers.py:175-215`): argument validation in upstream's order, the session check (Decisions 18), the window scan, the tree walk over the focused window, the non-application windows and, with a capture rectangle, the application windows inside it, the region filter, capture and rendering, and the shared state store (spec §5). Every machine dependency is injected, so the orchestration is unit-tested; one Desktop test runs the real stack against the test window.

**Files:**
- Modify: `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (append)
- Create: `windows/src/DesktopMcp.Desktop/Snapshots/DesktopSession.cs`, `SnapshotService.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Snapshots/SnapshotServiceTests.cs` (one class Category=Desktop)

**Interfaces:**
- Consumes: everything from Tasks 2-3 and 8-15: `SnapshotSelection`, `RegionFilter`, `SnapshotSettings`, `IDisplayProvider`, `IVirtualDesktops`, `IWindowScanner`/`WindowScan`/`WindowCatalog`, `ITreeCapture`/`TreeService`, `UiaWorker`, `HangBlockList`, `UiaWindowProbe`, `UiaWindowWalker`, `Win32WindowSystem`, `IScreenCapture`, `IImageRenderer`, `RenderOptions`, `Annotation`, `PixelRect`, `PixelPoint`, `GridLines`.
- Produces (namespace `DesktopMcp.Desktop.Snapshots`):
  - `public sealed record SnapshotRequest` (init `UseVision` false, `UseDom` false, `UseAnnotation` true, `UseUiTree` true, `long? WidthReferenceLine`, `long? HeightReferenceLine`, `IReadOnlyList<long>? Displays`, `RegionArgument? Region`).
  - `public sealed class DesktopStateStore` (`DesktopSnapshot? Latest`; internal `Replace`).
  - `public interface IDesktopSnapshots { DesktopStateStore State { get; } Task<DesktopSnapshot> CaptureAsync(SnapshotRequest, CancellationToken); }`.
  - `public sealed class SnapshotService : IDesktopSnapshots`: `const string Backend = "wgc"`, `const string UseDomUnavailable`, public parameterless ctor (cheap: no thread, COM or runtime), internal ctor `(SnapshotDependencies)`.
  - internal `sealed record SnapshotDependencies` (required `Displays`, `Windows`, `Tree`, `Desktops`, `Capture`, `Renderer`, `Func<ScreenPoint> Cursor`, `Action EnsureSession`, `Func<string, string?> Environment`, `TextWriter ProfileLog`; `static CreateDefault()`).
  - internal `static class DesktopSession { ScreenPoint CursorPosition(); void ThrowIfUnavailable(); }`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/Snapshots/SnapshotServiceTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Capture;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Tests.Snapshots;

public class SnapshotServiceTests
{
    private static readonly DisplayInfo Left = new(1, @"\\.\DISPLAY2", new ScreenRect(-1920, 0, 0, 1080), false, null, 96, 1.0, "landscape");
    private static readonly DisplayInfo Main = new(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1600), true, null, 144, 1.5, "landscape");
    private static readonly ScreenRect Screen = new(-1920, 0, 2560, 1600);

    private static readonly WindowInfo Notepad = new("Notepad", 1, WindowStatus.Normal, new ScreenRect(100, 100, 900, 700), 11, 50, false);
    private static readonly WindowInfo Paint = new("Paint", 2, WindowStatus.Normal, new ScreenRect(-1500, 100, -500, 700), 12, 60, false);

    private sealed class FakeDisplays : IDisplayProvider
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => [Main, Left];
        public ScreenRect GetVirtualScreen() => Screen;
    }

    private sealed class FakeWindows(WindowScan scan) : IWindowScanner
    {
        public int Scans { get; private set; }
        public Task<WindowScan> ScanAsync(CancellationToken cancellationToken)
        {
            Scans++;
            return Task.FromResult(scan);
        }
    }

    private sealed class FakeTree : ITreeCapture
    {
        public (long? Active, long[] Others)? Request { get; private set; }

        public Task<TreeSnapshot> CaptureAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, CancellationToken cancellationToken)
        {
            Request = (active, others.ToArray());
            var near = new UiElement(0, "Save", "button", "Notepad", new ScreenRect(110, 110, 150, 130), new ScreenPoint(130, 120), UiMetadata.Empty);
            var far = new UiElement(1, "Brush", "button", "Paint", new ScreenRect(-1400, 110, -1300, 130), new ScreenPoint(-1350, 120), UiMetadata.Empty);
            var root = new SemanticNode(SemanticKind.Desktop, "Desktop", "Desktop");
            return Task.FromResult(new TreeSnapshot([near, far], [], root, false, 500));
        }
    }

    private sealed class FakeDesktops : IVirtualDesktops
    {
        public IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows) => windows;
        public (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops() =>
            (new VirtualDesktopInfo("{A}", "Desktop 1"), [new VirtualDesktopInfo("{A}", "Desktop 1")]);
    }

    private sealed class FakeCapture : IScreenCapture
    {
        public ScreenRect? Area { get; private set; }
        public Task<CapturedImage> CaptureAsync(ScreenRect area, IReadOnlyList<ScreenRect> monitors, CancellationToken cancellationToken)
        {
            Area = area;
            return Task.FromResult(new CapturedImage(area.Width, area.Height, []));
        }
    }

    private sealed class FakeRenderer : IImageRenderer
    {
        public RenderOptions? Options { get; private set; }
        public Task<byte[]> RenderPngAsync(CapturedImage image, RenderOptions options)
        {
            Options = options;
            return Task.FromResult<byte[]>([0x89, 0x50]);
        }
    }

    private readonly FakeWindows _windows = new(new WindowScan([11, 12, 13], [Notepad, Paint], Notepad));
    private readonly FakeTree _tree = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeRenderer _renderer = new();
    private readonly Dictionary<string, string> _environment = [];
    private readonly StringWriter _profile = new();

    private SnapshotService Service(Action? ensureSession = null) => new(new SnapshotDependencies
    {
        Displays = new FakeDisplays(),
        Windows = _windows,
        Tree = _tree,
        Desktops = new FakeDesktops(),
        Capture = _capture,
        Renderer = _renderer,
        Cursor = () => new ScreenPoint(120, 120),
        EnsureSession = ensureSession ?? (() => { }),
        Environment = name => _environment.GetValueOrDefault(name),
        ProfileLog = _profile,
    });

    [Fact]
    public async Task ASnapshotWalksTheFocusedWindowFirstAndTheNonApplicationWindows()
    {
        var snapshot = await Service().CaptureAsync(new SnapshotRequest(), default);
        Assert.Equal(11, _tree.Request!.Value.Active);
        Assert.Equal([13L], _tree.Request.Value.Others);
        Assert.Same(Notepad, snapshot.ActiveWindow);
        Assert.Equal([Paint], snapshot.Windows);
        Assert.Equal(new ScreenPoint(120, 120), snapshot.Cursor);
        Assert.Null(snapshot.Png);
        Assert.Null(snapshot.Backend);
        Assert.Null(_capture.Area);
    }

    [Fact]
    public async Task TheStoreHoldsTheLatestSnapshot()
    {
        var service = Service();
        var first = await service.CaptureAsync(new SnapshotRequest(), default);
        Assert.Same(first, service.State.Latest);
        var second = await service.CaptureAsync(new SnapshotRequest { UseUiTree = false }, default);
        Assert.Same(second, service.State.Latest);
    }

    [Fact]
    public async Task AScreenshotSkipsTheWindowScanAndCapturesTheVirtualScreen()
    {
        var snapshot = await Service().CaptureAsync(new SnapshotRequest { UseVision = true, UseUiTree = false, UseAnnotation = false }, default);
        Assert.Equal(0, _windows.Scans);
        Assert.Null(_tree.Request);
        Assert.Equal(Screen, _capture.Area);
        Assert.Equal(new PixelSize(4480, 1600), snapshot.OriginalSize);
        Assert.Equal(1920.0 / 4480, snapshot.Scale);
        Assert.Equal("wgc", snapshot.Backend);
        Assert.Equal(RenderOptions.Plain(1920.0 / 4480).Scale, _renderer.Options!.Scale);
        Assert.Empty(_renderer.Options.Annotations);
        Assert.Same(TreeSnapshot.Empty, snapshot.Tree);
    }

    [Fact]
    public async Task ARegionFiltersWindowsElementsAndTheCursorAndOffsetsTheAnnotations()
    {
        var request = new SnapshotRequest
        {
            UseVision = true, Region = new RegionArgument([100, 100, 1000, 800], null), WidthReferenceLine = 3, HeightReferenceLine = 2,
        };
        var snapshot = await Service().CaptureAsync(request, default);

        Assert.Equal(new ScreenRect(100, 100, 1000, 800), snapshot.Region);
        Assert.Empty(snapshot.Windows);                     // Paint is outside the region
        Assert.Equal(["Save"], snapshot.Tree.Interactive.Select(e => e.Name));
        var options = _renderer.Options!;
        Assert.Equal(new Annotation(new PixelRect(10, 10, 50, 30), "0"), Assert.Single(options.Annotations));
        Assert.Equal(new PixelPoint(20, 20), options.Cursor);
        Assert.Equal(new GridLines(3, 2), options.Grid);
        Assert.Equal(new ScreenRect(100, 100, 1000, 800), _capture.Area);
    }

    [Fact]
    public async Task ADisplaySelectionCapturesTheUnionAndWalksApplicationWindowsInside()
    {
        var snapshot = await Service().CaptureAsync(new SnapshotRequest { UseVision = true, Displays = [1, 1] }, default);
        Assert.Equal(Left.Bounds, _capture.Area);
        Assert.Equal([1L], snapshot.SelectedDisplays);
        Assert.Null(_tree.Request!.Value.Active);         // Notepad is on the other display
        Assert.Equal([12L, 13L], _tree.Request.Value.Others);
        Assert.Null(snapshot.Cursor);
    }

    [Theory]
    [InlineData(true, false, "use_dom=True requires use_ui_tree=True")]
    [InlineData(true, true, SnapshotService.UseDomUnavailable)]
    public async Task UseDomIsRejected(bool useDom, bool useUiTree, string message)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Service().CaptureAsync(new SnapshotRequest { UseDom = useDom, UseUiTree = useUiTree }, default));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task SelectionErrorsUseUpstreamMessages()
    {
        var invalid = await Assert.ThrowsAsync<ArgumentException>(() => Service().CaptureAsync(new SnapshotRequest { Displays = [5] }, default));
        Assert.Equal("Invalid display index 5. Available displays: 0,1", invalid.Message);
        var region = await Assert.ThrowsAsync<ArgumentException>(() => Service().CaptureAsync(new SnapshotRequest { Region = new RegionArgument(null, "[1,2]") }, default));
        Assert.Equal(SnapshotSelection.RegionShapeMessage, region.Message);
    }

    [Fact]
    public async Task NoInteractiveSessionFailsBeforeAnyDesktopRead()
    {
        var error = await Assert.ThrowsAsync<DesktopException>(() => Service(() => throw new DesktopException("locked")).CaptureAsync(new SnapshotRequest(), default));
        Assert.Equal("locked", error.Message);
        Assert.Equal(0, _windows.Scans);
    }

    [Fact]
    public async Task ProfilingWritesOneLineOfStageTimings()
    {
        _environment[SnapshotSettings.ProfileSnapshotVariable] = "1";
        await Service().CaptureAsync(new SnapshotRequest(), default);
        Assert.StartsWith("Snapshot profile: desktop_context_ms=", _profile.ToString());
        Assert.Contains("use_vision=False use_dom=False use_ui_tree=True use_annotation=True displays=None", _profile.ToString());
    }
}

[Trait("Category", "Desktop")]
public sealed class SnapshotServiceDesktopTests : IDisposable
{
    private readonly TestWindow _window = new(left: 120, top: 120, width: 420, height: 260);
    private readonly UiaWorker<UiaSession> _uia = new(UiaSession.Create);

    public void Dispose()
    {
        _uia.Dispose();
        _window.Dispose();
    }

    [Fact]
    public async Task SnapshotsTheTestWindowRegionWithAnAnnotatedImage()
    {
        var blocks = new HangBlockList(TimeProvider.System, HangBlockList.DefaultDuration);
        var system = new Win32WindowSystem();
        // ownProcessId -1: the test window belongs to this process, which the server itself would skip.
        var machine = SnapshotDependencies.CreateDefault() with
        {
            Windows = new WindowCatalog(system, new UiaWindowProbe(_uia), new VirtualDesktopService(), blocks, -1, Task.Delay),
            Tree = new TreeService(system, new UiaWindowWalker(_uia), blocks, _ => null, Task.Delay),
        };
        var request = new SnapshotRequest { UseVision = true, Region = new RegionArgument([120, 120, 540, 380], null) };

        var snapshot = await new SnapshotService(machine).CaptureAsync(request, default);
        var text = SnapshotText.Render(snapshot, includeUiDetails: true, uiDetailNote: null);

        Assert.Contains($"window \"{_window.Title}\"", text);
        Assert.Contains($"\"{TestWindow.ButtonText}\"  [action: click]", text);
        Assert.Contains("Screenshot Region: (120,120,540,380)", text);
        var png = await PngTestDecoder.DecodeAsync(snapshot.Png!);
        Assert.Equal((420, 260), (png.Width, png.Height));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL with `CS0234` (`DesktopMcp.Desktop.Snapshots` does not exist).

- [ ] **Step 3: Implement**

Append to `windows/src/DesktopMcp.Desktop/NativeMethods.txt`:

```text
// Snapshot: the cursor and the interactive-session check (spec §5)
GetCursorPos
ProcessIdToSessionId
OpenInputDesktop
GetUserObjectInformation
CloseDesktop
```

`windows/src/DesktopMcp.Desktop/Snapshots/DesktopSession.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;

namespace DesktopMcp.Desktop.Snapshots;

/// <summary>The cursor position and whether a desktop the user sees is available (spec §5).</summary>
internal static unsafe class DesktopSession
{
    /// <summary>upstream uia/core.py:176-184 GetCursorPos.</summary>
    public static ScreenPoint CursorPosition()
    {
        PInvoke.GetCursorPos(out var point);
        return new ScreenPoint(point.X, point.Y);
    }

    /// <summary>
    /// Throws a <see cref="DesktopException"/> when there is no interactive session (session 0, a service) or the input
    /// desktop is not the user's "Default" desktop (the lock screen or a UAC prompt) (Decisions 18).
    /// </summary>
    public static void ThrowIfUnavailable()
    {
        if (!PInvoke.ProcessIdToSessionId((uint)Environment.ProcessId, out var session) || session == 0)
            throw new DesktopException("No interactive desktop: the server runs in session 0 (as a service). Run it in a signed-in user's session to use the desktop tools.");
        var desktop = PInvoke.OpenInputDesktop(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);
        if (desktop.IsNull)
            throw new DesktopException("The desktop is not available: the screen is locked or a secure desktop (such as a UAC prompt) is showing.");
        try
        {
            var name = stackalloc char[64];
            uint needed;
            if (PInvoke.GetUserObjectInformation((HANDLE)(nint)desktop.Value, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, name, 128, &needed)
                && !new string(name).Equals("Default", StringComparison.OrdinalIgnoreCase))
                throw new DesktopException("The desktop is not available: the screen is locked or a secure desktop (such as a UAC prompt) is showing.");
        }
        finally
        {
            PInvoke.CloseDesktop(desktop);
        }
    }
}
```

`windows/src/DesktopMcp.Desktop/Snapshots/SnapshotService.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Capture;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Imaging;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Snapshots;

/// <summary>A Snapshot or Screenshot request after the argument type checks (upstream tools/snapshot.py:37-60, 86-106).</summary>
public sealed record SnapshotRequest
{
    public bool UseVision { get; init; }
    public bool UseDom { get; init; }
    public bool UseAnnotation { get; init; } = true;
    public bool UseUiTree { get; init; } = true;
    public long? WidthReferenceLine { get; init; }
    public long? HeightReferenceLine { get; init; }
    public IReadOnlyList<long>? Displays { get; init; }
    public RegionArgument? Region { get; init; }
}

/// <summary>The latest snapshot; labels resolve against it (spec §5 "Shared state").</summary>
public sealed class DesktopStateStore
{
    private DesktopSnapshot? _latest;

    public DesktopSnapshot? Latest => Volatile.Read(ref _latest);

    internal void Replace(DesktopSnapshot snapshot) => Volatile.Write(ref _latest, snapshot);
}

public interface IDesktopSnapshots
{
    DesktopStateStore State { get; }

    /// <summary>
    /// upstream Desktop.get_state (desktop/service.py:87-315). Errors are exceptions whose message is the text upstream
    /// puts after "Error capturing desktop state: ".
    /// </summary>
    Task<DesktopSnapshot> CaptureAsync(SnapshotRequest request, CancellationToken cancellationToken);
}

/// <summary>Everything <see cref="SnapshotService"/> reads from the machine; tests replace each part.</summary>
internal sealed record SnapshotDependencies
{
    public required IDisplayProvider Displays { get; init; }
    public required IWindowScanner Windows { get; init; }
    public required ITreeCapture Tree { get; init; }
    public required IVirtualDesktops Desktops { get; init; }
    public required IScreenCapture Capture { get; init; }
    public required IImageRenderer Renderer { get; init; }
    public required Func<ScreenPoint> Cursor { get; init; }
    public required Action EnsureSession { get; init; }
    public required Func<string, string?> Environment { get; init; }
    public required TextWriter ProfileLog { get; init; }

    /// <summary>The real machine. Creating it starts no thread and touches no COM, UIA or Windows App SDK.</summary>
    public static SnapshotDependencies CreateDefault()
    {
        var uia = new UiaWorker<UiaSession>(UiaSession.Create);
        var blocks = new HangBlockList(TimeProvider.System, HangBlockList.DefaultDuration);
        var windows = new Win32WindowSystem();
        var desktops = new VirtualDesktopService();
        return new SnapshotDependencies
        {
            Displays = new Win32Displays(),
            Windows = new WindowCatalog(windows, new UiaWindowProbe(uia), desktops, blocks, System.Environment.ProcessId, Task.Delay),
            Tree = new TreeService(windows, new UiaWindowWalker(uia), blocks, System.Environment.GetEnvironmentVariable, Task.Delay),
            Desktops = desktops,
            Capture = new GraphicsCaptureService(),
            Renderer = new Win2DRenderer(),
            Cursor = DesktopSession.CursorPosition,
            EnsureSession = DesktopSession.ThrowIfUnavailable,
            Environment = System.Environment.GetEnvironmentVariable,
            ProfileLog = Console.Error,
        };
    }
}

/// <summary>
/// upstream Desktop.get_state (desktop/service.py:87-315) and capture_desktop_state's checks
/// (tools/_snapshot_helpers.py:175-215).
/// </summary>
public sealed class SnapshotService : IDesktopSnapshots
{
    /// <summary>The "Screenshot Backend" value (Decisions 11): Windows.Graphics.Capture.</summary>
    public const string Backend = "wgc";

    /// <summary>use_dom=True before the browser DOM exists (P6; Decisions 4).</summary>
    public const string UseDomUnavailable = "use_dom=True is not supported yet: browser DOM extraction arrives in a later release";

    private readonly SnapshotDependencies _machine;

    public SnapshotService() : this(SnapshotDependencies.CreateDefault())
    {
    }

    internal SnapshotService(SnapshotDependencies machine) => _machine = machine;

    public DesktopStateStore State { get; } = new();

    public async Task<DesktopSnapshot> CaptureAsync(SnapshotRequest request, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var stages = new List<(string Name, double Milliseconds)>();
        void Stage(string name)
        {
            stages.Add((name, clock.Elapsed.TotalMilliseconds - stages.Sum(s => s.Milliseconds)));
        }

        if (request.UseDom && !request.UseUiTree) throw new ArgumentException("use_dom=True requires use_ui_tree=True");
        var displayIndices = SnapshotSelection.ParseDisplays(request.Displays);
        if (request.UseDom) throw new NotSupportedException(UseDomUnavailable);

        var displays = _machine.Displays.GetDisplays();
        var screen = _machine.Displays.GetVirtualScreen();
        var region = SnapshotSelection.ParseRegion(request.Region, screen);
        var captureRect = region ?? (displayIndices is null ? null : SnapshotSelection.DisplayUnion(displayIndices, displays));
        _machine.EnsureSession();

        // upstream desktop/service.py:137-150: the Screenshot path skips the window scan.
        var scan = request.UseUiTree ? await _machine.Windows.ScanAsync(cancellationToken).ConfigureAwait(false) : WindowScan.Empty;
        ScreenPoint? cursor = _machine.Cursor();
        var (activeDesktop, allDesktops) = _machine.Desktops.GetDesktops();
        var active = scan.Active;
        IReadOnlyList<WindowInfo> windows = scan.Windows.Where(window => window.Handle != active?.Handle).ToArray();
        Stage("desktop_context");

        var tree = TreeSnapshot.Empty;
        if (request.UseUiTree)
        {
            // upstream desktop/service.py:174-193: the focused window, the windows that are not applications, and with a
            // capture rectangle the application windows inside it.
            var applications = scan.Windows.Select(window => window.Handle).ToHashSet();
            var inArea = captureRect is { } area
                ? windows.Where(window => RegionFilter.Window(window, area) is not null).Select(window => window.Handle).ToHashSet()
                : [];
            long? treeActive = active is not null && (captureRect is not { } bounds || RegionFilter.Window(active, bounds) is not null)
                ? active.Handle
                : null;
            var others = scan.Candidates
                .Where(handle => handle != active?.Handle && (!applications.Contains(handle) || inArea.Contains(handle)))
                .ToArray();
            tree = await _machine.Tree.CaptureAsync(treeActive, others, screen, cancellationToken).ConfigureAwait(false);
        }
        Stage("tree_capture");

        if (captureRect is { } rect)
        {
            active = RegionFilter.Window(active, rect);
            windows = RegionFilter.Windows(windows, rect);
            if (request.UseUiTree) tree = RegionFilter.Tree(tree, rect);
            cursor = RegionFilter.Cursor(cursor, rect);
        }
        Stage("region_filter");

        PixelSize? originalSize = null;
        double? scale = null;
        byte[]? png = null;
        if (request.UseVision)
        {
            var area = captureRect ?? screen;
            var image = await _machine.Capture.CaptureAsync(area, displays.Select(display => display.Bounds).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            Stage("screenshot_capture");
            originalSize = new PixelSize(image.Width, image.Height);
            scale = SnapshotSettings.ImageScale(_machine.Environment(SnapshotSettings.ScreenshotScaleVariable), image.Width, image.Height);
            png = await _machine.Renderer.RenderPngAsync(image, request.UseAnnotation
                ? Annotate(tree, cursor, area, request, scale.Value)
                : RenderOptions.Plain(scale.Value)).ConfigureAwait(false);
            Stage("screenshot_render");
        }

        var snapshot = new DesktopSnapshot
        {
            ActiveDesktop = activeDesktop,
            AllDesktops = allDesktops,
            ActiveWindow = active,
            Windows = windows,
            Cursor = cursor,
            OriginalSize = originalSize,
            Scale = scale,
            Region = captureRect,
            SelectedDisplays = displayIndices,
            Displays = displays,
            Backend = request.UseVision ? Backend : null,
            Tree = tree,
            Png = png,
        };
        State.Replace(snapshot);
        if (SnapshotSettings.ProfileEnabled(_machine.Environment(SnapshotSettings.ProfileSnapshotVariable)))
            Profile(stages, clock.Elapsed.TotalMilliseconds, request, displayIndices);
        return snapshot;
    }

    // upstream desktop/service.py:1385-1486: the interactive elements' boxes, the grid when both counts are set, and the
    // cursor, all relative to the captured area.
    private static RenderOptions Annotate(TreeSnapshot tree, ScreenPoint? cursor, ScreenRect area, SnapshotRequest request, double scale)
    {
        var annotations = tree.Interactive
            .Select((element, label) => new Annotation(
                new PixelRect(element.Box.Left - area.Left, element.Box.Top - area.Top, element.Box.Right - area.Left, element.Box.Bottom - area.Top),
                label.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        GridLines? grid = request is { WidthReferenceLine: { } columns and not 0, HeightReferenceLine: { } rows and not 0 }
            ? new GridLines((int)Math.Clamp(columns, int.MinValue, int.MaxValue), (int)Math.Clamp(rows, int.MinValue, int.MaxValue))
            : null;
        PixelPoint? marker = cursor is { } point ? new PixelPoint(point.X - area.Left, point.Y - area.Top) : null;
        return new RenderOptions(scale, annotations, marker, grid);
    }

    // upstream desktop/service.py:294-311: one line of stage timings.
    private void Profile(List<(string Name, double Milliseconds)> stages, double total, SnapshotRequest request, IReadOnlyList<long>? displays)
    {
        var parts = stages.Select(stage => FormattableString.Invariant($"{stage.Name}_ms={stage.Milliseconds:0.0}"))
            .Append(FormattableString.Invariant($"total_ms={total:0.0}"))
            .Append($"use_vision={request.UseVision} use_dom={request.UseDom} use_ui_tree={request.UseUiTree} use_annotation={request.UseAnnotation}")
            .Append($"displays={(displays is null ? "None" : "[" + string.Join(", ", displays) + "]")}");
        _machine.ProfileLog.WriteLine("Snapshot profile: " + string.Join(" ", parts));
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~SnapshotServiceTests"`
Expected: `Passed!` with `Failed: 0` (10 tests).

Run (interactive session; the test window appears near the top-left corner): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~SnapshotServiceDesktopTests"`
Expected: `Passed!` with `Failed: 0`: the rendered text holds the test window and its `"Press me"  [action: click]` line, `Screenshot Region: (120,120,540,380)`, and a 420x260 PNG. It reads (never changes) whatever other windows overlap that region.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/NativeMethods.txt windows/src/DesktopMcp.Desktop/Snapshots windows/tests/DesktopMcp.Desktop.Tests/Snapshots
git commit -m "Assemble desktop snapshots from the window scan, tree, displays, desktops and capture" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 17: Snapshot and Screenshot tools

upstream `tools/snapshot.py` and `tools/_snapshot_helpers.py:165-172, 266-362`: loose booleans for `use_*` (spec §4), integer reference lines, the display and region type checks (Decisions 16), the text from Task 4, the PNG as an image block, and upstream's error texts as normal results (Decisions 17).

**Files:**
- Create: `windows/src/DesktopMcp.Server/Tools/SnapshotTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolResults.cs`, `ToolServices.cs`, `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Server.Tests/Tools/SnapshotToolTests.cs`

**Interfaces:**
- Consumes: `IDesktopSnapshots`, `SnapshotRequest`, `SnapshotService`, `DesktopStateStore` (Task 16); `SnapshotText`, `SnapshotSelection` (Tasks 3-4); `ToolArguments` (P1).
- Produces: `ToolResults.Content(string text, byte[]? png)`; `ToolServices.Snapshots` (`IDesktopSnapshots`); `SnapshotTool` and `ScreenshotTool` registered for `Snapshot` and `Screenshot`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Server.Tests/Tools/SnapshotToolTests.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tests;

public class SnapshotToolTests
{
    private sealed class FakeSnapshots(Func<SnapshotRequest, DesktopSnapshot> capture) : IDesktopSnapshots
    {
        public SnapshotRequest? Request { get; private set; }
        public DesktopStateStore State { get; } = new();

        public Task<DesktopSnapshot> CaptureAsync(SnapshotRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(capture(request));
        }
    }

    private static DesktopSnapshot Sample(byte[]? png) => new()
    {
        ActiveDesktop = VirtualDesktopInfo.Default,
        AllDesktops = [VirtualDesktopInfo.Default],
        Windows = [],
        Cursor = new ScreenPoint(1, 2),
        Displays = [],
        Tree = TreeSnapshot.Empty,
        Png = png,
        OriginalSize = png is null ? null : new PixelSize(10, 10),
        Scale = png is null ? null : 1.0,
    };

    private static Task<CallToolResult> CallAsync(string tool, FakeSnapshots snapshots, string json) =>
        ToolCalls.CallAsync(ToolCalls.Create(tool, ToolServices.CreateDefault() with { Snapshots = snapshots }), json);

    [Fact]
    public async Task SnapshotWithoutVisionIsOnePlainTextBlock()
    {
        var snapshots = new FakeSnapshots(_ => Sample(null));
        var result = await CallAsync("Snapshot", snapshots, "{}");
        var text = ToolCalls.OkText(result);
        Assert.StartsWith("Cursor Position: (1, 2)\n\nActive Desktop:", text);
        Assert.EndsWith("UI Tree:\nNo elements", text);
        Assert.Null(result.StructuredContent);
        Assert.Equal(new SnapshotRequest(), snapshots.Request);
    }

    [Fact]
    public async Task SnapshotArgumentsAreLooseBooleansIntegersAndSelections()
    {
        var snapshots = new FakeSnapshots(_ => Sample([1, 2, 3]));
        var result = await CallAsync("Snapshot", snapshots,
            """{"use_vision": "TRUE", "use_annotation": "no", "use_ui_tree": true, "width_reference_line": 4, "height_reference_line": "3", "display": [0], "region": "[0, 0, 10, 10]"}""");
        var request = snapshots.Request!;
        Assert.True(request.UseVision);
        Assert.False(request.UseAnnotation);
        Assert.Equal((4L, 3L), (request.WidthReferenceLine, request.HeightReferenceLine));
        Assert.Equal([0L], request.Displays);
        Assert.Equal("[0, 0, 10, 10]", request.Region!.Json);
        Assert.Equal(2, result.Content.Count);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.DecodedData.ToArray());
    }

    [Fact]
    public async Task CaptureFailuresBecomeUpstreamsTextResult()
    {
        var snapshots = new FakeSnapshots(_ => throw new ArgumentException("region must satisfy right > left and bottom > top"));
        var result = await CallAsync("Snapshot", snapshots, """{"region": [5, 5, 1, 1]}""");
        Assert.Equal("Error capturing desktop state: region must satisfy right > left and bottom > top. Please try again.", ToolCalls.OkText(result));
    }

    [Fact]
    public async Task ArgumentTypeErrorsAreToolErrors()
    {
        var result = await CallAsync("Snapshot", new FakeSnapshots(_ => Sample(null)), """{"display": "0"}""");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Snapshot': display must be a list of integers", ToolCalls.Text(result));
    }

    [Fact]
    public async Task ScreenshotAlwaysCapturesWithoutTheTree()
    {
        var snapshots = new FakeSnapshots(_ => Sample([9]));
        var result = await CallAsync("Screenshot", snapshots, "{}");
        Assert.Equal(new SnapshotRequest { UseVision = true, UseAnnotation = false, UseUiTree = false }, snapshots.Request);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("\n" + SnapshotText.ScreenshotNote + "\n\nActive Desktop:", text);
        Assert.DoesNotContain("UI Tree:\n", text);
        Assert.IsType<ImageContentBlock>(result.Content[1]);
    }

    [Fact]
    public async Task ScreenshotFailuresUseItsOwnPrefix()
    {
        var snapshots = new FakeSnapshots(_ => throw new InvalidOperationException("The Windows App SDK runtime 2.5.1.0 or later is not installed"));
        var result = await CallAsync("Screenshot", snapshots, "{}");
        Assert.Equal("Error capturing screenshot: The Windows App SDK runtime 2.5.1.0 or later is not installed. Please try again.", ToolCalls.OkText(result));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Server.Tests`
Expected: FAIL with `CS0117`/`CS1061` (`ToolServices` has no `Snapshots`).

- [ ] **Step 3: Implement**

In `windows/src/DesktopMcp.Server/Tools/ToolResults.cs`, add before `/// <summary>A JSON result (spec §4)`:

```csharp
    /// <summary>
    /// A result for a tool without an outputSchema (Snapshot, Screenshot; spec §4): the text, then the PNG when there is
    /// one, and no structuredContent.
    /// </summary>
    public static CallToolResult Content(string text, byte[]? png) => new()
    {
        Content = png is null
            ? [new TextContentBlock { Text = text }]
            : [new TextContentBlock { Text = text }, ImageContentBlock.FromBytes(png, "image/png")],
    };

```

In `windows/src/DesktopMcp.Server/Tools/ToolServices.cs`, add `using DesktopMcp.Desktop.Snapshots;` after `using DesktopMcp.Desktop.Displays;`, add the property after `Displays`:

```csharp
    public required IDesktopSnapshots Snapshots { get; init; }
```

and the default after `Displays = new Win32Displays(),`:

```csharp
        Snapshots = new SnapshotService(),
```

In `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs`, add after the `"DisplayInventory"` arm:

```csharp
        "Snapshot" => new SnapshotTool(protocolTool, services.Snapshots),
        "Screenshot" => new ScreenshotTool(protocolTool, services.Snapshots),
```

`windows/src/DesktopMcp.Server/Tools/SnapshotTool.cs`:

```csharp
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Snapshots;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/snapshot.py:37-72. Argument types are checked first (an isError result); everything after that,
/// including a missing Windows App SDK runtime, becomes upstream's "Error capturing desktop state" text (Decisions 17).
/// </summary>
public sealed class SnapshotTool(Tool protocolTool, IDesktopSnapshots snapshots) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var snapshotRequest = new SnapshotRequest
        {
            UseVision = arguments.LooseBool("use_vision", false),
            UseDom = arguments.LooseBool("use_dom", false),
            UseAnnotation = arguments.LooseBool("use_annotation", true),
            UseUiTree = arguments.LooseBool("use_ui_tree", true),
            WidthReferenceLine = arguments.OptionalInteger("width_reference_line"),
            HeightReferenceLine = arguments.OptionalInteger("height_reference_line"),
            Displays = SnapshotSelection.ReadDisplayArgument(arguments.Get("display")),
            Region = SnapshotSelection.ReadRegionArgument(arguments.Get("region")),
        };
        try
        {
            var snapshot = await snapshots.CaptureAsync(snapshotRequest, cancellationToken).ConfigureAwait(false);
            return ToolResults.Content(SnapshotText.Render(snapshot, includeUiDetails: true, uiDetailNote: null), snapshot.Png);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ToolResults.Content($"Error capturing desktop state: {e.Message}. Please try again.", null);
        }
    }
}

/// <summary>upstream tools/snapshot.py:86-120: vision always on, no UI tree, no DOM.</summary>
public sealed class ScreenshotTool(Tool protocolTool, IDesktopSnapshots snapshots) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var snapshotRequest = new SnapshotRequest
        {
            UseVision = true,
            UseDom = false,
            UseAnnotation = arguments.LooseBool("use_annotation", false),
            UseUiTree = false,
            WidthReferenceLine = arguments.OptionalInteger("width_reference_line"),
            HeightReferenceLine = arguments.OptionalInteger("height_reference_line"),
            Displays = SnapshotSelection.ReadDisplayArgument(arguments.Get("display")),
            Region = SnapshotSelection.ReadRegionArgument(arguments.Get("region")),
        };
        try
        {
            var snapshot = await snapshots.CaptureAsync(snapshotRequest, cancellationToken).ConfigureAwait(false);
            return ToolResults.Content(SnapshotText.Render(snapshot, includeUiDetails: false, SnapshotText.ScreenshotNote), snapshot.Png);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ToolResults.Content($"Error capturing screenshot: {e.Message}. Please try again.", null);
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for the three projects (`ContractTests` still match the contract: the tool definitions come from `spec/windows/tools.json`).

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Server/Tools windows/tests/DesktopMcp.Server.Tests/Tools/SnapshotToolTests.cs
git commit -m "Add the Snapshot and Screenshot tools" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 18: Clipboard: the WinRT-on-MTA root cause

The P0/P1 follow-up (spec §8 and §12): under NativeAOT a WinRT clipboard call on an MTA thread blocks instead of throwing, and afterwards WinRT can block on STA too (unverified; see Decisions and spec §8). Hypothesis: the MTA call is marshalled to the clipboard objects' apartment through the process's main STA, and after the first STA thread of the process (P1's short-lived clipboard thread) has exited, nothing pumps that apartment. The experiment reads the clipboard text (it never writes) from the thread pool in three fresh processes: with no STA ever created, after a short-lived STA thread, and with the dispatcher thread (a live main STA with a message loop). There is no unit test: the subject is process-wide COM state that only a fresh NativeAOT process shows. P2 changes no product code here; `SystemClipboard` already bounds WinRT and falls back to Win32.

**Files:**
- Create: `windows/probes/DesktopMcp.Probe/ClipboardMtaProbe.cs`; modify `windows/probes/DesktopMcp.Probe/Program.cs`
- Modify: `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`, `docs/superpowers/specs/2026-10-19-windows-port-design.md` (§8 Clipboard, §12)

**Interfaces:**
- Consumes: `WinRtClipboard.GetText`, `StaThread.RunAsync` (P1), `DispatcherQueueThread.Start` (Task 7), `BootstrapProbe.DescribeApartment` (P0).
- Produces: probe command `desktop-mcp-probe clipboard-mta fresh|after-sta|with-dispatcher` (exit 0 when the read returned within 5 s).

- [ ] **Step 1: Write the probe**

`windows/probes/DesktopMcp.Probe/ClipboardMtaProbe.cs`:

```csharp
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Threading;

namespace DesktopMcp.Probe;

/// <summary>
/// Root-cause experiment for the WinRT clipboard blocking on an MTA thread under NativeAOT (P0 follow-up, P2 Task 18).
/// Hypothesis: an MTA call reaches the clipboard's apartment-threaded objects through the process's main STA; when the
/// first STA thread of the process has already exited, nothing pumps that apartment and the call waits forever. Each
/// mode must run in a new process, because the outcome depends on what the process did before. Read-only: it only
/// reads the clipboard text.
/// </summary>
internal static class ClipboardMtaProbe
{
    public static async Task<bool> RunAsync(string mode)
    {
        await using var setup = await PrepareAsync(mode);
        if (setup is null)
        {
            Console.Error.WriteLine($"clipboard-mta: unknown mode '{mode}' (fresh | after-sta | with-dispatcher)");
            return false;
        }
        try
        {
            var (apartment, text) = await Task.Run(() => (BootstrapProbe.DescribeApartment(), WinRtClipboard.GetText()))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"clipboard-mta {mode}: WinRT GetText on {apartment}: OK ({text?.Length.ToString() ?? "no"} text)");
            return true;
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"clipboard-mta {mode}: WinRT GetText on the thread pool (MTA) blocked for 5 s");
            return false;
        }
        catch (Exception e)
        {
            Console.WriteLine($"clipboard-mta {mode}: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            return false;
        }
    }

    private static async Task<Setup?> PrepareAsync(string mode)
    {
        switch (mode)
        {
            case "fresh":
                // No STA thread has ever existed in this process.
                return new Setup(null);
            case "after-sta":
                // The first STA thread of the process reads the clipboard and exits, as SystemClipboard's StaThread does.
                var staApartment = await StaThread.RunAsync(() => { _ = WinRtClipboard.GetText(); return BootstrapProbe.DescribeApartment(); },
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Console.WriteLine($"clipboard-mta after-sta: first STA thread was {staApartment} and has exited");
                return new Setup(null);
            case "with-dispatcher":
                // A live main STA with a message loop: the Windows App SDK dispatcher thread.
                var dispatcher = DispatcherQueueThread.Start();
                Console.WriteLine($"clipboard-mta with-dispatcher: dispatcher thread is {await dispatcher.InvokeAsync(BootstrapProbe.DescribeApartment)}");
                return new Setup(dispatcher);
            default:
                return null;
        }
    }

    private sealed class Setup(IAsyncDisposable? resource) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => resource?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
```

In `windows/probes/DesktopMcp.Probe/Program.cs`, replace the usage comment with

```csharp
// AOT probes. Usage: desktop-mcp-probe bootstrap | dispatcher | imaging [out.png] | capture [out.png] | clipboard | toast | uia
//                    | clipboard-mta fresh|after-sta|with-dispatcher
```

and add after the `"uia"` arm:

```csharp
    "clipboard-mta" => await ClipboardMtaProbe.RunAsync(args.Length > 1 ? args[1] : "fresh"),
```

- [ ] **Step 2: Publish the probe**

Run: `dotnet publish probes/DesktopMcp.Probe/DesktopMcp.Probe.csproj -c Release -r win-x64 -o probes/out`
Expected: no warnings; `probes/out` holds `desktop-mcp-probe.exe` and its pdb.

- [ ] **Step 3: Run each mode in its own process, under NativeAOT and under the JIT**

Run (interactive session; read-only):
```powershell
foreach ($mode in 'fresh', 'after-sta', 'with-dispatcher') {
    foreach ($run in 1, 2) { probes/out/desktop-mcp-probe.exe clipboard-mta $mode; "exit $LASTEXITCODE" }
}
foreach ($mode in 'fresh', 'after-sta', 'with-dispatcher') { dotnet run --project probes/DesktopMcp.Probe -c Release -- clipboard-mta $mode }
```
Expected: one `clipboard-mta <mode>: ...` result line per run (`OK`, `blocked for 5 s`, or an exception), plus the setup lines (`first STA thread was ...`, `dispatcher thread is MainSTA`). Record all lines.

- [ ] **Step 4: Record the outcome**

Decide from the NativeAOT lines (both runs of a mode must agree; if they do not, record the flake and treat the mode as "blocks"):

| fresh | after-sta | with-dispatcher | Conclusion to record |
|---|---|---|---|
| OK | blocks | OK | Confirmed: an MTA WinRT clipboard call needs a live main STA; it blocks once the process's first STA thread has exited. P4 starts the dispatcher (a live main STA) at startup, after which WinRT on the thread pool is expected to work; SystemClipboard keeps its STA thread and fallback until P4 measures that. |
| blocks | blocks | OK | Partly confirmed: MTA calls need a live main STA even before any STA existed. Same consequence for P4. |
| blocks | blocks | blocks | Rejected: a pumping main STA does not help; the blocking is elsewhere (likely CsWinRT's AOT marshalling of the clipboard's async operation). Keep the follow-up open with these lines. |
| any other | | | Record the lines as measured and keep the follow-up open. |

Replace the P2 clipboard item in `docs/superpowers/plans/2026-10-19-windows-p0-followups.md` (the bullet that starts `- Clipboard: under NativeAOT a WinRT clipboard call on an MTA thread blocks`) with, for a confirmed outcome, under the **P4** section:

```markdown
- Clipboard: WinRT on an MTA thread blocks under NativeAOT because <conclusion from the table> (P2 Task 18: `desktop-mcp-probe clipboard-mta`, <date>, NativeAOT fresh=<result>, after-sta=<result>, with-dispatcher=<result>; JIT <results>). Once P4 starts the dispatcher at startup, rerun the probe modes and the clipboard probe, and drop SystemClipboard's dedicated STA thread if WinRT works from the thread pool.
```

or, for a rejected or unclear outcome, keep it under **P2 leftovers** (create that heading after the P2 section) with the same measured lines and "root cause still unknown".

In `docs/superpowers/specs/2026-10-19-windows-port-design.md`, §8 Clipboard bullet, replace `Known issue; the root cause is a P2 follow-up.` with `P2 measured: <conclusion, one sentence> (P2 plan, Task 18).`, and in §12's last row replace `Finding the root cause is a P2 follow-up.` with `P2 measured: <conclusion, one sentence>.`

- [ ] **Step 5: Commit**

```bash
git add windows/probes/DesktopMcp.Probe/ClipboardMtaProbe.cs windows/probes/DesktopMcp.Probe/Program.cs docs/superpowers/plans/2026-10-19-windows-p0-followups.md docs/superpowers/specs/2026-10-19-windows-port-design.md
git commit -m "Measure why the WinRT clipboard blocks on MTA threads under NativeAOT" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 19: Release verification and documentation

The P2 checkpoint. xUnit runs under the JIT, so the AOT smoke test now calls the read-only desktop tools: DisplayInventory and Snapshot without vision before the graphics-stack check (neither may load d2d1 and friends), then Screenshot and Snapshot with vision. It closes the P1 leftover about `smoke-stdio.ps1`'s `finally` and the P0 follow-up "re-check with the user watching whether a capture border appears", and records the results.

**Files:**
- Modify: `windows/scripts/smoke-stdio.ps1`
- Modify: `docs/superpowers/specs/2026-10-19-windows-port-design.md` (§2, §5, §6, §9, §10, §11, §12)
- Modify: `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`

**Interfaces:**
- Consumes: every tool and probe from Tasks 1-18, `build-release.ps1`.
- Produces: `smoke-stdio.ps1 -Exe <path> [-Spec <path>] [-SkipCapture]`; the measured P2 results in the spec; the follow-ups list without the closed P2 items.

- [ ] **Step 1: Call the desktop tools in the smoke test**

In `windows/scripts/smoke-stdio.ps1`:

1. In the header comment, replace

```powershell
#    PowerShell call starts a ping that must die with the server (the kill-on-close Job Object, spec §5).
```

with

```powershell
#    PowerShell call starts a ping that must die with the server (the kill-on-close Job Object, spec §5).
#    The read-only desktop tools follow: DisplayInventory and Snapshot without vision before the graphics check (neither
#    may load the graphics stack), then Screenshot and Snapshot with vision, which need the Windows App SDK runtime and
#    an interactive session; -SkipCapture leaves those two out (Windows Sandbox has no runtime).
```

2. Replace the `param(...)` block's last parameter

```powershell
    [string]$Spec = (Join-Path $PSScriptRoot '../../spec/windows/tools.json')
)
```

with

```powershell
    [string]$Spec = (Join-Path $PSScriptRoot '../../spec/windows/tools.json'),
    [switch]$SkipCapture
)
```

3. In `Invoke-Session`, replace the `finally` block and the closing brace of the function

```powershell
    finally {
        $script:process.StandardInput.Close()
        if (-not $script:process.WaitForExit(5000)) { $script:process.Kill($true); throw 'server did not exit after stdin closed' }
    }
}
```

with (the P1 leftover: a throwing `Close()` no longer leaves the server running, and a failure in `finally` no longer hides the body's error)

```powershell
    finally {
        # Close() can throw when the server already died; it must not keep the process alive or hide the body's error.
        try { $script:process.StandardInput.Close() } catch { }
        if (-not $script:process.WaitForExit(5000)) {
            $script:process.Kill($true)
            Write-Warning 'server did not exit after stdin closed; killed it'
            $script:exitFailure = 'server did not exit after stdin closed'
        }
    }
    if ($script:exitFailure) { $failure = $script:exitFailure; $script:exitFailure = $null; throw $failure }
}
# A PNG image block: the content type, the MIME type and the PNG signature of the decoded data.
function Assert-PngImage($Result, [string]$Name) {
    $image = @($Result.content | Where-Object { $_.type -eq 'image' })
    if ($image.Count -ne 1 -or $image[0].mimeType -ne 'image/png') { throw "$Name returned no PNG image: $($Result | ConvertTo-Json -Depth 4)" }
    $bytes = [Convert]::FromBase64String($image[0].data)
    if ($bytes.Length -lt 8 -or $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50) { throw "$Name image is not a PNG" }
    return $bytes.Length
}
```

4. Insert before the line `        # A grandchild of the server: the kill-on-close job must stop it when the server exits.`:

```powershell
        # Read-only desktop tools that must not load the graphics stack: DisplayInventory and Snapshot without vision.
        $displays = Invoke-Tool 30 'DisplayInventory' @{}
        if ($displays.isError -or $displays.structuredContent.result.Count -lt 1) { throw "DisplayInventory failed: $($displays | ConvertTo-Json -Depth 6)" }
        $snapshot = Invoke-Tool 31 'Snapshot' @{ use_vision = $false }
        $snapshotText = $snapshot.content[0].text
        if ($snapshot.isError -or $snapshot.content.Count -ne 1 -or -not $snapshotText.StartsWith('Cursor Position: ') -or -not $snapshotText.Contains("`nUI Tree:`ndesktop")) {
            throw "Snapshot failed: $snapshotText"
        }
        $script:desktopRead = "DisplayInventory ($($displays.structuredContent.result.Count) displays) and Snapshot without vision OK"

```

5. Replace

```powershell
        $script:full = "$($init.result.serverInfo.name) $($init.result.serverInfo.version), $($list.result.tools.Count) tools match spec/windows/tools.json, Wait and $($cases.Count) headless tools OK, graphics stack not loaded ($($script:process.Modules.Count) modules)"
    }
```

with

```powershell
        $script:full = "$($init.result.serverInfo.name) $($init.result.serverInfo.version), $($list.result.tools.Count) tools match spec/windows/tools.json, Wait and $($cases.Count) headless tools OK, $script:desktopRead, graphics stack not loaded ($($script:process.Modules.Count) modules)"

        # Capture under NativeAOT: Win2D and Windows.Graphics.Capture through the static activation.
        if (-not $SkipCapture) {
            $screenshot = Invoke-Tool 40 'Screenshot' @{}
            if ($screenshot.isError -or -not $screenshot.content[0].text.StartsWith('Cursor Position: ')) { throw "Screenshot failed: $($screenshot.content[0].text)" }
            $screenshotBytes = Assert-PngImage $screenshot 'Screenshot'
            $vision = Invoke-Tool 41 'Snapshot' @{ use_vision = $true; region = @(0, 0, 200, 120) }
            $visionBytes = Assert-PngImage $vision 'Snapshot'
            if (-not $vision.content[0].text.Contains('Screenshot Region: (')) { throw "Snapshot with a region failed: $($vision.content[0].text)" }
            $script:full += ", Screenshot ($screenshotBytes bytes PNG) and Snapshot with vision ($visionBytes bytes PNG) OK"
        }
    }
```

- [ ] **Step 2: Run the full default suite**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each of the three projects. Record the passed counts.

- [ ] **Step 3: Run the Desktop tests by hand**

Run (interactive session; the P1 clipboard tests change the clipboard text and restore it, the toast test shows a toast, and test windows appear briefly): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath=`
Expected: `Passed!` with `Failed: 0`. Record the count. `DispatcherQueueThreadTests` is timing-dependent; if only its timer test fails, rerun it once and record both runs.

- [ ] **Step 4: Publish both RIDs and run the checks**

Run (interactive session): `pwsh scripts/build-release.ps1`
Expected: no publish warnings; `win-x64: <size> MB, folder OK (desktop-mcp.exe, desktop-mcp.pdb)` and the same for `win-arm64`; `smoke OK: desktop-mcp 0.1.0, 21 tools match spec/windows/tools.json, Wait and 5 headless tools OK, DisplayInventory (<n> displays) and Snapshot without vision OK, graphics stack not loaded (<n> modules), Screenshot (<n> bytes PNG) and Snapshot with vision (<n> bytes PNG) OK, PowerShell OK, its child died with the server; --config excluded Registry (20 tools match the rest)`; `AOT activation check OK: ...`; `uia: ...`; `AOT UIA check OK: ...`. Record both exe sizes (`(Get-Item dist/win-x64/desktop-mcp.exe).Length`) and the module count.

Run:
```powershell
probes/out/desktop-mcp-probe.exe capture $env:TEMP\p2-capture.png; Remove-Item $env:TEMP\p2-capture.png
probes/out/desktop-mcp-probe.exe clipboard
probes/out/desktop-mcp-probe.exe toast
```
Expected: the capture lines from Task 15 Step 5 (record `borderlessAccess`); the clipboard lines as in P1 or as Task 18 recorded; `toast: shown`.

- [ ] **Step 5: Snapshot by hand through the AOT exe**

Run (interactive session, with Notepad or any window in front):
```powershell
$psi = [Diagnostics.ProcessStartInfo]::new((Resolve-Path dist/win-x64/desktop-mcp.exe).Path); $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
$server = [Diagnostics.Process]::Start($psi)
$server.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"manual","version":"1"}}}'); $null = $server.StandardOutput.ReadLine()
$server.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$server.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Snapshot","arguments":{}}}')
($server.StandardOutput.ReadLine() | ConvertFrom-Json).result.content[0].text
$server.StandardInput.Close(); $server.WaitForExit(5000)
```
Expected: the Snapshot text: `Cursor Position: (x, y)`, `Visible Displays: ...`, the desktop tables, `Focused Window:` with the front window, `Opened Windows:`, and `UI Tree:` starting `desktop` with labelled lines such as `[0] (x,y) button "Close"  [action: click]`. Check by eye that labels count up from 0 without gaps across the interactive lines and continue on the scrollable lines. Save the text as the P2 sample for the record (not committed).

- [ ] **Step 6: Check the capture border with the user watching (needs the user)**

Ask the user to watch the screen while you run `foreach ($i in 1..3) { probes/out/desktop-mcp-probe.exe capture $env:TEMP\border.png; Start-Sleep 1 }; Remove-Item $env:TEMP\border.png`, and to say whether a yellow capture border appeared around the screen. Record the answer with the `borderSuppressed` and `borderlessAccess` values. If the user cannot watch, leave the follow-up open and say so in the report.

- [ ] **Step 7: Run the smoke test without the Windows App SDK runtime (needs the user; optional)**

Ask the user to copy `windows/dist/win-x64/desktop-mcp.exe`, `windows/scripts/smoke-stdio.ps1` and `spec/windows/tools.json` into Windows Sandbox (PowerShell 7 installed first) and run `pwsh smoke-stdio.ps1 -Exe .\desktop-mcp.exe -Spec .\tools.json -SkipCapture`. Expected: the `smoke OK` line with `DisplayInventory (...) and Snapshot without vision OK`, which shows that the UIA path needs no Windows App SDK runtime; the kill-on-close ping check is known to fail there (the P4 job-inheritance follow-up). Then ask them to call Screenshot once (as in Step 5 with `"name":"Screenshot"`) and confirm the text starts with `Error capturing screenshot: The Windows App SDK runtime ` and names the downloads page. If the user declines, say so in the report.

- [ ] **Step 8: Record the results in the spec**

In `docs/superpowers/specs/2026-10-19-windows-port-design.md` (the spec wraps at about 120 columns, so match each quoted sentence across line breaks):

1. §2, the Win32/COM bindings row, Decision cell: replace `P2 adds `comInterop.useComSourceGenerators` (`[GeneratedComInterface]`) for UIA` with `since P2, DesktopMcp.Desktop sets `allowMarshaling: true`, `comInterop.useComSourceGenerators` and `CsWin32RunAsBuildTask`, so UIA and the shell interfaces are `[GeneratedComInterface]` interfaces; DesktopMcp.Core keeps `allowMarshaling: false` (P2 plan, Decisions 1)`.
2. §2, the WinRT first row: replace `Planned: P2 DisplayInventory through `DisplayArea` plus `GetDpiForMonitor`;` with `Evaluated in P2: `DisplayArea` agrees with `GetMonitorInfo`, but DisplayInventory stays on Win32 because device names and indices need it and DisplayInventory needs no runtime (P2 plan, Decisions 15);`.
3. §5, the UIA bullet: replace it with `**UIA** calls run one at a time on one dedicated MTA worker thread (`UiaWorker`), never on the dispatcher thread: a UIA client calling from a thread that owns windows can deadlock on its own windows. `IUIAutomation2` connection (2 s) and transaction (5 s) timeouts and per-job deadlines (window probe 5 s, window walk 20 s) bound a hung target app; a job past its deadline abandons the worker thread, the next job gets a new one, and the window and its process are skipped for 90 s (P2 plan, Decisions 2).`
4. §6 Capture: replace `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` is not called yet.` with `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` is requested once per process (P2: <borderlessAccess value>).`; replace `P2 moves the call into the entry points themselves.` with `Since P2 the entry points call it themselves.`
5. §6 Displays: replace `P2 evaluates `Microsoft.UI.Windowing.DisplayArea` for the bounds and work area, with `GetDpiForMonitor` for the DPI (WinRT first, §2).` with `P2 evaluated `Microsoft.UI.Windowing.DisplayArea`: for a PerMonitorV2 process it reports the same bounds and work areas, but the Win32 path stays (P2 plan, Decisions 15).`
6. §6 Tree and Snapshot, the Browser DOM bullet: append `P2 walks `RootWebArea` like any element and answers `use_dom=True` with an error text until P6 (P2 plan, Decisions 4).`
7. §9: add after the P1 results paragraph: `P2 results (<date>, Windows build <build>): default suite <n> tests passed (Core <a>, Server <b>, Desktop non-desktop <c>); Desktop suite <n> passed (by hand); build-release: win-x64 <size> bytes, win-arm64 <size> bytes, each dist folder holding only the exe and its pdb; stdio smoke OK under NativeAOT (adds DisplayInventory and Snapshot without vision before the graphics check, <n> modules, then Screenshot and Snapshot with vision); AOT activation and UIA checks OK; capture probe <w>x<h> in <ms> ms, borderSuppressed=<value>, borderlessAccess=<value>; capture border with the user watching: <seen | not seen | not checked>; DisplayArea evaluation: <agrees on n monitor(s)>; Windows Sandbox: <result or not run>. Desktop tests need a PerMonitorV2 test process (`TestDpi`) and use their own test window.`
8. §10: add a bullet after "P1 decisions": `- **P2 decisions**: P2's departures from upstream for Snapshot, Screenshot and DisplayInventory are listed in the P2 plan's "Decisions on upstream ambiguities" (`docs/superpowers/plans/2026-10-19-windows-p2-desktop-read.md`): the text layout and labels, `Screenshot Backend: wgc`, regions clipped to the screen, window order, the UIA hang breaker, and `use_dom` before P6.`
9. §11, P2 row, Outcome cell: `The agent can see the screen (done; results in §9)`.
10. §12: in the Windows.Graphics.Capture row, replace `Whether a border was visible is not yet confirmed by the user; recheck in P2 with the user watching.` with the Step 6 result; in the "UIA calls into hung apps" row, replace the mitigation with `IUIAutomation2 timeouts, per-job deadlines, a replaceable UIA worker and a 90 s block of hung windows (P2 plan, Decisions 2)`.

- [ ] **Step 9: Update the follow-ups list**

In `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`:

1. In the P2 section, delete every item this plan closed: COMException translation, the DisplayArea evaluation, the Win2D entry points, the `DispatcherQueueThread` item, the `Win2DRenderer` item, the `AppRuntime` item, the `GraphicsCaptureService` item (keep only "re-check with the user watching" if Step 6 did not run), the UIA hang breaker, and the clipboard item (Task 18 moved it). Move "Scrape use_dom" and "OCR fallback in Snapshot" to the P6 section, appending ` (deferred from P2: P2 plan, Decisions 4 and 20)`. If the P2 section is then empty, replace it with `All P2 items are done (see docs/superpowers/plans/2026-10-19-windows-p2-desktop-read.md).`
2. In "P1 leftovers", delete the `build-release.ps1` skip-line item and the `smoke-stdio.ps1` `Close()`/`finally` item.
3. Add under P3: `WaitFor and the label-based tools: add the metadata P2 does not read (IsEnabled for element_enabled, selection state) to UiaElementData and UiElement (P2 plan, Decisions 6).`
4. Add under P6: `Browser DOM mode: enter it below RootWebArea, build the browser window's semantic tree from the DOM lists, accept use_dom=True in Snapshot and Scrape, and add the IA2 path for Firefox (P2 plan, Decisions 4).`, `DESKTOP_MCP_WATCHDOG: a UIA focus-changed handler that logs at debug level (P2 plan, Decisions 22).` and `Windows Sandbox: rerun smoke-stdio.ps1 -SkipCapture there to show Snapshot without vision needs no runtime` if Step 7 did not run.

- [ ] **Step 10: Commit**

```bash
git add windows/scripts/smoke-stdio.ps1 docs/superpowers/specs/2026-10-19-windows-port-design.md docs/superpowers/plans/2026-10-19-windows-p0-followups.md
git commit -m "Smoke-test the desktop read tools under NativeAOT and record the P2 results" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Self-review

**Spec coverage (§11 P2 row, §5, §6, §9, §10, §12 and the task brief):**

| Requirement | Task |
|---|---|
| UIA on CsWin32 `[GeneratedComInterface]`, no built-in COM, exact NativeMethods.json, AOT spike | 1 (Decisions 1) |
| `CUIAutomation8` as `IUIAutomation6`, one cache request with spec §6's property set and the three patterns, children one level at a time at `TreeScope_Children` under the control view | 1, 11 |
| BSTRs freed and VARIANTs disposed; immutable records out of Desktop | 11 (`UiaValues`), 2 |
| UIA off the dispatcher, serialised, IUIAutomation2 timeouts, hang breaker | 10, 13 (Decisions 2) |
| Windows walked: visible, current virtual desktop, not ours, plus Progman and the taskbars; excluded processes before UIA; active window plus non-app windows plus app windows in the region | 13, 16 |
| Classification from `tree/config.py`, browser rules, Word nodes from TextPattern, modal reset, window-name fixes | 12, 11 (Decisions 4, 7, 8) |
| Element limit with the truncation note, RuntimeId cycle guard, three retries at 0.5/1/2 s | 12, 13 (Decisions 9, 23) |
| Virtual desktop names through the internal manager by build, then registry, then Desktop N | 9 (Decisions 19) |
| Region and display selection, validation and messages | 3, 16 (Decisions 14, 16) |
| Snapshot text renderer in Core, sections, cleanups, labels, tabulate tables | 4 (Decisions 10) |
| Capture: Windows.Graphics.Capture per monitor from the display id, Win2D shared device, composite, `IsBorderRequired = false`, `RequestAccessAsync(Borderless)` | 15 |
| Imaging: scale formula, HighQualityCubic, 2 px boxes, Arial 12 labels, cursor marker, grid, PNG, fixed palette | 14, 3 (Decisions 12, 13) |
| Displays with upstream's fields; DisplayArea evaluation | 8 (Decisions 15) |
| Snapshot `[Text, Image]` or `[Text]`, Screenshot always with the image, DisplayInventory JSON + structuredContent | 17, 8 |
| Loose booleans for `use_*`, list/string coercion for `region` | 17, 3 |
| `DesktopStateStore` with the latest snapshot | 16 |
| No interactive session gives an error result | 16 (Decisions 18) |
| `DESKTOP_MCP_PROFILE_SNAPSHOT`, `MAX_TREE_ELEMENTS`, `EXCLUDE_PROCESSES`, `SCREENSHOT_SCALE`; `SCREENSHOT_BACKEND` ignored | 3, 16 (Decisions 11, 29) |
| Lazy Windows App SDK; install hint for desktop tools; no graphics DLLs without capture | 14, 15, 17, 19 (Global Constraints) |
| Testing: Core rendering from synthetic nodes; Desktop integration in `Category=Desktop`; AOT smoke | 2-4, 8-17, 19 |
| Contract unchanged | 17 (`ContractTests`), 19 (smoke contract comparison) |

**P2 follow-ups** (`2026-10-19-windows-p0-followups.md`): every item maps to a task or a Decision in "Follow-ups this plan picks up". Deferred with reasons: Scrape `use_dom` (Decisions 4), OCR fallback (Decisions 20); the main-STA question is Decisions 21; the capture border needs the user (Task 19 Step 6).

**Placeholder scan:** no "TBD", "TODO", "implement later" or "similar to Task N". The bracketed values in Task 18 Step 4 and Task 19 Steps 8-9 (`<date>`, `<size>`, `<conclusion>`) are measurements that earlier steps of the same task record. Every code step shows complete files, or the exact old and new text for files that several tasks change (`NativeMethods.txt`, `Program.cs` of the probe, `ToolResults.cs`, `ToolServices.cs`, `ToolRegistry.cs`, `build-release.ps1`, `smoke-stdio.ps1`, `PyText.cs`, `DispatcherQueueThreadTests.cs`).

**Type consistency** (checked across tasks; every file in this plan was compiled and its tests run together while the plan was written, on a scratch copy of the P1 tree at 580eeb7):
- `ScreenRect`/`ScreenPoint`/`PixelSize` and the records of Task 2 are used with those names in Tasks 3-4 and 8-17; `DesktopSnapshot.SelectedDisplays` is `IReadOnlyList<long>?` in Tasks 2, 4 and 16.
- `SnapshotSelection.ReadDisplayArgument`/`ReadRegionArgument`/`ParseDisplays`/`ParseRegion`/`DisplayUnion` and `RegionArgument(Values, Json)` match between Tasks 3, 16 and 17.
- `SnapshotSettings.MaxTreeElementsVariable` and friends are used by Tasks 4, 13 and 16; `ImageScale(string?, int, int)` by Task 16.
- `UiaSession.Create`, `ElementRequest`, `ChildrenRequest`, `ProbeRequest`, `ControlView` (Task 1) are what Task 11 uses; `UiaWorker<TSession>.RunAsync(Func<TSession, T>, TimeSpan, CancellationToken)` (Task 10) is what Tasks 11, 13 and 16 call.
- `UiaElementData`, `WordBox`, `ITreeSource` (Task 11) match `TreeWalker` (Task 12) and the fakes in its tests; `WindowProbe` (Task 11) matches `IWindowProbe` (Task 13).
- `ElementBudget(int)` and `TreeWalker(source, budget, nextId, maxDepth)` (Task 12) are used by `UiaWindowWalker` and `TreeService` (Task 13).
- `IWindowScanner`, `WindowScan.Empty`, `ITreeCapture.CaptureAsync(long?, IReadOnlyList<long>, ScreenRect, CancellationToken)` (Task 13) match `SnapshotService` and its fakes (Task 16).
- `IScreenCapture.CaptureAsync(ScreenRect, IReadOnlyList<ScreenRect>, CancellationToken)` (Task 15) and `IImageRenderer.RenderPngAsync(CapturedImage, RenderOptions)` (Task 14) match `SnapshotService` (Task 16) and the probe.
- `IDesktopSnapshots`, `SnapshotRequest`, `DesktopStateStore` (Task 16) match `SnapshotTool`/`ScreenshotTool` and their fake (Task 17); `ToolServices` grows `Displays` (Task 8) and `Snapshots` (Task 17), and tests use `ToolServices.CreateDefault() with { ... }` with those names.
- `ComErrors.Translate`/`ShouldTranslate`/`IsTimeout`/`IsDeadElement` (Task 5) are used with those names in Tasks 7, 11, 12, 13, 14 and 15.
- `TestDpi.EnsurePerMonitorV2` (Task 8) and `TestWindow` (Task 11) are used by Tasks 11, 13, 15 and 16.
