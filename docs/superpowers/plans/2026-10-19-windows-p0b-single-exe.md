# Windows port P0b: single exe on WinRT and the system Windows App SDK, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `desktop-mcp.exe` as one file again while keeping the system-installed Windows App SDK runtime. Bootstrap the runtime ourselves, drop Win2D, and move every desktop piece that has a WinRT or Windows App SDK API onto it.

**Architecture:**
- Raise the minimum OS to Windows 11 22H2 (build 22621) so that the OS Dynamic Dependencies API can replace `Microsoft.WindowsAppRuntime.Bootstrap.dll`.
- Replace Win2D (whose native DLL cannot be bundled):
  - drawing uses Direct2D/DirectWrite;
  - scaling and PNG encoding use WinRT `Windows.Graphics.Imaging`;
  - capture creates its own D3D11 device and reads frames through WinRT `SoftwareBitmap`.
- The dedicated dispatcher thread moves to `Microsoft.UI.Dispatching`.
- Reference only the Windows App SDK component packages we use, so the publish output is just the exe.

**Tech Stack:** .NET 10 NativeAOT, CsWinRT (Windows SDK 22621 projection), Windows App SDK 2.5 component packages, CsWin32 (Direct2D, DirectWrite, WIC, D3D11), xUnit.

**Spec:** `docs/superpowers/specs/2026-10-19-windows-port-design.md`. The decisions below supersede the spec where they differ; Task C updates the spec.

## Decisions (user, 2026-10-19)

1. **Packaging.** Ship exactly one file, `desktop-mcp.exe`. No companion DLLs.
2. **Minimum OS.** Windows 11 22H2, build 22621. Windows 10 is dropped; its support ended in October 2025.
3. **Windows App SDK.** Keep using the system-installed runtime (2.5), but bootstrap it with the OS Dynamic Dependencies API (`TryCreatePackageDependency` / `AddPackageDependency` in kernelbase), the way Rust projects do, instead of the bootstrap DLL.
4. **Drawing.** Direct2D/DirectWrite. **Scaling and PNG:** WinRT `Windows.Graphics.Imaging`. Win2D is removed.
5. **WinRT first.** Wherever WinRT or the Windows App SDK has the API, use it; keep raw Win32/COM only where neither has one. This applies now to the dispatcher thread (`Microsoft.UI.Dispatching`) and to display ids (`Microsoft.UI.Windowing.DisplayArea` / `Win32Interop`). Later phases get it for display info (P2), overlays (P4, `AppWindow` with WinUI 3 or Composition evaluated first) and the clipboard (P1, WinRT `Clipboard` first).

## Global Constraints

- TFM `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` **10.0.22621.0**.
- RIDs `win-x64` and `win-arm64`; the default RID comes from the dotnet process architecture (already in `windows/Directory.Build.props`).
- `TreatWarningsAsErrors`, `IsAotCompatible`, no warning suppression. Package versions published at least two weeks before 2026-10-19.
- **The publish folder for each RID contains only `desktop-mcp.exe`** (plus `.pdb`), and that exe alone runs. The probe exe has the same rule.
- Windows App SDK initialisation stays lazy: the server starts and headless tools work without the runtime, and desktop features fail with an install hint.
- The existing interfaces keep their names and shapes:
  - `IDesktopDispatcher` (`HasThreadAccess`, `ManagedThreadId`, `InvokeAsync`, `InvokeAsync<T>`, `StartTimerAsync`)
  - `IImageRenderer.RenderPngAsync`
  - `CapturedImage`, `PixelRect`, `Annotation`
  - `IScreenCapture.CapturePrimaryMonitorAsync`, `GraphicsCaptureService.IsSupported`
  - `AppRuntime.EnsureInitialized()`
  
  The tests in `windows/tests/DesktopMcp.Desktop.Tests` keep their assertions.
- Desktop tests run with `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath=`; the default `dotnet test DesktopMcp.slnx` skips them.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Branch `windows-port`.

---

### Task A: Windows 11 minimum, self-bootstrapping, App SDK dispatcher

**Files:**
- Modify: `windows/Directory.Build.props`, `windows/Directory.Packages.props`, `windows/src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj`
- Modify: `windows/src/DesktopMcp.Desktop/AppRuntime.cs`, `windows/src/DesktopMcp.Desktop/Interop/Native.cs`
- Replace: `windows/src/DesktopMcp.Desktop/Dispatching/DispatcherQueueThread.cs` (implementation moves to `Microsoft.UI.Dispatching`; class name stays)
- Modify: `windows/src/DesktopMcp.Desktop/Capture/GraphicsCaptureService.cs`. Remove the pre-22621 fallbacks: the `CreateForMonitor` interop path and the `IsBorderRequired` version guard; `TryCreateFromDisplayId` is always present on 22621. Keep using Win2D's device until Task B.
- Modify: `windows/probes/DesktopMcp.Probe/Program.cs` (new `bootstrap` probe)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/DispatcherQueueThreadTests.cs` (unchanged assertions), new `windows/tests/DesktopMcp.Desktop.Tests/AppRuntimeTests.cs`

- [ ] **Step 1: Minimum OS.** Set `SupportedOSPlatformVersion` to `10.0.22621.0`. Fix any CA1416 analyzer fallout by removing now-dead version checks, never by suppression.
- [ ] **Step 2: Failing tests.**
  - `AppRuntimeTests`:
    - `EnsureInitialized` is idempotent and thread-safe: 8 parallel calls all succeed.
    - After it, a Windows App SDK WinRT type activates: `Microsoft.UI.Windowing.DisplayArea.Primary` is non-null with non-empty `OuterBounds`.
    - Its `DisplayId` equals `Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor(MonitorFromPoint(0,0,PRIMARY))`.
  - Run them and the existing dispatcher tests; record RED.
- [ ] **Step 3: Self-bootstrap.** Rewrite `AppRuntime.EnsureInitialized` to call kernelbase `TryCreatePackageDependency` and then `AddPackageDependency`:
  - package family `Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe`;
  - minimum version from the SDK's generated runtime version constant (the 2.5.x framework);
  - architecture filter for the current process;
  - lifetime `Process`, with `CreatePackageDependencyOptions_None` and `AddPackageDependencyOptions_None`.
  
  Declare the APIs with `LibraryImport`, or with CsWin32 if it generates them AOT-safely. Keep the lock and idempotency. On failure, throw `InvalidOperationException` with the version, an install hint (link the Windows App SDK downloads page, https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) and the HRESULT. Keep the SDK auto-initializers off, and make sure no bootstrap DLL is copied to the output (check the SDK's documented properties for unpackaged apps; prefer component packages that do not carry it).
- [ ] **Step 4: Component packages.** Replace the `Microsoft.WindowsAppSDK` metapackage with only the component packages that provide what we use: the runtime version constants, `Microsoft.UI.Dispatching`, `Microsoft.UI.Windowing.DisplayArea` and `Microsoft.UI.Win32Interop`, plus the WinRT projection support they need. Keep Win2D for now. Record the chosen packages, versions and publish dates.
- [ ] **Step 5: Dispatcher on `Microsoft.UI.Dispatching`.**
  - `DispatcherQueueThread.Start()` calls `AppRuntime.EnsureInitialized()` and then `Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnDedicatedThread()`.
  - Keep `TryEnqueue`, `DispatcherQueueTimer` and `ShutdownQueueAsync` semantics, the lambda rule (no bare WinRT method groups as delegates), and the disposal behaviour.
  - Remove the `CreateDispatcherQueueController` P/Invoke.
  - Record the apartment the new thread runs in, and confirm WH_MOUSE_LL callbacks still arrive on it through the `dispatcher` probe.
- [ ] **Step 6: GREEN.** Run the Desktop tests (9 existing plus the new ones) and the default suite.
- [ ] **Step 7: AOT verification.** Publish the probe for win-x64. Run `bootstrap`, `dispatcher`, `imaging` and `capture`; all must pass with 0 warnings. List the publish folder: at this point it may still contain Win2D's `Microsoft.Graphics.Canvas.dll`, but **no** `Microsoft.WindowsAppRuntime.Bootstrap.dll` and none of the DirectML/onnxruntime/WebView2 payload. Record the folder listing and sizes.
- [ ] **Step 8: Commit** `Bootstrap the Windows App SDK through the OS and target Windows 11 22H2`.

Decision rule: if WinRT types from the App SDK do not activate after self-bootstrapping under NativeAOT, STOP and report BLOCKED with the exact HRESULTs and what was tried.

---

### Task B: Direct2D drawing, WinRT imaging, own capture device (Win2D removed)

**Files:**
- Delete: `windows/src/DesktopMcp.Desktop/Imaging/Win2DRenderer.cs`, `windows/tests/DesktopMcp.Desktop.Tests/Win2DRendererTests.cs`
- Create: `windows/src/DesktopMcp.Desktop/Imaging/D2DRenderer.cs`, `windows/tests/DesktopMcp.Desktop.Tests/D2DRendererTests.cs`
- Create: `windows/src/DesktopMcp.Desktop/NativeMethods.txt`, `windows/src/DesktopMcp.Desktop/NativeMethods.json` (CsWin32)
- Modify: `windows/src/DesktopMcp.Desktop/Capture/GraphicsCaptureService.cs`, `Interop/Native.cs`, `windows/Directory.Packages.props`, `DesktopMcp.Desktop.csproj`, probe `Program.cs`

**Interfaces:**
- `public sealed class D2DRenderer : IImageRenderer` with `public static IReadOnlyList<(byte R, byte G, byte B)> Palette` (the same 12 colours, same order).

- [ ] **Step 1: CsWin32.** Add `Microsoft.Windows.CsWin32` (the newest stable release published on or before 2026-10-05; 0.3.355 was 2026-10-07, so check dates) with the configuration the design probes proved:
  - `NativeMethods.json`: `{"allowMarshaling": true, "comInterop": {"useComSourceGenerators": true}}`
  - csproj: `CsWin32RunAsBuildTask`, `DisableRuntimeMarshalling` (already on)
  
  Generate only what this task needs: D2D1 factory and WIC bitmap render target, DirectWrite factory and text format/layout, WIC imaging factory and bitmap, D3D11 device creation, `IDXGIDevice`, `CreateDirect3D11DeviceFromDXGIDevice`. Keep BSTR, HRESULT and COM pointers inside `DesktopMcp.Desktop`.
- [ ] **Step 2: Failing tests.** `D2DRendererTests`: the same four tests as `Win2DRendererTests` with identical inputs and assertions, constructing `D2DRenderer`. Add two tests that pin the box contract:
  - the right and bottom edges land on pixels 139 and 119 for box (40,50,140,120) at scale 1;
  - pixel 39 on the box's row stays gray.
  
  Keep using `PngTestDecoder` (WinRT `BitmapDecoder`). Record RED.
- [ ] **Step 3: Implement `D2DRenderer.RenderPngAsync`.**
  1. Validate that `scale` is in (0, 1] and that `Bgra.Length == Width*Height*4`.
  2. Scale with WinRT `Windows.Graphics.Imaging`. For example, create a `SoftwareBitmap` from the BGRA bytes, encode through `BitmapEncoder` with `BitmapTransform` (`ScaledWidth`/`ScaledHeight`, `BitmapInterpolationMode.Fant`), or use an equivalent WinRT path. Pick one, describe it in the report, and keep it WinRT.
  3. Draw on the scaled pixels with Direct2D on a WIC bitmap render target, `D2D1_ANTIALIAS_MODE_ALIASED`, using the same geometry as the Win2D version:
     - a 2 px stroke inset by 1 px;
     - a label above the top-right corner (below the box if it would leave the image top), with its x clamped to ≥ 0;
     - DirectWrite Arial 12 text with 2 px horizontal and 1 px vertical padding;
     - white text.
  4. Encode the PNG with WinRT `BitmapEncoder` (`BitmapEncoder.PngEncoderId`) into an `InMemoryRandomAccessStream`, and read the bytes back using the returned buffer and length.
  
  Share one D2D factory, DirectWrite factory and WIC factory per renderer instance.
- [ ] **Step 4: Capture without Win2D.**
  - Create a D3D11 device (`D3D11_CREATE_DEVICE_BGRA_SUPPORT`, hardware driver), wrap it with `CreateDirect3D11DeviceFromDXGIDevice`, and project it to `Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice` (CsWinRT `MarshalInterface<IDirect3DDevice>.FromAbi` or the generated equivalent).
  - Read frames with `SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)` and `CopyToBuffer`, repacking the stride if needed.
  - Fix the deferred items from the P0 review in this file:
    - dispose a late frame on timeout or cancel;
    - unsubscribe `FrameArrived` in `finally`;
    - make `Pack` internal and unit-test the padded-stride branch;
    - remove the diagnostic statics from the public API (the probe may print what it needs from its own code).
- [ ] **Step 5: Remove Win2D** from the packages and projects. `git grep -i "Microsoft.Graphics.Canvas\|Win2D" windows/` must find nothing in code or build files.
- [ ] **Step 6: GREEN and AOT.**
  - Run the Desktop tests and the default suite.
  - Publish the probe for win-x64 and run `bootstrap`, `dispatcher`, `imaging` and `capture`. Expect 0 warnings. Open both PNGs (Read tool) and describe them.
  - Run `pwsh scripts/build-release.ps1` for both RIDs.
  - **The publish folders must contain only `desktop-mcp.exe` and `desktop-mcp.pdb`** (and the probe folder only `desktop-mcp-probe.exe` plus its pdb). Copy each exe alone to an empty scratch folder and run it there:
    - the probe's `imaging` and `capture`;
    - the main exe's `smoke-stdio.ps1`.
  - Record sizes.
- [ ] **Step 7: Commit** `Draw with Direct2D and encode with WinRT imaging; drop Win2D`.

Decision rule: if a step cannot meet the only-the-exe rule without suppressing warnings, STOP and report BLOCKED with the folder listing and what was tried.

---

### Task C: Documentation

**Files:**
- Modify: `docs/superpowers/specs/2026-10-19-windows-port-design.md`, `README.md`, `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`

- [ ] **Step 1: Spec.** Update the spec to match the five decisions above and the measured results from Tasks A and B. Change these sections: §1 (single exe, Windows 11 22621+, App SDK runtime required), §2 (Language/WinRT/Abstraction/Minimum OS/App SDK rows, and a new "WinRT first" row), §3 (layout: no Win2D; CsWin32 now in P0), §5 (dispatcher on `Microsoft.UI.Dispatching`), §6 (D2D, WinRT imaging, own D3D11 device, `DisplayArea` for display ids), §7 (overlays: `AppWindow` with WinUI 3 or Composition evaluated first in P4; no Win2D), §8 Clipboard (WinRT `Clipboard` first), §10 (installation), §12 (risks: self-bootstrap, D2D). Remove every stale mention of Win2D, the bootstrap DLL, companion DLLs, 19041 and the Windows 10 fallbacks, except where the text explains history.
- [ ] **Step 2: Root README.** The "No runtime dependencies" exception sentence must say the Windows port needs the Windows App SDK runtime installed and ships as one exe. Update the port table row's language cell if it needs to change.
- [ ] **Step 3: Follow-ups doc.** Remove items that Tasks A and B resolved (Win2D items, bootstrap DLL, 113 MB payload, capture leaks, `Pack` test, diagnostic statics, default RID). Add: P1 Clipboard via WinRT first; P2 DisplayInventory via `DisplayArea` plus `GetDpiForMonitor`; P4 overlays evaluate `AppWindow` + WinUI 3/Composition first, and check whether WinUI 3 XAML needs a `.pri` file under NativeAOT (which would break the single-exe rule).
- [ ] **Step 4: Commit** `Record the single-exe WinRT design`.
