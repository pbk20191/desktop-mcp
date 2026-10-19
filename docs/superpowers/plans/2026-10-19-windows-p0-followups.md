# Windows port: follow-ups carried out of P0

Findings from the P0 and P0b task reviews and the final whole-branch review that were deliberately
left for later phases. Each later phase's plan must pick up its section.

## P1 (CLI, config, stdio server, headless tools)

All other P1 items are done (see docs/superpowers/plans/2026-10-19-windows-p1-headless.md).

- Run the smoke test once on a machine without the Windows App SDK runtime (for example Windows Sandbox) to back the lazy-initialisation claim in spec §10. Copy `windows/dist/win-x64/desktop-mcp.exe`, `windows/scripts/smoke-stdio.ps1` and `spec/windows/tools.json` in, install PowerShell 7, and run `pwsh smoke-stdio.ps1 -Exe .\desktop-mcp.exe -Spec .\tools.json`. Needs the user.

## P1 leftovers (minor review findings deferred from the P1 tasks)

- `build-release.ps1` silently skips the AOT activation check on a non-x64 host or without win-x64; print a skip line.
- Fuzzy matching: `.5` ties use a different float formula from rapidfuzz (rare mismatch), and lengths count UTF-16 units rather than code points; `PyText.Repr` escapes fewer non-printables than Python.
- `ConfigFile.Load` maps only `TomlException`: an I/O or access error, or a directory path, surfaces raw or as "not found"; invalid UTF-8 is replaced, not rejected.
- An explicit empty list flag (`--tools ""`) falls back to the config file, unlike an explicit empty `--auth-key`; config lists are not trimmed.
- CLI: `--config` does not reject a directory (click's `dir_okay=False`); parse-error wording differs from click's; `--control-idle-timeout` is parsed by System.CommandLine, not click's float rules.
- `smoke-stdio.ps1`: if `Close()` throws, the server process is not killed, and a throw in `finally` masks the body's error.
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

- Translate COMException/HRESULT failures into a Desktop-level exception with a user-facing message at the `IScreenCapture` / `IImageRenderer` / `IDesktopDispatcher` boundary (spec §3 rule).
- DisplayInventory: evaluate `Microsoft.UI.Windowing.DisplayArea` for bounds and work area, plus `GetDpiForMonitor` for the DPI (spec §2 "WinRT first").
- Win2D entry points (`Win2DRenderer`, `GraphicsCaptureService`) call `AppRuntime.EnsureInitialized()` themselves. A missing call works under the JIT but fails under AOT with `REGDB_E_CLASSNOTREG`.
- `DispatcherQueueThread`:
  - `Start()` leaks the controller when `TryEnqueue` fails, and it waits without a timeout.
  - `DisposeAsync` should hop off the dying thread (`AsTask().ConfigureAwait(false)`).
  - The dispatcher thread is the process's main STA (`APTTYPE_MAINSTA`) because nothing else in the process is STA. Revisit this if a later phase expects to own the main STA itself.
- `Win2DRenderer`:
  - Use the buffer and length that `ReadAsync` returns.
  - Validate the length of `Bgra`.
  - Clamp label positions (no negative x; no label off-canvas) and guard boxes under 2 px.
  - Pick a label text colour that contrasts with yellow and cyan palette entries.
  - Retry once when the shared `CanvasDevice` is lost.
  - Add tests that pin the right and bottom box edges and the label position.
- `AppRuntime`:
  - Add a race test for concurrent first calls (`EnsureInitializedIsIdempotentAndThreadSafe` runs 8 parallel calls but cannot pin the lock).
  - Make the failure message name the failing call (`TryCreatePackageDependency` or `AddPackageDependency`).
  - Comment the X86OnArm64 / 64-bit architecture handling in `CurrentArchitecture()`.
  - Label the `PackageFullName` fallback (the family name, used when the OS returns no full name) so diagnostics do not mistake it for a resolved package.
  - Verify the downloads page link (`DownloadsPage`) for the 2.5 runtime.
  - A machine with only an older 2.x runtime (below 2.5.1) gets the install hint; check that its wording says "or later".
- `GraphicsCaptureService`:
  - Dispose a late frame on timeout or cancel, and unsubscribe `FrameArrived`.
  - Confirm multithread protection on the shared device.
  - Make `Pack` internal and unit-test the padded-stride branch.
  - Remove the diagnostic static `LastBorderSuppressed` from the public API.
  - Call `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` where available.
  - Re-check with the user watching whether a capture border appears.
- UIA hang breaker (from desk-mcp, idea only): put a hard deadline on every UIA call, make the UIA worker replaceable when a call hangs, and block a timed-out process or HWND for about 90 s. Snapshot skips a blocked window and does not apply the 3x retry to a timeout.
- Scrape use_dom: read DOM text nodes from the last snapshot once Snapshot exists (spec §8); P1 always returns "No DOM information found".
- Clipboard: under NativeAOT a WinRT clipboard call on an MTA thread blocks instead of throwing (the P1 probe gets `TimeoutException 0x80131505` from its own bound), and afterwards WinRT can block on STA in the same process too; find the root cause and revisit the WinRT clipboard path. P1 bounds WinRT by 5 s on a dedicated STA thread and falls back to Win32.
- OCR fallback in Snapshot: when UIA yields nothing for a window, run WinRT `Windows.Media.Ocr` on the captured frame in memory (tiled, with de-duplication) and report the text lines. This changes the Snapshot text, not the tool schema.

## P3 (desktop act)

Ideas from the desk-mcp analysis (`DigitalDog1/desk-mcp` at e6d42fc, Apache-2.0). Re-implement them; do not copy code.

- Before acting on a label, look the element up again by RuntimeId and use its current centre. Return a "stale label" error if it is gone, and note it if it is disabled.
- Check the count `SendInput` returns. When input is blocked (UIPI, elevated target), return a clear "input blocked: the target window runs elevated" error.
- Add a short pause between moving the cursor and pressing the button, because WinForms has been seen dropping instant clicks. Add a WinForms target to the desktop tests.
- `Coercion.JsonList` returns non-arrays: the `loc`/`region` callers must check `ValueKind`, or `JsonList` must reject them.

## P4 (desktop control)

- Catch and log exceptions thrown from dispatcher timer ticks. Today they may fail-fast the process.
- Document that `TimerHandle.Dispose` off the dispatcher thread may let one more tick run.
- Add a `Func<Task>` overload and the `DispatcherQueueSynchronizationContext` that spec §5 describes.
- Overlays: evaluate `AppWindow` with WinUI 3 or Composition plus Win2D before `UpdateLayeredWindow`. Check whether WinUI 3 XAML needs a `.pri` file under NativeAOT, which would break the single-exe rule.
- Process allowlist (new config key): just before `SendInput`, check the process that owns the target window against an optional allowlist. desk-mcp's title-based allowlist is bypassed whenever no title is given, so match on the process.

## P5 (HTTP)

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
