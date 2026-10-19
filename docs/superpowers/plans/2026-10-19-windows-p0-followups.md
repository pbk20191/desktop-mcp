# Windows port: follow-ups carried out of P0

Findings from the P0 task reviews and the final whole-branch review that were deliberately left
for later phases. Each later phase's plan must pick up its section.

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

## P2 (desktop read: UIA, tree, Snapshot, capture, Screenshot)

- Translate COMException/HRESULT failures into a Desktop-level exception with a user-facing message at the `IScreenCapture` / `IImageRenderer` / `IDesktopDispatcher` boundary (spec §3 rule).
- `DispatcherQueueThread`:
  - `Start()` leaks the controller when `TryEnqueue` fails, and it waits without a timeout.
  - `DisposeAsync` should hop off the dying thread (`AsTask().ConfigureAwait(false)`).
  - Use `sizeof` instead of `Marshal.SizeOf`.
- `Win2DRenderer`:
  - Use the buffer and length that `ReadAsync` returns.
  - Validate the length of `Bgra`.
  - Clamp label positions (no negative x; no label off-canvas) and guard boxes under 2 px.
  - Pick a label text colour that contrasts with yellow and cyan palette entries.
  - Retry once when the shared `CanvasDevice` is lost.
  - Add tests that pin the right and bottom box edges and the label position.
- `AppRuntime`:
  - Wrap `DllNotFoundException` / `TypeInitializationException` (missing `Microsoft.WindowsAppRuntime.Bootstrap.dll`).
  - Verify the aka.ms install URL for the 2.x runtime.
- `GraphicsCaptureService`:
  - Dispose a late frame on timeout or cancel, and unsubscribe `FrameArrived`.
  - Confirm multithread protection on the shared device.
  - Make `Pack` internal and unit-test the padded-stride branch.
  - Remove the diagnostic statics (`LastItemCreationPath`, `LastBorderSuppressed`) from the public API.
  - Call `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` where available.
  - Re-check with the user watching whether a capture border appears.

## P4 (desktop control)

- Catch and log exceptions thrown from dispatcher timer ticks. Today they may fail-fast the process.
- Document that `TimerHandle.Dispose` off the dispatcher thread may let one more tick run.
- Add a `Func<Task>` overload and the `DispatcherQueueSynchronizationContext` that spec §5 describes.

## P6 (release)

- Trim publish output to `desktop-mcp.exe`, `Microsoft.Graphics.Canvas.dll` and `Microsoft.WindowsAppRuntime.Bootstrap.dll`. The Windows App SDK metapackage currently adds about 113 MB per RID (DirectML, onnxruntime, AI.MachineLearning, WebView2).
- `build-release.ps1`: remove the dead `$LASTEXITCODE` check after the smoke call, and clean `dist/<rid>` before publishing.
- READMEs: the core-tools example names `Shell` (the Windows equivalent is PowerShell); NOTICE line 3 is about 110 characters.
- Spec polish: §1's sentence about shipped DLLs is long; re-flow the long line in §4 and the ragged line in §5.
