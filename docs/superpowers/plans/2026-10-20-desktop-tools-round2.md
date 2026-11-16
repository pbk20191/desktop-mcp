# Desktop Tools Round 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permission reporting and guards, password-field protection, conditional `Wait`, window maximize/full-screen/display moves, app quit, and name-first Spotlight with an app scope.

**Architecture:** One new tool (`Permissions`) and one shared guard; everything else extends existing tools (`Wait`, `Window`, `App`, `Type`, `Snapshot`, `Spotlight`). Each system call sits behind a seam (protocol or injectable probe) so the logic is unit-tested without permissions or real windows.

**Tech Stack:** Swift 6, ApplicationServices (AX, `AXIsProcessTrusted`), CoreGraphics (`CGPreflightScreenCaptureAccess`), AppKit (`NSScreen`, `NSRunningApplication`, `NSWorkspace`), CoreServices (`AEDeterminePermissionToAutomateTarget`, MDQuery), XCTest.

**Spec:** `docs/superpowers/specs/2026-10-20-desktop-tools-round2-design.md`

## Global Constraints

- Swift 6 strict concurrency; no new `@unchecked Sendable` (`Locked`/`UncheckedSendable` exist); `.macOS(.v13)`; no new dependencies; blocking work through `offload`.
- Work on branch `pbk/native-desktop-tools` (worktree `.claude/worktrees/native-desktop-tools`), on top of 67dc512.
- Accessibility error copy, exactly: `Accessibility is not granted to desktop-mcp. Grant it in System Settings > Privacy & Security > Accessibility, then restart desktop-mcp.` (telemetry label `Accessibility not granted`).
- Secure fields: role or subrole `AXSecureTextField`; value shown as `(hidden)`, no length; `Type` into one returns `Typed N characters into a secure field at (x,y).` and never echoes the text.
- Wait timeout copy: `Timed out after <n>s waiting for <condition>.` (label `Wait timed out`); default `timeout` 10, clamped by `Shell.clampTimeout`; backoff 100 ms → 1 s.
- App quit copy: `Quit requested for <name>.`, `Force-quit <name>.`, `Application '<name>' is not running.`; never desktop-mcp's own pid or `loginwindow`.
- Spotlight app scope: `/Applications`, `/System/Applications`, `~/Applications`.
- Tests never touch real windows, menus, apps, the general pasteboard, Shortcuts, or TCC state; tests that need "not trusted" use the injectable probe.
- `spec/tools.json` and the tool-count assertions (ToolRegistryTests, NIOHTTPServerTests, StdioBridgeServerTests) change only in Task 1 (new `Permissions` tool) and for schema changes of `Wait`, `Window`, `App`, `Spotlight`.
- Build/test: `DEVELOPER_DIR=/Applications/Xcode-16.4.app/Contents/Developer swift test --filter <Class>`; full suite before each commit; `git checkout -- Package.resolved` before committing; commits end with a `Co-Authored-By:` trailer naming the authoring model.

## Review Focus

1. A tool run without Accessibility must fail with the copy above, never with an empty or "No windows." answer. Pinned in Task 1 (`testGuardedToolsRefuseWithoutAccessibility`, driven through the registry for `Window` and `Menu`).
2. A secure field's value must not appear anywhere in Snapshot output, including as a title fallback or description. Pinned in Task 2 (`testSecureFieldValueNeverAppears`).
3. `Wait until element` must not match a secure field by its value. Pinned in Task 5 (`testElementWaitIgnoresSecureValues`).
4. `maximize` on a window on a second display (negative x) fills that display's visible frame, not the main one's. Pinned in Task 3 (`testMaximizeUsesTheWindowsDisplay`).
5. `App quit` with a name that matches several processes quits all of them and never desktop-mcp itself. Pinned in Task 4 (`testQuitTargetsAllMatchesButNeverSelf`).

---

### Task 1: Permissions tool and the Accessibility guard

**Files:**
- Create: `macos/Sources/DesktopMCPCore/PermissionsTool.swift`
- Modify: `Tools.swift` (register `Permissions` after `Menu`, guard calls in `Click`/`Type`/`Scroll`/`Move`/`Shortcut`/`Desktop`, `App` resize/move), `Desktop.swift` (`snapshot` note), `WindowTool.swift`, `MenuTool.swift`, `spec/tools.json`, the three tool-count tests, `macos/README.md`
- Test: `macos/Tests/DesktopMCPCoreTests/PermissionsToolTests.swift`

**Interfaces:**
- Produces:
  - `public struct PermissionState: Sendable, Equatable { var accessibility: Bool; var screenRecording: Bool; var automation: Automation; enum Automation: String, Sendable { case granted, denied = "not granted", notDetermined = "not determined" } }`
  - `public enum Permissions { static let probe: Locked<@Sendable () -> PermissionState>; static func current() -> PermissionState; static func requireAccessibility() throws; static let accessibilityMessage: String; static func format(_ s: PermissionState) -> String; static func tool() -> Tool }` — `probe` defaults to the real checks; tests swap it and restore it in `tearDown`.
  - Every later task's AX-dependent path calls `try Permissions.requireAccessibility()` first.

- [ ] **Step 1: Write the failing tests**

```swift
func testFormat()   // all granted / mixed → exact lines "Accessibility: granted", "Screen Recording: not granted", "Automation (System Events): not determined", then the two "Needs …" lines from spec 1a
func testRequireAccessibilityThrowsTheExactCopy()   // probe accessibility false → ToolError message == Permissions.accessibilityMessage, telemetry "Accessibility not granted"
func testGuardedToolsRefuseWithoutAccessibility() async   // probe false; call Window (mode list) and Menu (mode list) handlers via DesktopMCP.makeRegistry → both throw the copy; Wait with only duration still works
func testSnapshotNotesMissingAccessibility()   // pure: Desktop.permissionNote(trusted: false) == accessibilityMessage + "\n"; true → ""
```

- [ ] **Step 2:** Run `--filter PermissionsToolTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec 1a/1b. `AEDeterminePermissionToAutomateTarget` with an `AEAddressDesc` for bundle id `com.apple.systemevents`, `typeWildCard`/`typeWildCard`, `askUserIfNeeded: false`; `noErr` → granted, `errAEEventNotPermitted` → not granted, `errAEEventWouldRequireUserConsent` → not determined, anything else (e.g. `procNotFound`) → not determined. Schema: no properties. Annotations: readOnly true, idempotent true. README: Tools row and the permissions paragraph.
- [ ] **Step 4:** Run the class, then the full suite; expect PASS (tool counts +1).
- [ ] **Step 5:** Commit `Add the Permissions tool and refuse Accessibility-bound calls without the grant`.

---

### Task 2: Secure fields

**Files:**
- Modify: `macos/Sources/DesktopMCPCore/Tree.swift` (value read/emission), `Desktop.swift` (`type`), `Tools.swift` (`Type` result), `macos/README.md`
- Test: `macos/Tests/DesktopMCPCoreTests/SecureFieldTests.swift`

**Interfaces:**
- Consumes: none.
- Produces: `enum SecureField { static func isSecure(role: String?, subrole: String?) -> Bool; static let hidden = "(hidden)"; static func typedMessage(count: Int, x: Int, y: Int) -> String }`; `Desktop.isSecureField(atX:y:) async -> Bool` (AX `AXUIElementCopyElementAtPosition` on the system-wide element, role/subrole check; false on any error).

- [ ] **Step 1: Write the failing tests**

```swift
func testIsSecure()   // ("AXTextField","AXSecureTextField") true; ("AXSecureTextField",nil) true; ("AXTextField",nil) false
func testSecureFieldValueNeverAppears()   // build a Tree node fixture the way Tree's formatting tests do, role AXTextField subrole AXSecureTextField, value "hunter2", no title → formatted output contains "(hidden)" and not "hunter2", and not "7"-length hints such as "•••••••"
func testTypedMessage()   // typedMessage(count: 7, x: 10, y: 20) == "Typed 7 characters into a secure field at (10,20)."
```

- [ ] **Step 2:** Run `--filter SecureFieldTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec 2a/2b: Tree reads `kAXSubroleAttribute` where it reads role, and for a secure element skips `kAXValueAttribute` entirely (no value read, no title fallback from value, metadata `value` = `(hidden)`). `Type`: before typing, `isSecureField(atX:y:)`; when true, type as usual and return `typedMessage`; otherwise the existing message. If Tree has no formatting fixture seam, extract a pure `Tree.describe(role:subrole:title:value:description:)` used by the walker and test that.
- [ ] **Step 4:** Run the class, the Tree/Formatting tests, then the full suite; expect PASS.
- [ ] **Step 5:** Commit `Never read or echo password fields; Type reports only the length for them`.

---

### Task 3: Window maximize, full screen and displays

**Files:**
- Modify: `macos/Sources/DesktopMCPCore/WindowTool.swift`, `Geometry.swift` (display visible frames), `spec/tools.json`, `macos/README.md`
- Test: `macos/Tests/DesktopMCPCoreTests/WindowToolTests.swift`

**Interfaces:**
- Consumes: Task 1 `Permissions.requireAccessibility()`.
- Produces:
  - `public struct DisplayFrame: Sendable, Equatable { var index: Int; var name: String; var frame: CGRect; var visible: CGRect; var scale: Double; var main: Bool }` (points, top-left origin of the main display, `Displays.all()` order) and `Displays.frames() -> [DisplayFrame]` (from `NSScreen.screens` on the main actor, matched to `CGDirectDisplayID` via `NSScreenNumber`).
  - `WindowSource` gains `func setFrame(_ w: WindowInfo, _ frame: CGRect) async -> Bool` and `func setFullScreen(_ w: WindowInfo, _ on: Bool) async -> Bool`, plus `func displays() async -> [DisplayFrame]` (fake returns fixtures).
  - `WindowTool.maximizedFrame(for window: CGRect, displays: [DisplayFrame]) -> CGRect` (visible frame of the display containing the window's centre, else the nearest by centre distance) and `WindowTool.frame(_ window: CGRect, movedFrom: DisplayFrame, to: DisplayFrame) -> CGRect` (same offset relative to the visible frame, size kept, clamped inside the target's visible frame), `WindowTool.formatDisplays(_:) -> String`.
  - New modes `maximize`, `fullscreen`, `exit_fullscreen`, `display`, `displays`; argument `display` (integer).

- [ ] **Step 1: Write the failing tests**

```swift
func testMaximizeUsesTheWindowsDisplay()   // displays: main [0,0,1440,900] visible [0,25,1440,800]; left [-1920,0,1920,1080] visible [-1920,25,1920,1055]; window centre at x -500 → [-1920,25,1920,1055]
func testMaximizeOffscreenWindowUsesNearestDisplay()
func testMoveToDisplayKeepsOffsetAndClamps()   // window [100,125,800,600] on main → display 1 → [-1820,125,800,600]; a 2500-wide window is clamped to 1920
func testFormatDisplays()   // "0\tBuilt-in\t[0,0,1440,900]\tvisible [0,25,1440,800]\tscale 2\tmain"
func testFullscreenModesCallTheSource() async throws   // fake records setFullScreen(true/false); outputs "Full screen <id>." / "Left full screen <id>."
func testDisplayModeNeedsAValidIndex() async   // missing → "display required for display mode."; 9 → "No display 9; see Window displays."
func testNewModesRefuseWithoutAccessibility() async   // probe false → the Task 1 copy
```

- [ ] **Step 2:** Run `--filter WindowToolTests`; expect the new tests to fail to compile.
- [ ] **Step 3:** Implement per spec decision 4 in `AXWindowSource` (`setFrame` = position then size then position again, as Rectangle does; `AXFullScreen` set via `AX.set`), with the same re-resolve-by-number rule as the other modes. Result copy: `Maximized <id>.`, `Full screen <id>.`, `Left full screen <id>.`, `Moved <id> to display <n>.`. Update the tool description and `spec/tools.json`.
- [ ] **Step 4:** Run the class, then the full suite; expect PASS.
- [ ] **Step 5:** Commit `Window: maximize, enter and leave full screen, move to a display, list displays`.

---

### Task 4: App quit and force quit

**Files:**
- Modify: `macos/Sources/DesktopMCPCore/Desktop.swift` (`app`), `Tools.swift` (`App` schema/description), `spec/tools.json`, `macos/README.md`
- Test: create `macos/Tests/DesktopMCPCoreTests/AppQuitTests.swift`

**Interfaces:**
- Produces: `public struct RunningAppInfo: Sendable, Equatable { var pid: pid_t; var name: String; var bundleID: String? }`; `enum AppQuit { static func targets(_ name: String, running: [RunningAppInfo], ownPID: pid_t) throws -> [RunningAppInfo] }` (match like `Window`'s filter; drop own pid and `com.apple.loginwindow`; none left → `Application '<name>' is not running.`); `Desktop` uses `NSRunningApplication(processIdentifier:)?.terminate()` / `forceTerminate()` inside `offload`.

- [ ] **Step 1: Write the failing tests**

```swift
func testQuitTargetsAllMatchesButNeverSelf()   // two "Preview" pids + own pid named "Preview" → the two others
func testLoginwindowIsNeverATarget()
func testNotRunning()                           // → "Application 'Nope' is not running."
func testMatchesBundleIDExactlyAndNameCaseInsensitively()
func testQuitCopy()                             // Desktop.quitMessage(name:force:) → "Quit requested for Preview." / "Force-quit Preview."
```

- [ ] **Step 2:** Run `--filter AppQuitTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec decision 5; `App` modes enum gains `quit`, `force_quit`; `name` required for both (`"App name or bundle ID required for quit."`). The handler never calls `terminate` in tests (the seam is `targets`).
- [ ] **Step 4:** Run the class, then the full suite; expect PASS.
- [ ] **Step 5:** Commit `App: quit or force-quit by name or bundle id, never desktop-mcp itself`.

---

### Task 5: Conditional Wait

**Files:**
- Create: `macos/Sources/DesktopMCPCore/WaitTool.swift` (move `Wait` out of Tools.swift)
- Modify: `Tools.swift`, `spec/tools.json`, `macos/README.md`
- Test: create `macos/Tests/DesktopMCPCoreTests/WaitToolTests.swift`

**Interfaces:**
- Consumes: Task 1 guard; `WindowSource` and `WindowTool.format` (Task 3 state); `SecureField.isSecure` (Task 2).
- Produces:
  - `enum WaitTool { static func poll<T: Sendable>(timeout: Double, check: @Sendable () async -> T?) async throws -> T? }` (nil on timeout; backoff 0.1 s ×2 up to 1 s; honours cancellation);
  - `protocol ElementFinder: Sendable { func find(app: String?, title: String?, role: String?) async -> [FoundElement] }`, `struct FoundElement: Sendable, Equatable { var role: String; var title: String; var center: CGPoint }`, `struct AXElementFinder: ElementFinder` (menu-bar owner by default, depth ≤ 25, matches title/description/value case-insensitively, never reads values of secure elements);
  - `WaitTool.tool(windows: any WindowSource = AXWindowSource(), elements: any ElementFinder = AXElementFinder(), running: @escaping @Sendable () async -> [RunningAppInfo] = …) -> Tool`.

- [ ] **Step 1: Write the failing tests**

```swift
func testPlainDurationStillWorks() async throws       // duration 0 → "Waited for 0 seconds."
func testPollReturnsAsSoonAsTheCheckSucceeds() async throws   // check succeeds on the 3rd call → value, elapsed < 1 s
func testPollTimesOut() async throws                  // timeout 0.3 → nil, elapsed between 0.3 and 1.5 s
func testAppRunningAndQuit() async throws             // fake running list flips → "App Preview is running." / "App Preview quit."
func testWindowAppearsReturnsTheListLine() async throws
func testWindowGone() async throws
func testElementFoundReturnsCentre() async throws     // 'AXButton "Save" at [120,340]'
func testElementWaitIgnoresSecureValues() async throws // a secure element whose value is "Save" never matches title "Save"
func testTimeoutCopy() async                           // "Timed out after 1s waiting for window \"Report\" in Preview."
func testArgumentRules() async                         // until element without title/role → "title or role required for element."; app_running without app → "app required for app_running."
```

- [ ] **Step 2:** Run `--filter WaitToolTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec decision 3. Condition descriptions for the timeout copy: `app <name> to run`, `app <name> to quit`, `window "<title>" in <app>` (parts present only when given), `window … to close`, `element <role> "<title>"`. `timeout` clamped with `Shell.clampTimeout`; `Wait` annotations unchanged (readOnly). The element and window conditions call the Accessibility guard; app conditions do not.
- [ ] **Step 4:** Run the class, then the full suite; expect PASS.
- [ ] **Step 5:** Commit `Wait: wait until an app runs or quits, a window appears or closes, or an element appears`.

---

### Task 6: Spotlight name-first ranking and app scope

**Files:**
- Modify: `macos/Sources/DesktopMCPCore/SpotlightTool.swift`, `macos/README.md` (and the tool description in `spec/tools.json`)
- Test: `macos/Tests/DesktopMCPCoreTests/SpotlightToolTests.swift`

**Interfaces:**
- Produces: `SpotlightTool.nameQuery(_ query: String) throws -> String` (name clauses only), `SpotlightTool.contentOnlyQuery(_ query: String) throws -> String` (the existing words query `&& (kMDItemDisplayName != "*w1*"cdw || …)`), `SpotlightTool.defaultScope(kind: String) -> [String]` (app dirs for `application`, else `["~"]`), `SpotlightTool.merge(first: [Item], firstTotal: Int, second: [Item], secondTotal: Int, limit: Int) -> (items: [Item], total: Int)`.

- [ ] **Step 1: Write the failing tests**

```swift
func testNameQuery() throws          // "tax 2025" → #"kMDItemDisplayName == "*tax*"cdw && kMDItemDisplayName == "*2025*"cdw"#
func testContentOnlyQuery() throws   // words query && (kMDItemDisplayName != "*tax*"cdw || kMDItemDisplayName != "*2025*"cdw)
func testDefaultScope()              // application → ["/Applications","/System/Applications","~/Applications"]; any/pdf → ["~"]; an explicit scope wins
func testMergeNameMatchesFirst()     // first 2 items, second 3, limit 4 → first's two then second's newest two; total 5
func testSecondQuerySkippedWhenFirstFills() // merge with limit 2 and first.count 2 → second rows unused, total = firstTotal + secondTotal
func testLiveAppSearchFindsSafari() async throws   // words "safari", kind application, no scope → a row ending "/Safari.app"; XCTSkip if Spotlight returns nothing
```

- [ ] **Step 2:** Run `--filter SpotlightToolTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec decision 6. The handler runs the name query; runs the content-only query only for its count when the first filled `limit`, else reads `limit - first.count` rows from it; both share the existing deadline and stop line. Raw queries keep the single-query path.
- [ ] **Step 4:** Run the class, then the full suite; expect PASS.
- [ ] **Step 5:** Commit `Spotlight: name matches first, apps found without a scope`.
