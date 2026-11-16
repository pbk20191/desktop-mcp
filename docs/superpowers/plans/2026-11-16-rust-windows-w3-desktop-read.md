# Rust Windows W3: reading the desktop — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On Windows, `Snapshot` (with and without `use_vision`), `Window` (`list`, `displays`), `Wait` (app,
window and element conditions), `OCR` (opt-in) and the Windows-only `Screenshot` read the live desktop through UI
Automation, Win32 and `Windows.Graphics.Capture`, with the core's arguments and the platform's text.

**Architecture:** `crates/windows` gains a window catalog (Win32 + DWM), displays (per-monitor DPI), virtual-desktop
filtering, a UI Automation walk on pool threads, screen capture to BGRA, Direct2D annotation, PNG encoding and
`Windows.Media.Ocr`. `core` gains two small, macOS-neutral hooks in the snapshot text. Window actions, input and
App come in W4; until then the `Window` action modes answer "not available on Windows yet".

**Tech Stack:** `windows` 0.62 (UI Automation COM, Win32 windowing/DWM/HiDpi, Direct3D11, Direct2D, DirectWrite,
WIC, `Windows.Graphics.Capture`, `Windows.Graphics.Imaging`, `Windows.Media.Ocr`).

**Spec:** `docs/superpowers/specs/2026-11-16-rust-port-windows-design.md` (`Desktop` on Windows; Platform tools;
Gate placement's virtual-desktop note)

**Reference (behaviour, not code):** the C# port on `windows-port`:
`windows/src/DesktopMcp.Desktop/Tree/{TreeWalker,TreeService,TreeConfig,ElementBudget,WindowCatalog,WindowSystem}.cs`,
`Uia/{UiaSession,UiaTreeSource,UiaJob}.cs`, `Snapshots/SnapshotService.cs`, `Capture/GraphicsCaptureService.cs`,
`Imaging/AnnotationLayout.cs`, `Displays/Win32Displays.cs`, `VirtualDesktops/*.cs`. Read them with
`git -C D:/Workspace/desktop-mcp show windows-port:<path>`.

**Deviation from the spec, recorded:** `DisplayInventory` (a Windows-only tool in the spec) is not added: the shared
`Window` tool's `displays` mode already lists displays with their frames, work areas and scale.

## Global Constraints

- Everything from W1/W2 still holds: one message loop (main thread, STA); blocking or cross-process work on the
  thread pool; WinRT first; no Windows App SDK; tests minimal (owner).
- **Automated tests never inject input, install hooks, show windows or touch the clipboard.** Read-only reads of the
  live desktop (enumerating windows and monitors, a UIA walk) are allowed in tests; **screen capture is not**
  (it can show a capture border and records the user's screen) — capture and annotation are tested on synthetic
  pixels, and the live capture goes to the owner's checks.
- UI Automation runs on pool threads in the MTA (`CoIncrementMTAUsage` once at start-up), never on the main STA.
- Coordinates are physical pixels of the virtual screen: the process is per-monitor DPI aware v2
  (`SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` first thing in `serve`).
- A window that does not answer `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 200 ms)` is listed but not walked;
  at most 300 children per parent are read (both from the C# port).
- macOS output stays byte-identical (`tools/list` snapshot and `snapshot::text` for a macOS `State`).

## Review Focus

1. **A hung application window** — the walk skips it within ~200 ms and the snapshot still returns (Task 4 test
   `hung_window_is_listed_not_walked` with a test window whose thread sleeps).
2. **Mixed-DPI monitors** — window frames, element boxes and display frames share one physical-pixel space (Task 2
   test `display_frames_are_physical`).
3. **A window on another virtual desktop** — not listed and not walked (Task 3, by DWM cloaking,
   `DWMWA_CLOAKED`; live check).
4. **Elevated (admin) windows** — UIPI hides their elements: listed with no elements, never an error (Task 4).
5. **Very large trees (a browser page, Explorer with thousands of files)** — the walk stops at its budget and says
   so; Snapshot answers within its deadline (Task 4 test `budget_stops_the_walk`, synthetic).

---

### Task 1: core hooks in the snapshot text

**Files:** Modify `crates/core/src/snapshot.rs`; Test `crates/core/tests/snapshot.rs`

**Interfaces:**
- Produces on `snapshot::State`:
  ```rust
  /// Platform lines printed after "Input Coordinate Space" (Windows: the current virtual desktop). Empty on macOS.
  pub notes: Vec<String>,
  /// Replaces the macOS hint printed when `use_vision` was asked and no screenshot came back.
  pub screenshot_hint: Option<String>,
  ```
  `WindowView::description` keeps its format; on Windows `bundle_id` holds the executable name (`notepad.exe`).

- [ ] **Step 1: Test** `notes_and_hint_are_printed`: a `State` with `notes = ["Virtual desktop: Desktop 2 (of 3)"]` prints that line under the coordinate space; with `screenshot_hint = Some("X")`, `use_vision` and no screenshot, the text ends with `X` and not the macOS hint. Existing snapshot tests stay unchanged.
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** `cargo test -p desktop-mcp-core` — PASS; macOS check compiles.
- [ ] **Step 5:** Commit `Let a platform add lines and its own capture hint to the snapshot text`.

### Task 2: windows and displays (Win32, DWM, DPI)

**Files:** Create `crates/windows/src/windows_list.rs`, `crates/windows/src/displays.rs`; Modify `crates/windows/src/desktop.rs` (`window_source`), `crates/cli/src/main.rs` (DPI awareness); Test `crates/windows/tests/windows_list.rs`

**Interfaces:**
- Produces:
  ```rust
  // windows_list.rs
  pub struct TopWindow { pub hwnd: isize, pub pid: u32, pub exe: String, pub app: String, pub title: String,
                         pub frame: Frame, pub focused: bool, pub minimized: bool, pub fullscreen: bool }
  /// Visible, uncloaked, unowned (or app-window) top-level windows with a title, in z-order; frames from
  /// DWMWA_EXTENDED_FRAME_BOUNDS; `app` from the executable's FileDescription, else its stem.
  pub fn top_windows() -> Vec<TopWindow>;           // blocking: call on the pool
  pub struct Win32Windows;                          // impl core::window::WindowSource
  // displays.rs
  pub fn displays() -> Vec<DisplayFrame>;           // EnumDisplayMonitors + GetMonitorInfoW + GetDpiForMonitor; index left→right; visible = rcWork; main = primary
  pub fn coordinate_space(displays: &[DisplayFrame]) -> String;
  ```
- `WindowSource` read methods (`windows`, `reading`, `windows_of`, `app_exists`, `displays`) are implemented here,
  `number = Some(hwnd as u32)`; the action methods answer `false`/`FrameResult::Failed` until W4.
- `app` matching (`app: Option<&str>`) is case-insensitive on `app` or `exe` (with or without `.exe`).

- [ ] **Step 1: Tests** (read-only on the live desktop): `top_windows_are_sane` (every entry has a non-empty title, pid ≠ 0, frame width/height > 0 unless minimized; at most one `focused`); `display_frames_are_physical` (the primary display's frame equals `GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN)` at its origin after DPI awareness is set in the test).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `List windows and displays on Windows`.

### Task 3: virtual desktops

**Files:** Create `crates/windows/src/virtual_desktops.rs`; Test in `crates/windows/tests/windows_list.rs`

**Interfaces:**
- Produces:
  ```rust
  /// Whether `hwnd` is on the current virtual desktop (documented IVirtualDesktopManager); true when unknown.
  pub fn on_current_desktop(hwnd: isize) -> bool;
  /// "Virtual desktop: <name> (<n> of <count>)", from the registry (Explorer\VirtualDesktops: VirtualDesktopIDs,
  /// Desktops\{id}\Name; the current id from SessionInfo\<session>\VirtualDesktops\CurrentVirtualDesktop); None
  /// when unreadable. Unnamed desktops are "Desktop <n>".
  pub fn current_desktop_line() -> Option<String>;
  pub fn desktop_line(ids: &[u128], names: &[(u128, String)], current: u128) -> Option<String>; // pure, for tests
  ```
- **(my call, recorded)** No undocumented `IVirtualDesktopManagerInternal`: the documented manager filters windows
  and the registry gives names and order, so nothing depends on per-build IIDs (the spec named the undocumented
  interface; this replaces it with less risk).
- `top_windows` drops windows `on_current_desktop` says are elsewhere; Snapshot puts `current_desktop_line()` in
  `State::notes`.

- [ ] **Step 1: Test** `desktop_line_names_and_counts`: ids `[a, b, c]`, names `[(b, "Work")]`, current `b` → `"Virtual desktop: Work (2 of 3)"`; current `c` → `"Virtual desktop: Desktop 3 (3 of 3)"`; unknown current → `None`.
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS. **Step 5:** Commit `Keep to the current virtual desktop`.

### Task 4: the UI Automation walk

**Files:** Create `crates/windows/src/uia.rs` (session: `IUIAutomation`, cache request, MTA), `crates/windows/src/tree.rs` (walk, classification, budget); Modify `crates/windows/src/desktop.rs` (`find_element`); Test `crates/windows/tests/tree.rs`

**Interfaces:**
- Produces:
  ```rust
  pub struct WalkLimits { pub children_per_parent: usize /* 300 */, pub elements: usize /* 5000 */, pub deadline: Duration /* 4 s */ }
  pub struct Walk { pub tree: TreeState, pub windows: Vec<WindowView>, pub active: Option<WindowView>, pub stopped_early: bool }
  /// Walks the given top-level windows (skipping ones that do not answer WM_NULL in 200 ms) on the pool.
  pub fn walk(windows: &[TopWindow], limits: &WalkLimits) -> Walk;            // blocking
  /// The rules, pure: whether an element is interactive or scrollable, from its cached properties.
  pub fn classify(element: &ElementProps) -> Classification;
  pub struct ElementProps { pub control_type: i32, pub name: String, pub class_name: String, pub framework: String,
                            pub enabled: bool, pub offscreen: bool, pub keyboard_focusable: bool, pub has_invoke: bool,
                            pub has_toggle: bool, pub has_selection_item: bool, pub has_value: bool,
                            pub scrollable: bool, pub rect: (f64, f64, f64, f64) }
  ```
- One `IUIAutomationCacheRequest` per walk with the properties above (`TreeScope_Element | TreeScope_Children`),
  `AutomationElementMode_None`; children by `FindAllBuildCache(TreeScope_Children, TrueCondition)`; boxes clipped to
  their window. Classification follows the C# `TreeWalker`/`TreeConfig` rules (interactive control types, pattern
  checks, "skip word reads on Chromium content"); `control_type` in `TreeNode` is the UIA programmatic name without
  `ControlType.` (`Button`, `Edit`, …), which is also the Windows `Wait role` vocabulary.
- `stopped_early` → a `State::notes` line `"Element list cut short (… elements / … s); narrow with Window list."`.
- `find_element(app, title, role, deadline)`: the same walk over matching windows, first element whose control type
  equals `role` and whose name contains `title` (case-insensitive, exact preferred), centre as `FoundElement`.
- Elevated windows: UIA returns access-denied or nothing; the window stays in the list without elements.

- [ ] **Step 1: Tests:** `classify_rules` (pure: a Button with Invoke is interactive; a disabled one is not; a Pane that is scrollable is scrollable; a Text is neither; a Document from Chromium skips word reads); `budget_stops_the_walk` (pure walk over a synthetic tree source behind the same trait the UIA source implements: 10 000 children → `stopped_early`, ≤ limit); `hung_window_is_listed_not_walked` (a test-created hidden-from-taskbar message-only window is not "shown"; instead create a normal invisible window `WS_POPUP` without `WS_VISIBLE` whose thread sleeps 2 s and call the WM_NULL probe on it directly: returns "not answering" within 300 ms).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS; plus a read-only `walk` over the live desktop completes under 5 s (printed counts, no assertion on content).
- [ ] **Step 5:** Commit `Walk the UI Automation tree on the thread pool`.

### Task 5: capture, annotation and PNG

**Files:** Create `crates/windows/src/capture.rs` (Windows.Graphics.Capture → BGRA), `crates/windows/src/imaging.rs` (Direct2D annotation over a WIC bitmap, PNG encode, scaling); Test `crates/windows/tests/imaging.rs`

**Interfaces:**
- Produces:
  ```rust
  pub struct Pixels { pub width: u32, pub height: u32, pub bgra: Vec<u8>, pub origin: (i32, i32) } // origin in screen pixels
  /// One frame of `display` (borderless when the system allows, `GraphicsCaptureAccess`), copied to the CPU.
  pub async fn capture_display(display: &DisplayFrame) -> Result<Pixels, String>;
  /// Numbered boxes for `elements` (screen coordinates) drawn onto `pixels`; colours and label placement as the C#
  /// `AnnotationLayout` (label inside the box's top-left, outside when the box is too small).
  pub fn annotate(pixels: &mut Pixels, elements: &[TreeNode]) -> Result<(), String>;   // Direct2D on a WIC bitmap
  pub fn encode_png(pixels: &Pixels, max_side: Option<u32>) -> Result<Vec<u8>, String>; // WIC/BitmapEncoder
  ```
  Capture uses a D3D11 device (`D3D11CreateDevice`, BGRA support), `Direct3D11CaptureFramePool::CreateFreeThreaded`,
  one frame, a staging texture copy and `Map`. Snapshot captures the display holding the focused window (else the
  primary), annotates the interactive elements on it and puts the PNG in `State::screenshot`; on failure
  `screenshot_hint` says capture failed and to call Snapshot again.

- [ ] **Step 1: Tests** (synthetic pixels only): `annotate_draws_inside_the_image` (a 200×100 white image, one element box → some pixels change inside the box, none outside a 4 px margin); `encode_png_scales_the_long_side` (3000×1000 → PNG whose IHDR says 1568×523).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Capture a display and annotate it with Direct2D`.

### Task 6: Snapshot, Window, Wait, OCR and Screenshot on Windows

**Files:** Modify `crates/windows/src/desktop.rs` (`snapshot`, `read_text`, `running_apps`, `find_element`), `crates/windows/src/platform.rs` (text, `TOOLS`, the `Screenshot` tool); Create `crates/windows/src/ocr.rs`, `crates/windows/src/screenshot_tool.rs`, `crates/windows/src/processes.rs`; Test `crates/windows/tests/desktop.rs`

**Interfaces:**
- `running_apps()` from a Toolhelp snapshot: regular apps = processes owning a top window; name = `app`, bundle id slot = exe.
- `read_text(request)`: capture the region's display, crop, `OcrEngine::TryCreateFromLanguage` for the first
  supported of `request.languages` (else `TryCreateFromUserProfileLanguages`), `RecognizeAsync` on a `SoftwareBitmap`;
  lines' frames = union of word rects in screen pixels; `confidence` 1.0 (Windows OCR reports none) and
  `min_confidence`/`fast` accepted but without effect (stated in the Windows OCR text).
- `Screenshot` (Windows-only, from `spec/windows/tools.json`'s Screenshot): arguments `display` (index, default the
  focused window's) and optional `region` [x, y, w, h]; answers the PNG (max side 1568) and one text line with the
  captured frame.
- Platform text for `Snapshot` (no Screen Recording wording), `Window` (Windows modes; "minus the taskbar"), `Wait`
  (`role` is a UIA control type such as `Button`), `OCR` (no Screen Recording; confidence note).
- `TOOLS` (W3): `Clipboard, Notification, Scrape, Shell, Snapshot, Wait, Window, Screenshot` (+ `OCR` opt-in).

- [ ] **Step 1: Tests:** `windows_tools_are_listed` (tools/list names equal the W3 list; Snapshot's description mentions UI Automation; Wait's `role` text says control type); `snapshot_text_of_the_live_desktop` (read-only, `use_vision` false: the text has `Focused Window:` and `Open Applications:` sections and no macOS wording).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS; parity still green; macOS check compiles.
- [ ] **Step 5:** Commit `Serve Snapshot, Window, Wait, OCR and Screenshot on Windows`.

### Task 7: live checks

**Files:** Modify `docs/superpowers/plans/2026-11-16-rust-windows-live-checks.md` (W3 section)

- [ ] **Step 1:** Add: Snapshot over Notepad, Explorer, Edge, Settings and an elevated window; `use_vision` image
  boxes line up with the elements on 100 % and 150 % displays; the capture border is not shown (or is accepted);
  windows on another virtual desktop are absent and the desktop line names the current one; Window list/displays
  on two monitors; Wait for a window and an element; OCR on Korean and English text; Screenshot of each display.
- [ ] **Step 2:** Commit `List the W3 checks for the owner`.
