# Windows port: follow-ups carried out of P0

Findings from the P0 and P0b task reviews and the final whole-branch review that were deliberately
left for later phases. Each later phase's plan must pick up its section.

## P1 (CLI, config, stdio server, headless tools)

All other P1 items are done (see docs/superpowers/plans/2026-10-19-windows-p1-headless.md).

- Windows Sandbox run (2026-10-20, OS build 26100, no Windows App SDK runtime): the server started and passed initialize, the 21-tool contract comparison, Wait and the five headless tools. That backs the lazy-initialisation claim in spec §10. The run then failed at the kill-on-close check: `ping … outlived the server`. See the P4 item on job inheritance.

## P1 leftovers (minor review findings deferred from the P1 tasks)

- Fuzzy matching: `.5` ties use a different float formula from rapidfuzz (rare mismatch), and lengths count UTF-16 units rather than code points; `PyText.Repr` escapes fewer non-printables than Python.
- `ConfigFile.Load` maps only `TomlException`: an I/O or access error, or a directory path, surfaces raw or as "not found"; invalid UTF-8 is replaced, not rejected.
- An explicit empty list flag (`--tools ""`) falls back to the config file, unlike an explicit empty `--auth-key`; config lists are not trimmed.
- CLI: `--config` does not reject a directory (click's `dir_okay=False`); parse-error wording differs from click's; `--control-idle-timeout` is parsed by System.CommandLine, not click's float rules.
- PowerShell: the cancellation test does not check that the tree died; the post-Ctrl+Break grace waits on process exit, not on the end of the output streams (a grandchild holding a pipe).
- FileSystem: `SameVolume` compares roots, not volumes, so a volume mounted on a folder deletes the destination and then errors; a cross-volume move follows links where upstream's `copytree(symlinks=True)` keeps them; an AppExecLink in the copy fallback fails; add a comment on the delete walk's TOCTOU (same as CPython); thin tests for links, 8.3 names, UNC and drive roots.
- Registry: a provider path with a doubled separator after the hive is not normalised; `PS*` names are shown where pwsh hides them; `Format-List` empty values and multi-line values differ in trailing space and indent.
- Process: cap the `NtQuerySystemInformation` growth loop and check the walk against `returnLength`; add an offset test for the `SYSTEM_PROCESS_INFORMATION` overlay (Reserved1 at 8, ImageName at 56); CPU sampling is not cancellable; exit code 259 is ambiguous; a snapshot failure before argument validation hides the argument error.
- Clipboard: `LastWinRtError` is stale after a later success; a zero-size `GlobalSize` gives a `Win32Exception` with code 0; the 200 ms happy-path timeouts in `SystemClipboardTests` could flake on a loaded machine; the probe's exit code ignores the WinRT result.
- Notification: two thread-pool hops per call; the timeout text uses the current culture (use the invariant culture); a `COMException` with an empty message gives a blank error.
- HTML to Markdown: dropped end-tag counts never expire (clear them when the element at `MaxDepth` pops); the `Children` property copies on each access; deep lists can make the Markdown about 10x the input, so consider bounding the returned text.
- Scrape: the `ScrapeException` doc overclaims (a mid-body `IOException`/`InvalidDataException` escapes); 4xx bodies are read before the throw; add tests for gzip over the cap, the timeout, an http to https redirect, SNI and several resolved addresses.
- CLI: the split help text for `--control`/`--no-control` differs from upstream's; System.CommandLine's RootCommand adds `--version` and `-?`/`-h`.
- Clipboard: abandoned STA work can land after cancellation, and an abandoned WinRT `SetText` can land after the Win32 fallback write (a race).
- Config: `Reason()` depends on Tomlyn's message text; `ConfigFile.SourcePath` is not made absolute.
- ServePlan does not re-check the CLI's transport and policy choices.
- FileSystem: `GetMaxByteCount` throws `OverflowException`; a cross-root same-volume move (subst, UNC) with a junction loop now fails where a rename would work.
- Registry: Win32 `IOException` text appears in the OS language.
- Process: `kill` can kill the server's own process (upstream parity); no tests for `force` string coercion or PID 0 and 4.
- Tests and probes still hand-declare a few imports (`AppRuntimeTests`' `GetModuleHandleW`, the probes); `ProcessJobTests` uses throwaway `SafeFileHandle` wrappers.
- Deferred test and fidelity minors: Task 2 ctor validation and case tests; Task 3 LCS allocation per window; Task 4 `ToolArguments` edge tests; Task 6 IPv6 parse leniency and no comment on the mask tricks; Task 7 the help test's `Contains` check; Task 8 the slow-machine timeout test; Task 9 `PathResolver` edge cases, the ucs-2/iso8859/csunicode11utf7 aliases and dangling junctions; Task 10 `REG_LINK`, and `AHiveIsNeverDeleted` cannot tell the guards apart; Task 11b the HSTRING handle is not disposed on a create failure; Task 14 triple parse and `ReadPort`; Task 16 double parse, test naming and `LoopbackHttpServer`.
- Clipboard: the fallback to Win32 sticks after a WinRT timeout. Scrape: DNS resolution runs outside the 10 s timeout. Test-only APIs live in product code (`ProcessSnapshot.Names`, `IsProcessInJob`). Duplicated helpers: the max-delay constant, and the temp folders in `Server.Tests`. `ProcessJob`'s namespace. For P5: strip `DESKTOP_MCP_*` secrets from the PowerShell child's environment.
- Fnmatch: the doc says ntpath.normcase, but the code uses `re.IGNORECASE` semantics, so a range across the case boundary (`[A-_]`) differs; reversed ranges compare UTF-16 units rather than code points.

## P2 (desktop read: UIA, tree, Snapshot, capture, Screenshot)

All other P2 items are done (see docs/superpowers/plans/2026-10-19-windows-p2-desktop-read.md). Pending checks that need the user:


Windows Sandbox result (2026-10-20, run by the user; no Windows App SDK runtime): with `-SkipCapture`, the whole first session passed (initialize, tools/list, the headless tools, DisplayInventory, Snapshot without vision with the real-desktop and tree-element assertions, the graphics-stack check) and the server exited within 5 s of stdin closing; the run then failed at the kill-on-close check (`ping 6272 outlived the server: no kill-on-close job`), which the P4 item "Kill-on-close job inheritance" explains, so the second session (the excluding config) did not run. An earlier run without `-SkipCapture` passed the same steps, and then Screenshot returned the missing-runtime error under NativeAOT without crashing: `Error capturing screenshot: The Windows App SDK runtime (Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe, x64) is not installed: TryCreatePackageDependency failed with HRESULT 0x80670016. Install the Windows App SDK 2.5 runtime (2.5.1.0) or later from https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads.. Please try again.`. So Snapshot without vision needs no runtime.

## P2 leftovers

Minor review findings deferred from the P2 tasks, one line per task (from the SDD ledger). The final P2 review triaged them: what it fixed or settled is gone from these lines.

- Task 1: the MTA thread plus TCS helper is duplicated between the probe and a test (UiaWorker now owns the pattern); a foreground hang beyond the UIA timeout fails the probe after 30 s; RuntimeId presence does not gate the probe's exit code; the locked-session behaviour is unverified.
- Task 2: `Round` with negative digits throws; `RoundToInteger` with NaN, infinity or out-of-range values; `Capitalize` vs Python (final sigma, titlecase, dotted I); `ProcessService.Round1` duplicates `PyText.Round`; unchecked int geometry; no negative or empty rect tests; the deep-chain test covers only `PruneStructural`.
- Task 3: a region JSON `null` or `""` should mean no region (upstream `_as_region`); the invalid-JSON test asserts only NotEmpty; integers beyond long give the type message; `DisplayUnion` on an empty list; a shared Metadata reference; thin semantic and region tests; float underscores.
- Task 4: truthiness (scale 0, an empty backend or note) differs from upstream; test gaps (no active window, a non-empty windows table, multiple displays, an Informative node, a note with details); `childPrefix` is allocated for leaves.
- Task 5: redundant `map[0x80131505]`; awkward CLASSNOTREG wording; no facility-7 UnauthorizedAccess test; call sites must wrap COM and WinRT calls only.
- Task 6: an Assert inside a raw thread can crash the test host on a timeout; missing-lock detection is probabilistic.
- Task 7: a `TryEnqueue` throw leaks the controller and is untranslated (no callers until P4); the worst-case start is twice `StartTimeout` (document it); a test event can be disposed early on failure.
- Task 8: no `GetDeviceCaps` fallback for the system DPI; the null dpi/scale branch is untested; duplicate-index test; an `EnumDisplayMonitors` failure looks like "no displays".
- Task 9: the Explorer-restart drop uses `IsDeadElement` without `RPC_S_SERVER_UNAVAILABLE` and is untested (or create the manager per call); a wall-clock under-500 ms assert; duplicate ImmersiveShell setup per read; a late abandonment mark from a descheduled caller (bounded, practically unreachable); the concurrency test proves less than it claims; `ContinueWith` OOM; the full-timeout test has 400 ms of slack.
- Task 10: fake-clock awaits in `UiaWorkerTests` (about lines 59, 67, 81) lack a real-time `WaitAsync` bound; test naming (`CancellationAtTheGate`); the Post/Dispose race keeps the timer alive.
- Task 11: the visible-range loop is not counted in steps; `PostThreadMessage` and child `CreateWindow` results unchecked; a taskbar button for the off-screen test window; `CreationLock` TryEnter; the race test passes vacuously; no real Toggle or Range test; the vartype check is not isolated by its test; `UiaValuesTests` leaks an array on assert failure; `TestWindow.Dispose` is not idempotent and can mask test exceptions; FontSize is read for all visible ranges up front.
- Task 12: the deep test's semantic tree is flat; pending frames are bounded by depth times the child cap rather than the visits left; `TryConsume(0)` semantics.
- Task 13: excluded processes are still probed by the window catalog (upstream parity; see the Chromium hang item); pseudo-process names; `Parallel.For` test naming; `Debug.Assert(!Exhausted)` in `Slice`; no test for a persistent dead element while the window exists; 100 ms wasted after the last active-window attempt; a dead element in the focused read is retried without `IsWindow`.
- Task 14: null Annotations; label contrast for green and its doc; grid alpha, label ink box and cursor ring stroke vs PIL; a label right-edge test; banker's rounding.
- Task 15: blank overflow, OOM and `RO_E_CLOSED` outside the `DesktopException` contract; `IsSupported` uncached and unbounded; `Compose` is test-only; the FirstFrame won-race branch is untested; a CTS leak if the work never completes; an unobserved exception from a timed-out access request; process-wide static access state can race parallel test classes; the `MakeOpaque` wiring is untested.
- Task 16: profile field names vs upstream; `InvalidOperationException` wrapping breadth; `TheDefaultServiceIsCheap` asserts nothing (the smoke covers it); a redundant `Distinct`; `GetCursorPos` and `ProcessIdToSessionId` results unchecked; storing the PNG; the render gets no token; no comment that a failed snapshot's sequence never stores.
- Task 18: a long follow-ups line; no comment on the probe's timeout path.
- Task 19: the AOT smoke's exe took 0.03-7.5 s to exit after a capture in the interrupted first Task 19 attempt, the one during which VS Code hung (its last thread in process teardown, no CPU), then 23-25 ms on five measured runs with Code.exe excluded; cause unknown, so the capture session waits up to 30 s. `ClipboardTests.SystemClipboardRoundTripsThroughWinRt` failed twice on 2026-10-20 (the WinRT read after a WinRT write returned null) while Windows Sandbox and Chrome Remote Desktop were open; the clipboard code is unchanged since P1; rerun with both closed.
- Task 20: the table test checks the row count, not the column alignment; bidi and format characters (U+202E, U+FEFF) in names and values are not handled.
- Chromium hang: closed (P2 Decisions 36-38; the user's check with VS Code open passed on 2026-10-20, see spec §12). Watch for AppHang entries from Chromium processes; one from `old_mscopilot_proxy.exe` (Edge Beta 156) at 14:35 on 2026-10-20 has no known cause.
- **Fake attributes inside a tree line (Task 20 review; won't fix, final P2 review).** Names and values can still fake attributes within their own line because quotes and brackets are not escaped (e.g. `OK"  [action: click]  [focused]`). Escaping `"` would change real names such as `Save "a.txt"` away from upstream's raw text, and the fake stays inside its own line: it cannot mint a label.
- Clipboard: WinRT on an MTA thread blocks under NativeAOT; root cause still unknown. Rejected hypothesis: a missing live main STA (P2 Task 18: `desktop-mcp-probe clipboard-mta`, 2026-10-20, NativeAOT fresh=blocks, after-sta=blocks (first STA thread was MainSTA), with-dispatcher=blocks (dispatcher thread is MainSTA), each twice with the same result; JIT fresh=OK, after-sta=OK, with-dispatcher=OK, all on the implicit MTA). Narrowed with temporary, uncommitted probe steps: `Clipboard.GetContent()` and `DataPackageView.Contains(Text)` return in about 15 ms under both runtimes, and `GetTextAsync()` returns an operation that stays `Started` when polled for 12 s under both. The operation completes only once a `Completed` handler is attached. Under the JIT the handler fires about 12 ms later on an STA thread, not the main STA. Under NativeAOT it never fires, from the thread pool with or without the dispatcher. So the block is in delivering the operation's completion to the CsWinRT AOT `AsyncOperationCompletedHandler<string>`, not in `GetContent`. On an explicitly initialised MTA thread (`CoInitializeEx`), `GetTextAsync` fails at once with `RPC_E_WRONG_THREAD 0x8001010E` under both runtimes. Next step: check whether the AOT delegate CCW is agile (`IAgileObject`, free-threaded marshaller), and repeat the P1 claim that WinRT then blocks on STA too, in a fresh process. `SystemClipboard` keeps its STA thread, its 5 s bound and the Win32 fallback.

## P3 (desktop act)

All P3 items are done (see docs/superpowers/plans/2026-10-20-windows-p3-desktop-act.md), except:

- After an action: report a foreground window or title change (result text change: user decision).
- Highlight the target briefly before acting, with the P4 overlay, off by default.
- Later options, all contract changes: selector targeting (`process:X >> role:Button name:OK`), tree diffs in action results, pattern-based MultiSelect.

Pending checks that need the user (P3 Task 16). None of these ran during P3, because they inject input, take the foreground or launch apps on the user's machine. Run them in an interactive session with hands off the mouse and keyboard, from `windows/` (the AOT exe is `dist/win-x64/desktop-mcp.exe` after `pwsh scripts/build-release.ps1`):

- The whole Desktop suite: `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath=`. Test windows and the WinForms target appear, take the foreground and receive input; the paste test saves and restores the clipboard text. A test that fails because the foreground was taken sent nothing (`GuardedInjector`); rerun it. It includes the P3 tests that never ran:
  - Task 4, the 5 `InputDesktopTests` (the signature on injected mouse events, a button click, typing into the edit through the clipboard, every WinForms ToolStrip click taken, pixel-exact coordinates): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~InputDesktopTests"`. The signature test reads a low-level hook, so the remote mouse must stay idle. If `TypeWritesIntoTheTestWindowsEdit` leaves "hello world" in front of the text, the plain EDIT ignored Ctrl+A: fix the test target, not `DesktopInput`.
  - Task 5: `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~SwitchesToTheTestWindowFromAnotherTestWindow"`.
  - Task 10: `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~TypeSequenceDesktopTests"`.
  - Tests that read the user's real windows and were left out of P3's runs: `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~UiaSessionTests|FullyQualifiedName~SnapshotServiceDesktopTests"`.
- By-hand act session through the AOT exe over stdio (as P2 Task 19 Step 5; the user saves their work and watches). Expected results in brackets:
  1. `App {mode: 'launch', name: 'notepad'}` [`Notepad launched.`; the first real `IApplicationActivationManager.ActivateApplication`, and the first NativeAOT activation of `ApplicationActivationManager`].
  2. `Snapshot` [the Notepad edit has a label].
  3. `Type {label: <edit>, text: 'héllo 😀 and a twenty-plus character tail'}` [no `Warning:` line; the paste path runs, and the user's clipboard text is back afterwards].
  4. `Shortcut {shortcut: 'ctrl+a'}`.
  5. `App {mode: 'resize', name: 'notepad', window_loc: [100, 100], window_size: [800, 600]}` [`... resized to 800x600 at 100,100.`].
  6. `App {mode: 'switch', name: 'notepad'}` after focusing another window [`Switched to ... window.`].
  7. `WaitFor {condition: 'focused_element', text: <part of the edit's name>, timeout: 5}` [satisfied; the first real focused-element read, Task 14].
  8. `Click {label: <File menu>}` [the menu opens], then `Shortcut {shortcut: 'esc'}`.
  9. `App {mode: 'launch_executable', executable: 'C:\Windows\System32\charmap.exe'}` [JSON with the pid; the first successful `CreateProcess` with the handle list].
  10. Close the server (close stdin). Expected (spec §12; preflight F19): Notepad (packaged, started by the system) is still running; Character Map (non-packaged, `launch_executable`) closes with the server. The user closes Notepad without saving.
  11. A non-packaged Start-menu app through App `launch` (a Win32 shortcut, or an app whose AppsFolder parsing name is a `{KNOWNFOLDER}\...\x.exe` path, for example `App {mode: 'launch', name: 'character map'}` if it is listed): record whether `ShellExecuteEx` of `shell:AppsFolder\<parsing name>` returns a process handle (Task Manager shows whether the app is the server's child) and whether the app dies with the server. If no handle comes back, the system started the app outside the job and it survives: record that in spec §12.
  12. Note (Task 13 review, minor 4): a launch slower than the 10 s abandon still runs on its STA thread, so the app can appear after the `Failed to launch <name>: it did not start within 10 s.` result. If it happens, record the app and the time.
- `pwsh scripts/build-release.ps1` again on the final branch (the act-tool smoke checks, the AOT activation and UIA checks), as for every release build.

## P3 leftovers

Minor review findings deferred from the P3 tasks, one line per task (from the SDD ledger). Lines marked "final review" are for the final P3 review to triage.

- Task 1: minor (deferred): test gaps (ctrl+ctrl+c lastKey bump, ctrl+f4, Up->Down and Up->char pauses, table sizes and spot values, `( ) { }` inside a hold, lone surrogates).
- Task 2: minor (deferred): the Pair/LabelList shape message only for string-held non-lists; unused `name` in `Point`; `TimeSpan` tick rounding of the interval.
- Task 4: minor (deferred): the guard's denormalisation may be 1 px off near window edges; unused `WinFormsTarget` members; the structural rule test misses a target-typed `SendInputInjector x = new();`; the guard's `Track` is skipped when the inner `Send` throws (bookkeeping drift).
- Task 5: minor (deferred): the cancelled check and `SetForegroundWindow` are not atomic (one harmless call); `Abandon`'s detach runs on the caller's thread (win32k, not bounded).
- Task 7: minor (deferred): no test pins the point-over-hung-window null path; no test for IsOffscreen plus ScrollItem then ScrollIntoView, or for a RuntimeId winning over an earlier identity match; `windowBounds` is read before the job and not refreshed after a scroll; a duplicated `Element(UiaElementData)` helper and fake predicate; no WM_NULL re-check before `ReadWindow`.
- Task 8: minor (deferred; final review): owned popups shown inside the owner's tree skip the point step (the walk may hit the 2,000 cap and report stale) and the fallback point clips to the owner's rect; a possible relaxation to `GA_ROOTOWNER == identity.Window` is a ruling change for the final review to judge.
- Task 9: minor (deferred): Move and Click with `loc: []` and no label give the shape error (upstream's falsy check gives "Either loc or label must be provided.").
- Task 10: minor (deferred): real sleeps in Server unit tests (the auto-advancing clock came with Task 14); Type's argument-error order differs from upstream on doubly invalid input (same convention as Click).
- Task 13: minor (deferred; final review should fix 1-2): (1) Decisions 31 should say a failed or timed-out AppsFolder enumeration is an `isError` result with no shortcut fallback; (2) narrow `LaunchAsync`'s catch-all to `DesktopException`/`Win32Exception`; (3) the `Activating the app failed:` double prefix; (4) a launch can still appear after the 10 s abandon (in the pending by-hand checks above).

## P4 (desktop control)

- Replace DesktopInputLock with the control gate's desktop lock; supply IActionCheckpoint and InputLedger's physical-input predicate from InputMonitor (P3 plan, Decisions 18-20).
- **Kill-on-close job inheritance (low priority; user decision, 2026-10-20).** In Windows Sandbox (build 26100), `AssignProcessToJobObject` on the server succeeds, but processes it starts afterwards do not join the job. This holds for `Process.Start` with or without a console and with or without redirection. So PowerShell children and their descendants outlive the server, and the smoke script's ping check fails there. On the dev host (build 26220) and on any host where the job is inherited, the check passes.
  - Still unknown:
    - whether this happens on ordinary 24H2 (26100) machines or only in the Sandbox;
    - whether a process created inside the job, rather than one assigned to it after it started, passes the job on to its children.
  - Next steps:
    - Reproduce on a real 26100 machine or VM.
    - Try creating the PowerShell child with `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_JOB_LIST`, or with `CREATE_SUSPENDED` + `AssignProcessToJobObject` + `ResumeThread`. Both go through CsWin32 `CreateProcess`, because `ProcessStartInfo` has no job option. Then check whether its grandchildren stay in the job.
    - If they do not, add a tree-kill of the PowerShell child's descendants when the server exits.
    - Make the smoke script detect a host where job inheritance does not work and report the ping check as a warning there.
  - The diagnostic scripts were throwaway (session scratchpad `jobprobe\probe*.ps1`).

- Catch and log exceptions thrown from dispatcher timer ticks. Today they may fail-fast the process.
- Document that `TimerHandle.Dispose` off the dispatcher thread may let one more tick run.
- Add a `Func<Task>` overload and the `DispatcherQueueSynchronizationContext` that spec §5 describes.
- Overlays: evaluate `AppWindow` with WinUI 3 or Composition plus Win2D before `UpdateLayeredWindow`. Check whether WinUI 3 XAML needs a `.pri` file under NativeAOT, which would break the single-exe rule.
- Process allowlist (new config key): just before `SendInput`, check the process that owns the target window against an optional allowlist. desk-mcp's title-based allowlist is bypassed whenever no title is given, so match on the process.

## P5 (HTTP)

- Telemetry is opt-in (user decision, 2026-10-20; spec Telemetry): send only with `ANONYMIZED_TELEMETRY=true` and an explicit `POSTHOG_API_KEY`; no upstream key is built in. List this in spec §10 as a deliberate difference.

- Scrape sampling: call SampleAsync with upstream's system prompt and max_tokens 2048, falling back to the raw content (tools/scrape.py:52-71).
- IP allowlist: IPv4-mapped IPv6 client addresses need mapping in the matcher.
- JSONL audit log written from the SDK call filter, so every tool call is recorded with its name, arguments (with secrets redacted), result status and duration.

## P6 (release)

- Win2D static cache (`windows/build/Win2DStatic.targets`):
  - Make concurrent cold-cache publishes safe (publishing the solution can race on the download; the scripts publish one project at a time).
  - `LinkNative` re-runs on every publish (about 0.8 s), because `NativeLibrary` items such as `d2d1.lib` are not files, so the target's inputs are never up to date. `SdkNativeLibrary` would avoid it but is less documented.
  - Try to shrink the WinUI/Foundation/WebView2 exclusion block in `Directory.Build.props`. It was kept because `Microsoft.Graphics.Win2D` 1.4.0 depends on `Microsoft.WindowsAppSDK.WinUI`; nothing tried to remove one of the three entries.
- Provenance of the Win2D static libs: add GitHub artifact attestations to the fork's workflow and verify them (`gh attestation verify`) when bumping the pin, or record the producing run URL next to the hashes in Win2DStatic.props.
- Move the fork to the next Win2D release: bump `Win2DStaticVersion` and both pinned hashes in `windows/build/Win2DStatic.props` after the fork publishes the new release.
- Run the ARM64 exe on ARM64 hardware (smoke test and probe). It is build-verified only.
- Only CsWinRT activation is routed to the static Win2D. A direct `RoGetActivationFactory` call for a `Microsoft.Graphics.Canvas.*` class fails with `REGDB_E_CLASSNOTREG`; keep any new Win2D use on the projected types.
- `D3DCOMPILER_47.dll` is delay-loaded from System32 (it ships with the OS); confirm it is present on the supported Windows 11 builds.
- Each exe `.pdb` is about 115 MB and the cache about 173 MB per platform; keep both out of the release assets and out of git.
- Ship one `.mcpb` bundle per architecture: an MCP Bundle with server type "binary" pointing at `desktop-mcp.exe`, and `user_config` mapped to `DESKTOP_MCP_*` variables. Generate its version from the build so it cannot drift.
- `desktop-mcp doctor` CLI command: report the OS build, Windows App SDK runtime, session and desktop, elevation, DPI awareness and display list. It must not inject input.
- MSAA fallback in Snapshot for windows where UIA returns too little: walk deeper automatically.
- Optional `Ocr` and `ReadTable` tools, off by default (user approved, 2026-10-19). They extend the upstream contract, so they live outside `spec/windows/tools.json`'s upstream set: add a section for port-specific tools, enabled through config, and list them in the README's differences.
- Windows On-device Agent Registry (`odr.exe`, preview): do not register yet. Packaged or sparse servers run contained in a separate agent session, which cannot drive the user's desktop. Add a README note. The `.mcpb` above can be registered experimentally with `odr mcp add` under Developer Mode with agent-connector protections reduced. Reassess when ODR reaches GA or Microsoft documents user-desktop access for third-party connectors. Research notes: `odr-research.md` (session scratchpad, summarised here).
- Rejected from desk-mcp: Chrome DevTools Protocol browser tools (an open debug port and arbitrary JS evaluation), batch execution and delta snapshots (contract changes with unproven gains), and its compact JSON tree output (our line format is about 7x smaller).
- NOTICE line 3 is about 110 characters.
- Registry list wraps nothing: if a client depends on PowerShell's Format-List line wrapping, reproduce it; P1 prints each value on one line (P1 plan, Decisions 13).
- Spec polish: re-flow the long line in §4 and the ragged line in §5.
- OCR fallback in Snapshot: when UIA yields nothing for a window, run WinRT `Windows.Media.Ocr` on the captured frame in memory (tiled, with de-duplication) and report the text lines. This changes the Snapshot text, not the tool schema (deferred from P2: P2 plan, Decisions 4 and 20).
- Browser DOM mode and Scrape `use_dom`: dropped (user decision, 2026-10-20; P2 plan, Decisions 39). The Windows README should tell users to pair desktop-mcp with a browser automation MCP server (Playwright MCP, Chrome DevTools MCP) for web pages.
- DESKTOP_MCP_WATCHDOG: a UIA focus-changed handler that logs at debug level (P2 plan, Decisions 22).
