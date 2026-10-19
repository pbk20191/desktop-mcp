# Windows port: follow-ups carried out of P0

Findings from the P0 and P0b task reviews and the final whole-branch review that were deliberately
left for later phases. Each later phase's plan must pick up its section.

## P1 (CLI, config, stdio server, headless tools)

- `ServerTextTests` hardcodes `"0.1.0"`; derive the expected value from the `Version` property.
- Add a test for `SpecTool`'s cancellation branch (Wait is the first cancellable tool).
- Move the `PendingTool` test out of `ContractTests` when the first real tool tests land.
- Use the same `JsonTypeInfo` form in `ToolSpecCatalog` and `ContractTests`.
- Give a clear error for duplicate tool names in the contract, as part of the `--tools` / `--exclude-tools` filtering work.
- `smoke-stdio.ps1`: require `isError` to be false for the Wait call once Wait is implemented, and compare the full `tools/list` JSON with `spec/windows/tools.json`. Today the contract test only proves that the embedded spec round-trips.
- `DispatcherQueueThreadTests` timer test is timing-dependent; it is excluded with the Desktop category now, so keep it out of any CI default.
- Record the stdio smoke result in the spec.
- Run the smoke test once on a machine without the Windows App SDK runtime (for example Windows Sandbox) to back the lazy-initialisation claim in spec §10.
- Clipboard: try the WinRT `Clipboard` first and keep the Win32 clipboard as the fallback (spec §2 "WinRT first").
- Add an automated AOT activation check: run the probe's `imaging` step in `build-release.ps1`, or have the smoke test call Screenshot once it exists. Today nothing automated activates Win2D under AOT.
- Restrict `AllowedReferenceRelatedFileExtensions` (`Directory.Build.targets`) to publishes, so that `dotnet run` of the exe projects keeps line numbers for Core, Desktop and Server.

## P2 (desktop read: UIA, tree, Snapshot, capture, Screenshot)

- Translate COMException/HRESULT failures into a Desktop-level exception with a user-facing message at the `IScreenCapture` / `IImageRenderer` / `IDesktopDispatcher` boundary (spec §3 rule).
- DisplayInventory: evaluate `Microsoft.UI.Windowing.DisplayArea` for bounds and work area, plus `GetDpiForMonitor` for the DPI (spec §2 "WinRT first").
- Win2D entry points (`Win2DRenderer`, `GraphicsCaptureService`) call `AppRuntime.EnsureInitialized()` themselves. A missing call works under the JIT but fails under AOT with `REGDB_E_CLASSNOTREG`.
- Add the automated AOT activation check from P1 if it is not in place yet.
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
- OCR fallback in Snapshot: when UIA yields nothing for a window, run WinRT `Windows.Media.Ocr` on the captured frame in memory (tiled, with de-duplication) and report the text lines. This changes the Snapshot text, not the tool schema.

## P3 (desktop act)

Ideas from the desk-mcp analysis (`DigitalDog1/desk-mcp` at e6d42fc, Apache-2.0). Re-implement them; do not copy code.

- Before acting on a label, look the element up again by RuntimeId and use its current centre. Return a "stale label" error if it is gone, and note it if it is disabled.
- Check the count `SendInput` returns. When input is blocked (UIPI, elevated target), return a clear "input blocked: the target window runs elevated" error.
- Add a short pause between moving the cursor and pressing the button, because WinForms has been seen dropping instant clicks. Add a WinForms target to the desktop tests.

## P4 (desktop control)

- Catch and log exceptions thrown from dispatcher timer ticks. Today they may fail-fast the process.
- Document that `TimerHandle.Dispose` off the dispatcher thread may let one more tick run.
- Add a `Func<Task>` overload and the `DispatcherQueueSynchronizationContext` that spec §5 describes.
- Overlays: evaluate `AppWindow` with WinUI 3 or Composition plus Win2D before `UpdateLayeredWindow`. Check whether WinUI 3 XAML needs a `.pri` file under NativeAOT, which would break the single-exe rule.
- Process allowlist (new config key): just before `SendInput`, check the process that owns the target window against an optional allowlist. desk-mcp's title-based allowlist is bypassed whenever no title is given, so match on the process.

## P5 (HTTP)

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
- Spec polish: re-flow the long line in §4 and the ragged line in §5.
