# Native Desktop Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add seven MCP tools (`Open`, `Clipboard`, `RunShortcut`, `Spotlight`, `Window`, `Menu`, `OCR`), each a thin wrapper over one macOS API, in order of simplicity.

**Architecture:** One Core file per tool exposing `static func tool(...) -> Tool`, appended to `DesktopMCP.tools(_:)` in `Tools.swift`. Every system call sits behind a small seam (a protocol, a closure, or a named pasteboard) so the logic around it is unit-tested without Accessibility or Screen Recording.

**Tech Stack:** Swift 6, AppKit (NSWorkspace, NSPasteboard), CoreServices (MDQuery), ApplicationServices (AX), Vision, ScreenCaptureKit (existing `Screenshot`), `/usr/bin/shortcuts`, XCTest.

**Spec:** `docs/superpowers/specs/2026-10-20-native-desktop-tools-design.md`

## Global Constraints

- Swift 6 strict concurrency; no `@unchecked Sendable` (existing `Locked`/`UncheckedSendable` may be used); platform `.macOS(.v13)`; SDK 0.12.1; no new dependencies.
- Blocking calls (MDQuery, AX, Vision, pasteboard, NSWorkspace) go through the existing `offload` helper (`Apps.swift`).
- Tool names exactly: `Open`, `Clipboard`, `RunShortcut`, `Spotlight`, `Window`, `Menu`, `OCR`; registered after `Notification` in `DesktopMCP.tools(_:)`, in this order; excludable like all tools.
- Coordinates in and out are screen points, origin top-left of the main display (what `Click` takes).
- Errors the model sees are `ToolError("<exact copy>")`; copy is fixed in each task.
- Tests: `DEVELOPER_DIR=/Applications/Xcode-16.4.app/Contents/Developer ANONYMIZED_TELEMETRY=false swift test --filter <Class>` from the repo root; never touch the general pasteboard, never run a real Shortcut, never press a real menu item, never move or close a real window, never open apps or files in tests.
- `swift test` rewrites `Package.resolved`: `git checkout -- Package.resolved` before every commit.
- Each task adds its row to the README's Tools table (`macos/README.md`), in the existing style.
- Commits end with a blank line and `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; local on branch `pbk/native-desktop-tools` off `master`.

## Review Focus

1. `Open` with a path containing spaces or non-ASCII characters (`~/문서/a b.pdf`): opens that file, no URL-escaping damage. Pinned in Task 1 (`testResolvesNonASCIIPathWithSpaces`).
2. `Clipboard read` when the pasteboard holds both text and file URLs (Finder copy): both are reported. Pinned in Task 2 (`testReadReportsTextAndFilesTogether`).
3. `Spotlight` query words containing Spotlight syntax (`*`, `"`, `\`, `==`): escaped, never break the query or widen it. Pinned in Task 4 (`testWordsWithQuerySyntaxAreEscaped`).
4. `Window` acting on a window that moved or closed after `list`: refuses with the "changed since it was listed" error, never acts on another window. Pinned in Task 5 (`testMovedWindowIsRefused`, `testClosedWindowIsRefused`).
5. `OCR` on a Retina display region not at the origin: returned centres are in points and land on the text. Pinned in Task 7 (`testNormalizedBoxMapsToScreenPoints`, `testSecondDisplayOffset`).

---

### Task 1: `Open`

**Files:**
- Create: `macos/Sources/DesktopMCPCore/OpenTool.swift`
- Modify: `macos/Sources/DesktopMCPCore/Tools.swift` (append the tool), `macos/README.md`
- Test: `macos/Tests/DesktopMCPCoreTests/OpenToolTests.swift`

**Interfaces:**
- Produces:
  - `public protocol Workspace: Sendable { func open(_ url: URL, withApplicationAt app: URL?) async throws; func reveal(_ url: URL) async; func applicationURL(for nameOrBundleID: String) async -> URL? }`
  - `public struct SystemWorkspace: Workspace` (NSWorkspace; app lookup by bundle id, then by name via `NSWorkspace.urlForApplication(withBundleIdentifier:)` and `/Applications`, `/System/Applications`, `~/Applications` `<name>.app`).
  - `enum OpenTool { enum Target: Equatable { case url(URL), file(URL) }; static func resolve(_ target: String) throws -> Target; static func tool(workspace: any Workspace) -> Tool }`
  - `DesktopMCP.stringArray() -> Value` (schema helper, `{"type":"array","items":{"type":"string"}}`) and `ToolArguments.stringList(_ name: String) throws -> [String]?` in `Tools.swift`; Tasks 2, 4 and 6 use them.

- [ ] **Step 1: Write the failing tests** (a `RecordingWorkspace` actor records calls)

```swift
func testTildeIsExpanded() throws                   // resolve("~") == .file(URL(fileURLWithPath: NSHomeDirectory()))
func testResolvesNonASCIIPathWithSpaces() throws    // file "문서 a b.txt" in a temp dir → .file(url) with url.path equal to the input
func testMissingFileErrors()                        // "No such file: /nope/x"
func testURLIsNotCheckedOnDisk() throws             // "https://example.com/a?b=1" → .url, unchanged absoluteString
func testEmptyTargetErrors()                        // "" and "  " → "target is required."
func testOpenWithUnknownAppErrors() async           // app "NoSuchApp" → "Application 'NoSuchApp' not found."
func testOpenWithAppPassesAppURL() async throws     // workspace records (fileURL, appURL)
func testRevealRequiresAnExistingPath() async       // mode reveal + URL target → "reveal needs an existing file or folder path."
func testOutputs() async throws                     // "Opened <target>." / "Opened <target> with <App>." / "Revealed <path> in Finder."
```

- [ ] **Step 2:** Run `--filter OpenToolTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec decision 1. Schema: `target` (string, required), `mode` (`open` | `reveal`, default `open`), `app` (nullable string). Annotations: readOnly false, destructive false, idempotent false, openWorld true. `<App>` in output is the app bundle's display name (`FileManager.displayName(atPath:)` without `.app`).
- [ ] **Step 4:** Run `--filter OpenToolTests`, then the full suite; expect PASS.
- [ ] **Step 5:** Commit `Add the Open tool: open a file or URL, optionally with an app, or reveal it in Finder`.

---

### Task 2: `Clipboard`

**Files:**
- Create: `macos/Sources/DesktopMCPCore/ClipboardTool.swift`
- Modify: `Tools.swift`, `macos/README.md`
- Test: `macos/Tests/DesktopMCPCoreTests/ClipboardToolTests.swift`

**Interfaces:**
- Consumes: Task 1 `stringArray()`, `stringList(_:)`.
- Produces: `enum ClipboardTool { static let textLimit = 100_000; static let imageMaxSide = 1568; static func read(_ name: NSPasteboard.Name) async -> [MCP.Tool.Content]; static func write(text: String?, paths: [String]?, to name: NSPasteboard.Name) async throws -> String; static func tool(pasteboard: NSPasteboard.Name = .general) -> Tool }`. The pasteboard object is created from the name inside `offload`, never stored.

- [ ] **Step 1: Write the failing tests** (each test uses `NSPasteboard.Name("desktop-mcp-test-\(UUID())")` and calls `releaseGlobally()` in `tearDown`)

```swift
func testReadEmpty() async                    // text == "The clipboard is empty."
func testWriteThenReadText() async throws     // write "안녕 hi" → "Copied 5 characters."; read → text content exactly "안녕 hi"
func testTextIsTruncated() async throws       // 100_010 chars → first 100_000 + "… (truncated, 100010 characters)"
func testWritePathsRequiresExisting() async   // "No such file: /nope"
func testWritePathsReadsBackAsFiles() async throws // two temp files → "Copied 2 files."; read contains "Files:\n<p1>\n<p2>"
func testReadReportsTextAndFilesTogether() async throws // set string + file URL items directly → both sections present, text first
func testImageIsPNGScaledToMaxSide() async throws // put a 3000×1000 PNG → read returns .image mimeType "image/png", decoded width 1568, height 523
func testTextAndPathsAreExclusive() async     // "Pass either text or paths, not both."
func testWriteNeedsSomething() async          // "write needs text or paths."
```

- [ ] **Step 2:** Run `--filter ClipboardToolTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec decision 2. Read order: text, then `Files:` block, then image content; TIFF converted to PNG through ImageIO; scaling with the existing `Screenshot.makeContext` pattern (Core Graphics, high interpolation). Annotations: readOnly false, destructive true, idempotent false, openWorld false.
- [ ] **Step 4:** Run the class, then the full suite; expect PASS.
- [ ] **Step 5:** Commit `Add the Clipboard tool: read text, files and images, write text or files`.

---

### Task 3: `RunShortcut`

**Files:**
- Create: `macos/Sources/DesktopMCPCore/RunShortcutTool.swift`
- Modify: `Tools.swift`, `macos/README.md`
- Test: `macos/Tests/DesktopMCPCoreTests/RunShortcutToolTests.swift`

**Interfaces:**
- Consumes: `Shell.run(_:timeout:) -> Shell.Run` (`stdout`, `stderr`, `status`, `timedOut`), `Shell.clampTimeout(_:)`.
- Produces: `enum RunShortcutTool { typealias Runner = @Sendable ([String], Double) async throws -> Shell.Run; static let binary = "/usr/bin/shortcuts"; static func closeMatches(_ name: String, in names: [String]) -> [String]; static func tool(runner: @escaping Runner = Shell.run) -> Tool }`.

- [ ] **Step 1: Write the failing tests** (a fake `Runner` records argv and answers `list` with fixed names; for `run` it asserts the input file's contents and `0600` mode, then writes to the `--output-path` file)

```swift
func testListReturnsNamesOnePerLine() async throws  // fake stdout "A\n\n B \n" → "A\nB"; argv == [binary, "list"]
func testListWithFolder() async throws               // argv == [binary, "list", "--folder-name", "Work"]
func testRunPassesInputAndReturnsOutput() async throws // input "hello" → file holds "hello", mode 0o600; output "Shortcut 'Resize' finished with status 0.\n<out>"
func testRunWithoutOutputSaysSo() async throws       // empty output file → "...status 0.\n(no output)"
func testUnknownNameRunsNothing() async              // names ["Resize Image","Make GIF"], name "resize" → error "No shortcut named 'resize'. Close matches: Resize Image"; no run argv recorded
func testUnknownNameWithoutMatches() async           // "No shortcut named 'zzz'."
func testTempFilesAreRemoved() async throws          // after run, neither path exists
func testTimeoutIsClamped() async throws             // timeout 100000 → runner receives Double(Shell.clampTimeout(100000))
func testCloseMatchesOrderAndCap()                   // prefix matches before substring matches, case-insensitive, at most 20
```

- [ ] **Step 2:** Run `--filter RunShortcutToolTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec decision 3. Schema: `mode` (`run` | `list`, default `run`), `name`, `input`, `folder` (nullable strings), `timeout` (integer, default 60). A timed-out run → `"Shortcut '<name>' timed out after <n>s."`. Annotations: destructive true, openWorld true.
- [ ] **Step 4:** Run the class, then the full suite; expect PASS.
- [ ] **Step 5:** Commit `Add the RunShortcut tool: list and run the user's Shortcuts`.

---

### Task 4: `Spotlight`

**Files:**
- Create: `macos/Sources/DesktopMCPCore/SpotlightTool.swift`
- Modify: `Tools.swift`, `macos/README.md`
- Test: `macos/Tests/DesktopMCPCoreTests/SpotlightToolTests.swift`

**Interfaces:**
- Consumes: Task 1 `stringArray()`, `stringList(_:)`.
- Produces: `enum SpotlightTool { struct Item: Sendable, Equatable { var path: String; var kind: String; var modified: Date; var size: Int?; var text: String? }; static func wordsQuery(_ query: String) throws -> String; static func kindClause(_ kind: String) -> String?; static func search(_ query: String, scope: [URL], timeout: Double) async throws -> [Item]; static func format(_ items: [Item], limit: Int, content: Bool) -> String; static func tool() -> Tool }`.

- [ ] **Step 1: Write the failing tests**

```swift
func testWordsQuery() throws
// wordsQuery("tax 2025") == #"(kMDItemDisplayName == "*tax*"cdw || kMDItemTextContent == "tax"cdw) && (kMDItemDisplayName == "*2025*"cdw || kMDItemTextContent == "2025"cdw)"#
func testWordsWithQuerySyntaxAreEscaped() throws   // word a"b\c*= → a\"b\\c\*= inside both clauses
func testEmptyQueryErrors()                       // "query is required."
func testKindClauses()                            // pdf → #"kMDItemContentTypeTree == "com.adobe.pdf""#; any → nil; document public.content; image public.image; folder public.folder; application com.apple.application
func testInvalidRawQueryErrors() async            // raw "kMDItem ==" → "Invalid Spotlight query: kMDItem =="
func testFormatSortsNewestFirstAndTruncates()     // 3 items, limit 2 → newest two, tab-separated "<path>\t<kind>\t<ISO-8601>\t<size>", then "1 more not shown"
func testFormatWithContent()                      // text of 400 chars → indented next line with first 300
func testNoMatches()                              // [] → "No matches."
func testLiveQueryFindsACreatedFile() async throws // create a uniquely named file in the temp dir, poll search for 5 s; XCTSkip("not indexed yet") if absent
```

- [ ] **Step 2:** Run `--filter SpotlightToolTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec decision 4. `search` runs `MDQueryCreate` / `MDQuerySetSearchScope` / `MDQueryExecute(kMDQuerySynchronous)` inside `offload`, with a 10 s watchdog that calls `MDQueryStop`; attributes read: `kMDItemPath`, `kMDItemKind`, `kMDItemContentModificationDate`, `kMDItemFSSize`, `kMDItemTextContent` (only when `content`). `limit` clamped to 1...200; `scope` default `[home]`, `~` expanded. Annotations: readOnly true, idempotent true.
- [ ] **Step 4:** Run the class, then the full suite; expect PASS (the live test may skip).
- [ ] **Step 5:** Commit `Add the Spotlight tool: search the system index by words or a raw query`.

---

### Task 5: `Window`

**Files:**
- Create: `macos/Sources/DesktopMCPCore/WindowTool.swift`
- Modify: `Tools.swift`, `macos/README.md`
- Test: `macos/Tests/DesktopMCPCoreTests/WindowToolTests.swift`

**Interfaces:**
- Produces:
  - `public struct WindowInfo: Sendable, Equatable { var pid: pid_t; var index: Int; var app: String; var title: String; var frame: CGRect; var focused: Bool; var minimized: Bool; var fullscreen: Bool; var id: String { "\(pid):\(index)" } }`
  - `public protocol WindowSource: Sendable { func windows(app: String?) async -> [WindowInfo]; func windows(pid: pid_t) async -> [WindowInfo]; func focus(_ w: WindowInfo) async -> Bool; func setMinimized(_ w: WindowInfo, _ on: Bool) async -> Bool; func move(_ w: WindowInfo, to: CGPoint) async -> Bool; func resize(_ w: WindowInfo, to: CGSize) async -> Bool; func close(_ w: WindowInfo) async -> Bool }`
  - `struct AXWindowSource: WindowSource` (regular-activation-policy apps; `AXWindows`, `AXTitle`, position/size, `AXMinimized`, `AXFullScreen`, focused = frontmost app's `AXFocusedWindow`; focus = activate + `AXRaise` + `AXMain`; close = press `AXCloseButton`).
  - `actor WindowCache { static let capacity = 64; func remember(_ windows: [WindowInfo]); func listed(_ id: String) -> WindowInfo? }` (insertion-ordered, oldest dropped).
  - `enum WindowTool { static func format(_ windows: [WindowInfo]) -> String; static func resolve(_ id: String, source: any WindowSource, cache: WindowCache) async throws -> WindowInfo; static func tool(source: any WindowSource = AXWindowSource(), cache: WindowCache = WindowCache()) -> Tool }`

- [ ] **Step 1: Write the failing tests** (a `FakeWindowSource` actor holds a mutable window list and records actions)

```swift
func testListFormat()            // "123:0\tSafari\t\"Docs\"\t[10,20,800,600]\tfocused" ; no flags → no trailing tab
func testInvalidIdErrors() async // "abc" → "Invalid window id: abc"
func testUnlistedIdErrors() async // "Window 1:0 was not listed; call Window list first."
func testMovedWindowIsRefused() async   // list, then fake changes frame → "Window 1:0 changed since it was listed; call Window list again."; no action recorded
func testClosedWindowIsRefused() async  // list, then fake removes it → same error
func testMoveUpdatesTheCache() async throws // move to [5,5] succeeds, a following resize on the same id succeeds
func testModesNeedTheirArguments() async   // move without loc → "loc required for move mode."; resize without size → "size required for resize mode."; non-list without id → "id required for <mode> mode."
func testCloseReportsRequest() async throws // "Close requested for 1:0 (\"Docs\")."
func testCacheDropsOldest() async          // remember 65 single-window listings → first id no longer listed
```

- [ ] **Step 2:** Run `--filter WindowToolTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec decision 5. Frame comparison after rounding to integers. A source action returning false → `"Could not <mode> window <id>."`. Successful `move`/`resize` re-reads the window and updates the cache. Outputs: `"Focused <id>."`, `"Minimized <id>."`, `"Restored <id>."`, `"Moved <id> to (x,y)."`, `"Resized <id> to w×h."`. Annotations: destructive true.
- [ ] **Step 4:** Run the class, then the full suite; expect PASS.
- [ ] **Step 5:** Commit `Add the Window tool: list windows and focus, move, resize, minimize or close one by id`.

---

### Task 6: `Menu`

**Files:**
- Create: `macos/Sources/DesktopMCPCore/MenuTool.swift`
- Modify: `Tools.swift`, `macos/README.md`
- Test: `macos/Tests/DesktopMCPCoreTests/MenuToolTests.swift`

**Interfaces:**
- Consumes: Task 1 `stringArray()`, `stringList(_:)`; `App.menuBar` (`Apps.swift`).
- Produces:
  - `protocol MenuNode { var title: String { get }; var enabled: Bool { get }; var shortcut: String? { get }; var isSeparator: Bool { get }; func children() -> [Self]; func press() -> Bool }`
  - `struct AXMenuNode: MenuNode` (wraps an `AXUIElement`; `children()` reads the item's `AXMenu` child's `AXChildren` without opening the menu; `shortcut` from `AXMenuItemCmdChar` + `AXMenuItemCmdModifiers`). Used only inside `offload`.
  - `enum MenuTool { static func shortcutLabel(char: String, modifiers: Int) -> String; static func resolve<N: MenuNode>(_ path: [String], in roots: [N]) throws -> [N]; static func list<N: MenuNode>(_ roots: [N], depth: Int) -> String; static func tool() -> Tool }`
  - Test-only `struct TreeNode: MenuNode` in the test file (title, enabled, shortcut, children, a `Locked<Int>` press counter).

- [ ] **Step 1: Write the failing tests** (a tree: Apple / File[New ⌘N, Export… (disabled), Open Recent > [a, b]] / Edit[Copy, copy])

```swift
func testExactThenCaseInsensitiveThenEllipsis() throws // ["file","Export..."] resolves to File > Export…
func testAppleAlias() throws                           // ["Apple"] resolves to root 0
func testAmbiguousErrors()                             // ["Edit","COPY"] → "Menu item 'COPY' is ambiguous: Copy, copy"
func testExactBeatsCaseInsensitive() throws            // ["Edit","copy"] → the lowercase item, no error
func testMissingListsSiblings()                        // ["File","Save"] → "No menu item 'Save' under File. Available: New, Export…, Open Recent"
func testMissingTopLevel()                             // ["View"] → "No menu item 'View' under the menu bar. Available: Apple, File, Edit"
func testDisabledIsNotPressed()                        // select File > Export… → "Menu item File > Export… is disabled."; press count 0
func testSelectPressesLeafOnly() throws                // File > Open Recent > b → only b pressed; output "Selected File > Open Recent > b."
func testListIndentsAndMarks()                         // depth 2: "File\n  New  ⌘N\n  Export…  [disabled]\n  Open Recent\n..." separators omitted
func testDepthIsCapped()                               // depth 9 behaves as 4
func testShortcutLabels()                              // ("E",0) "⌘E"; ("E",1) "⇧⌘E"; ("E",6) "⌃⌥⌘E"; ("E",8) "E"
```

- [ ] **Step 2:** Run `--filter MenuToolTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec decision 6, with one refinement: children are read without opening menus; only if a bar item reports no children is it pressed to populate, and Escape is sent (`Input` key press) when a later step fails. Modifier bits: 1 shift, 2 option, 4 control, 8 no-command; rendered in the order ⌃⌥⇧⌘. Schema: `mode` (`list` | `select`, default `list`), `app` (nullable), `path` (string array), `depth` (integer, default 2). No frontmost app or no menu bar → `"No menu bar for <app or 'the frontmost app'>."`. Annotations: destructive true.
- [ ] **Step 4:** Run the class, then the full suite; expect PASS.
- [ ] **Step 5:** Commit `Add the Menu tool: list an app's menus and select an item by its path`.

---

### Task 7: `OCR`

**Files:**
- Create: `macos/Sources/DesktopMCPCore/OCRTool.swift`
- Modify: `macos/Sources/DesktopMCPCore/Screenshot.swift` (make the per-display capture reachable: `static func capture(_ display: DisplayInfo) async -> CGImage?`, used by both paths), `Tools.swift`, `Config.swift`, `ServePlan.swift`, `macos/Sources/desktop-mcp/DesktopMCPCommand.swift` (`--include-tools`), `macos/README.md`
- Test: `macos/Tests/DesktopMCPCoreTests/OCRToolTests.swift`, `ToolRegistryTests.swift`, `ConfigTests.swift`

**Opt-in (owner, 2026-10-20):** `OCR` is not registered by default. Mirror the existing exclude plumbing:
- `DesktopMCPConfig.Tools.include: [String]` parsed from `[tools] include` and written back like `exclude`;
- `ServePlan.includedTools: [String]`, merged from `--include-tools` / `DESKTOP_MCP_INCLUDE_TOOLS` (`ServePlan.environmentKeys.includeTools`) and config, deduplicated, part of `==`;
- `DesktopMCP.optInTools: Set<String> = ["OCR"]`; `makeRegistry(desktop:excluding:including:)` registers an opt-in tool only when it is in `including`; `exclude` still removes it; log copy per spec decision 7;
- `Serve` passes `plan.includedTools`; README options table gains the `--include-tools` row and the Tools table marks `OCR` as opt-in.
- Tool-count assertions do not change (OCR is off by default); `spec/tools.json` describes the default tool list, so OCR is added there only if the contract test compares against an opt-in-aware list (check the test; if it compares the default registry, leave OCR out and say so in the report).

**Interfaces:**
- Consumes: `Displays.all()`, `DisplayInfo` (`logicalLeft/Top/Width/Height`, `scale`), `Screenshot.capture(_:)`, `CGPreflightScreenCaptureAccess`.
- Produces: `enum OCRTool { struct Line: Sendable, Equatable { var text: String; var box: CGRect; var confidence: Float }; static let lineLimit = 500; static func recognize(_ image: CGImage, languages: [String], fast: Bool, minConfidence: Float) throws -> [(text: String, normalized: CGRect, confidence: Float)]; static func toScreen(_ normalized: CGRect, imagePixels: CGSize, origin: CGPoint, scale: Double) -> CGRect; static func order(_ lines: [Line]) -> [Line]; static func format(_ lines: [Line]) -> String; static func tool() -> Tool }`

- [ ] **Step 1: Write the failing tests**

```swift
func testNormalizedBoxMapsToScreenPoints() // toScreen((0,0,0.5,0.5), pixels 200×100, origin (100,50), scale 2) == CGRect(x:100, y:75, width:50, height:25)
func testSecondDisplayOffset()              // origin (-1440, 0), scale 1, pixels 1440×900, normalized (0.5,0.5,0.1,0.1) == CGRect(x:-720, y:360, width:144, height:90)
func testRecognizesRenderedKoreanAndEnglish() throws // Core Text renders "Hello 안녕하세요" 48pt black on white 1200×200 → joined text contains "Hello" and "안녕하세요"
func testMinConfidenceDropsLines() throws   // same image, minConfidence 1.01 → []
func testOrderTopToBottomThenLeftToRight()  // lines at (y 10,x 50), (y 12,x 5), (y 40,x 0) → x5, x50, y40 (same row within half the median height)
func testFormatAndCap()                     // "[125,87] \"Hi\" (50×25, confidence 0.93)"; 501 lines → 500 + "1 more lines not shown"; [] → "No text found."
func testRegionValidation() async           // [0,0,0,10] → "region must be [x, y, width, height] with positive width and height"
func testOCRIsOffByDefault() async          // ToolRegistryTests: makeRegistry().toolNames does not contain "OCR"
func testIncludeEnablesOCR() async          // makeRegistry(including: ["OCR"]) contains it, last; excluding: ["OCR"], including: ["OCR"] does not
func testIncludeMergesLikeExclude() throws  // ConfigTests: config ["OCR"] + env "OCR, Shell" → includedTools ["OCR","Shell"]; config round-trips `include = ["OCR"]`
```

- [ ] **Step 2:** Run `--filter OCRToolTests`; expect compile failure.
- [ ] **Step 3:** Implement per spec decision 7. Default region: frontmost app's `mainWindow` frame (`AX.rect`), else the main display. For each display the region intersects: capture, crop to the intersection in pixels (`(point - logicalOrigin) * scale`), recognize, map back with `toScreen`, then merge and `order`. No display intersects → `"region is outside every display."`. No Screen Recording (`CGPreflightScreenCaptureAccess()` false, or every capture nil) → `"Screen capture failed. If this persists, grant Screen Recording in System Settings > Privacy & Security."`. Schema: `region` (4-int array, nullable), `languages` (string array, default `["ko-KR","en-US"]`), `level` (`accurate` | `fast`), `min_confidence` (number, default 0.3). Annotations: readOnly true, idempotent false.
- [ ] **Step 4:** Run the class, then the full suite (existing Snapshot tests must still pass after the `Screenshot` change); expect PASS.
- [ ] **Step 5:** Commit `Add the OCR tool: read text and its click coordinates from a screen region`.
