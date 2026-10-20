# Windows port P3: desktop act, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the agent act on the desktop: SendInput-based Click, Type, Scroll, Move, Shortcut, MultiSelect and MultiEdit, the App tool (launch, launch_executable, switch, resize) and WaitFor, with upstream's contract and output text, label actions that re-find their element at action time, and an InputLedger that never leaves a key or button held.

**Architecture:**
- `DesktopMcp.Core` gains the pure input model (`Core/Input`: key tables, the shortcut parser, upstream's SendKeys pause rules, text-to-keys, pointer paths, coordinate normalisation, argument parsing), fuzzy `WRatio`, `PyJson`, `CommandLine` and the WaitFor conditions. All of it is unit-tested.
- `DesktopMcp.Desktop` gains `Input` (the SendInput injector with the `dwExtraInfo` signature, `InputLedger`, `DesktopInput` with upstream's timings), `WindowControl` (foreground and placement), `Actions` (label re-lookup on the UIA worker, action targets, Type read-back), `Apps`, `Waiting` and the GPU gate. `DesktopMachine` owns the stateful services (one UIA worker, one hang block list) that Snapshot and the act tools share.
- `DesktopMcp.Server` gains nine tools. Input tools run one at a time under `DesktopInputLock` until P4's control gate takes over; P4 also supplies the real `IActionCheckpoint` and the physical-input check of `InputLedger`.

**Tech Stack:** .NET 10 NativeAOT, CsWin32 0.3.347 as a build task with `comInterop.useComSourceGenerators`, CsWinRT, ModelContextProtocol 2.2.0, xUnit 2.9.3. No new package references; the Desktop test project adds the Windows Forms framework reference (Task 4).

**Spec:** `docs/superpowers/specs/2026-10-19-windows-port-design.md`: §3 (boundary rule), §5 (threads, serialisation, cancellation, shared state), §6 (Input, UIA, displays), §7 (what P4 adds on top), §8 (App, WaitFor, Wait/MultiSelect/MultiEdit), §9, §10, §11 (P3 row), §12. The upstream reference is CursorTouch/Windows-MCP at `128f2960c79b74bd2630f4dbc9d4f664f4ce89b3`; paths such as `tools/input.py:256` are relative to `src/windows_mcp/`. Every output string, default, limit and timing comes from there unless spec §10 or the Decisions below say otherwise. The previous phases' Decisions still hold, in particular P2 Decisions 2, 6, 36-39 (`docs/superpowers/plans/2026-10-19-windows-p2-desktop-read.md`).

**Scope (spec §11, P3 row, and `spec/windows/tools.json`):** Input, InputLedger, Click, Type, Scroll, Move, Shortcut, MultiSelect, MultiEdit, App, WaitFor; plus the GPU single-slot gate from the follow-ups (Decisions 9). Not in P3: the control gate, InputMonitor, overlays and ControlStatus (P4), telemetry (P5).

## Open questions for the user (answered 2026-10-20)

**Answers:** 1 — **launched apps die with the server** (the user chose the P1 rule, not the recommendation; see the ruling after the question). 2 — the recommendation (delegated).

1. **Apps launched by App outlive the server?** P1 put the server itself in a kill-on-close Job Object with no breakaway (`ProcessJob.cs`, P1 Decisions 19), so every process the server creates dies when it exits. A Win32 app started by App `launch` (ShellExecuteEx of a shortcut, as spec §8 says) or by `launch_executable` would be killed, with unsaved work, when the MCP client closes the server. Upstream has no job and its launched apps survive. **Recommended: they survive.** The job gains `JOB_OBJECT_LIMIT_BREAKAWAY_OK` (PowerShell children never ask to break away, so they still die with the server); `launch_executable` creates the process with `CREATE_BREAKAWAY_FROM_JOB`; `launch` activates packaged apps with `IApplicationActivationManager` (the system starts them, outside the job) and starts everything else as `explorer.exe "shell:AppsFolder\<id>"` with `CREATE_BREAKAWAY_FROM_JOB`, so the app becomes the shell's child. Side effects: those Win32 launches report no PID, so the wait uses upstream's title fallback (upstream also had no PID for AppsFolder ids), and an elevated server launches them unelevated. The other answer (keep the P1 rule and document that launched apps die with the server) removes the job change and the explorer route from Task 13. Spec §8 and §12 are updated in Task 16 either way.
   **Ruling for the user's answer (binding; overrides Decisions 31 and 34 and Task 13 where they differ):** `ProcessJob` is unchanged (no `BREAKAWAY_OK`, no `ProcessJobTests` change, no Core `NativeMethods.txt` change). `launch_executable` uses `CreateProcess` with `EXTENDED_STARTUPINFO_PRESENT` only (no `CREATE_BREAKAWAY_FROM_JOB`), so the process is in the job and dies with the server. A non-packaged AppsFolder entry is started with `ShellExecuteEx` of `shell:AppsFolder\<parsing name>` with `SEE_MASK_NOCLOSEPROCESS` (spec §8), so a process it creates directly is in the job; when `hProcess` is returned its PID drives the wait, else the title fallback. Packaged apps are still activated with `IApplicationActivationManager`: the system starts them outside our job, so they survive the server; this cannot be avoided and is documented in spec §12, as is that an app that hands off to an already running instance or a broker (e.g. some Office or browser launches) also survives. `AppLauncher.StartFromShell(string parsingName)` returns `int?` (the PID, or null).
2. **Labels after WaitFor.** Upstream's WaitFor calls `get_state` on every poll (`tools/input.py:480-485`), which silently replaces the label map with a tree the model never saw. With Decisions 6 there is no full snapshot per poll, so **recommended: WaitFor (and App switch/resize) leave the label map alone; labels keep resolving against the last Snapshot.** Listed in spec §10.

## Global Constraints

- TFM `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` **10.0.22621.0**; RIDs `win-x64` and `win-arm64` (default from the dotnet process architecture, already in `windows/Directory.Build.props`).
- `TreatWarningsAsErrors`, `IsAotCompatible`, no warning suppression. No new package references in P3 (the Desktop test project's `UseWindowsForms` is a framework reference, Task 4).
- **The publish folder for each RID contains only `desktop-mcp.exe`** (plus `.pdb`), and that exe alone runs; the probe exe has the same rule. `pwsh scripts/build-release.ps1` enforces it and must keep passing: the folder check, the stdio smoke test, the AOT activation check and the AOT UIA check.
- Win32 and COM bindings come from CsWin32 0.3.347 through each project's `NativeMethods.txt`; new Win32 APIs are added there, never declared by hand. `DesktopMcp.Desktop/NativeMethods.json` stays exactly `{"$schema": "https://aka.ms/CsWin32.schema.json", "public": false, "allowMarshaling": true, "comInterop": {"useComSourceGenerators": true}}`; `DesktopMcp.Core` keeps `allowMarshaling: false`. **No built-in COM interop** (`[ComImport]`, `Marshal.GetObjectForIUnknown`, `dynamic` COM).
- Boundary rule (spec §3): BSTR, VARIANT, SAFEARRAY, HRESULT, HWND, `INPUT` and COM pointers never leave `DesktopMcp.Desktop`'s public surface; window handles cross it as `long`, points as `ScreenPoint`. COM and WinRT failures become `DesktopException` (`ComErrors.Translate`). Every BSTR is freed and every `ComVariant` disposed before a UIA wrapper returns.
- **Bounded blocking:** any call that can block on another application runs under a deadline or asynchronously: UIA jobs through `UiaWorker` (label locate 5 s, Type read-back 2 s, WaitFor's focused-element read 5 s and walks with the window walk's 20 s, plus the IUIAutomation2 connection 2 s and transaction 5 s); the foreground sequence (`AttachThreadInput` can block on a hung thread) on its own thread with 2 s; the AppsFolder enumeration 10 s; window restore and resize use `ShowWindowAsync` and `SWP_ASYNCWINDOWPOS`, never a synchronous cross-process `MoveWindow`/`ShowWindow`.
- **UIA protection (P2 Decisions 36-38) applies to every new UIA path:** the walked or targeted top-level window answers `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG | SMTO_BLOCK, 250 ms)` before any UIA call into it and before each children read; children are read by sibling navigation, at most 300 per parent; no `TreeScope_Subtree` or `TreeScope_Descendants` cache request, `FindFirst` or `FindAll`; no TextPattern call on an element whose FrameworkId is `Chrome`. UIA runs only on the `UiaWorker` MTA thread, never on the dispatcher thread.
- **Input:** every injected event goes through `SendInput` (no `keybd_event`, `mouse_event`, `SetCursorPos`, `PostMessage` or `SendMessage` input) and carries `dwExtraInfo = 0x444D4350` ("DMCP", `InputSignature.ExtraInfo`, the value the P0 hook probe used). Mouse coordinates are physical virtual-desktop coordinates (the process is PerMonitorV2), sent as `MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK`. `SendInput`'s return count is checked on every call. Every injected key or button down that is not released in the same `SendInput` batch is recorded in `InputLedger` first and released in a `finally`.
- **Serialisation and checkpoints:** input tools (Click, Type, Scroll, Move, Shortcut, MultiSelect, MultiEdit, App `switch`/`resize`/`launch`/`launch_executable`) hold `DesktopInputLock` for their whole call (Decisions 18). Every input step calls `IActionCheckpoint.Checkpoint()` before and `RecordStep()` after, as upstream calls `checkpoint_current`/`record_step_current`; P3's implementation is a no-op. Every step also honours the call's `CancellationToken`.
- **Untrusted and huge inputs:** UIA trees are walked with explicit stacks; the label re-find walk visits at most 2,000 elements; key sequences, wheel and click counts, drag steps and typed text are processed step by step with a checkpoint and a cancellation check per step, so a huge `wheel_times`, `clicks` or `text` is cancellable at once and allocates nothing proportional up front beyond the tokens of the text.
- **Seams:** every external dependency (SendInput, Win32 window calls, UIA, shell, clipboard, clock, delays, displays, the label store) sits behind an internal interface or delegate, so the always-run tests use fakes. Real-desktop tests carry `[Trait("Category", "Desktop")]`, which `tests/default.runsettings` excludes; run them with `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath=`.
- **Desktop tests never act on the user's windows** (Decisions 11). They inject only through `GuardedInjector` (Task 4), which checks before every `SendInput` call that the foreground window belongs to the test's own window thread and that every absolute mouse event lands on the test's own window, and throws (sending nothing) otherwise. Tests that would need another application's window (App launch of a real app, switch to a user window) are not automated; Task 16 lists them as by-hand checks that need the user.
- Windows App SDK initialisation stays lazy: no P3 path calls `AppRuntime.EnsureInitialized()`; the act tools and WaitFor need no runtime and never load the graphics stack. `scripts/smoke-stdio.ps1` keeps failing if a run without capture loads d2d1, d3d11, dwrite or d3dcompiler_47.
- Naming follows the macOS port: `desktop-mcp`, `~/.desktop-mcp`, `DESKTOP_MCP_*`.
- The tool contract `spec/windows/tools.json` does not change. Output text and behaviour match upstream except where spec §10 or the Decisions below say otherwise. Result shapes (spec §4): tools with an outputSchema (Click, Type, Scroll, Move, MultiSelect, MultiEdit, WaitFor) return `ToolResults.Text`; Shortcut and App have none and return `ToolResults.Content(text, null)`; argument and raised errors are `isError` results `Error calling tool '<Name>': <message>` (from `SpecTool`).
- No telemetry is sent from any P3 path (Decisions 10).
- Commit messages end with a blank line and `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Work happens on branch `windows-p3`.

All `dotnet` and `pwsh` commands run from `windows/`; `git` commands run from the repository root.

## Review Focus

The five conditions the spec implies but no feature test would naturally hit, most likely first. Each has a test in the task named.

1. **A monitor left of or above the primary (negative coordinates).** Moves, drags and normalisation must use Python's floor division and half-even rounding and land on the exact pixel. Tests: Task 2 `MoveStepsFloorTowardsNegativeInfinity`, `NormaliseCoversTheVirtualDesktopWithNegativeOrigin`; Task 4 `CoordinatesLandOnTheTargetPixel` (Desktop).
2. **A call cancelled or failing in the middle of a held key or button** (a shortcut, a drag, MultiSelect's Ctrl, Scroll's Shift). Nothing may stay held. Tests: Task 4 `ShortcutCancelledMidwayReleasesTheHeldModifiers`, `DragReleasesTheButtonWhenCancelled`, `MultiSelectReleasesCtrlWhenATargetFails`; Task 3 `ReleaseAllReleasesEveryHoldAndRethrowsTheFirstError`.
3. **Text with emoji, CRLF, braces or a non-text clipboard.** Type must send surrogate pairs, map `\n` to Enter, skip `\r`, type braces literally, and never lose a clipboard image. Tests: Task 1 `TextKeysSendSurrogatePairsAndNewlines`; Task 4 `TypeDoesNotPasteWhenTheClipboardHoldsOnlyNonText`, `TypeWritesIntoTheTestWindowsEdit` (Desktop, with `héllo 😀`).
4. **A label whose window closed, hung or re-rendered since the Snapshot.** The result must be a specific error or a correct re-find, never a click into a hung app's UIA provider or at a stale point. Tests: Task 7 `NotRespondingWindowMakesNoUiaCall`, `AmbiguousIdentityIsStale`, `WindowGoneIsStale`.
5. **Two input tool calls at once** (stdio clients can pipeline calls; ungated until P4). Their events must not interleave. Test: Task 9 `InputToolsRunOneAtATime`.

---

## Decisions on upstream ambiguities

Each decision is binding for the tasks; the citation names the upstream code (or spec text) it departs from or interprets. Decisions 1-12 are the controller's decisions, delegated by the user (2026-10-20).

1. **Label actions re-find and gate the element** (`desktop/service.py:663-697` returns the stored centre; follow-ups P3, desk-mcp and terminator idea 1). Before acting on a label whose element has an identity (Decisions 40), the UIA worker looks it up again by RuntimeId, first through `ElementFromPointBuildCache` at the stored centre and up to 32 control-view ancestors, then by the bounded walk of Decisions 2. The found element must be enabled and have non-empty bounds that intersect a monitor. The action point is the cached ClickablePoint when it lies inside the bounds and on a monitor, else the centre of the bounds clipped to the element's top-level window and the monitors. Specific errors (texts in Task 8) for stale, disabled, off-screen and not-responding labels. Why: the screen changes between Snapshot and action (scrolling, re-layout, closed dialogs); a stored centre then clicks the wrong thing silently. ClickablePoint instead of the centre is listed in spec §10.
2. **Stale label re-find** (follow-ups, terminator idea 2). When the RuntimeId is not under the stored point or its ancestors, the worker walks the label's top-level window (sibling navigation, 300 children per parent, depth 256, at most 2,000 elements visited, the WM_NULL check before each children read, P2 Decisions 36-38). A RuntimeId match ends the walk at once. Otherwise elements with the same ControlType, AutomationId and Name (at least one of AutomationId and Name non-empty) are counted and the walk stops at the second; the element is used only when exactly one matched and the walk was not cut by a cap. The result text does not change. Why: Chromium, WinUI and list views recreate elements with new RuntimeIds on re-render; uniqueness in one window, checked in full, keeps a wrong match out.
3. **Off-screen targets** (terminator idea 3). When the found element reports IsOffscreen or its bounds miss every monitor and it supports ScrollItemPattern, the worker calls `ScrollIntoView`, refreshes the element and re-checks; it fails with the off-screen error rather than click outside. IsOffscreen alone never fails a label (terminator issue #473: wrong on secondary monitors). `SetFocus` is not called (it can change a list's selection).
4. **Type read-back** (terminator idea 4; spec §10). After Type and each MultiEdit item, the focused element's `ValuePattern` value is read on the UIA worker (2 s) up to three times, 150 ms apart, until it contains the typed text (CRLF and CR read as LF). Skipped when the element is a password field, has no ValuePattern, `press_enter` is true (a submit clears or navigates), or the text is empty. TextPattern is never used (unbounded provider work on large documents, P2 Decisions 36). A match leaves upstream's text unchanged; a mismatch appends one line (Task 10). Why: lost keystrokes, IME and autocomplete damage and a wrongly focused field are otherwise invisible.
5. **Not adopted in P3:** reporting foreground or title changes after an action, selector targeting, tree diffs in results, pattern-based MultiSelect, and target highlighting (P4 overlay). They change result text or the contract; they stay in the follow-ups.
6. **WaitFor without full Snapshots** (`tools/input.py:478-504` calls `get_state` each poll; spec §8 "polls snapshots"). Per poll WaitFor reads only what the condition needs: `active_window` the foreground window's probe (one UIA call); `focused_element` the focused element (one cached read) and its top-level window's name; `element_exists`, `element_enabled` and `text_exists` the window scan and then an early-exit walk of the same windows Snapshot walks (the active window and the non-application windows), with the walker's own classification, budget, caps and P2 Decisions 36-38, stopping at the first matching element. No capture, no virtual-desktop read, no store update, no retry inside a poll (the next poll is the retry). `IsEnabled` and the selection state are read into the element metadata (P2 Decisions 6).
7. **Foreground before keyboard input** (`desktop/service.py:586-661`; terminator idea 6). Before Type and MultiEdit send keys, and in App `switch`, the target top-level window must answer the WM_NULL check (a hung window gets an error, no input); a minimised window is restored with `ShowWindowAsync(SW_RESTORE)`; then `SetForegroundWindow`; if that does not take, the `AttachThreadInput` sequence of `service.py:616-658` runs on a dedicated thread bounded at 2 s, without upstream's BringWindowToTop (a synchronous cross-thread call inside the attached region; the following `SetWindowPos(HWND_TOP, ..., SWP_ASYNCWINDOWPOS)` sets the z-order), and on a timeout or cancellation the caller detaches every recorded attach itself and the abandoned thread makes no further call; then the foreground is polled every 50 ms for 1 s. For keys, success means the foreground window belongs to the target's GUI thread (a dropdown or popup of the same thread counts); App switch keeps upstream's handle comparison. Failure is a clear error and nothing is typed.
8. **SendInput** (follow-ups P3; spec §6). The count `SendInput` returns is checked; a short count with `ERROR_ACCESS_DENIED` while this process is not elevated gives `InputBlockedException` with the elevated text (Task 3); any other short count gives the generic text. Before mouse input the window under the point, and before keyboard input the foreground window, is checked when its integrity level can be read: a higher level than ours fails with the same elevated text before anything is sent (Microsoft documents that a UIPI block is not always visible in the return value). A click sends the absolute move, waits **50 ms**, then sends the button down and up (each with the absolute move flags, so the press lands on the target even if the cursor moved). Why 50 ms: Windows Forms controls update their hover state from `WM_MOUSEMOVE` (`OnMouseEnter`/`OnMouseMove`; a ToolStrip item must be selected before it takes a click), and a press queued in the same batch as the move is handled before that update, so instant clicks get dropped; 50 ms gives the target's message loop a turn and equals upstream's own press-to-release hold (`uia/core.py:289`). The pause is skipped when the cursor is already at the point (the second click of a double click). Every injected event carries `0x444D4350`.
9. **GPU single-slot gate** (follow-ups P4 "One gate for the GPU work (P3/P4)"). `Win2DRenderer` and `GraphicsCaptureService` share one slot; a call waits for a live holder, and fails at once while a holder that passed its deadline is still running, so abandoned renders and captures cannot pile up behind a wedged device lock. Task 15.
10. **Telemetry** stays opt-in (spec §8); no P3 tool sends anything.
11. **Tests never act on the user's windows.** Input tests run against `ActiveTestWindow` (the P2 test window, activated) and `WinFormsTarget`, both owned by the test process, through `GuardedInjector` (see Global Constraints). If the test window cannot be made the foreground window, the test fails before sending input. Desktop tests stay out of the default run.
12. **Release verification** (Task 16): the AOT smoke adds only calls that inject nothing and activate nothing (argument errors, out-of-range labels and points, a shortcut parse error, App lookups of a name no app has, WaitFor timeouts); `build-release.ps1` and the spec and follow-up updates as in P2 Task 19.
13. **All pointer movement through SendInput** (`uia/core.py:198-206, 388-447` and `desktop/service.py:976-986` move with `SetCursorPos`; `uia/core.py:217-248` presses with `mouse_event`/`keybd_event`). Spec §6 says all injection goes through SendInput, and P4's input monitor recognises our events only by the signature, which `SetCursorPos` cannot carry. Each upstream `SetCursorPos(x, y)` becomes one absolute move event; normalisation is upstream's `round((x - left) * 65535 / max(width - 1, 1))` (`uia/core.py:234-236`) with half-even rounding.
14. **Extended-key flag and scan codes** (`uia/core.py:1297-1319, 1572-1581` set `KEYEVENTF_EXTENDEDKEY` on every virtual-key event). The flag is set only for extended keys (`KeyTables.ExtendedKeys`, plus any scan code with the 0x100 bit); the scan code comes from `_SCKeys` (`uia/core.py:1434-1459`), else `MapVirtualKeyW(vk, MAPVK_VK_TO_VSC)` with 0x100 added for `_VKtoSC`'s list (`1473-1481`). Why: an extended `VK_MENU` is Right Alt, which is AltGr on many European layouts and the Han/Eng key on Korean layouts, so upstream's `alt+...` shortcuts can toggle the IME or type AltGr characters; an extended `VK_CONTROL` is Right Ctrl. Right Shift (`VK_RSHIFT`) is not extended: its fixed scan code is `0x036`, not upstream's `0x136`, which never reached Windows because `keybd_event`'s BYTE `bScan` truncated it (`uia/core.py:1711`); the intent (the flag only on real extended keys) wins over the "any scan code with the 0x100 bit" wording.
15. **Points outside the virtual desktop are rejected for every pointer action** (`desktop/service.py:699-720` checks only in `click` and `type`). Move, drag (both ends), Scroll and MultiSelect use the same check and message, because Windows clamps such a point and the input lands elsewhere.
16. **Shortcut parsing validates before sending** (`desktop/service.py:988-1001` builds a SendKeys string that `uia/core.py:1485-1700` parses while sending). The whole sequence is parsed first; an empty key (`ctrl+`) gives `Invalid shortcut 'ctrl+': empty key name` and an unknown multi-character key gives `Invalid shortcut 'ctrl+foo': unknown key 'foo'` (upstream sent Ctrl down, then crashed in `ord()` with Ctrl held). Single characters `(`, `)`, `{`, `}` are literal characters (upstream treats them as SendKeys syntax). Otherwise the events and their order are upstream's, including its hold rules (Task 1).
17. **Click counts** (`tools/input.py:255-256`; `desktop/service.py:730-755`; spec §10). `clicks` below 0 is an error `clicks must be 0 or more` (upstream clicked once and reported `None`); 3 or more reports `Clicked <button> <n> times at (x,y).`; 0, 1 and 2 keep `Hover`/`Single`/`Double ... clicked at (x,y).`.
18. **One input lock until P4** (spec §5: the desktop lock belongs to the control gate). P3 adds `DesktopInputLock` (`SemaphoreSlim(1)`, waited with the call's token) around every input tool, so two calls cannot interleave their events. P4 replaces it with the gate's lock.
19. **InputLedger** (`desktop/control_ledger.py:15-82`) is ported in P3 with upstream's semantics and texts. It is released on cancellation and errors (each holder's `finally`) and on shutdown (`ApplicationStopping`). The physical-key predicate returns false until P4's input monitor supplies it. Unlike upstream, every key down that a shortcut holds across a pause also goes through the ledger (upstream's SendKeys never did), so a cancelled shortcut cannot leave Ctrl down.
20. **Checkpoints** (`desktop/control.py:295-322`): `IActionCheckpoint` with a no-op implementation; P4 implements it.
21. **Paste path and a non-text clipboard** (`desktop/service.py:816-842` never restores when the prior clipboard had no text, which loses an image or files on the clipboard). When the clipboard holds data but no Unicode text (`CountClipboardFormats() > 0 && !IsClipboardFormatAvailable(CF_UNICODETEXT)`, no `OpenClipboard` needed), Type uses the per-key path instead of pasting. The paste threshold counts code points (`len(text) >= 20`).
22. **Reported coordinates** (`tools/input.py:253-256, 297`): label actions report the point actually used (Decisions 1), not the stored centre.
23. **MultiSelect and MultiEdit with labels** (`tools/multi.py:43-50, 81-100` resolve all labels first). All labels are range-checked first with upstream's wrapper texts; each label is then located just before its own action. A located failure mid-way gives the label error plus ` Stopped after <k> of <n> items.` when k > 0.
24. **No store replacement outside Snapshot** (Open question 2; `tools/input.py:480`, `desktop/service.py:424-425`): WaitFor and App switch/resize read the window list through the window catalog and never replace `DesktopStateStore`. App resize without a name uses the current foreground window (`ScanActiveAsync`), not the stale stored one (`service.py:453`).
25. **WaitFor metadata matching** (`tools/input.py:86-87, 114-115` match `str(value)` of every metadata entry, so `text="true"` matches any node through `has_focused=True`). Only text-valued metadata is matched: value, minimum, maximum, toggle state, expand/collapse state, shortcut. Case-insensitive matching uses `ToLowerInvariant` for Python's `casefold`.
26. **WaitFor `use_dom=True`** gives the Snapshot error `SnapshotService.UseDomUnavailable` after the other validation (P2 Decisions 39).
27. **WaitFor `element_enabled`** (spec §10 "checks the enabled state"). The walker already drops disabled interactive elements (`tree/service.py:546-548`), so the check adds `Metadata.IsEnabled` for scrollable elements, which the walker keeps when disabled.
28. **WaitFor match order and transient failures.** The early-exit walk reports the first matching element in visit order (upstream: the first in its interactive list, then its scrollable list). A poll that fails with a UIA timeout, a dead element or a window that stops answering counts as "not matched" with the previous detail; other exceptions end the call. The desktop-session check (P2 Decisions 18) runs once before the first poll.
29. **App window choice with duplicate names** (`desktop/service.py:437` builds a dict, so the last window with a name wins): the first window wins (the active window, then z-order).
30. **Fuzzy matching for App** (`desktop/service.py:438, 533` use fuzzywuzzy 0.18 `process.extractOne` with `WRatio`, cutoff 70). `Fuzzy.WRatio` ports fuzzywuzzy's `WRatio` (Task 12) with two differences: non-ASCII characters are kept (fuzzywuzzy's `force_ascii=True` strips them, so a Korean or Japanese app or window name can never match), and `partial_ratio` is P1's optimal-alignment `Fuzzy.PartialRatio` (rapidfuzz), not fuzzywuzzy's matching-block heuristic, which can only score lower. Ties keep the first choice, as `extractOne`.
31. **App launch mechanism** (Open question 1; spec §8; `desktop/service.py:330-557` uses PowerShell `Get-StartApps` and `Start-Process`). One AppsFolder enumeration (`SHCreateItemFromParsingName("shell:AppsFolder")`, `BHID_EnumItems`; display name and parsing name per item, on an STA thread, bounded 10 s) replaces `Get-StartApps` plus the AppsFolder names; keys are lower-cased display names, first one wins. A parsing name of the form `<family>!<app>` is activated with `IApplicationActivationManager.ActivateApplication` (PID known); anything else is started with `ShellExecuteEx` of `shell:AppsFolder\<parsing name>` and `SEE_MASK_NOCLOSEPROCESS` (PID when a process handle is returned; in the server's kill-on-close job; Open question 1's ruling). The wait polls every 0.5 s (`uia/core.py:31`): up to 10 s for a visible top-level window of the PID, then up to 10 s for a visible top-level window whose Win32 title contains the name case-insensitively (`service.py:491-507`).
32. **App resize** (`desktop/service.py:444-476` calls `MoveWindow` synchronously). The window must answer the WM_NULL check (else `<name> is not responding`), then `SetWindowPos(..., SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS)`; a failed call gives `Failed to resize <name>: <message>` (upstream reported success when `MoveWindow` failed). Texts are normal results, as upstream's resize errors.
33. **App switch** keeps `switch_app`'s texts and its 1 s handle verification (`service.py:559-584`), on top of Decisions 7's bounded sequence; an exception gives `Error switching app: <message>`, and every switch failure is an `isError` result, as upstream raises.
34. **launch_executable** (`tools/app.py:129-171`). `~` expands to the user profile, the path is made absolute and, when it exists, resolved to its final path (Python's `Path.resolve()`); messages `Executable does not exist: <path>` and `Working directory does not exist: <path>`. The command line is `subprocess.list2cmdline` (`CommandLine.Join`); `CreateProcess` gets `EXTENDED_STARTUPINFO_PRESENT` (no breakaway: the process dies with the server, Open question 1's ruling), std handles on `NUL` and a `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` holding only that handle (Python's `close_fds=True`). The result is `json.dumps(..., indent=2)` (`PyJson.Dumps`: key order pid, executable, args, cwd; `ensure_ascii`).
35. **Missing names** (`desktop/service.py:531, 559` would crash on `None`): `name is required for mode="launch"` and `name is required for mode="switch"` (raised, `isError`).
36. **Move and drag arithmetic** (`desktop/service.py:958-985`): move steps use Python floor division (towards negative infinity), drag steps Python `round` (half to even); a C# `/` would truncate towards zero and miss pixels on monitors with negative coordinates.
37. **Scroll** keeps upstream's horizontal scrolling by Shift plus the vertical wheel (`service.py:870-901`) and returns its invalid type/direction texts as normal results; without a point it reports `Scrolled <type> <direction> by <n> wheel times at current cursor position (x,y).` (spec §10), the cursor read before the first wheel event.
38. **Argument shapes** (pydantic plus `_as_loc`/`_as_point`, `tools/input.py:40-75`; follow-up "`Coercion.JsonList` returns non-arrays"). Points, point lists, label lists, MultiEdit items and App's `window_loc`/`window_size` are parsed by `ActArguments` (Task 2), which checks the JSON kind after `Coercion.JsonList` and uses `Coercion.Integer` for items. Enum parameters (`button`, `type`, `direction`, `caret_position`, `mode`) outside their contract values give `<name> must be one of: <values>`. MultiEdit `text` must be a string. Invalid items are quoted as their JSON text.
39. **Element texts in errors** use the tree's rendering: lower-cased control type and the name escaped by `UnicodeCleanup.EscapeControl` (P2 Decisions 35).
40. **Element identity** (Task 6). An interactive or scrollable element gets an `ElementIdentity` (window handle, RuntimeId, control type id, AutomationId, trimmed Name, cached ClickablePoint) when it has a RuntimeId. Word elements (`tree/service.py:320-348`) and elements without a RuntimeId have none: a label for them acts at the stored centre after the point check (upstream's behaviour).
41. **WaitFor and App switch scans skip blocked and excluded windows** exactly as Snapshot (P2 Decisions 2, `DESKTOP_MCP_EXCLUDE_PROCESSES`).

## Follow-ups this plan picks up

From `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`:

| Follow-up | Where |
|---|---|
| P3: re-find the label by RuntimeId; stale-label error; disabled (an error here, not a note: Decisions 1) | Tasks 7-8; Decisions 1-2 |
| P3: check SendInput's count; "input blocked: the target window runs elevated" | Task 3; Decisions 8 |
| P3: pause between move and press; WinForms target in the desktop tests | Tasks 4 (pause, WinForms test); Decisions 8 |
| P3: `Coercion.JsonList` returns non-arrays (`loc` callers) | Task 2; Decisions 38 |
| P3: IsEnabled and selection state in UiaElementData and UiElement (P2 Decisions 6) | Task 6 |
| P3 terminator ideas 1-6 | Tasks 4-8, 10, 14; Decisions 1-4, 6-7 |
| P3 terminator ideas 7-8 and later options | Not adopted: Decisions 5 (kept in the follow-ups) |
| P4 "One gate for the GPU work (P3/P4)" | Task 15; Decisions 9 |
| P4 "Process allowlist (new config key)" | Stays in P4 (it belongs with the control policy) |

## File map

```
windows/
  scripts/smoke-stdio.ps1                                   (act-tool safe calls; Task 16)
  src/DesktopMcp.Core/Input/KeyTables.cs                     (Task 1)
  src/DesktopMcp.Core/Input/KeyTokens.cs                     (KeyToken, KeyInput, KeyStep; Task 1)
  src/DesktopMcp.Core/Input/ShortcutParser.cs                (Task 1)
  src/DesktopMcp.Core/Input/SendKeysPlan.cs                  (Task 1)
  src/DesktopMcp.Core/Input/TextKeys.cs                      (Task 1)
  src/DesktopMcp.Core/Input/PointerPath.cs                   (MouseButton, PointerPath, MouseCoordinates; Task 2)
  src/DesktopMcp.Core/Input/ActArguments.cs                  (Task 2)
  src/DesktopMcp.Core/Snapshots/SnapshotModel.cs             (ElementIdentity, UiMetadata.IsEnabled/IsSelected; Task 6)
  src/DesktopMcp.Core/Snapshots/WaitConditions.cs            (Task 14)
  src/DesktopMcp.Core/Text/Fuzzy.cs                          (WRatio, ExtractOne; Task 12)
  src/DesktopMcp.Core/Text/PyJson.cs                         (Task 13)
  src/DesktopMcp.Core/Text/CommandLine.cs                    (Task 13)
  src/DesktopMcp.Desktop/NativeMethods.txt                   (Tasks 3, 4, 5, 7, 13)
  src/DesktopMcp.Desktop/DesktopMachine.cs                   (Task 8)
  src/DesktopMcp.Desktop/Input/IInputInjector.cs             (InjectedInput, MouseFlags, InputSignature; Task 3)
  src/DesktopMcp.Desktop/Input/SendInputInjector.cs          (InputBlockedException, ProcessIntegrity; Task 3)
  src/DesktopMcp.Desktop/Input/InputLedger.cs                (InputUnavailableException; Task 3)
  src/DesktopMcp.Desktop/Input/DesktopInput.cs               (IDesktopInput, DragResult, CaretPosition, InputTiming, IActionCheckpoint; Task 4)
  src/DesktopMcp.Desktop/ClipboardAccess/ClipboardFormats.cs (Task 4)
  src/DesktopMcp.Desktop/WindowControl/ForegroundService.cs  (IForeground, ForegroundResult; Task 5)
  src/DesktopMcp.Desktop/WindowControl/WindowPlacement.cs    (Task 5)
  src/DesktopMcp.Desktop/Tree/WindowSystem.cs                (thread id, bounds, title, WindowFromPoint; Task 5)
  src/DesktopMcp.Desktop/Tree/TreeSource.cs                  (ClickablePoint, IsSelected; Task 6)
  src/DesktopMcp.Desktop/Tree/TreeWalker.cs                  (identity, Describe, stopWhen; Tasks 6, 14)
  src/DesktopMcp.Desktop/Tree/TreeService.cs                 (window handle; FindAsync; Tasks 6, 14)
  src/DesktopMcp.Desktop/Tree/WindowCatalog.cs               (ScanActiveAsync; Task 12)
  src/DesktopMcp.Desktop/Uia/UiaSession.cs                   (ActionRequest; Task 7)
  src/DesktopMcp.Desktop/Uia/UiaTreeSource.cs                (reads ClickablePoint, IsSelected; Task 6)
  src/DesktopMcp.Desktop/Uia/UiaValues.cs                    (CachedPoint; Task 6)
  src/DesktopMcp.Desktop/Actions/LabelLocator.cs             (IElementSource, LabelLocator, UiaElementSource, UiaLabelLocator; Task 7)
  src/DesktopMcp.Desktop/Actions/ActionTargets.cs            (IActionTargets, ActionTarget, LabelErrors; Task 8)
  src/DesktopMcp.Desktop/Actions/TypeReadBack.cs             (ITypeReadBack; Task 10)
  src/DesktopMcp.Desktop/Apps/AppService.cs                  (IAppService, AppResult; Tasks 12-13)
  src/DesktopMcp.Desktop/Apps/AppCatalog.cs                  (Task 13)
  src/DesktopMcp.Desktop/Apps/AppLauncher.cs                 (Task 13)
  src/DesktopMcp.Desktop/Waiting/WaitConditionService.cs     (IWaitConditions; Task 14)
  src/DesktopMcp.Desktop/Imaging/GpuGate.cs                  (Task 15; used by Win2DRenderer.cs and Capture/GraphicsCaptureService.cs)
  src/DesktopMcp.Desktop/Snapshots/SnapshotService.cs        (DesktopMachine ctor, TreeTargets; Tasks 8, 14)
  src/DesktopMcp.Server/Tools/{ClickTool,MoveTool,ScrollTool}.cs          (Task 9)
  src/DesktopMcp.Server/Tools/DesktopInputLock.cs            (Task 9)
  src/DesktopMcp.Server/Tools/{TypeTool,ShortcutTool}.cs     (Task 10)
  src/DesktopMcp.Server/Tools/{MultiSelectTool,MultiEditTool}.cs          (Task 11)
  src/DesktopMcp.Server/Tools/AppTool.cs                     (Task 12)
  src/DesktopMcp.Server/Tools/WaitForTool.cs                 (Task 14)
  src/DesktopMcp.Server/Tools/{ToolServices,ToolRegistry}.cs (Tasks 9-14)
  src/DesktopMcp.Server/Hosting/StdioServer.cs               (ledger release on stopping; Task 9)
  tests/DesktopMcp.Core.Tests/Input/{ShortcutParserTests,SendKeysPlanTests,TextKeysTests,PointerPathTests,ActArgumentsTests}.cs
  tests/DesktopMcp.Core.Tests/Text/{FuzzyWRatioTests,PyJsonTests,CommandLineTests}.cs
  tests/DesktopMcp.Core.Tests/Snapshots/WaitConditionsTests.cs
  tests/DesktopMcp.Desktop.Tests/DesktopMcp.Desktop.Tests.csproj (UseWindowsForms; Task 4)
  tests/DesktopMcp.Desktop.Tests/{TestWindow,ActiveTestWindow,GuardedInjector,WinFormsTarget}.cs
  tests/DesktopMcp.Desktop.Tests/Input/{SendInputInjectorTests,InputLedgerTests,DesktopInputTests,InputDesktopTests}.cs
  tests/DesktopMcp.Desktop.Tests/WindowControl/ForegroundServiceTests.cs
  tests/DesktopMcp.Desktop.Tests/Actions/{LabelLocatorTests,ActionTargetsTests,TypeReadBackTests}.cs
  tests/DesktopMcp.Desktop.Tests/Apps/{AppServiceTests,AppLauncherTests}.cs
  tests/DesktopMcp.Desktop.Tests/Waiting/WaitConditionServiceTests.cs
  tests/DesktopMcp.Desktop.Tests/Imaging/GpuGateTests.cs
  tests/DesktopMcp.Desktop.Tests/Tree/{TreeWalkerTests,TreeServiceTests}.cs (additions)
  tests/DesktopMcp.Server.Tests/Tools/{ClickToolTests,MoveToolTests,ScrollToolTests,TypeToolTests,ShortcutToolTests,MultiToolTests,AppToolTests,WaitForToolTests}.cs
  tests/DesktopMcp.Server.Tests/Tools/FakeDesktop.cs         (shared fakes for the act tools; Task 9)
docs/superpowers/specs/2026-10-19-windows-port-design.md     (Task 16)
docs/superpowers/plans/2026-10-19-windows-p0-followups.md    (Task 16)
```

## Tasks

1. Core input model: key tables, shortcut parser, SendKeys pauses, text keys
2. Core pointer math and act-tool arguments
3. SendInput injector, input signature and InputLedger
4. Desktop input primitives with upstream's timings, and the guarded test targets
5. Foreground and window placement
6. Element identity and metadata in the snapshot
7. UIA label locator: re-lookup, re-find, gate, scroll into view
8. Action targets and the shared desktop machine
9. Click, Move and Scroll tools
10. Type and Shortcut tools with read-back
11. MultiSelect and MultiEdit tools
12. Fuzzy WRatio, App switch and resize
13. App launch and launch_executable
14. WaitFor
15. GPU single-slot gate
16. Release verification and documentation

---

### Task 1: Core input model: key tables, shortcut parser, SendKeys pauses, text keys

Upstream's key handling is a SendKeys string parser that sends while it parses (`uia/core.py:1485-1744`); Shortcut builds that string (`desktop/service.py:988-1001`) and Type escapes one character at a time (`desktop/service.py:62-78, 807-810`). This task ports the parts P3 needs as pure data: tokens, then wire-level steps with upstream's pauses. Scan codes and the extended flag are computed in Task 3 from these tables (Decisions 14).

**Files:**
- Create: `windows/src/DesktopMcp.Core/Input/KeyTables.cs`, `KeyTokens.cs`, `ShortcutParser.cs`, `SendKeysPlan.cs`, `TextKeys.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Input/ShortcutParserTests.cs`, `SendKeysPlanTests.cs`, `TextKeysTests.cs`

**Interfaces:**
- Consumes: `PyText.Repr` (P1).
- Produces (namespace `DesktopMcp.Core.Input`):
  - `public readonly record struct KeyToken(ushort VirtualKey, bool Up, string? Character = null)`: a virtual-key down or up, or (when `Character` is set) one character typed as Unicode (one or two UTF-16 units).
  - `public readonly record struct KeyInput(ushort VirtualKey, char Unicode, bool Up)`: one wire event; `VirtualKey == 0` means a Unicode unit.
  - `public sealed record KeyStep(IReadOnlyList<KeyInput> Inputs, TimeSpan PauseAfter)`.
  - `public static class KeyTables` with `IReadOnlyDictionary<string, ushort> SpecialKeyNames` (upper-case keys, `uia/enums.py:1399-1548`), `IReadOnlyDictionary<char, ushort> CharacterCodes` (`uia/enums.py:1551-1637`), `IReadOnlyDictionary<string, string> Aliases` (`desktop/service.py:47-54`, lower-case keys), `IReadOnlySet<string> HoldKeys` (`uia/core.py:1516-1532`), `IReadOnlyDictionary<ushort, ushort> FixedScanCodes` (`uia/core.py:1434-1459`), `IReadOnlySet<ushort> ScanExtendedKeys` (`VK_APPS, VK_CANCEL, VK_SNAPSHOT, VK_DIVIDE, VK_NUMLOCK`, `uia/core.py:1473-1481`), `IReadOnlySet<ushort> ExtendedKeys`, and constants `VkShift = 0x10`, `VkControl = 0x11`, `VkMenu = 0x12`, `VkReturn = 0x0D`, `VkTab = 0x09`, `VkBack = 0x08`, `VkHome = 0x24`, `VkEnd = 0x23`, `VkA = 0x41`, `VkV = 0x56`.
  - `public static class ShortcutParser { public static IReadOnlyList<KeyToken> Parse(string shortcut); }`
  - `public static class SendKeysPlan { public static IReadOnlyList<KeyStep> Build(IReadOnlyList<KeyToken> tokens, TimeSpan interval, TimeSpan waitTime); public static readonly TimeSpan HotkeyInterval; }` (10 ms)
  - `public static class TextKeys { public static IEnumerable<IReadOnlyList<KeyToken>> PerCharacter(string text); }`

- [ ] **Step 1: Write the failing tests**

```csharp
using DesktopMcp.Core.Input;

namespace DesktopMcp.Core.Tests.Input;

public class ShortcutParserTests
{
    private static KeyToken D(ushort vk) => new(vk, false);
    private static KeyToken U(ushort vk) => new(vk, true);
    private static KeyToken C(string ch) => new(0, false, ch);

    [Fact]
    public void CtrlCHoldsCtrlAroundTheLetter() =>
        Assert.Equal([D(0x11), D(0x43), U(0x43), U(0x11)], ShortcutParser.Parse("ctrl+c"));

    [Fact]
    public void HoldKeysNest() =>
        Assert.Equal([D(0x11), D(0x10), D(0x1B), U(0x1B), U(0x10), U(0x11)], ShortcutParser.Parse("ctrl+shift+esc"));

    [Fact]
    public void ANonHoldKeyEndsTheHold() =>
        Assert.Equal([D(0x11), D(0x41), U(0x41), U(0x11), C("b")], ShortcutParser.Parse("ctrl+a+b"));

    [Fact]
    public void AliasesAndCaseResolve()
    {
        Assert.Equal([D(0x5B), D(0x52), U(0x52), U(0x5B)], ShortcutParser.Parse("Windows+R"));
        Assert.Equal([D(0x08), U(0x08)], ShortcutParser.Parse(" backspace "));
        Assert.Equal([D(0x12), D(0x73), U(0x73), U(0x12)], ShortcutParser.Parse("alt+F4"));
    }

    // uia/core.py:1687-1689: a character without a virtual key is appended after the release, not inside the hold.
    [Fact]
    public void ACharacterWithoutAVirtualKeyIsTypedAfterTheHold() =>
        Assert.Equal([D(0x11), U(0x11), C("!")], ShortcutParser.Parse("ctrl+!"));

    [Fact]
    public void BracesAndParenthesesAreLiteral() =>
        Assert.Equal([C("(")], ShortcutParser.Parse("("));

    [Theory]
    [InlineData("ctrl+", "Invalid shortcut 'ctrl+': empty key name")]
    [InlineData("", "Invalid shortcut '': empty key name")]
    [InlineData("ctrl+foo", "Invalid shortcut 'ctrl+foo': unknown key 'foo'")]
    public void BadShortcutsFailBeforeAnythingIsSent(string shortcut, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => ShortcutParser.Parse(shortcut)).Message);
}

public class SendKeysPlanTests
{
    private static readonly TimeSpan Ms10 = TimeSpan.FromMilliseconds(10);

    // uia/core.py:1702-1744: hotkey pause after a down and between ups; interval after the last event; waitTime at the end.
    [Fact]
    public void ShortcutPausesFollowUpstream()
    {
        var steps = SendKeysPlan.Build(ShortcutParser.Parse("ctrl+c"), Ms10, TimeSpan.FromMilliseconds(500));
        Assert.Equal(4, steps.Count);
        Assert.Equal([new KeyInput(0x11, '\0', false)], steps[0].Inputs);
        Assert.Equal([Ms10, Ms10, Ms10, TimeSpan.FromMilliseconds(510)], steps.Select(s => s.PauseAfter));
    }

    [Fact]
    public void AnAstralCharacterIsTwoUnicodeUnitsInOneStep()
    {
        var step = Assert.Single(SendKeysPlan.Build([new KeyToken(0, false, "😀")], TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(50)));
        Assert.Equal([new KeyInput(0, '\uD83D', false), new KeyInput(0, '\uD83D', true), new KeyInput(0, '\uDE00', false), new KeyInput(0, '\uDE00', true)], step.Inputs);
        Assert.Equal(TimeSpan.FromMilliseconds(90), step.PauseAfter);
    }

    [Fact]
    public void NoTokensGiveNoSteps() => Assert.Empty(SendKeysPlan.Build([], Ms10, TimeSpan.FromMilliseconds(500)));
}

public class TextKeysTests
{
    // desktop/service.py:62-78: \n is Enter, \t is Tab, \r is skipped, braces are characters; one entry per code point.
    [Fact]
    public void TextKeysSendSurrogatePairsAndNewlines()
    {
        var keys = TextKeys.PerCharacter("a\r\n{😀\t").ToArray();
        Assert.Equal(6, keys.Length);
        Assert.Equal([new KeyToken(0, false, "a")], keys[0]);
        Assert.Empty(keys[1]);
        Assert.Equal([new KeyToken(0x0D, false), new KeyToken(0x0D, true)], keys[2]);
        Assert.Equal([new KeyToken(0, false, "{")], keys[3]);
        Assert.Equal([new KeyToken(0, false, "😀")], keys[4]);
        Assert.Equal([new KeyToken(0x09, false), new KeyToken(0x09, true)], keys[5]);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~DesktopMcp.Core.Tests.Input"`
Expected: FAIL to compile (`ShortcutParser` does not exist).

- [ ] **Step 3: Implement the five files**

`KeyTables`: port the two upstream tables literally (all 160-odd special names, including `HANGUL`, `HANJA`, `LBUTTON` and the `OEM_*` names). `ExtendedKeys` = `VK_RMENU 0xA5, VK_RCONTROL 0xA3, VK_INSERT 0x2D, VK_DELETE 0x2E, VK_HOME 0x24, VK_END 0x23, VK_PRIOR 0x21, VK_NEXT 0x22, VK_LEFT..VK_DOWN 0x25-0x28, VK_NUMLOCK 0x90, VK_SNAPSHOT 0x2C, VK_DIVIDE 0x6F, VK_LWIN 0x5B, VK_RWIN 0x5C, VK_APPS 0x5D, VK_CANCEL 0x03` and the browser, volume, media and launch keys `0xA6-0xB7`.

`ShortcutParser.Parse` (the algorithm the tests pin; `desktop/service.py:991-999` then `uia/core.py:1541-1641`): split on `+`, trim each part; an empty part throws the empty-key message. A part of length 1 is a character; a longer part is lower-cased, mapped through `Aliases`, upper-cased and looked up in `SpecialKeyNames` (missing: the unknown-key message quoting the trimmed part with `PyText.Repr`). Then keep a token list, `hold = false`, `insertIndex`, `lastKey`:
- special key K, not holding: append K down and K up; if K's name is in `HoldKeys`, `hold = true` and `insertIndex = count - 1` (the position of K up).
- special key K, holding: if K equals `lastKey`, `insertIndex += 1`; insert K down and K up at `insertIndex`; if K is a hold key `insertIndex += 1`, else `hold = false`.
- character c, holding, `c` in `CharacterCodes`: (same `lastKey` rule) insert its VK down and up at `insertIndex`, `hold = false`.
- character c otherwise: append `KeyToken(0, false, c)`; when holding, `hold = false`.

`SendKeysPlan.Build`: one step per token. A character token becomes down and up per UTF-16 unit in one step with `interval` after it. A VK token pauses `interval` when it is the last token, or when it is an up followed by a character or by a down; otherwise `HotkeyInterval`. The last step's pause also includes `waitTime`.

`TextKeys.PerCharacter`: iterate code points (`Rune`), yielding `[]` for `\r`, Enter down/up for `\n`, Tab down/up for `\t`, else one character token.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~DesktopMcp.Core.Tests.Input"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Input windows/tests/DesktopMcp.Core.Tests/Input
git commit -m "Add the core key tables, shortcut parser and SendKeys pauses" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Core pointer math and act-tool arguments

Moves and drags compute their intermediate points like upstream (`desktop/service.py:930-986`, Decisions 36); absolute coordinates are normalised like `uia/core.py:234-236` (Decisions 13). `ActArguments` holds every argument shape and message of the act tools (Decisions 38), which also closes the `Coercion.JsonList` follow-up.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Input/PointerPath.cs`, `windows/src/DesktopMcp.Core/Input/ActArguments.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Input/PointerPathTests.cs`, `ActArgumentsTests.cs`

**Interfaces:**
- Consumes: `ScreenPoint`, `ScreenRect` (P2), `Coercion` (P1), `PyText.RoundToInteger` (P2).
- Produces:
  - `public enum MouseButton { Left, Right, Middle }`
  - `public static class PointerPath { IReadOnlyList<ScreenPoint> MoveSteps(ScreenPoint from, ScreenPoint to); (IReadOnlyList<ScreenPoint> Points, TimeSpan Interval) DragSteps(ScreenPoint from, ScreenPoint to, double? duration); }`
  - `public static class MouseCoordinates { (int X, int Y) Normalize(ScreenPoint point, ScreenRect virtualScreen); string? OutsideMessage(ScreenPoint point, ScreenRect virtualScreen); }`
  - `public static class ActArguments` with: `ScreenPoint? Point(JsonElement? value, string name, string shapeMessage)`, `IReadOnlyList<ScreenPoint> PointList(JsonElement value, string name)`, `IReadOnlyList<long> LabelList(JsonElement value, string name)`, `IReadOnlyList<(ScreenPoint Point, string Text)> EditLocs(JsonElement value)`, `IReadOnlyList<(long Label, string Text)> EditLabels(JsonElement value)`, `double? Duration(JsonElement? value)`, `(int A, int B)? Pair(JsonElement? value, string name)`, `double FiniteNumber(JsonElement value, string name)`, `string Choice(JsonElement? value, string name, string defaultValue, params string[] allowed)`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class PointerPathTests
{
    // desktop/service.py:981-984: steps = max(1, min(80, max(|dx|, |dy|) // 20)), cx + (x - cx) * step // steps (floor).
    [Fact]
    public void MoveStepsFloorTowardsNegativeInfinity() =>
        Assert.Equal([new ScreenPoint(-21, 0), new ScreenPoint(-41, 0)], PointerPath.MoveSteps(new(0, 0), new(-41, 0)));

    [Fact]
    public void MoveStepsAreCappedAt80() => Assert.Equal(80, PointerPath.MoveSteps(new(0, 0), new(5000, 10)).Count);

    // desktop/service.py:958-971: duration steps = max(1, min(200, ceil(d / 0.01))), interval d / steps, round half to even.
    [Fact]
    public void DragWithADurationUsesTenMillisecondSteps()
    {
        var (points, interval) = PointerPath.DragSteps(new(0, 0), new(5, 0), 0.04);
        Assert.Equal([new ScreenPoint(1, 0), new ScreenPoint(2, 0), new ScreenPoint(4, 0), new ScreenPoint(5, 0)], points); // 1.25, 2.5, 3.75, 5
        Assert.Equal(TimeSpan.FromMilliseconds(10), interval);
    }

    [Fact]
    public void DragWithoutADurationUsesTheMoveStepCountAndTenMilliseconds()
    {
        var (points, interval) = PointerPath.DragSteps(new(0, 0), new(100, 0), null);
        Assert.Equal(5, points.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(10), interval);
    }

    [Fact]
    public void NormaliseCoversTheVirtualDesktopWithNegativeOrigin()
    {
        var screen = new ScreenRect(-1920, 0, 2560, 1600); // width 4480
        Assert.Equal((0, 0), MouseCoordinates.Normalize(new(-1920, 0), screen));
        Assert.Equal((65535, 65535), MouseCoordinates.Normalize(new(2559, 1599), screen));
        Assert.Equal((28093, 0), MouseCoordinates.Normalize(new(0, 0), screen)); // round(1920 * 65535 / 4479)
    }

    [Fact]
    public void OutsideMessageIsUpstreams() =>
        Assert.Equal("Coordinates (3000,5) are outside the desktop bounds x=-1920..2559, y=0..1599",
            MouseCoordinates.OutsideMessage(new(3000, 5), new ScreenRect(-1920, 0, 2560, 1600)));
}

public class ActArgumentsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private const string Shape = "Location must be a list of exactly 2 integers [x, y]";

    [Theory]
    [InlineData("[10, 20]")]
    [InlineData("\"[10, 20]\"")]
    [InlineData("[\"10\", 20.0]")]
    public void PointsAcceptListsJsonStringsAndIntegralValues(string json) =>
        Assert.Equal(new ScreenPoint(10, 20), ActArguments.Point(J(json), "loc", Shape));

    [Theory]
    [InlineData("[1]")]
    [InlineData("\"5\"")]          // JsonList used to return the number 5 (the follow-up)
    [InlineData("[1, 2, 3]")]
    [InlineData("[true, 2]")]
    [InlineData("[1.5, 2]")]
    public void BadPointsGiveTheCallersShapeMessage(string json) =>
        Assert.Equal(Shape, Assert.Throws<ArgumentException>(() => ActArguments.Point(J(json), "loc", Shape)).Message);

    [Fact]
    public void EditItemsMustBeTriples() =>
        Assert.Equal("Each loc item must be [x, y, text]. Invalid: [1,2]",
            Assert.Throws<ArgumentException>(() => ActArguments.EditLocs(J("[[1,2]]"))).Message);

    [Fact]
    public void EditLabelsUseUpstreamsTexts()
    {
        Assert.Equal("Each label item must be [label, text]. Invalid: [1]", Assert.Throws<ArgumentException>(() => ActArguments.EditLabels(J("[[1]]"))).Message);
        Assert.Equal("Invalid label id in item: [\"x\",\"a\"]", Assert.Throws<ArgumentException>(() => ActArguments.EditLabels(J("[[\"x\",\"a\"]]"))).Message);
    }

    [Theory]
    [InlineData("\"abc\"", "duration must be a finite number of seconds")]
    [InlineData("true", "duration must be a finite number of seconds")]
    [InlineData("11", "duration must be between 0 and 10 seconds")]
    public void DurationFollowsUpstream(string json, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => ActArguments.Duration(J(json))).Message);

    [Fact]
    public void ChoicesNameTheirValues() =>
        Assert.Equal("button must be one of: left, right, middle",
            Assert.Throws<ArgumentException>(() => ActArguments.Choice(J("\"top\""), "button", "left", "left", "right", "middle")).Message);
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~DesktopMcp.Core.Tests.Input"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `PointerPath.cs` (with `MouseButton` and `MouseCoordinates`) and `ActArguments.cs`**

Floor division by `Math.Floor((double)a / b)` on longs, or an integer floor helper; drag points with `PyText.RoundToInteger`. `Point` calls `Coercion.JsonList`, requires an array of exactly two items each accepted by `Coercion.Integer` (a JSON boolean is rejected), and throws `shapeMessage` for any failure, including a JSON parse failure of a string (P2 Decisions 16 keeps .NET's parser message only for regions). `PointList`: each item through `Point` with `Each loc must be a list of exactly 2 integers [x, y]`; `LabelList`: `labels must be a list of integers`. `EditLocs`/`EditLabels` quote invalid items with `JsonElement.GetRawText()` (minified as written by the client). `Pair`: `<name> must be a list of exactly 2 integers`. `FiniteNumber`: a JSON number or a numeric string (`double.TryParse` invariant, which also reads `NaN` and `Infinity`), not a boolean, finite, else `<name> must be a finite number`. `Duration`: null when absent, else `FiniteNumber` semantics with upstream's two messages (`desktop/service.py:906-919`).

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~DesktopMcp.Core.Tests.Input"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Input windows/tests/DesktopMcp.Core.Tests/Input
git commit -m "Add pointer paths, coordinate normalisation and act-tool argument parsing" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: SendInput injector, input signature and InputLedger

The only path to `SendInput` (Global Constraints), with the signature, the count check and the UIPI diagnosis (Decisions 8, 14), and upstream's ledger (Decisions 19).

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Input/IInputInjector.cs`, `SendInputInjector.cs`, `InputLedger.cs`
- Modify: `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (add `SendInput`, `INPUT`, `MapVirtualKey`, `OpenProcessToken`, `GetTokenInformation`, `TOKEN_MANDATORY_LABEL`, `TOKEN_ELEVATION`, `GetSidSubAuthority`, `GetSidSubAuthorityCount`, `GetCurrentProcess`, `WindowFromPoint`)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Input/SendInputInjectorTests.cs`, `InputLedgerTests.cs`

**Interfaces:**
- Consumes: `KeyInput`, `KeyTables` (Task 1); `DesktopException` (P2).
- Produces (namespace `DesktopMcp.Desktop.Input`):
  - `[Flags] public enum MouseFlags : uint { None = 0, Move = 0x1, LeftDown = 0x2, LeftUp = 0x4, RightDown = 0x8, RightUp = 0x10, MiddleDown = 0x20, MiddleUp = 0x40, Wheel = 0x800, VirtualDesk = 0x4000, Absolute = 0x8000, AbsoluteMove = Move | Absolute | VirtualDesk }`
  - `public readonly record struct InjectedInput(bool IsKeyboard, MouseFlags Mouse, int X, int Y, int WheelDelta, KeyInput Key)` with `static InjectedInput MouseAt(MouseFlags flags, (int X, int Y) normalized)`, `static InjectedInput Wheel(int delta)`, `static InjectedInput Keyboard(KeyInput key)`.
  - `public interface IInputInjector { void Send(IReadOnlyList<InjectedInput> inputs); }`
  - `public static class InputSignature { public const uint ExtraInfo = 0x444D4350; }`
  - `public sealed class SendInputInjector : IInputInjector` (public parameterless ctor; internal ctor `(SendInputCall send, Func<ushort, ushort> mapVirtualKey, Func<bool> selfElevated, Func<string?> foregroundProcessName)`; `internal delegate uint SendInputCall(ReadOnlySpan<INPUT> inputs, out int lastError)`).
  - `public sealed class InputBlockedException(string message) : DesktopException(message)`; `public static string InputBlockedException.ElevatedText(string? processName)`.
  - `internal static class ProcessIntegrity { bool CurrentIsElevated { get; } int? IntegrityRid(int processId); bool IsHigherThanCurrent(int processId); }` (null when the token cannot be read; "higher" only when both levels are known).
  - `public sealed class InputLedger(Func<string, bool>? physicalDownDelivered = null)` with `void Press(string name, Action sendDown, Action sendUp)`, `void Release(string name)`, `void ReleaseAll()`, `void BlockNew()`, `void Enable()`, `IReadOnlyList<string> Pending { get; }`; `public sealed class InputUnavailableException(string message) : DesktopException(message)`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class SendInputInjectorTests
{
    private sealed class Recorder
    {
        public List<INPUT> Inputs { get; } = [];
        public uint Result { get; set; } = uint.MaxValue; // MaxValue: report everything inserted
        public int Error { get; set; }
        public uint Send(ReadOnlySpan<INPUT> inputs, out int lastError)
        {
            Inputs.AddRange(inputs.ToArray());
            lastError = Error;
            return Result == uint.MaxValue ? (uint)inputs.Length : Result;
        }
    }

    private static SendInputInjector Create(Recorder recorder, bool elevated = false) =>
        new(recorder.Send, vk => vk == 0x12 ? (ushort)0x38 : (ushort)0x1E, () => elevated, () => "Taskmgr.exe");

    [Fact]
    public void EveryEventCarriesTheSignature()
    {
        var recorder = new Recorder();
        Create(recorder).Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove, (10, 20)), InjectedInput.Keyboard(new(0x41, '\0', false))]);
        Assert.All(recorder.Inputs, input => Assert.Equal((nuint)0x444D4350, input.type == INPUT_TYPE.INPUT_MOUSE ? input.Anonymous.mi.dwExtraInfo : input.Anonymous.ki.dwExtraInfo));
    }

    // Decisions 14: Alt is the left Alt (no extended flag); Right Ctrl, arrows and Win are extended.
    [Fact]
    public void OnlyExtendedKeysGetTheExtendedFlag()
    {
        var recorder = new Recorder();
        Create(recorder).Send([InjectedInput.Keyboard(new(0x12, '\0', false)), InjectedInput.Keyboard(new(0x25, '\0', true))]);
        Assert.Equal((ushort)0x38, recorder.Inputs[0].Anonymous.ki.wScan);
        Assert.False(recorder.Inputs[0].Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY));
        Assert.True(recorder.Inputs[1].Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP));
    }

    [Fact]
    public void UnicodeUnitsUseTheUnicodeFlag()
    {
        var recorder = new Recorder();
        Create(recorder).Send([InjectedInput.Keyboard(new(0, '한', false))]);
        Assert.Equal(('한', (ushort)0, KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE),
            ((char)recorder.Inputs[0].Anonymous.ki.wScan, (ushort)recorder.Inputs[0].Anonymous.ki.wVk, recorder.Inputs[0].Anonymous.ki.dwFlags));
    }

    [Fact]
    public void AccessDeniedWhileNotElevatedIsTheElevatedError()
    {
        var recorder = new Recorder { Result = 0, Error = 5 };
        var error = Assert.Throws<InputBlockedException>(() => Create(recorder).Send([InjectedInput.Wheel(120)]));
        Assert.Equal("Input blocked: the target window (Taskmgr.exe) runs elevated (as administrator) and this server does not, so Windows blocks input to it (UIPI). Run desktop-mcp elevated to control it.", error.Message);
    }

    [Fact]
    public void AnyOtherShortCountIsTheGenericError()
    {
        var recorder = new Recorder { Result = 1, Error = 0 };
        var error = Assert.Throws<DesktopException>(() => Create(recorder, elevated: true).Send([InjectedInput.Wheel(120), InjectedInput.Wheel(120)]));
        Assert.Equal("SendInput inserted 1 of 2 input events (Win32 error 0): the input was blocked, for example by the secure desktop or another application.", error.Message);
    }
}

public class InputLedgerTests
{
    [Fact]
    public void ReleaseSendsUpAndForgetsTheHold()
    {
        var log = new List<string>();
        var ledger = new InputLedger();
        ledger.Press("key:ctrl", () => log.Add("down"), () => log.Add("up"));
        Assert.Equal(["key:ctrl"], ledger.Pending);
        ledger.Release("key:ctrl");
        ledger.Release("key:ctrl");
        Assert.Equal(["down", "up"], log);
        Assert.Empty(ledger.Pending);
    }

    [Fact]
    public void APhysicalDownSuppressesTheInjectedUp()
    {
        var log = new List<string>();
        var ledger = new InputLedger(name => name == "key:shift");
        ledger.Press("key:shift", () => log.Add("down"), () => log.Add("up"));
        ledger.Release("key:shift");
        Assert.Equal(["down"], log);
    }

    [Fact]
    public void ADownThatThrowsIsReleasedAndRethrown()
    {
        var log = new List<string>();
        var ledger = new InputLedger();
        Assert.Throws<InvalidOperationException>(() => ledger.Press("mouse:left", () => throw new InvalidOperationException("boom"), () => log.Add("up")));
        Assert.Equal(["up"], log);
        Assert.Empty(ledger.Pending);
    }

    [Fact]
    public void UpstreamsGuardTexts()
    {
        var ledger = new InputLedger();
        ledger.Press("key:ctrl", () => { }, () => { });
        Assert.Equal("AI input already held: key:ctrl", Assert.Throws<InvalidOperationException>(() => ledger.Press("key:ctrl", () => { }, () => { })).Message);
        Assert.Equal("AI input remains held", Assert.Throws<InvalidOperationException>(ledger.Enable).Message);
        ledger.ReleaseAll();
        ledger.BlockNew();
        Assert.Equal("AI input is unavailable", Assert.Throws<InputUnavailableException>(() => ledger.Press("key:alt", () => { }, () => { })).Message);
    }

    [Fact]
    public void ReleaseAllReleasesEveryHoldAndRethrowsTheFirstError()
    {
        var ledger = new InputLedger();
        var released = new List<string>();
        ledger.Press("a", () => { }, () => throw new InvalidOperationException("first"));
        ledger.Press("b", () => { }, () => released.Add("b"));
        Assert.Equal("first", Assert.Throws<InvalidOperationException>(ledger.ReleaseAll).Message);
        Assert.Equal(["b"], released);
        Assert.Equal(["a"], ledger.Pending); // upstream keeps a failed entry for shutdown's second attempt
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`SendInputInjector.Send` builds all events of one call into one `INPUT[]` and calls `SendInput` once (upstream sends one event per call; one batch keeps a click's down and up together). Keyboard: VK events get `wScan = scan & 0xFF` (`FixedScanCodes`, else `mapVirtualKey(vk)` with `0x100` for `ScanExtendedKeys`) and `KEYEVENTF_EXTENDEDKEY` when `ExtendedKeys` contains the VK or the scan has `0x100`; Unicode events `wVk = 0`, `wScan = unit`, `KEYEVENTF_UNICODE`. Mouse: `dx`, `dy`, `mouseData = WheelDelta`, `dwFlags`. Every `dwExtraInfo = InputSignature.ExtraInfo`. A short count throws as the tests show; `foregroundProcessName` reads the foreground window's process image name (Win32WindowSystem's `GetProcessName`). `InputLedger`: one `Lock`; `Press` records before calling `sendDown` and holds the lock through it (`control_ledger.py:30-53`); `Release` skips `sendUp` when the predicate says a physical down was delivered; `ReleaseAll` snapshots the names, releases each, keeps failed entries and rethrows the first error.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~DesktopMcp.Desktop.Tests.Input"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Input windows/src/DesktopMcp.Desktop/NativeMethods.txt windows/tests/DesktopMcp.Desktop.Tests/Input
git commit -m "Add the signed SendInput injector and the input ledger" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Desktop input primitives with upstream's timings, and the guarded test targets

`DesktopInput` is upstream's `Desktop.click/type/scroll/drag/move/shortcut/multi_select` (`desktop/service.py:722-1031`) over the injector, with upstream's waits, the click pause (Decisions 8), the ledger for every held key or button (Decisions 19) and the non-text clipboard rule (Decisions 21). The guarded test targets make every later Desktop input test safe (Decisions 11).

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Input/DesktopInput.cs`, `windows/src/DesktopMcp.Desktop/ClipboardAccess/ClipboardFormats.cs`
- Modify: `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (`GetDoubleClickTime`, `CountClipboardFormats`), `windows/tests/DesktopMcp.Desktop.Tests/DesktopMcp.Desktop.Tests.csproj` (`<UseWindowsForms>true</UseWindowsForms>`; if the SDK rejects it with `IsAotCompatible` (NETSDK1175), also `<IsAotCompatible>false</IsAotCompatible>` in this test project only), `windows/tests/DesktopMcp.Desktop.Tests/TestWindow.cs` (count `BN_CLICKED` from the button as `ButtonClicks`; `EditText` reads the edit with `GetWindowTextW`; `EditHandle`, `ButtonHandle`; an `activate` ctor flag that shows with `SW_SHOW`)
- Create (tests): `ActiveTestWindow.cs`, `GuardedInjector.cs`, `WinFormsTarget.cs`, `Input/DesktopInputTests.cs`, `Input/InputDesktopTests.cs`

**Interfaces:**
- Consumes: Tasks 1-3; `IClipboard` (P1); `ScreenPoint`, `ScreenRect`.
- Produces (namespace `DesktopMcp.Desktop.Input`):
  - `public interface IActionCheckpoint { void Checkpoint(); void RecordStep(); }` and `public sealed class NoCheckpoint : IActionCheckpoint { public static NoCheckpoint Instance { get; } }`
  - `public enum CaretPosition { Idle, Start, End }`
  - `public sealed record DragResult(ScreenPoint Start, double? Duration)`
  - `public static class InputTiming` (`OperationWait` 500 ms `uia/core.py:34`; `ButtonHold` 50 ms `core.py:289`; `PressAfterMove` 50 ms Decisions 8; `MoveStep` 3 ms `service.py:986`; `DragSettle` 500 ms `service.py:943`; `DragButtonWait` 50 ms `service.py:952-953`; `WheelInterval` 50 ms `core.py:528`; `KeyWait` 50 ms; `TypeInterval` 40 ms `service.py:809`; `ShortcutInterval` 10 ms `service.py:1000`; `ClearSettle` 500 ms `service.py:792`; `PasteSettle` 50 ms `service.py:830`; `PasteThreshold` 20 code points; `MultiSelectClickWait` 200 ms and `MultiSelectSettle` 500 ms `service.py:1020-1022`; `KeyHoldWait` 50 ms `service.py:876`)
  - `public interface IDesktopInput` with:
    - `Task ClickAsync(ScreenPoint point, MouseButton button, long clicks, CancellationToken ct)`
    - `Task MoveAsync(ScreenPoint to, CancellationToken ct)`
    - `Task<DragResult> DragAsync(ScreenPoint? from, ScreenPoint to, double? duration, CancellationToken ct)`
    - `Task<string?> ScrollAsync(ScreenPoint? at, string type, string direction, long wheelTimes, CancellationToken ct)` (an upstream invalid-combination text, or null)
    - `Task PressKeysAsync(IReadOnlyList<KeyToken> tokens, TimeSpan interval, TimeSpan waitTime, CancellationToken ct)`
    - `Task TypeAsync(ScreenPoint at, string text, CaretPosition caret, bool clear, bool pressEnter, Func<CancellationToken, Task>? afterClick, CancellationToken ct)`
    - `Task<IReadOnlyList<ScreenPoint>> MultiSelectAsync(bool pressCtrl, int count, Func<int, CancellationToken, Task<ScreenPoint>> target, CancellationToken ct)`
    - `ScreenPoint Cursor { get; }`
  - `public sealed class DesktopInput : IDesktopInput` (public ctor `(InputLedger ledger, IClipboard clipboard, IActionCheckpoint checkpoint)`; internal ctor adding `IInputInjector injector, Func<ScreenPoint> cursor, Func<ScreenRect> virtualScreen, Func<TimeSpan, CancellationToken, Task> delay, Func<uint> doubleClickTimeMs, Func<bool> clipboardHoldsOnlyNonText`).
  - `public static class ClipboardFormats { public static bool HoldsOnlyNonText(); }`
  - Tests: `ActiveTestWindow : IDisposable` (a `TestWindow` made foreground by the test thread with `AttachThreadInput` + `SetForegroundWindow`, verified with `GetForegroundWindow`, else throws `InvalidOperationException("The test window could not be made the foreground window; no input was sent.")`); `GuardedInjector(IInputInjector inner, nint window)`; `WinFormsTarget : IDisposable` (a Form on its own STA thread with a `Button`, a `ToolStrip` holding one `ToolStripButton`, and a `TextBox`; counters `ButtonClicks`, `ToolStripClicks`; `ActivateAndWait()`).

- [ ] **Step 1: Write the failing unit tests (fakes)**

`windows/tests/DesktopMcp.Desktop.Tests/Input/DesktopInputTests.cs` uses a `FakeInjector` recording `(InjectedInput, elapsedFakeTime)` and a `FakeDelay` that records each requested delay and advances the fake time (no real sleeping):

```csharp
public class DesktopInputTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1001, 1001); // normalise: round(p * 65535 / 1000)

    [Fact]
    public void ClickSendsMovePauseDownUpWithTheSignatureFlags()
    {
        var rig = new InputRig(cursor: new(0, 0));
        rig.Input.ClickAsync(new(100, 200), MouseButton.Left, 1, default).GetAwaiter().GetResult();
        Assert.Equal([MouseFlags.AbsoluteMove, MouseFlags.AbsoluteMove | MouseFlags.LeftDown, MouseFlags.AbsoluteMove | MouseFlags.LeftUp],
            rig.Sent.Select(i => i.Mouse));
        Assert.All(rig.Sent, i => Assert.Equal((6554, 13107), (i.X, i.Y)));
        Assert.Equal([50, 50, 500], rig.Delays.Select(d => (int)d.TotalMilliseconds));
    }

    [Fact]
    public void DoubleClickWaitsHalfTheDoubleClickTimeAndPausesOnlyBeforeTheFirstPress()
    {
        var rig = new InputRig(cursor: new(0, 0), doubleClickTimeMs: 500);
        rig.Input.ClickAsync(new(10, 10), MouseButton.Left, 2, default).GetAwaiter().GetResult();
        Assert.Equal([50, 50, 250, 50, 500], rig.Delays.Select(d => (int)d.TotalMilliseconds));
    }

    [Fact]
    public void HoverOnlyMoves()
    {
        var rig = new InputRig(cursor: new(0, 0));
        rig.Input.ClickAsync(new(10, 10), MouseButton.Right, 0, default).GetAwaiter().GetResult();
        Assert.Equal([MouseFlags.AbsoluteMove], rig.Sent.Select(i => i.Mouse));
    }

    [Fact]
    public void DragReleasesTheButtonWhenCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var rig = new InputRig(cursor: new(0, 0), onDelay: count => { if (count == 4) cancel.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => rig.Input.DragAsync(null, new(500, 0), null, cancel.Token).GetAwaiter().GetResult());
        Assert.Equal(MouseFlags.AbsoluteMove | MouseFlags.LeftUp, rig.Sent[^1].Mouse);
        Assert.Empty(rig.Ledger.Pending);
    }

    [Fact]
    public void ScrollHorizontalHoldsShiftAroundTheWheel()
    {
        var rig = new InputRig(cursor: new(0, 0));
        Assert.Null(rig.Input.ScrollAsync(null, "horizontal", "right", 2, default).GetAwaiter().GetResult());
        Assert.Equal(new KeyInput(0x10, '\0', false), rig.Sent[0].Key);
        Assert.Equal([-120, -120], rig.Sent.Where(i => i.Mouse == MouseFlags.Wheel).Select(i => i.WheelDelta));
        Assert.Equal(new KeyInput(0x10, '\0', true), rig.Sent[^1].Key);
    }

    [Theory]
    [InlineData("vertical", "left", "Invalid direction. Use \"up\" or \"down\".")]
    [InlineData("horizontal", "up", "Invalid direction. Use \"left\" or \"right\".")]
    public void ScrollInvalidCombinationsReturnUpstreamsText(string type, string direction, string text) =>
        Assert.Equal(text, new InputRig(cursor: new(0, 0)).Input.ScrollAsync(null, type, direction, 1, default).GetAwaiter().GetResult());

    [Fact]
    public void HugeWheelCountsStopAtCancellation()
    {
        using var cancel = new CancellationTokenSource();
        var rig = new InputRig(cursor: new(0, 0), onDelay: count => { if (count == 6) cancel.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => rig.Input.ScrollAsync(null, "vertical", "down", 1_000_000, cancel.Token).GetAwaiter().GetResult());
        Assert.InRange(rig.Sent.Count, 1, 4);
    }

    [Fact]
    public void ShortcutCancelledMidwayReleasesTheHeldModifiers()
    {
        using var cancel = new CancellationTokenSource();
        var rig = new InputRig(cursor: new(0, 0), onDelay: count => { if (count == 2) cancel.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => rig.Input.PressKeysAsync(ShortcutParser.Parse("ctrl+shift+s"), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(500), cancel.Token).GetAwaiter().GetResult());
        Assert.Equal([new KeyInput(0x11, '\0', false), new KeyInput(0x10, '\0', false), new KeyInput(0x10, '\0', true), new KeyInput(0x11, '\0', true)],
            rig.Sent.Select(i => i.Key));
        Assert.Empty(rig.Ledger.Pending);
    }

    [Fact]
    public void TypePastesLongPlainTextAndRestoresTheClipboard()
    {
        var rig = new InputRig(cursor: new(0, 0), clipboard: "before");
        rig.Input.TypeAsync(new(5, 5), "twenty characters ok", CaretPosition.Idle, false, false, null, default).GetAwaiter().GetResult();
        Assert.Contains(new KeyInput(0x56, '\0', false), rig.Sent.Select(i => i.Key)); // Ctrl+V
        Assert.Equal("before", rig.Clipboard.Text);
    }

    [Fact]
    public void TypeDoesNotPasteWhenTheClipboardHoldsOnlyNonText()
    {
        var rig = new InputRig(cursor: new(0, 0), clipboard: null, onlyNonText: true);
        rig.Input.TypeAsync(new(5, 5), "twenty characters ok", CaretPosition.Idle, false, false, null, default).GetAwaiter().GetResult();
        Assert.Equal(20, rig.Sent.Count(i => i.IsKeyboard && i.Key.VirtualKey == 0 && !i.Key.Up));
        Assert.Equal(0, rig.Clipboard.Writes);
    }

    [Fact]
    public void TypeRunsTheAfterClickHookBeforeAnyKey()
    {
        var rig = new InputRig(cursor: new(0, 0));
        var sentAtHook = -1;
        rig.Input.TypeAsync(new(5, 5), "x", CaretPosition.End, true, true, _ => { sentAtHook = rig.Sent.Count; return Task.CompletedTask; }, default).GetAwaiter().GetResult();
        Assert.Equal(3, sentAtHook); // move, down, up
        Assert.Equal(new KeyInput(0x0D, '\0', true), rig.Sent[^1].Key); // press_enter
    }

    [Fact]
    public void MultiSelectReleasesCtrlWhenATargetFails()
    {
        var rig = new InputRig(cursor: new(0, 0));
        Assert.Throws<InvalidOperationException>(() => rig.Input.MultiSelectAsync(true, 3,
            (i, _) => i == 1 ? throw new InvalidOperationException("stale") : Task.FromResult(new ScreenPoint(5, 5)), default).GetAwaiter().GetResult());
        Assert.Equal(new KeyInput(0x11, '\0', true), rig.Sent[^1].Key);
        Assert.Empty(rig.Ledger.Pending);
    }
}
```

`InputRig` (in the same file) wires `DesktopInput`'s internal ctor with the fakes, a fresh `InputLedger`, a `FakeClipboard` (`Text`, `Writes`) and `Screen`; its fake cursor follows the last absolute move, and `onDelay` gets the running count of delays.

- [ ] **Step 2: Write the failing Desktop tests**

`windows/tests/DesktopMcp.Desktop.Tests/Input/InputDesktopTests.cs`, every test `[Trait("Category", "Desktop")]`, each creating `ActiveTestWindow` (or `WinFormsTarget`) and `new DesktopInput(...)` over `new GuardedInjector(new SendInputInjector(), window)`:
- `InjectedMouseEventsCarryTheSignature`: a `WH_MOUSE_LL` hook on a test STA thread with a message loop (as `probes/DesktopMcp.Probe/HookProbe.cs`) records `dwExtraInfo` of events flagged `LLMHF_INJECTED`; `MoveAsync` to the test window's centre; every recorded event has `0x444D4350`.
- `ClickPressesTheTestWindowsButton`: `ClickAsync(button centre, Left, 1)` makes `ButtonClicks == 1`.
- `TypeWritesIntoTheTestWindowsEdit`: `TypeAsync(edit centre, "héllo 😀", Idle, clear: true, pressEnter: false)` makes `EditText == "héllo 😀"`; then a 25-character text through the paste path, with the clipboard text saved before and asserted restored after.
- `WinFormsToolStripButtonTakesEveryClick`: ten `ClickAsync` calls on the `ToolStripButton`'s centre (from `ToolStrip.Items[0].Bounds` mapped with `PointToScreen`) make `ToolStripClicks == 10`.
- `CoordinatesLandOnTheTargetPixel`: `MoveAsync` to the window's four inner corners; after each, `GetCursorPos` equals the target.

`GuardedInjector.Send`: before forwarding, the foreground window's thread (`GetWindowThreadProcessId(GetForegroundWindow())`) must equal the test window's thread, and each `Absolute` mouse event, denormalised over the virtual screen, must have `GetAncestor(WindowFromPoint(p), GA_ROOT) == window`; otherwise throw `InvalidOperationException("Test input guard: <reason>; nothing was sent.")` without forwarding.

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile (`DesktopInput` does not exist).

- [ ] **Step 4: Implement `DesktopInput` and `ClipboardFormats`**

One rule for every step: `checkpoint.Checkpoint()`, `ct.ThrowIfCancellationRequested()`, send, `checkpoint.RecordStep()`, then the delay with `ct`. Releases (a ledger `sendUp` and its wait) never take `ct`: they run in `finally` after a cancellation, so their waits use `CancellationToken.None`. `PressKeysAsync` releases what it still holds in reverse press order. A "move to p" is one `MouseAt(AbsoluteMove, Normalize(p))`. Sequences, each citing upstream:
- Click (`service.py:722-755`, `uia/core.py:280-319`): `clicks == 0`: one move. Otherwise for each click: move and `PressAfterMove` when the cursor is not at the point yet; down then `ButtonHold`; up then `OperationWait` (left with `clicks >= 2`: `doubleClickTimeMs / 2` between clicks and `OperationWait` after the last). Right and middle repeat single clicks.
- Move (`service.py:976-986`): `PointerPath.MoveSteps(cursor, to)`, `MoveStep` after each.
- Drag (`service.py:930-974`): `DragSettle`; start = `from ?? cursor`; `ledger.Press("mouse:left", down: move to start + LeftDown then DragButtonWait, up: LeftUp at the current cursor then DragButtonWait)`; the `DragSteps` points with their interval; `Release` in `finally`.
- Scroll (`service.py:844-904`): validate the type/direction combination first and return upstream's text; `at` given: `MoveAsync(at)`; each tick `Wheel(+120 up / -120 down)`, `WheelInterval`, `OperationWait`; horizontal holds Shift with `ledger.Press("key:shift", ...)` (`KeyHoldWait` after its down and up) and maps left to +120 and right to -120.
- PressKeysAsync: `SendKeysPlan.Build`; each VK down that is followed by a pause before its own up is pressed through the ledger as `key:vk:<hex>` and released (its up step) normally or in `finally`.
- Type (`service.py:767-814`): `ClickAsync(at, Left, 1)`; `afterClick`; Home or End (`PressKeysAsync` with 10 ms and `KeyWait`); `clear`: `ClearSettle`, Ctrl+A, Back; text: paste when `PyText.CodePointLength(text) >= PasteThreshold`, no `\n`, `\t`, `{`, `}`, and `!clipboardHoldsOnlyNonText()` (`_paste_text`, `service.py:816-842`: read the prior text, swallowing errors; set; `PasteSettle`; Ctrl+V with `KeyWait`; in `finally` restore the prior text when it is non-null and the clipboard still holds `text`), else each `TextKeys.PerCharacter` entry through `PressKeysAsync(entry, TypeInterval, KeyWait)` (an empty entry still checkpoints); `pressEnter`: Enter with `KeyWait`.
- MultiSelect (`service.py:1003-1025`): Ctrl through the ledger as `key:ctrl`; for each index: `target(i)`, left click with `MultiSelectClickWait` after the up, then `MultiSelectSettle`; returns the points; Ctrl released in `finally`.

`ClipboardFormats.HoldsOnlyNonText()` is `CountClipboardFormats() > 0 && !IsClipboardFormatAvailable(CF_UNICODETEXT)`.

- [ ] **Step 5: Run the unit tests, then the Desktop tests by hand**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~DesktopMcp.Desktop.Tests.Input"`
Expected: PASS (unit tests; Desktop ones are filtered out).

Run (interactive session, hands off mouse and keyboard; the test windows appear and take the foreground): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~InputDesktopTests"`
Expected: PASS. A guard failure (foreground taken by another window) fails the test without sending input: rerun with nothing else opening windows.

- [ ] **Step 6: Commit**

```bash
git add windows/src/DesktopMcp.Desktop windows/tests/DesktopMcp.Desktop.Tests
git commit -m "Add the desktop input primitives with upstream's timings and guarded test targets" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Foreground and window placement

Decisions 7 and 32: reliable foreground for keyboard input and App switch, and a resize that cannot block on a hung window.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/WindowControl/ForegroundService.cs`, `WindowPlacement.cs`
- Modify: `windows/src/DesktopMcp.Desktop/Tree/WindowSystem.cs`, `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (`SetForegroundWindow`, `BringWindowToTop`, `AttachThreadInput`, `AllowSetForegroundWindow`, `ShowWindowAsync`, `SetWindowPos`, `GetCurrentThreadId`, `GetWindowRect`, `GetWindowText`, `GetWindowTextLength`)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/WindowControl/ForegroundServiceTests.cs`

**Interfaces:**
- Consumes: `IWindowSystem`, `Win32WindowSystem.CheckResponding` (P2), `DesktopException`.
- Produces:
  - `IWindowSystem` additions: `uint GetThreadId(long window)`, `ScreenRect GetBounds(long window)`, `long WindowFromPoint(ScreenPoint point)` (the root, `GA_ROOT`), `string GetTitle(long window)`, `WindowResponse CheckResponding(long window)`.
  - `public sealed record ForegroundResult(bool Succeeded, bool WasMinimized)`
  - `public interface IForeground { Task<ForegroundResult> SwitchToAsync(long window, CancellationToken ct); Task EnsureKeyboardTargetAsync(long window, string windowName, CancellationToken ct); }`
  - `public sealed class ForegroundService : IForeground` (public ctor; internal ctor `(IWindowSystem windows, IForegroundCalls calls, Func<TimeSpan, CancellationToken, Task> delay, TimeProvider time)`; `internal interface IForegroundCalls` wraps `ShowWindowAsync`, `SetForegroundWindow`, `BringWindowToTop`, `AttachThreadInput`, `AllowSetForegroundWindow`, `SetWindowPos`, `GetCurrentThreadId`, `IsIconic`).
  - `public sealed class WindowPlacement` (`Task ResizeAsync(long window, string name, ScreenRect target, CancellationToken ct)`; throws `DesktopException` with `<name> is not responding` or `Failed to resize <name>: <message>`).

- [ ] **Step 1: Write the failing tests**

```csharp
public class ForegroundServiceTests
{
    [Fact]
    public async Task AHungWindowGetsNoCall()
    {
        var rig = new ForegroundRig { Response = WindowResponse.NotResponding };
        var error = await Assert.ThrowsAsync<DesktopException>(() => rig.Service.EnsureKeyboardTargetAsync(42, "Notepad", default));
        Assert.Equal("Notepad is not responding; nothing was sent.", error.Message);
        Assert.Empty(rig.Calls.Log);
    }

    [Fact]
    public async Task AMinimisedWindowIsRestoredAsynchronously()
    {
        var rig = new ForegroundRig { Minimized = true, TakesForegroundOn = "SetForegroundWindow" };
        var result = await rig.Service.SwitchToAsync(42, default);
        Assert.Equal(new ForegroundResult(true, true), result);
        Assert.Equal(["ShowWindowAsync(42, SW_RESTORE)", "SetForegroundWindow(42)"], rig.Calls.Log.Take(2));
    }

    [Fact]
    public async Task AttachThreadInputIsTheFallbackAndIsUndone()
    {
        var rig = new ForegroundRig { TakesForegroundOn = "AttachThreadInput" };
        await rig.Service.EnsureKeyboardTargetAsync(42, "Notepad", default);
        Assert.Equal(rig.Calls.Log.Count(c => c.StartsWith("AttachThreadInput(") && c.EndsWith(", True)")),
                     rig.Calls.Log.Count(c => c.StartsWith("AttachThreadInput(") && c.EndsWith(", False)")));
    }

    [Fact]
    public async Task APopupOfTheSameThreadCountsForKeys()
    {
        var rig = new ForegroundRig { ForegroundWindow = 77, ForegroundThreadSameAsTarget = true };
        await rig.Service.EnsureKeyboardTargetAsync(42, "Notepad", default);
        Assert.DoesNotContain("SetForegroundWindow(42)", rig.Calls.Log);
    }

    [Fact]
    public async Task FailureAfterOneSecondIsAClearError()
    {
        var rig = new ForegroundRig { TakesForegroundOn = null };
        var error = await Assert.ThrowsAsync<DesktopException>(() => rig.Service.EnsureKeyboardTargetAsync(42, "Notepad", default));
        Assert.Equal("Could not bring Notepad to the foreground; nothing was typed.", error.Message);
        Assert.False((await rig.Service.SwitchToAsync(42, default)).Succeeded);
    }
}
```

`ForegroundRig` holds a fake `IWindowSystem` (target 42, thread 7; foreground window and thread configurable), a fake `IForegroundCalls` that logs calls as strings and makes the foreground the target when the named call happens, and a fake clock whose delays advance time. Add one `[Trait("Category", "Desktop")]` test `SwitchesToTheTestWindowFromAnotherTestWindow`: two `TestWindow`s, the first active; `SwitchToAsync(second)` succeeds and `GetForegroundWindow()` is the second.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

The sequence (`desktop/service.py:559-661` plus Decisions 7): `CheckResponding` (`NotResponding` or `Hung` → the not-responding error); `IsIconic` → `ShowWindowAsync(SW_RESTORE)` and poll `!IsIconic` every 50 ms up to 1 s; done when the success test already holds; else `SetForegroundWindow` and poll 100 ms; else on a new background thread bounded at 2 s (abandon past it): `AllowSetForegroundWindow(ASFW_ANY)`, attach the current thread to the foreground and target threads (skip failures and equal threads, `service.py:629-641`), `SetForegroundWindow`, `BringWindowToTop`, `SetWindowPos(HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS)`, detach in reverse; then poll every 50 ms for 1 s (`service.py:570-577`). Success test: `SwitchToAsync` the foreground handle equals the window; `EnsureKeyboardTargetAsync` the foreground window's thread equals the target's. `WindowPlacement.ResizeAsync`: `CheckResponding` first, then `SetWindowPos(window, 0, l, t, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS)`; false → `Failed to resize <name>: <Win32Exception message>`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~ForegroundServiceTests"`
Expected: PASS. Then the Desktop test by hand as in Task 4 Step 5.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop windows/tests/DesktopMcp.Desktop.Tests/WindowControl
git commit -m "Add the bounded foreground sequence and asynchronous window placement" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Element identity and metadata in the snapshot

The label map must carry what the re-lookup needs (Decisions 40) and what WaitFor matches (Decisions 27, P2 Decisions 6). The tree text does not change.

**Files:**
- Modify: `windows/src/DesktopMcp.Core/Snapshots/SnapshotModel.cs`, `windows/src/DesktopMcp.Desktop/Tree/TreeSource.cs`, `Tree/TreeWalker.cs`, `Tree/TreeService.cs`, `Uia/UiaTreeSource.cs`, `Uia/UiaValues.cs`, `Uia/UiaSession.cs` (no new properties: ClickablePoint and SelectionItem IsSelected are already in `TraversalProperties`)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Tree/TreeWalkerTests.cs`, `Uia/UiaValuesTests.cs`, `windows/tests/DesktopMcp.Core.Tests/Snapshots/SnapshotTextTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ElementIdentity(long Window, string RuntimeId, int ControlTypeId, string AutomationId, string Name, ScreenPoint? ClickablePoint)` (Core.Snapshots)
  - `UiElement` gains `public ElementIdentity? Identity { get; init; }` (positional parameters unchanged, so P2 code and tests compile).
  - `UiMetadata` gains `public bool IsEnabled { get; init; } = true;` and `public bool? IsSelected { get; init; }`.
  - `UiaElementData` gains `public ScreenPoint? ClickablePoint { get; init; }` and `public bool? IsSelected { get; init; }`.
  - `WindowContext` gains `public long Handle { get; init; }`; `UiaWindowWalker.Walk` sets it to the walked window.
  - `UiaValues.CachedPoint(IUIAutomationElement element, UIA_PROPERTY_ID id) : ScreenPoint?` (a two-element `VT_ARRAY | VT_R8`, rounded half-even; null for anything else).

- [ ] **Step 1: Write the failing tests**

In `TreeWalkerTests.cs` (the P2 fake `ITreeSource`; extend its element builder and walk helper with the parameters these tests use):

```csharp
[Fact]
public void InteractiveAndScrollableElementsCarryTheirIdentity()
{
    var button = Element("button", TreeConfig.Button, runtimeId: "42.1.2", automationId: "ok", name: " OK ", clickable: new ScreenPoint(15, 15));
    var list = Element("list", 50008, runtimeId: "42.1.3", verticallyScrollable: true, enabled: false);
    var walk = Walk(Window(children: [button, list]), handle: 4242);
    var element = Assert.Single(walk.Interactive);
    Assert.Equal(new ElementIdentity(4242, "42.1.2", TreeConfig.Button, "ok", "OK", new ScreenPoint(15, 15)), element.Identity);
    var scrollable = Assert.Single(walk.Scrollable);
    Assert.Equal("42.1.3", scrollable.Identity!.RuntimeId);
    Assert.False(scrollable.Metadata.IsEnabled);
}

[Fact]
public void WordsAndElementsWithoutARuntimeIdHaveNoIdentity()
{
    var edit = Element("edit", TreeConfig.Edit, runtimeId: null, name: "doc", words: [new WordBox("hello", new ScreenRect(1, 1, 20, 10))]);
    var walk = Walk(Window(children: [edit]), handle: 1);
    Assert.All(walk.Interactive, element => Assert.Null(element.Identity));
}

[Fact]
public void SelectionStateIsInTheMetadata()
{
    var item = Element("list item", TreeConfig.ListItem, runtimeId: "7", name: "a", selected: true, focusable: true);
    Assert.True(Assert.Single(Walk(Window(children: [item]), handle: 1).Interactive).Metadata.IsSelected);
}
```

In `SnapshotTextTests.cs`: `IdentityAndNewMetadataDoNotChangeTheText` renders the P2 sample snapshot twice, once with `Identity` and `IsSelected` set on every element, and asserts the two texts are equal. In `UiaValuesTests.cs`: `ClickablePointReadsTwoDoubles` and `ClickablePointIsNullForAnEmptyArray` with `ComVariant` arrays built as the existing tests build RuntimeIds.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile (`ElementIdentity` does not exist).

- [ ] **Step 3: Implement**

`UiaTreeSource.Read` reads `ClickablePoint` (`UIA_ClickablePointPropertyId`) and `IsSelected` (`UIA_SelectionItemIsSelectedPropertyId`, `CachedBool`). `TreeWalker` sets `Identity` on interactive and scrollable elements built from a node with a RuntimeId (word elements keep null), and `IsEnabled`/`IsSelected` in their metadata; `RegionFilter` already copies elements with `with`, so the identity survives (assert it in an existing region test).

- [ ] **Step 4: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: PASS for all three projects.

- [ ] **Step 5: Commit**

```bash
git add windows/src windows/tests
git commit -m "Record element identity, enabled and selection state in the snapshot" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: UIA label locator: re-lookup, re-find, gate, scroll into view

Decisions 1-3 on the UIA worker, behind an `IElementSource` seam so the algorithm is unit-tested.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Actions/LabelLocator.cs`
- Modify: `windows/src/DesktopMcp.Desktop/Uia/UiaSession.cs` (an `ActionRequest`: `TraversalProperties` plus `UIA_NativeWindowHandlePropertyId`, `UIA_IsScrollItemPatternAvailablePropertyId`, `UIA_IsValuePatternAvailablePropertyId`, `UIA_ValueValuePropertyId`, with the ScrollItem and Value patterns, `TreeScope_Element`), `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (`IUIAutomationScrollItemPattern`, `IUIAutomationValuePattern`, `MonitorFromRect` is already there)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Actions/LabelLocatorTests.cs`

**Interfaces:**
- Consumes: `UiaWorker<UiaSession>`, `UiaTreeSource.ReadSiblings`/`MaxChildren`, `Win32WindowSystem.CheckResponding`, `UiaElementData`, `ElementIdentity` (Task 6), `IDisplayProvider` (P2).
- Produces:
  - `internal interface IElementSource { WindowResponse CheckWindow(long window); bool WindowExists(long window); UiaElementData? ElementAt(ScreenPoint point); UiaElementData? Parent(UiaElementData element); UiaElementData ReadWindow(long window); IReadOnlyList<UiaElementData> Children(UiaElementData parent, out bool capped); bool ScrollIntoView(UiaElementData element); UiaElementData Refresh(UiaElementData element); bool CanScrollIntoView(UiaElementData element); }`
  - `internal enum LocateFailure { Stale, Disabled, OffScreen, NotResponding }`
  - `internal readonly record struct LocateResult(UiaElementData? Element, ScreenPoint Point, LocateFailure? Failure)`
  - `internal sealed class LabelLocator(IElementSource source, IReadOnlyList<ScreenRect> monitors, ScreenRect windowBounds)` with `const int MaxAncestors = 32`, `const int MaxVisited = 2000` and `LocateResult Locate(ElementIdentity identity, ScreenPoint storedCenter, CancellationToken ct)`.
  - `internal sealed class UiaLabelLocator(UiaWorker<UiaSession> worker, IDisplayProvider displays, IWindowSystem windows)` with `static readonly TimeSpan Deadline = 5 s` and `Task<LocateResult> LocateAsync(ElementIdentity identity, ScreenPoint storedCenter, CancellationToken ct)`; `internal sealed class UiaElementSource(UiaSession session) : IElementSource`.

- [ ] **Step 1: Write the failing tests**

`LabelLocatorTests` uses a `FakeElementSource` holding a tree of `UiaElementData` (parents, children, a point-to-element map, per-element ScrollItem support and a post-scroll bounds table) and logging every call:

```csharp
public class LabelLocatorTests
{
    private static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);
    private static ElementIdentity Id(string runtimeId = "1.2", string automationId = "ok", string name = "OK") =>
        new(100, runtimeId, TreeConfig.Button, automationId, name, null);

    [Fact]
    public void FindsTheElementByRuntimeIdUnderTheStoredPoint()
    {
        var source = FakeElementSource.Window(Button("1.2", new ScreenRect(10, 10, 50, 30)), pointMap: true);
        var result = new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default);
        Assert.Equal(new ScreenPoint(30, 20), result.Point);
        Assert.DoesNotContain("Children", source.Calls);
    }

    [Fact]
    public void FindsAMovedElementByWalkingTheWindow()
    {
        var source = FakeElementSource.Window(Button("1.2", new ScreenRect(200, 200, 240, 220)), pointMap: false);
        Assert.Equal(new ScreenPoint(220, 210), new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Point);
    }

    [Fact]
    public void ReFindsARecreatedElementByIdentityWhenExactlyOneMatches()
    {
        var source = FakeElementSource.Window(Button("9.9", new ScreenRect(10, 10, 50, 30)), pointMap: false);
        Assert.Null(new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void AmbiguousIdentityIsStale()
    {
        var source = FakeElementSource.Window([Button("9.8", new(10, 10, 50, 30)), Button("9.9", new(60, 10, 90, 30))], pointMap: false);
        Assert.Equal(LocateFailure.Stale, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void AWalkCutByACapIsStaleEvenWithOneMatch()
    {
        var source = FakeElementSource.Window(Button("9.9", new(10, 10, 50, 30)), pointMap: false, capChildren: true);
        Assert.Equal(LocateFailure.Stale, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void NoIdentityFieldsMeansNoReFind()
    {
        var source = FakeElementSource.Window(Button("9.9", new(10, 10, 50, 30), automationId: "", name: ""), pointMap: false);
        Assert.Equal(LocateFailure.Stale, new LabelLocator(source, [Monitor], Monitor).Locate(Id(automationId: "", name: ""), new(30, 20), default).Failure);
    }

    [Fact]
    public void NotRespondingWindowMakesNoUiaCall()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30)), pointMap: true, response: WindowResponse.NotResponding);
        Assert.Equal(LocateFailure.NotResponding, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
        Assert.Equal(["CheckWindow"], source.Calls);
    }

    [Fact]
    public void WindowGoneIsStale()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30)), pointMap: true, windowExists: false);
        Assert.Equal(LocateFailure.Stale, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void DisabledIsReported()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30), enabled: false), pointMap: true);
        Assert.Equal(LocateFailure.Disabled, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void OffScreenScrollsIntoViewThenUsesTheNewBounds()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 2000, 50, 2020), scrollTo: new ScreenRect(10, 500, 50, 520)), pointMap: false);
        var result = new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default);
        Assert.Equal(new ScreenPoint(30, 510), result.Point);
        Assert.Contains("ScrollIntoView", source.Calls);
    }

    [Fact]
    public void OffScreenWithoutScrollItemFails()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 2000, 50, 2020)), pointMap: false);
        Assert.Equal(LocateFailure.OffScreen, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void ClickablePointOutsideTheBoundsFallsBackToTheCentre()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30), clickable: new ScreenPoint(500, 500)), pointMap: true);
        Assert.Equal(new ScreenPoint(30, 20), new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Point);
    }

    [Fact]
    public void ClickablePointInsideTheBoundsIsUsed()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30), clickable: new ScreenPoint(12, 12)), pointMap: true);
        Assert.Equal(new ScreenPoint(12, 12), new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Point);
    }
}
```

Desktop tests (`[Trait("Category", "Desktop")]`, a plain `TestWindow`, no input): Snapshot-like identity from `UiaTreeSource` for the button, then `UiaLabelLocator.LocateAsync`: finds it at its centre; after `SetWindowPos` moves the window 100 px right it finds it at the new centre; after `EnableWindow(button, false)` → `Disabled`; after `Dispose` of the window → `Stale`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`LabelLocator.Locate` (the algorithm; every step checks `ct`):
1. `WindowExists(identity.Window)` false → `Stale`. `CheckWindow` → `NotResponding` or `Hung` → `NotResponding` (no other call).
2. `ElementAt(storedCenter)`; climb `Parent` up to `MaxAncestors` levels; an element whose RuntimeId equals `identity.RuntimeId` is the match.
3. Otherwise walk from `ReadWindow(identity.Window)` with an explicit stack, `Children` per element (call `CheckWindow` before each; not responding → `NotResponding`), depth 256, at most `MaxVisited` elements. A RuntimeId match returns at once. Count identity matches (same `ControlType`, `AutomationId`, trimmed `Name`; only when AutomationId or Name is non-empty) and stop at the second. Exactly one identity match and no cap hit (`capped`, depth, `MaxVisited`) → that element; else `Stale`.
4. Gate: `!IsEnabled` → `Disabled`. Off when `Bounds` is empty or intersects no monitor; when off or `IsOffscreen`, and `CanScrollIntoView`, call `ScrollIntoView` then `Refresh`. Still off → `OffScreen`.
5. Point: `ClickablePoint` when inside `Bounds` and on a monitor; else the centre of `Bounds ∩ windowBounds ∩ (the first monitor it intersects)`; an empty intersection → `OffScreen`.

`UiaElementSource` implements the seam with `ElementFromPointBuildCache` / `ControlView.GetParentElementBuildCache` / `ElementFromHandleBuildCache` / `UiaTreeSource.ReadSiblings` (same 300 cap) / `GetCachedPatternAs<IUIAutomationScrollItemPattern>` / `BuildUpdatedCache`, all with `ActionRequest`, and translates failures through `UiaJob.Run("UI Automation label lookup", ...)`; a dead element anywhere is `Stale`. `UiaLabelLocator.LocateAsync` runs one job with `Deadline`; `windowBounds` comes from `IWindowSystem.GetBounds`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~LabelLocatorTests"`
Expected: PASS; then the Desktop ones by hand (`-p:RunSettingsFilePath= --filter "FullyQualifiedName~LabelLocator"`).

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop windows/tests/DesktopMcp.Desktop.Tests/Actions
git commit -m "Re-find and gate label targets on the UIA worker" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Action targets and the shared desktop machine

Labels and points become `ActionTarget`s with upstream's texts and Decisions 1, 8, 15, 22 and 39. `DesktopMachine` makes Snapshot and the act tools share one UIA worker, one hang block list and one window catalog (spec §5: UIA jobs one at a time).

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/DesktopMachine.cs`, `windows/src/DesktopMcp.Desktop/Actions/ActionTargets.cs`
- Modify: `windows/src/DesktopMcp.Desktop/Snapshots/SnapshotService.cs` (`SnapshotDependencies.CreateDefault()` takes its parts from a `DesktopMachine`; public ctor `SnapshotService(DesktopMachine machine)`; the parameterless ctor uses `DesktopMachine.CreateDefault()`)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Actions/ActionTargetsTests.cs`

**Interfaces:**
- Consumes: `DesktopStateStore` (P2), `UiaLabelLocator` (Task 7), `MouseCoordinates.OutsideMessage` (Task 2), `ProcessIntegrity`, `InputBlockedException.ElevatedText` (Task 3), `IWindowSystem` (Task 5).
- Produces:
  - `public sealed class DesktopMachine { public static DesktopMachine CreateDefault(); }` (cheap: no thread, COM or runtime) with internal `Uia`, `Blocks`, `Windows`, `Desktops`, `Displays`, `Catalog`, `Tree`.
  - `public sealed record ActionTarget(ScreenPoint Point, long Window, string WindowName);`
  - `public interface IActionTargets { Task<ActionTarget> LabelAsync(long label, CancellationToken ct); ActionTarget Point(ScreenPoint point); void CheckLabels(IReadOnlyList<long> labels, string wrapper); void CheckForegroundInput(); }`
  - `public sealed class ActionTargets : IActionTargets` (public ctor `(DesktopMachine machine, DesktopStateStore store)`; internal ctor with seams `Func<long, ElementIdentity, ScreenPoint, CancellationToken, Task<LocateResult>> locate`, `IWindowSystem windows`, `Func<ScreenRect> virtualScreen`, `Func<int, bool> higherIntegrity`).
  - `public static class LabelErrors` with the exact texts below.

The texts (`label` is the number, `type` and `name` per Decisions 39, `window` the element's window name):
- `Desktop state is empty. Please call Snapshot first.` (`tools/input.py:28`)
- `Failed to find element with label {label}: Label {label} out of range` (`input.py:32`, `service.py:666-674`)
- `Label {label} is stale: {type} "{name}" is no longer in {window}. Call Snapshot again to get current labels.`
- `Label {label} ({type} "{name}") is disabled.`
- `Label {label} ({type} "{name}") is off screen and could not be scrolled into view. Call Snapshot again.`
- `{window} is not responding; no input was sent.`
- the bounds text `Coordinates ({x},{y}) are outside the desktop bounds x={left}..{right}, y={top}..{bottom}` (`service.py:717-720`)
- the elevated text of Task 3, for a point or the foreground window whose process has a higher integrity level.

- [ ] **Step 1: Write the failing tests**

```csharp
public class ActionTargetsTests
{
    [Fact]
    public async Task AnEmptyStoreAsksForASnapshot() =>
        Assert.Equal("Desktop state is empty. Please call Snapshot first.",
            (await Assert.ThrowsAsync<ArgumentException>(() => new TargetsRig(snapshot: null).Targets.LabelAsync(0, default))).Message);

    [Fact]
    public async Task LabelsCountInteractiveThenScrollable()
    {
        var rig = new TargetsRig(interactive: 2, scrollable: 1);
        Assert.Equal(rig.Scrollable[0].Center, (await rig.Targets.LabelAsync(2, default)).Point);
        Assert.Equal("Failed to find element with label 3: Label 3 out of range",
            (await Assert.ThrowsAsync<ArgumentException>(() => rig.Targets.LabelAsync(3, default))).Message);
        Assert.Equal("Failed to find element with label -1: Label -1 out of range",
            (await Assert.ThrowsAsync<ArgumentException>(() => rig.Targets.LabelAsync(-1, default))).Message);
    }

    [Fact]
    public async Task TheLocatedPointIsReported()
    {
        var rig = new TargetsRig(interactive: 1, locate: new LocateResult(null, new ScreenPoint(7, 8), null));
        Assert.Equal(new ActionTarget(new(7, 8), rig.Interactive[0].Identity!.Window, "Notepad"), await rig.Targets.LabelAsync(0, default));
    }

    [Theory]
    [InlineData(LocateFailure.Stale, "Label 0 is stale: button \"OK\" is no longer in Notepad. Call Snapshot again to get current labels.")]
    [InlineData(LocateFailure.Disabled, "Label 0 (button \"OK\") is disabled.")]
    [InlineData(LocateFailure.OffScreen, "Label 0 (button \"OK\") is off screen and could not be scrolled into view. Call Snapshot again.")]
    [InlineData(LocateFailure.NotResponding, "Notepad is not responding; no input was sent.")]
    public async Task LocateFailuresHaveSpecificTexts(LocateFailure failure, string text)
    {
        var rig = new TargetsRig(interactive: 1, locate: new LocateResult(null, default, failure));
        Assert.Equal(text, (await Assert.ThrowsAsync<DesktopException>(() => rig.Targets.LabelAsync(0, default))).Message);
    }

    [Fact]
    public async Task AWordLabelUsesTheStoredCentreWithoutALookup()
    {
        var rig = new TargetsRig(interactive: 1, identity: false);
        Assert.Equal(rig.Interactive[0].Center, (await rig.Targets.LabelAsync(0, default)).Point);
        Assert.Equal(0, rig.Locates);
    }

    [Fact]
    public void PointsOutsideTheDesktopAreRejected() =>
        Assert.Equal("Coordinates (5000,5) are outside the desktop bounds x=0..1919, y=0..1079",
            Assert.Throws<ArgumentException>(() => new TargetsRig().Targets.Point(new(5000, 5))).Message);

    [Fact]
    public void AnElevatedWindowUnderThePointIsBlockedBeforeInput() =>
        Assert.StartsWith("Input blocked: the target window (Taskmgr.exe) runs elevated",
            Assert.Throws<InputBlockedException>(() => new TargetsRig(elevatedAtPoint: true).Targets.Point(new(5, 5))).Message);

    [Fact]
    public void CheckLabelsUsesTheCallersWrapper() =>
        Assert.Equal("Failed to resolve labels [0, 9]: Label 9 out of range",
            Assert.Throws<ArgumentException>(() => new TargetsRig(interactive: 1).Targets.CheckLabels([0, 9], "Failed to resolve labels [0, 9]")).Message);
}
```

`TargetsRig` builds a `DesktopSnapshot` in a `DesktopStateStore` (its `Replace` is internal; the test project already has InternalsVisibleTo) with a window "Notepad", `button "OK"` elements carrying identities, a 1920x1080 virtual screen and a counted fake `locate`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`LabelAsync`: store empty → the empty text (`ArgumentException`); index into `Interactive` then `Scrollable` (`service.py:663-675`); an element without `Identity` → `Point(element.Center)`; else `locate(...)`, a failure → `DesktopException` with its text, else `Point(result.Point)` with the window from the identity. `Point`: the bounds check, then the window under the point (`IWindowSystem.WindowFromPoint`) and its integrity check, then `ActionTarget(point, window, title)`. `CheckForegroundInput`: the integrity check for the foreground window (Shortcut). `CheckLabels`: range-check every label and throw `<wrapper>: Label <n> out of range` (`tools/multi.py:47-50`).

- [ ] **Step 4: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: PASS (the Snapshot tests still pass with the shared machine).

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop windows/tests/DesktopMcp.Desktop.Tests/Actions
git commit -m "Resolve labels and points into action targets over a shared desktop machine" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Click, Move and Scroll tools

The first three tools, the services they share (`ToolServices`), the input lock (Decisions 18) and the shutdown release (Decisions 19).

**Files:**
- Create: `windows/src/DesktopMcp.Server/Tools/ClickTool.cs`, `MoveTool.cs`, `ScrollTool.cs`, `DesktopInputLock.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolServices.cs`, `ToolRegistry.cs`, `windows/src/DesktopMcp.Server/Hosting/StdioServer.cs`
- Test: `windows/tests/DesktopMcp.Server.Tests/Tools/FakeDesktop.cs`, `ClickToolTests.cs`, `MoveToolTests.cs`, `ScrollToolTests.cs`

**Interfaces:**
- Consumes: `IDesktopInput` (Task 4), `IActionTargets` (Task 8), `ActArguments` (Task 2), `InputLedger` (Task 3).
- Produces:
  - `ToolServices` gains `required DesktopMachine Machine`, `IDesktopInput Input`, `IActionTargets Targets`, `IForeground Foreground`, `InputLedger Ledger`, `DesktopInputLock InputLock`, `TimeProvider Time` (later tasks add `ReadBack`, `Apps`, `WaitConditions`). `CreateDefault()` builds one `DesktopMachine` and passes it to `SnapshotService` and `ActionTargets`.
  - `public sealed class DesktopInputLock { public Task<IDisposable> EnterAsync(CancellationToken ct); }`
  - `FakeDesktop` (tests): `FakeInput : IDesktopInput` (records calls as strings, optional gate `TaskCompletionSource` to block the first call), `FakeTargets : IActionTargets` (labels map to points; errors on demand), `FakeDesktop.Services()` returning `ToolServices.CreateDefault() with { Input = ..., Targets = ..., Foreground = ..., ... }`.

Texts (upstream unless noted): Click `Hover|Single|Double {button} clicked at ({x},{y}).`, `Clicked {button} {n} times at ({x},{y}).` (Decisions 17), `Either loc or label must be provided.`, `Location must be a list of exactly 2 integers [x, y]`, `clicks must be 0 or more`; Move `Moved the mouse pointer to ({x},{y}).`, `Dragged from ({sx},{sy}) to ({x},{y}).`, `Dragged from ({sx},{sy}) to ({x},{y}) over {d:.3f} seconds.`, `loc must be a list of exactly 2 integers [x, y]`, `from_loc must be a list of exactly 2 integers [x, y]`, `from_loc and duration require drag=True`, `drag must be true or false`; Scroll `Scrolled {type} {direction} by {n} wheel times at ({x},{y}).` and the cursor variant (Decisions 37).

- [ ] **Step 1: Write the failing tests**

```csharp
public class ClickToolTests
{
    [Theory]
    [InlineData("""{"loc": [5, 6]}""", "Single left clicked at (5,6).")]
    [InlineData("""{"loc": "[5, 6]", "button": "right", "clicks": 2}""", "Double right clicked at (5,6).")]
    [InlineData("""{"loc": [5, 6], "clicks": 0}""", "Hover left clicked at (5,6).")]
    [InlineData("""{"loc": [5, 6], "clicks": 3}""", "Clicked left 3 times at (5,6).")]
    public async Task ClickTexts(string json, string text) =>
        Assert.Equal(text, ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Click", FakeDesktop.Services()), json)));

    [Fact]
    public async Task ALabelWinsOverLocAndReportsTheLocatedPoint()
    {
        var desktop = new FakeDesktop { Labels = { [4] = new ScreenPoint(70, 80) } };
        var text = ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Click", desktop.Services()), """{"loc": [1, 1], "label": 4}"""));
        Assert.Equal("Single left clicked at (70,80).", text);
        Assert.Equal(["Click (70,80) Left 1"], desktop.Input.Calls);
    }

    [Theory]
    [InlineData("{}", "Either loc or label must be provided.")]
    [InlineData("""{"loc": [1]}""", "Location must be a list of exactly 2 integers [x, y]")]
    [InlineData("""{"loc": [1, 2], "clicks": -1}""", "clicks must be 0 or more")]
    [InlineData("""{"loc": [1, 2], "button": "top"}""", "button must be one of: left, right, middle")]
    public async Task ClickErrorsAreToolErrorsAndSendNothing(string json, string message)
    {
        var desktop = new FakeDesktop();
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Click", desktop.Services()), json);
        Assert.True(result.IsError);
        Assert.Equal($"Error calling tool 'Click': {message}", ToolCalls.Text(result));
        Assert.Empty(desktop.Input.Calls);
    }

    [Fact]
    public async Task InputToolsRunOneAtATime()
    {
        var desktop = new FakeDesktop { BlockFirstCall = true };
        var services = desktop.Services();
        var first = ToolCalls.CallAsync(ToolCalls.Create("Click", services), """{"loc": [1, 1]}""");
        await desktop.FirstCallStarted;
        var second = ToolCalls.CallAsync(ToolCalls.Create("Scroll", services), """{"loc": [2, 2]}""");
        await Task.Delay(100);
        Assert.Single(desktop.Input.Calls);
        desktop.ReleaseFirstCall();
        await Task.WhenAll(first, second);
        Assert.Equal(2, desktop.Input.Calls.Count);
    }
}

public class MoveToolTests
{
    [Theory]
    [InlineData("""{"loc": [5, 6]}""", "Moved the mouse pointer to (5,6).")]
    [InlineData("""{"loc": [5, 6], "drag": "true"}""", "Dragged from (1,2) to (5,6).")]
    [InlineData("""{"loc": [5, 6], "drag": true, "from_loc": [0, 0], "duration": "0.25"}""", "Dragged from (0,0) to (5,6) over 0.250 seconds.")]
    public async Task MoveTexts(string json, string text) =>
        Assert.Equal(text, ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Move", new FakeDesktop { Cursor = new(1, 2) }.Services()), json)));

    [Theory]
    [InlineData("""{"loc": [5, 6], "duration": 1}""", "from_loc and duration require drag=True")]
    [InlineData("""{"loc": [5, 6], "drag": true, "from_loc": [1]}""", "from_loc must be a list of exactly 2 integers [x, y]")]
    [InlineData("""{"loc": [5, 6], "drag": "yes"}""", "drag must be true or false")]
    [InlineData("""{"loc": [5, 6], "drag": true, "duration": 11}""", "duration must be between 0 and 10 seconds")]
    public async Task MoveErrors(string json, string message) =>
        Assert.Equal($"Error calling tool 'Move': {message}", ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("Move", new FakeDesktop().Services()), json)));
}

public class ScrollToolTests
{
    [Fact]
    public async Task WithoutALocationTheCursorIsReported() =>
        Assert.Equal("Scrolled vertical down by 1 wheel times at current cursor position (1,2).",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Scroll", new FakeDesktop { Cursor = new(1, 2) }.Services()), "{}")));

    [Fact]
    public async Task WithALocation() =>
        Assert.Equal("Scrolled horizontal left by 3 wheel times at (5,6).",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Scroll", new FakeDesktop().Services()), """{"loc": [5, 6], "type": "horizontal", "direction": "left", "wheel_times": 3}""")));

    [Fact]
    public async Task AnInvalidCombinationIsANormalResult() =>
        Assert.Equal("Invalid direction. Use \"up\" or \"down\".",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Scroll", new FakeDesktop { ScrollResult = "Invalid direction. Use \"up\" or \"down\"." }.Services()), """{"direction": "left"}""")));
}
```

Plus `StdioServerTests.StoppingReleasesHeldInput`: a server run over in-memory streams with `ToolServices` whose ledger holds `key:ctrl` (a fake down/up); after the host stops, `Pending` is empty.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Server.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

Each tool: parse arguments in upstream's order (`tools/input.py:245-254, 319-333, 362-404`), resolve the target (`Targets.LabelAsync` or `Targets.Point`), take `InputLock`, call `Input`, format with `CultureInfo.InvariantCulture` (`PyText.Fixed(d, 3)` for the duration). Move with `drag` checks both ends with `Targets.Point`. Scroll without a point reads `Input.Cursor` before scrolling. `ToolRegistry` maps the three names. `StdioServer.RunAsync` registers `ApplicationStopping` to call `services.Ledger.ReleaseAll()` (errors logged, not thrown); the `ToolServices` instance it builds is the one the tools use.

- [ ] **Step 4: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: PASS (`ContractTests` unchanged).

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Server.Tests
git commit -m "Add the Click, Move and Scroll tools behind one input lock" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Type and Shortcut tools with read-back

Type with the foreground check (Decisions 7) and read-back (Decisions 4); Shortcut with parse-first (Decisions 16) and the UIPI check of the foreground window (Decisions 8).

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Actions/TypeReadBack.cs`, `windows/src/DesktopMcp.Server/Tools/TypeTool.cs`, `ShortcutTool.cs`
- Modify: `ToolServices.cs` (`ITypeReadBack ReadBack`), `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Actions/TypeReadBackTests.cs`, `windows/tests/DesktopMcp.Server.Tests/Tools/TypeToolTests.cs`, `ShortcutToolTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 4, 5, 8; `UiaWorker`, `UiaSession.ActionRequest` (Task 7).
- Produces:
  - `public interface ITypeReadBack { Task<string?> ReadFocusedValueAsync(CancellationToken ct); }` (null: skipped or unreadable; one UIA job, 2 s: `GetFocusedElementBuildCache(ActionRequest)`; null for `IsPassword`, no ValuePattern, a failure or the deadline)
  - `public static class TypeReadBack { public static string? Warning(string typed, string? value); public static async Task<string?> CheckAsync(ITypeReadBack reader, string typed, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct); }` (`CheckAsync` reads up to 3 times 150 ms apart and returns the warning or null)
  - Warning text: `Warning: the focused element's value does not contain the typed text; it reads {PyText.Repr(value)}.` where a value longer than 200 code points is cut to its first 200 plus `...` before `Repr`.

Type texts: `Typed {text} at ({x},{y}).` (`input.py:297`), then `\n` and the warning when there is one; `clear must be true or false`, `press_enter must be true or false`, `caret_position must be one of: start, idle, end`, `Missing required argument: text`. Shortcut: `Pressed {shortcut}.` as `ToolResults.Content`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class TypeReadBackTests
{
    [Theory]
    [InlineData("hello", "say hello", null)]
    [InlineData("a\r\nb", "a\nb", null)]
    [InlineData("hello", null, null)]
    [InlineData("hello", "helo", "Warning: the focused element's value does not contain the typed text; it reads 'helo'.")]
    public void WarningOnlyOnMismatch(string typed, string? value, string? warning) =>
        Assert.Equal(warning, TypeReadBack.Warning(typed, value));

    [Fact]
    public void LongValuesAreCut() =>
        Assert.EndsWith("...'.", TypeReadBack.Warning("x", new string('y', 300)));

    [Fact]
    public async Task ChecksUpToThreeTimes()
    {
        var reads = new Queue<string?>(["", "hel", "hello"]);
        var reader = new FakeReader(() => reads.Dequeue());
        Assert.Null(await TypeReadBack.CheckAsync(reader, "hello", (_, _) => Task.CompletedTask, default));
        Assert.Equal(3, reader.Reads);
    }
}

public class TypeToolTests
{
    [Fact]
    public async Task MatchingReadBackKeepsUpstreamsText()
    {
        var desktop = new FakeDesktop { ReadBack = "hi there" };
        Assert.Equal("Typed hi there at (5,6).", ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "hi there", "loc": [5, 6]}""")));
    }

    [Fact]
    public async Task AMismatchAppendsOneWarningLine()
    {
        var desktop = new FakeDesktop { ReadBack = "hi thre" };
        Assert.Equal("Typed hi there at (5,6).\nWarning: the focused element's value does not contain the typed text; it reads 'hi thre'.",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "hi there", "loc": [5, 6]}""")));
    }

    [Fact]
    public async Task PressEnterSkipsTheReadBack()
    {
        var desktop = new FakeDesktop { ReadBack = "" };
        ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "q", "loc": [5, 6], "press_enter": "true"}"""));
        Assert.Equal(0, desktop.ReadBackCalls);
    }

    [Fact]
    public async Task TheForegroundIsCheckedAfterTheClickAndBeforeTheKeys()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(5, 5) }, ForegroundFails = "Could not bring Notepad to the foreground; nothing was typed." };
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "x", "label": 0}""");
        Assert.Equal("Error calling tool 'Type': Could not bring Notepad to the foreground; nothing was typed.", ToolCalls.Text(result));
        Assert.Equal(["Type (5,5) click-only"], desktop.Input.Calls); // the fake stops at the after-click hook
    }

    [Theory]
    [InlineData("""{"text": "x", "loc": [1, 2], "clear": "yes"}""", "clear must be true or false")]
    [InlineData("""{"loc": [1, 2]}""", "Missing required argument: text")]
    [InlineData("""{"text": "x"}""", "Either loc or label must be provided.")]
    public async Task TypeErrors(string json, string message) =>
        Assert.Equal($"Error calling tool 'Type': {message}", ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("Type", new FakeDesktop().Services()), json)));
}

public class ShortcutToolTests
{
    [Fact]
    public async Task PressedTextWithoutStructuredContent()
    {
        var desktop = new FakeDesktop();
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Shortcut", desktop.Services()), """{"shortcut": "ctrl+c"}""");
        Assert.Equal("Pressed ctrl+c.", ToolCalls.OkText(result));
        Assert.Null(result.StructuredContent);
        Assert.Equal(["Keys 4 10ms 500ms"], desktop.Input.Calls);
    }

    [Fact]
    public async Task AParseErrorSendsNothing()
    {
        var desktop = new FakeDesktop();
        Assert.Equal("Error calling tool 'Shortcut': Invalid shortcut 'ctrl+': empty key name",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("Shortcut", desktop.Services()), """{"shortcut": "ctrl+"}""")));
        Assert.Empty(desktop.Input.Calls);
    }
}
```

Add one Desktop test `TypeToolTypesIntoTheTestWindowAndReadsItBack`: `ActiveTestWindow`, the real `DesktopInput` over `GuardedInjector`, real `ForegroundService` and `UiaTypeReadBack`; Type into the edit's centre with `clear: true`; the result has no warning line and `EditText` matches.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Server.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

TypeTool (`tools/input.py:279-297`): parse `text`, `loc` (JSON), `clear` and `press_enter` (strict), `caret_position`; target; lock; `Input.TypeAsync(target.Point, ..., afterClick: ct => Foreground.EnsureKeyboardTargetAsync(target.Window, target.WindowName, ct))`; then, unless `press_enter` or `text == ""`, `TypeReadBack.CheckAsync`. ShortcutTool (`input.py:418-420`): `ShortcutParser.Parse` first, then lock, `Targets.CheckForegroundInput()`, `Input.PressKeysAsync(tokens, InputTiming.ShortcutInterval, InputTiming.OperationWait)`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src windows/tests
git commit -m "Add the Type and Shortcut tools with the foreground check and read-back" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: MultiSelect and MultiEdit tools

`tools/multi.py` with Decisions 23 (labels located per item, progress suffix) and Decisions 4 for each MultiEdit item.

**Files:**
- Create: `windows/src/DesktopMcp.Server/Tools/MultiSelectTool.cs`, `MultiEditTool.cs`
- Modify: `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Server.Tests/Tools/MultiToolTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 4, 5, 8, 10.
- Produces: the two tools.

Texts: `Multi-selected elements at:\n(x,y)\n(x,y)` (`multi.py:54-55`); `Multi-edited elements at: (x,y) with text 'a', (x,y) with text 'b'` (`multi.py:103-104`, the text raw inside the quotes, as upstream's f-string); `Either locs or labels must be provided.`; label range errors wrapped as `Failed to resolve labels [1, 9]: Label 9 out of range` (MultiSelect, the list as Python prints it) and `Failed to process labels: Label 9 out of range` (MultiEdit); `press_ctrl must be true or false`; a located failure mid-way gets ` Stopped after <k> of <n> items.` when k > 0. Each MultiEdit item is Type with `clear: true`, caret idle, no Enter, with the foreground check and read-back; a mismatch adds a line `Warning: the focused element's value at (x,y) does not contain the typed text; it reads '...'.`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MultiToolTests
{
    [Fact]
    public async Task LocsComeBeforeLabels()
    {
        var desktop = new FakeDesktop { Labels = { [3] = new ScreenPoint(30, 30) } };
        Assert.Equal("Multi-selected elements at:\n(1,2)\n(30,30)",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("MultiSelect", desktop.Services()), """{"locs": [[1, 2]], "labels": "[3]", "press_ctrl": "false"}""")));
        Assert.Equal(["MultiSelect ctrl=False 2"], desktop.Input.Calls);
    }

    [Fact]
    public async Task OutOfRangeLabelsFailBeforeAnyInput()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(1, 1) } };
        Assert.Equal("Error calling tool 'MultiSelect': Failed to resolve labels [0, 9]: Label 9 out of range",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("MultiSelect", desktop.Services()), """{"labels": [0, 9]}""")));
        Assert.Empty(desktop.Input.Calls);
    }

    [Fact]
    public async Task AStaleLabelMidwayNamesTheProgress()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(1, 1), [1] = new ScreenPoint(2, 2) }, StaleLabels = { 1 } };
        Assert.Equal("Error calling tool 'MultiSelect': Label 1 is stale: button \"OK\" is no longer in Notepad. Call Snapshot again to get current labels. Stopped after 1 of 2 items.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("MultiSelect", desktop.Services()), """{"labels": [0, 1]}""")));
    }

    [Fact]
    public async Task MultiEditTypesEachItemWithClear()
    {
        var desktop = new FakeDesktop { Labels = { [2] = new ScreenPoint(9, 9) }, ReadBackEchoesTypedText = true };
        Assert.Equal("Multi-edited elements at: (1,2) with text 'a', (9,9) with text 'b'",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("MultiEdit", desktop.Services()), """{"locs": [[1, 2, "a"]], "labels": [[2, "b"]]}""")));
        Assert.Equal(["Type (1,2) clear a", "Type (9,9) clear b"], desktop.Input.Calls);
    }

    [Fact]
    public async Task MultiEditWarnsPerMismatchingItem()
    {
        var desktop = new FakeDesktop { ReadBack = "zz" };
        Assert.Equal("Multi-edited elements at: (1,2) with text 'a'\nWarning: the focused element's value at (1,2) does not contain the typed text; it reads 'zz'.",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("MultiEdit", desktop.Services()), """{"locs": [[1, 2, "a"]]}""")));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Server.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

MultiSelect: parse `locs`/`labels` (`ActArguments.PointList`/`LabelList`), both null → error; `press_ctrl` strict, default true; `Targets.CheckLabels(labels, $"Failed to resolve labels {PythonList(labels)}")` before the lock; each loc through `Targets.Point`; `Input.MultiSelectAsync(pressCtrl, count, target)` where `target(i)` returns the loc points first, then `Targets.LabelAsync(labels[i - locCount])`. MultiEdit: `EditLocs`/`EditLabels`, `CheckLabels(labels, "Failed to process labels")`, then per item the Type sequence of Task 10 with `clear: true`. Wrap a failure after k > 0 items with the suffix.

- [ ] **Step 4: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Server.Tests
git commit -m "Add the MultiSelect and MultiEdit tools" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Fuzzy WRatio, App switch and resize

`Fuzzy.WRatio`/`ExtractOne` (Decisions 30), the App tool's argument rules (`tools/app.py:203-217`, Decisions 35), and the `switch` and `resize` modes (`desktop/service.py:415-476, 559-584`; Decisions 24, 29, 32, 33). The launch modes call `IAppService` methods that Task 13 implements.

**Files:**
- Modify: `windows/src/DesktopMcp.Core/Text/Fuzzy.cs`, `windows/src/DesktopMcp.Desktop/Tree/WindowCatalog.cs` (`ScanActiveAsync`)
- Create: `windows/src/DesktopMcp.Desktop/Apps/AppService.cs`, `windows/src/DesktopMcp.Server/Tools/AppTool.cs`
- Modify: `ToolServices.cs` (`IAppService Apps`), `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Text/FuzzyWRatioTests.cs`, `windows/tests/DesktopMcp.Desktop.Tests/Apps/AppServiceTests.cs`, `windows/tests/DesktopMcp.Server.Tests/Tools/AppToolTests.cs`

**Interfaces:**
- Consumes: `IWindowScanner` (P2), `IForeground`, `WindowPlacement` (Task 5), `TreeConfig.Title` (P2, Python `str.title()`).
- Produces:
  - `Fuzzy.WRatio(string s1, string s2) : int`, `Fuzzy.ExtractOne(string query, IReadOnlyList<string> choices, int cutoff) : (string Choice, int Score)?`
  - `IWindowScanner.ScanActiveAsync(CancellationToken) : Task<WindowInfo?>` (the foreground window as `ScanAsync` would report it, without probing the others)
  - `public sealed record AppResult(string Text, bool IsError)`
  - `public interface IAppService { Task<AppResult> SwitchAsync(string name, CancellationToken ct); Task<AppResult> ResizeAsync(string? name, (int X, int Y)? loc, (int W, int H)? size, CancellationToken ct); Task<AppResult> LaunchAsync(string name, CancellationToken ct); Task<AppResult> LaunchExecutableAsync(string executable, IReadOnlyList<string> args, string? cwd, CancellationToken ct); }`
  - `public sealed class AppService : IAppService` (public ctor `(DesktopMachine machine, IForeground foreground)`; internal ctor with `IWindowScanner`, `IForeground`, `WindowPlacement`, and the Task 13 seams).

`WRatio` (fuzzywuzzy 0.18 `fuzz.WRatio` with Decisions 30): `p = Process(s)` (lower-case; every char that is not a letter, digit or `_` becomes a space; trim); an empty side → 0. `base = Ratio(p1, p2)` (`200 * LCS / (len1 + len2)`); `lenRatio = max(len) / min(len)`; `partialScale = lenRatio > 8 ? 0.6 : 0.9`. If `lenRatio < 1.5`: `max(base, TokenSort(p1, p2) * 0.95, TokenSet(p1, p2) * 0.95)`; else `max(base, PartialRatio(p1, p2) * partialScale, PartialTokenSort * 0.95 * partialScale, PartialTokenSet * 0.95 * partialScale)`; round half to even. `TokenSort` sorts the space-split tokens and joins them with one space; `TokenSet` is fuzzywuzzy's: `sorted intersection` (I), `I + " " + sorted(diff1)`, `I + " " + sorted(diff2)` (each stripped), the max of the three pairwise ratios; the partial variants use `PartialRatio` instead of `Ratio`. `ExtractOne` processes the query and each choice, keeps the first highest score, and returns it when `>= cutoff`.

App texts: `executable, args, and cwd require mode="launch_executable"`; `executable is required for mode="launch_executable"`; `name, window_loc, and window_size are not supported for mode="launch_executable"`; `name is required for mode="launch"`; `name is required for mode="switch"`; `mode must be one of: launch, launch_executable, resize, switch`; switch: `Application {Title(name)} not found.`, `No windows found on the desktop.`, `Failed to bring {window.name} to the foreground within 1 second.`, `Restored {Title(window.name)} from minimized and switched to it.`, `Switched to {Title(window.name)} window.`, `Error switching app: {message}`; resize: `Application {Title(name)} not found.`, `No active window found`, `{name} is minimized`, `{name} is maximized`, `{name} resized to {w}x{h} at {x},{y}.`, `{name} is not responding`, `Failed to resize {name}: {message}`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class FuzzyWRatioTests
{
    [Theory]
    [InlineData("notepad", "Notepad", 100)]
    [InlineData("note", "notepad", 90)]               // lenRatio 1.75: partial 100 * 0.9
    [InlineData("Microsoft Edge", "Edge Microsoft", 95)] // token sort 100 * 0.95
    [InlineData("메모장", "메모장", 100)]               // non-ASCII is kept (Decisions 30)
    [InlineData("", "x", 0)]
    [InlineData("!!", "x", 0)]
    public void Scores(string a, string b, int score) => Assert.Equal(score, Fuzzy.WRatio(a, b));

    [Fact]
    public void ExtractOneKeepsTheFirstOfEqualScores() =>
        Assert.Equal(("Google Chrome", 90), Fuzzy.ExtractOne("chrome", ["Google Chrome", "Chrome Remote Desktop"], 70));

    [Fact]
    public void ExtractOneHonoursTheCutoff() => Assert.Null(Fuzzy.ExtractOne("calculator", ["Paint"], 70));
}

public class AppServiceTests
{
    [Fact]
    public async Task SwitchUsesUpstreamsTexts()
    {
        var rig = new AppRig(windows: [("Untitled - Notepad", 10, minimized: true)], switchSucceeds: true);
        Assert.Equal(new AppResult("Restored Untitled - Notepad from minimized and switched to it.", false), await rig.Service.SwitchAsync("notepad", default));
        Assert.Equal(new AppResult("Application Calculatorx not found.", true), await rig.Service.SwitchAsync("calculatorx", default));
    }

    [Fact]
    public async Task SwitchFailureIsAnError() =>
        Assert.Equal(new AppResult("Failed to bring Untitled - Notepad to the foreground within 1 second.", true),
            await new AppRig(windows: [("Untitled - Notepad", 10, false)], switchSucceeds: false).Service.SwitchAsync("notepad", default));

    [Fact]
    public async Task DuplicateNamesPickTheFirstWindow()
    {
        var rig = new AppRig(windows: [("Notepad", 10, false), ("Notepad", 11, false)], switchSucceeds: true);
        await rig.Service.SwitchAsync("notepad", default);
        Assert.Equal(10, rig.SwitchedTo);
    }

    [Fact]
    public async Task ResizeKeepsMissingValuesFromTheWindow()
    {
        var rig = new AppRig(windows: [("Notepad", 10, false)], bounds: new ScreenRect(100, 50, 900, 650));
        Assert.Equal(new AppResult("Notepad resized to 800x600 at 0,0.", false), await rig.Service.ResizeAsync("notepad", (0, 0), null, default));
        Assert.Equal(new ScreenRect(0, 0, 800, 600), rig.ResizedTo);
    }

    [Fact]
    public async Task ResizeWithoutANameUsesTheCurrentForegroundWindow()
    {
        var rig = new AppRig(windows: [], active: ("Paint", 20, false), bounds: new ScreenRect(0, 0, 10, 10));
        Assert.Equal(new AppResult("Paint resized to 300x200 at 0,0.", false), await rig.Service.ResizeAsync(null, null, (300, 200), default));
        Assert.Equal(new AppResult("No active window found", false), await new AppRig(windows: []).Service.ResizeAsync(null, null, null, default));
    }

    [Fact]
    public async Task ResizeRefusesMaximisedWindows() =>
        Assert.Equal(new AppResult("Notepad is maximized", false),
            await new AppRig(windows: [("Notepad", 10, false)], maximized: true).Service.ResizeAsync("notepad", null, (1, 1), default));
}

public class AppToolTests
{
    [Theory]
    [InlineData("""{"mode": "switch", "executable": "a.exe"}""", "executable, args, and cwd require mode=\"launch_executable\"")]
    [InlineData("""{"mode": "launch_executable"}""", "executable is required for mode=\"launch_executable\"")]
    [InlineData("""{"mode": "launch_executable", "executable": "a.exe", "name": "x"}""", "name, window_loc, and window_size are not supported for mode=\"launch_executable\"")]
    [InlineData("""{"mode": "switch"}""", "name is required for mode=\"switch\"")]
    [InlineData("""{"mode": "resize", "window_size": [1]}""", "window_size must be a list of exactly 2 integers")]
    [InlineData("""{"mode": "move"}""", "mode must be one of: launch, launch_executable, resize, switch")]
    public async Task ArgumentErrors(string json, string message) =>
        Assert.Equal($"Error calling tool 'App': {message}", ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("App", new FakeDesktop().Services()), json)));

    [Fact]
    public async Task ResultsHaveNoStructuredContentAndSwitchFailuresAreErrors()
    {
        var desktop = new FakeDesktop { AppResult = new AppResult("Application X not found.", true) };
        var result = await ToolCalls.CallAsync(ToolCalls.Create("App", desktop.Services()), """{"mode": "switch", "name": "x"}""");
        Assert.Equal("Error calling tool 'App': Application X not found.", ToolCalls.Text(result));
        desktop.AppResult = new AppResult("Notepad is minimized", false);
        var resize = await ToolCalls.CallAsync(ToolCalls.Create("App", desktop.Services()), """{"mode": "resize"}""");
        Assert.Equal("Notepad is minimized", ToolCalls.OkText(resize));
        Assert.Null(resize.StructuredContent);
    }
}
```

`AppRig` fakes `IWindowScanner` (scan and active), `IForeground` (`SwitchToAsync` result, records the handle) and a resize seam (records the rectangle).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build DesktopMcp.slnx`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`AppService.SwitchAsync`: `ScanAsync`; list = active then windows (dedupe by handle, keep first); names → first window per name; `ExtractOne(name, names, 70)`; `IForeground.SwitchToAsync` (Task 5); texts above. `ResizeAsync`: name → scan and find; no name → `ScanActiveAsync`; minimized/maximized from `WindowInfo.Status`; loc/size default to `WindowInfo.Bounds` left/top and width/height; `WindowPlacement.ResizeAsync` (a `DesktopException` becomes its message as a normal result). `AppTool`: the argument rules above (in upstream's order), `InputLock` for every mode (spec §7 counts all four as act modes), `AppResult.IsError` → throw `InvalidOperationException(text)` so `SpecTool` makes it `isError`, else `ToolResults.Content(text, null)`. `LaunchAsync`/`LaunchExecutableAsync` exist on the interface; until Task 13 `AppService` throws `NotSupportedException("not available in this build yet")` for them.

- [ ] **Step 4: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src windows/tests
git commit -m "Add fuzzy WRatio and the App tool's switch and resize modes" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: App launch and launch_executable

Decisions 31 and 34, with Open question 1's ruling (launched apps die with the server; read it first — it overrides this task's text where they differ: no ProcessJob change, no breakaway, `ShellExecuteEx` for non-packaged apps).

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Apps/AppCatalog.cs`, `AppLauncher.cs`, `windows/src/DesktopMcp.Core/Text/PyJson.cs`, `CommandLine.cs`
- Modify: `windows/src/DesktopMcp.Desktop/Apps/AppService.cs`, `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (`SHCreateItemFromParsingName`, `IShellItem`, `IEnumShellItems`, `BHID_EnumItems`, `SIGDN`, `IApplicationActivationManager`, `ApplicationActivationManager`, `ACTIVATEOPTIONS`, `CreateProcess`, `STARTUPINFOEXW`, `InitializeProcThreadAttributeList`, `UpdateProcThreadAttribute`, `DeleteProcThreadAttributeList`, `PROCESS_CREATION_FLAGS`, `CreateFile`, `GetFinalPathNameByHandle`, `CoTaskMemFree`), `ShellExecuteEx` and `SHELLEXECUTEINFOW` in the Desktop `NativeMethods.txt`
- Test: `windows/tests/DesktopMcp.Core.Tests/Text/PyJsonTests.cs`, `CommandLineTests.cs`, `windows/tests/DesktopMcp.Desktop.Tests/Apps/AppLauncherTests.cs`

**Interfaces:**
- Consumes: Task 12's `IAppService`, `Fuzzy.ExtractOne`, `StaThread` (P1), `IWindowSystem` (Task 5).
- Produces:
  - `public static class PyJson { public static string Dumps(IReadOnlyList<KeyValuePair<string, object?>> fields); }` (values: `long`, `string`, `IReadOnlyList<string>`, `null`; Python's `json.dumps(indent=2)` layout with `ensure_ascii`)
  - `public static class CommandLine { public static string Join(IEnumerable<string> args); }` (`subprocess.list2cmdline`)
  - `internal sealed record AppEntry(string Name, string ParsingName)`; `internal interface IAppCatalog { Task<IReadOnlyList<AppEntry>> ListAsync(CancellationToken ct); }`; `AppCatalog` (STA, 10 s)
  - `internal interface IAppLauncher { int ActivatePackaged(string appUserModelId); int? StartFromShell(string parsingName); int StartExecutable(string commandLine, string? cwd); }`; `AppLauncher` (the real one)
  - `internal static bool AppLauncher.IsPackaged(string parsingName)` (`^[^\\!]+![^\\!]+$`)

Texts: `{Title(name)} not found in start menu.`, `{Title(name)} launched.`, `Launching {Title(name)} sent, but window not detected yet.`, `Failed to launch {Title(name)}: {message}`, `Executable does not exist: {path}`, `Working directory does not exist: {path}`, `args must be a list of strings` (P1 `Coercion.StringList`).

- [ ] **Step 1: Write the failing tests**

```csharp
public class PyJsonTests
{
    [Fact]
    public void MatchesPythonsIndentedDump() =>
        Assert.Equal("{\n  \"pid\": 1234,\n  \"executable\": \"C:\\\\Tools\\\\caf\\u00e9.exe\",\n  \"args\": [\n    \"a b\"\n  ],\n  \"cwd\": null\n}",
            PyJson.Dumps([new("pid", 1234L), new("executable", @"C:\Tools\café.exe"), new("args", (IReadOnlyList<string>)["a b"]), new("cwd", null)]));

    [Fact]
    public void AnEmptyListIsBrackets() =>
        Assert.Equal("{\n  \"args\": []\n}", PyJson.Dumps([new("args", (IReadOnlyList<string>)[])]));
}

public class CommandLineTests
{
    [Fact]
    public void FollowsList2Cmdline() =>
        Assert.Equal("\"C:\\a b\\x.exe\" a\\\"b c\\ \"\" \"d e\\\\\"", CommandLine.Join([@"C:\a b\x.exe", "a\"b", @"c\", "", @"d e\"]));
}
```

In `AppLauncherTests` (`AppService` with fake catalog, launcher, window system and clock):

```csharp
[Fact]
public async Task PackagedAppsWaitForAWindowOfTheirProcess()
{
    var rig = new LaunchRig(apps: [new("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")], pid: 77, windowOfPidAfter: TimeSpan.FromSeconds(1));
    Assert.Equal(new AppResult("Calculator launched.", false), await rig.Service.LaunchAsync("calculator", default));
    Assert.Equal(["ActivatePackaged Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"], rig.Launcher.Calls);
}

[Fact]
public async Task Win32EntriesGoThroughTheShellAndWaitForATitle()
{
    var rig = new LaunchRig(apps: [new("Notepad", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe")], titleAfter: ("Untitled - Notepad", TimeSpan.FromSeconds(2)));
    Assert.Equal(new AppResult("Notepad launched.", false), await rig.Service.LaunchAsync("notepad", default));
    Assert.Equal([@"StartFromShell {1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe"], rig.Launcher.Calls);
}

[Fact]
public async Task NoWindowAfterTenSecondsIsUpstreamsText() =>
    Assert.Equal(new AppResult("Launching Notepad sent, but window not detected yet.", false),
        await new LaunchRig(apps: [new("Notepad", "x")]).Service.LaunchAsync("notepad", default));

[Fact]
public async Task UnknownAppsAreNotFound() =>
    Assert.Equal(new AppResult("Qxqxqx not found in start menu.", false),
        await new LaunchRig(apps: [new("Notepad", "x")]).Service.LaunchAsync("qxqxqx", default));

[Fact]
public async Task LaunchExecutableReportsPythonsJson()
{
    using var temp = new TempFile("tool.exe");
    var rig = new LaunchRig(apps: [], pid: 4321);
    var result = await rig.Service.LaunchExecutableAsync(temp.Path, ["a b"], null, default);
    Assert.Equal($"{{\n  \"pid\": 4321,\n  \"executable\": {JsonString(temp.FinalPath)},\n  \"args\": [\n    \"a b\"\n  ],\n  \"cwd\": null\n}}", result.Text);
    Assert.Equal([$"StartExecutable {CommandLine.Join([temp.FinalPath, "a b"])} cwd="], rig.Launcher.Calls);
}

[Fact]
public async Task AMissingExecutableIsAnError() =>
    Assert.StartsWith("Executable does not exist: ", (await new LaunchRig(apps: []).Service.LaunchExecutableAsync(@"C:\nope\none.exe", [], null, default)).Text);
```

(`LaunchExecutableAsync` failures are `AppResult(text, IsError: true)`, matching upstream's raised `ValueError`; `JsonString` is the test's own Python-style string quoting via `PyJson`.)

One Desktop test `AppsFolderListsKnownEntries` (reads only): `AppCatalog.ListAsync` returns more than 10 entries and at least one packaged parsing name; nothing is launched.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build DesktopMcp.slnx`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`AppCatalog.ListAsync`: on a new `StaThread`, `SHCreateItemFromParsingName("shell:AppsFolder")`, `BindToHandler(BHID_EnumItems, IEnumShellItems)`, each item's `SIGDN_NORMALDISPLAY` and `SIGDN_PARSINGNAME` (`CoTaskMemFree` both); bounded 10 s (abandon past it, `DesktopException("Listing the Start menu apps failed: it did not finish within 10 s.")`). `AppService.LaunchAsync`: map lower-cased names to parsing names (first wins); `ExtractOne(name, keys, 70)`; packaged → `ActivatePackaged` (`IApplicationActivationManager.ActivateApplication(id, null, AO_NONE)`), else `StartFromShell` (`ShellExecuteEx` of `shell:AppsFolder\<parsing name>` with `SEE_MASK_NOCLOSEPROCESS`; the PID from `hProcess` when one is returned, else null; close the handle); a launch exception → the failure text; then the wait of Decisions 31 through `IWindowSystem` (`EnumerateTopLevel`, `IsVisible`, `GetProcessId`, `GetTitle`) and the injected delay. `LaunchExecutableAsync`: path rules of Decisions 34 (final path through `CreateFile` + `GetFinalPathNameByHandle`, stripping `\\?\`), `StartExecutable` = `CreateProcess(null, commandLine, ..., inheritHandles: true, EXTENDED_STARTUPINFO_PRESENT, cwd)` with `STARTF_USESTDHANDLES` on one inheritable `NUL` handle that is the only entry of `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`; close the process, thread and `NUL` handles; `PyJson.Dumps` of pid, executable, args, cwd.

- [ ] **Step 4: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: PASS. Then `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~AppsFolderListsKnownEntries"`: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src windows/tests
git commit -m "Add App launch through the AppsFolder and launch_executable outside the server's job" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: WaitFor

`tools/input.py:16-212, 438-504` with Decisions 6, 24-28 and 41.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Snapshots/WaitConditions.cs`, `windows/src/DesktopMcp.Desktop/Waiting/WaitConditionService.cs`, `windows/src/DesktopMcp.Server/Tools/WaitForTool.cs`
- Modify: `windows/src/DesktopMcp.Desktop/Tree/TreeWalker.cs` (`stopWhen`, `WindowWalk.Match`, `Describe`), `Tree/TreeService.cs` (`ITreeCapture.FindAsync`), `Snapshots/SnapshotService.cs` (extract `TreeTargets`), `ToolServices.cs` (`IWaitConditions WaitConditions`), `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Snapshots/WaitConditionsTests.cs`, `windows/tests/DesktopMcp.Desktop.Tests/Waiting/WaitConditionServiceTests.cs`, `Tree/TreeWalkerTests.cs`, `Tree/TreeServiceTests.cs`, `windows/tests/DesktopMcp.Server.Tests/Tools/WaitForToolTests.cs`

**Interfaces:**
- Consumes: `IWindowScanner` (`ScanAsync`, `ScanActiveAsync`), `TreeService`, `UiaWorker`, `UiElement` metadata (Task 6), `DesktopSession.ThrowIfUnavailable` (P2), `PyText.Repr`/`Fixed`.
- Produces:
  - `public enum WaitCondition { TextExists, ActiveWindow, ElementExists, ElementEnabled, FocusedElement }`
  - `public static class WaitConditions { WaitCondition Validate(string condition, string? text, string? windowName, double timeout, double interval); string Name(WaitCondition c); bool TextMatches(string? value, string? expected); bool ElementMatches(UiElement element, string? text, string? windowName); IEnumerable<string> MetadataTexts(UiMetadata metadata); }`
  - `public interface IWaitConditions { Task<(bool Matched, string Detail)> CheckAsync(WaitCondition condition, string? text, string? windowName, CancellationToken ct); }`
  - `TreeWalker(..., Func<UiElement, bool>? stopWhen = null)`; `WindowWalk.Match : UiElement?`; `internal static UiElement? TreeWalker.Describe(UiaElementData node, WindowContext context)` (the element the walk would list for this node alone: interactive or scrollable, no words, no parent rules)
  - `ITreeCapture.FindAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, Func<UiElement, bool> match, CancellationToken ct) : Task<UiElement?>` (no retries; blocked, excluded and not-responding windows skipped as in `CaptureAsync`)
  - `internal static (long? Active, IReadOnlyList<long> Others) SnapshotService.TreeTargets(WindowScan scan, ScreenRect? area)` (the selection `CaptureAsync` already does, now shared)

Texts (verbatim from `tools/input.py`): the validation messages (lines 173-210), `text {text!r} appeared` / `was absent`, `active window matched {name!r}` / `active window was {name!r}`, `element matched {name!r}`, `matching element was absent`, `focused element matched {name!r}`, `matching focused element was absent`, `condition was not evaluated`, `WaitFor condition '{c}' satisfied after {elapsed:.2f}s and {n} attempt(s): {detail}.`, `Timed out after {timeout:.2f}s waiting for '{c}': {detail}.` (raised), `use_dom must be true or false`, then `SnapshotService.UseDomUnavailable` for `use_dom=True`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class WaitConditionsTests
{
    [Theory]
    [InlineData(" Enabled ", WaitCondition.ElementEnabled)]
    [InlineData("focused-element", WaitCondition.FocusedElement)]
    [InlineData("window", WaitCondition.ActiveWindow)]
    public void AliasesNormalise(string condition, WaitCondition expected) =>
        Assert.Equal(expected, WaitConditions.Validate(condition, "x", null, 10, 0.25));

    [Theory]
    [InlineData("nope", "x", null, 10.0, 0.25, "condition must be one of: text_exists, active_window, element_exists, element_enabled, focused_element")]
    [InlineData("text", null, null, 10.0, 0.25, "text is required when condition is text_exists")]
    [InlineData("window", null, null, 10.0, 0.25, "text or window_name is required when condition is active_window")]
    [InlineData("element", null, null, 10.0, 0.25, "text or window_name is required when condition is element_exists or element_enabled")]
    [InlineData("text", "x", null, 0.0, 0.25, "timeout must be greater than 0 and at most 120 seconds")]
    [InlineData("text", "x", null, 10.0, 6.0, "interval must be greater than 0 and at most 5 seconds")]
    [InlineData("text", "x", null, double.NaN, 0.25, "timeout must be a finite number")]
    public void ValidationFollowsUpstream(string c, string? text, string? window, double timeout, double interval, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => WaitConditions.Validate(c, text, window, timeout, interval)).Message);

    [Fact]
    public void BooleanMetadataNeverMatches()
    {
        var element = new UiElement(0, "Save", "button", "Notepad", new(0, 0, 1, 1), new(0, 0), new UiMetadata { HasFocus = true, Value = "draft" });
        Assert.False(WaitConditions.ElementMatches(element, "true", null));
        Assert.True(WaitConditions.ElementMatches(element, "DRAFT", "note"));
        Assert.False(WaitConditions.ElementMatches(element, "save", "paint"));
    }
}

public class WaitConditionServiceTests
{
    [Fact]
    public async Task ActiveWindowProbesOnlyTheForegroundWindow()
    {
        var rig = new WaitRig(active: "Untitled - Notepad");
        Assert.Equal((true, "active window matched 'Untitled - Notepad'"), await rig.Service.CheckAsync(WaitCondition.ActiveWindow, null, "notepad", default));
        Assert.Equal(["ScanActive"], rig.Calls);
    }

    [Fact]
    public async Task TextExistsChecksWindowNamesBeforeWalking()
    {
        var rig = new WaitRig(active: "A", windows: ["Downloads"]);
        Assert.Equal((true, "text 'down' appeared"), await rig.Service.CheckAsync(WaitCondition.TextExists, "down", null, default));
        Assert.DoesNotContain("Find", rig.Calls);
    }

    [Fact]
    public async Task ElementExistsStopsAtTheFirstMatch()
    {
        var rig = new WaitRig(active: "A", found: Element("OK"));
        Assert.Equal((true, "element matched 'OK'"), await rig.Service.CheckAsync(WaitCondition.ElementExists, "ok", null, default));
        Assert.Equal(["Scan", "Find"], rig.Calls);
    }

    [Fact]
    public async Task ElementEnabledRejectsADisabledScrollable()
    {
        var rig = new WaitRig(active: "A", found: Element("List", enabled: false));
        Assert.False((await rig.Service.CheckAsync(WaitCondition.ElementEnabled, "list", null, default)).Matched);
    }

    [Fact]
    public async Task ATransientUiaFailureIsNotAMatch()
    {
        var rig = new WaitRig(active: "A", findThrows: new UiaNotRespondingException("busy"));
        Assert.Equal((false, "matching element was absent"), await rig.Service.CheckAsync(WaitCondition.ElementExists, "ok", null, default));
    }
}

public class WaitForToolTests
{
    [Fact]
    public async Task SatisfiedOnTheThirdAttempt()
    {
        var desktop = new FakeDesktop { WaitResults = [(false, "matching element was absent"), (false, "matching element was absent"), (true, "element matched 'OK'")] };
        Assert.Equal("WaitFor condition 'element_exists' satisfied after 0.50s and 3 attempt(s): element matched 'OK'.",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", desktop.Services()), """{"condition": "element", "text": "ok"}""")));
    }

    [Fact]
    public async Task TimeoutIsAToolError()
    {
        var desktop = new FakeDesktop { WaitResults = [(false, "active window was 'A'")] };
        Assert.Equal("Error calling tool 'WaitFor': Timed out after 1.00s waiting for 'active_window': active window was 'A'.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", desktop.Services()), """{"condition": "active_window", "window_name": "b", "timeout": 1, "interval": 0.5}""")));
    }

    [Fact]
    public async Task UseDomIsRefused() =>
        Assert.Equal($"Error calling tool 'WaitFor': {SnapshotService.UseDomUnavailable}",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", new FakeDesktop().Services()), """{"condition": "text", "text": "a", "use_dom": "true"}""")));

    [Fact]
    public async Task WaitForLeavesTheLabelStoreAlone()
    {
        var desktop = new FakeDesktop { WaitResults = [(true, "text 'a' appeared")] };
        var services = desktop.Services();
        await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", services), """{"condition": "text", "text": "a"}""");
        Assert.Null(services.Snapshots.State.Latest);
    }
}
```

`FakeDesktop.WaitResults` repeats its last entry once exhausted. The tool tests use a `FakeTimeProvider`-style `ManualTime` in `ToolServices.Time` (the Desktop tests already have `ManualTime`; copy it into Server.Tests) whose delays complete immediately and advance the clock by the requested interval (0.25 s default). In `TreeWalkerTests`: `StopWhenEndsTheWalkAtTheFirstMatch` (three buttons, `stopWhen` matching the second visited; `Match` is it and the third is never read by the fake source). In `TreeServiceTests`: `FindAsyncSkipsBlockedWindowsAndDoesNotRetry`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build DesktopMcp.slnx`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`WaitConditions.Validate` in upstream's order (`input.py:173-212`). `WaitConditionService.CheckAsync` per Decisions 6: `ActiveWindow` → `ScanActiveAsync` (name `""` when null), expected `windowName ?? text`; `FocusedElement` → one UIA job (5 s): `GetFocusedElementBuildCache(ElementRequest)`, climb `Parent` to the first element with a native window handle (32 levels), its root window; the WM_NULL check of that window before the read; `TreeWalker.Describe` with that window's context; match `ElementMatches`; `ElementExists`/`ElementEnabled`/`TextExists` → `ScanAsync`; `TextExists` first tests the active window's and the windows' names; then `TreeTargets(scan, null)` and `FindAsync` with the predicate (`ElementEnabled` adds `Metadata.IsEnabled`; `TextExists` matches name, control type, window name and `MetadataTexts`). `UiaTimeoutException`, `UiaNotRespondingException`, a UIA timeout and a dead element → `(false, <the condition's absent text>)`. `WaitForTool`: parse (`condition` required string, `text`/`window_name` optional strings, `timeout`/`interval` through `ActArguments.FiniteNumber` with defaults 10 and 0.25), `Validate`, `use_dom` strict then the DOM error, `DesktopSession` check once, then upstream's loop (`input.py:473-504`) on `ToolServices.Time` with `Task.Delay(min(interval, remaining), Time, ct)`. No input lock (WaitFor reads only).

- [ ] **Step 4: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src windows/tests
git commit -m "Add WaitFor with per-condition reads and an early-exit tree search" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 15: GPU single-slot gate

Decisions 9 (follow-ups P4 item; P2 Tasks 14-15 findings).

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Imaging/GpuGate.cs`
- Modify: `windows/src/DesktopMcp.Desktop/Imaging/Win2DRenderer.cs`, `windows/src/DesktopMcp.Desktop/Capture/GraphicsCaptureService.cs` (both take an internal `GpuGate` in their internal ctors; the public ctors use `GpuGate.Shared`)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Imaging/GpuGateTests.cs`

**Interfaces:**
- Produces: `internal sealed class GpuGate { public static GpuGate Shared { get; } public Task<GpuLease> EnterAsync(string operation, TimeSpan wait, CancellationToken ct); }`; `internal sealed class GpuLease : IDisposable { public void AbandonUntil(Task stillRunning); }` (the slot is freed when `stillRunning` completes, not at `Dispose`).

Texts: `{operation} failed: an earlier graphics call is still running after its deadline; try again later.` (at once while an abandoned holder runs) and `{operation} failed: the graphics device stayed busy for {wait:0.###} s.` (wait exceeded).

- [ ] **Step 1: Write the failing tests**

```csharp
public class GpuGateTests
{
    [Fact]
    public async Task ASecondCallerWaitsForTheFirst()
    {
        var gate = new GpuGate();
        var first = await gate.EnterAsync("Render", TimeSpan.FromSeconds(5), default);
        var second = gate.EnterAsync("Render", TimeSpan.FromSeconds(5), default);
        Assert.False(second.IsCompleted);
        first.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task AnAbandonedCallMakesTheNextFailAtOnce()
    {
        var gate = new GpuGate();
        var running = new TaskCompletionSource();
        (await gate.EnterAsync("Screen capture", TimeSpan.FromSeconds(5), default)).AbandonUntil(running.Task);
        var error = await Assert.ThrowsAsync<DesktopException>(() => gate.EnterAsync("Image rendering", TimeSpan.FromSeconds(5), default));
        Assert.Equal("Image rendering failed: an earlier graphics call is still running after its deadline; try again later.", error.Message);
        running.SetResult();
        (await gate.EnterAsync("Image rendering", TimeSpan.FromSeconds(5), default)).Dispose();
    }

    [Fact]
    public async Task WaitingPastTheLimitFails()
    {
        var gate = new GpuGate();
        using var held = await gate.EnterAsync("Render", TimeSpan.FromSeconds(5), default);
        Assert.Equal("Render failed: the graphics device stayed busy for 0.05 s.",
            (await Assert.ThrowsAsync<DesktopException>(() => gate.EnterAsync("Render", TimeSpan.FromMilliseconds(50), default))).Message);
    }

    [Fact]
    public async Task ACancelledWaitLeavesTheSlotUntouched()
    {
        var gate = new GpuGate();
        var held = await gate.EnterAsync("Render", TimeSpan.FromSeconds(5), default);
        using var cancel = new CancellationTokenSource();
        var waiting = gate.EnterAsync("Render", TimeSpan.FromSeconds(5), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();
        (await gate.EnterAsync("Render", TimeSpan.FromSeconds(1), default)).Dispose();
    }
}
```

Add to `Win2DRendererTests`: `ATimedOutRenderHoldsTheGateUntilItEnds` (the existing fake slow render; after its timeout, a second render fails at once with the abandoned text; after the slow one completes, a third succeeds). Same shape in `GraphicsCaptureServiceTests` with the existing fake monitor capture.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

One `SemaphoreSlim(1)` plus an `abandoned` task field: `EnterAsync` throws the abandoned text when that task is not completed, then `WaitAsync(wait, ct)` (false → the busy text). `AbandonUntil` sets the field and releases the semaphore in a continuation of `stillRunning`. In `Win2DRenderer.RenderPngAsync`, enter with the render's own `Timeout` before `Task.Run`; on the timeout path call `AbandonUntil(work)` instead of disposing. In `GraphicsCaptureService.CaptureMonitorBoundedAsync`, the same around each monitor's work with `MonitorTimeout`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop windows/tests/DesktopMcp.Desktop.Tests
git commit -m "Share one GPU slot between rendering and capture" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 16: Release verification and documentation

The P3 checkpoint, as P2 Task 19 (Decisions 12).

**Files:**
- Modify: `windows/scripts/smoke-stdio.ps1`, `docs/superpowers/specs/2026-10-19-windows-port-design.md`, `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`

**Interfaces:**
- Consumes: every tool from Tasks 9-14; `build-release.ps1`.
- Produces: the smoke script's act-tool section; the measured P3 results in the spec; the follow-ups without the closed P3 items.

- [ ] **Step 1: Add the safe act-tool calls to the smoke test**

In `smoke-stdio.ps1`, after the Snapshot without vision and before the graphics-stack check, add a case table run through `Invoke-Tool` that asserts each result's error flag and text prefix. `$nobody` is `'qxqxqxqxqxqxqxqx'` (no app or window name scores 70 against it):

| Tool | Arguments | Expected |
|---|---|---|
| Click | `{}` | isError, `Error calling tool 'Click': Either loc or label must be provided.` |
| Click | `{loc: [-1000000, -1000000]}` | isError, contains `are outside the desktop bounds` |
| Click | `{label: 1000000}` | isError, `... Failed to find element with label 1000000: Label 1000000 out of range` |
| Type | `{text: 'x', loc: '[1'}` | isError, `Error calling tool 'Type': Location must be a list of exactly 2 integers [x, y]` |
| Shortcut | `{shortcut: 'ctrl+'}` | isError, `... Invalid shortcut 'ctrl+': empty key name` |
| Move | `{loc: [0, 0], duration: 1}` | isError, `... from_loc and duration require drag=True` |
| MultiSelect | `{}` | isError, `... Either locs or labels must be provided.` |
| MultiEdit | `{locs: [[1, 2]]}` | isError, `... Each loc item must be [x, y, text]. Invalid: [1,2]` |
| App | `{mode: 'launch', name: $nobody}` | not isError, `Qxqxqxqxqxqxqxqx not found in start menu.` (the AppsFolder enumeration under NativeAOT; nothing starts) |
| App | `{mode: 'switch', name: $nobody}` | isError, `... Application Qxqxqxqxqxqxqxqx not found.` |
| App | `{mode: 'resize', name: $nobody}` | not isError, `Application Qxqxqxqxqxqxqxqx not found.` |
| WaitFor | `{condition: 'active_window', window_name: $nobody, timeout: 0.5, interval: 0.25}` | isError, starts `Error calling tool 'WaitFor': Timed out after 0.50s waiting for 'active_window': active window was ` |
| WaitFor | `{condition: 'element_exists', text: $nobody, timeout: 0.6, interval: 0.3}` | isError, starts `... Timed out after 0.60s waiting for 'element_exists'` (the early-exit walk under NativeAOT) |

Append `, <n> act-tool checks OK (no input sent)` to the `smoke OK` summary. The header comment says these calls inject nothing and activate nothing.

- [ ] **Step 2: Run the full default suite**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project. Record the counts.

- [ ] **Step 3: Run the Desktop tests by hand**

Run (interactive session; hands off mouse and keyboard; test windows and the WinForms target appear, take the foreground and receive input; the clipboard text is saved and restored by the paste test): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath=`
Expected: `Passed!` with `Failed: 0`. Record the count and any guard failure (a test that failed because the foreground was taken sends nothing; rerun it).

- [ ] **Step 4: Publish both RIDs and run the checks**

Run (interactive session): `pwsh scripts/build-release.ps1`
Expected: no publish warnings; both folder lines; the `smoke OK` line with the act-tool checks and `graphics stack not loaded`; `AOT activation check OK`; `AOT UIA check OK`. Record both exe sizes and the module count.

- [ ] **Step 5: By-hand act session through the AOT exe (needs the user; optional)**

Ask the user to save their work, keep their hands off the input devices, and watch. With the AOT exe over stdio (as P2 Task 19 Step 5): `App {mode: 'launch', name: 'notepad'}` → `Notepad launched.`; `Snapshot`; `Type` into the edit by its label with `héllo 😀 and a twenty-plus character tail` → no warning line; `Shortcut ctrl+a`; `App resize` to 800x600 at 100,100; `App switch notepad`; `Click` the File menu by label; `Shortcut esc`; then close the server and confirm (Open question 1) that Notepad is still running; the user closes Notepad without saving. Record each result. If the user declines, say so in the report.

- [ ] **Step 6: Record the results in the spec**

In `docs/superpowers/specs/2026-10-19-windows-port-design.md`:
1. §5 Tool-call serialisation: add `Until P4, the input tools hold one input lock for their whole call (P3 plan, Decisions 18).`
2. §6 Input: replace the paragraph with one that states the signature value `0x444D4350` ("DMCP"), moves as absolute SendInput moves (no `SetCursorPos`), the 50 ms pause between move and press, extended-key flags only on extended keys, the count and UIPI checks, and that the timings and the paste path otherwise follow upstream (P3 plan, Decisions 8, 13, 14, 21).
3. §8 App: replace the `launch` bullet per Open question 1's answer (AppsFolder enumeration; packaged activation; `ShellExecuteEx` of `shell:AppsFolder` for the rest; the PID and title waits). WaitFor: replace `polls snapshots` with `polls only what each condition needs (the foreground window, the focused element, or the window scan plus an early-exit walk of the windows Snapshot walks), never a full Snapshot (P3 plan, Decisions 6)`.
4. §9: add `P3 results (<date>, Windows build <build>): default suite <n> (Core <a>, Server <b>, Desktop non-desktop <c>); Desktop suite <n> by hand; build-release: win-x64 <size> bytes, win-arm64 <size> bytes; stdio smoke OK under NativeAOT with <n> act-tool checks and no input sent; by-hand act session: <result or not run>. Input tests inject only through GuardedInjector into the tests' own windows.`
5. §10: under Output cleanups and Bug fixes add ClickablePoint instead of the centre for labels, the label errors, the Type read-back warning line, WaitFor and App leaving the label map alone, points outside the desktop rejected for every pointer action, shortcut validation before sending, `clicks` below 0 rejected, non-ASCII app names matching; and a bullet `- **P3 decisions**: ... listed in the P3 plan's "Decisions on upstream ambiguities" (docs/superpowers/plans/2026-10-20-windows-p3-desktop-act.md).`
6. §11 P3 row Outcome: `Upstream's core workflows (done; results in §9)`.
7. §12: the Job Object row: launched apps die with the server (user decision, 2026-10-20), except packaged apps and apps that hand off to a broker or an existing instance, which the system starts outside the job.

- [ ] **Step 7: Update the follow-ups list**

In `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`: replace the P3 section with `All P3 items are done (see docs/superpowers/plans/2026-10-20-windows-p3-desktop-act.md), except:` followed by the not-adopted items of Decisions 5 (foreground/title change reporting, target highlight with the P4 overlay, selector targeting, tree diffs, pattern-based MultiSelect); delete the GPU gate item from P4; add a `## P3 leftovers` section for minor review findings; add under P4: `Replace DesktopInputLock with the control gate's desktop lock; supply IActionCheckpoint and InputLedger's physical-input predicate from InputMonitor (P3 plan, Decisions 18-20).`

- [ ] **Step 8: Commit**

```bash
git add windows/scripts/smoke-stdio.ps1 docs/superpowers/specs/2026-10-19-windows-port-design.md docs/superpowers/plans/2026-10-19-windows-p0-followups.md
git commit -m "Smoke-test the act tools under NativeAOT without input and record the P3 results" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Self-review

**Spec coverage (§11 P3 row, §5, §6 Input, §8, §10, the follow-ups and the controller's decisions):**

| Requirement | Task |
|---|---|
| SendInput only, absolute virtual-desktop mouse, wheel, VK with scan codes, Unicode with surrogate pairs, fixed `dwExtraInfo` | 1, 3, 4 (Decisions 8, 13, 14) |
| Upstream's timings, step counts, drag durations, paste path, Type `clear`/`caret_position`/`press_enter` | 2, 4 (Decisions 21, 36) |
| Shortcut parsing from upstream's `SpecialKeyNames` and aliases | 1 (Decisions 16) |
| InputLedger: release on preemption (P4), cancellation, shutdown | 3, 4, 9 (Decisions 19) |
| Cancellation token through every step; checkpoints | 4 (Decisions 20) |
| Labels resolve against the latest snapshot | 6, 8 (Decisions 1, 2, 40) |
| Click, Type, Scroll, Move, Shortcut, MultiSelect, MultiEdit with upstream texts and spec §10 cleanups | 9, 10, 11 (Decisions 17, 22, 23, 37, 38) |
| App launch (AppsFolder, fuzzy 70, packaged activation, 10 s + 10 s waits), launch_executable, switch (restore, AllowSetForegroundWindow, AttachThreadInput, SetForegroundWindow, BringWindowToTop, 1 s verification), resize | 5, 12, 13 (Decisions 29-35) |
| WaitFor conditions, aliases, limits, messages; `element_enabled` checks IsEnabled | 14 (Decisions 6, 24-28) |
| GPU single-slot gate | 15 (Decisions 9) |
| Desktop tests in `Category=Desktop`, never on the user's windows; WinForms target | 4 and every Desktop test after it (Decisions 11) |
| Contract unchanged; result shapes | 9-14 (`ContractTests`), 16 (smoke contract comparison) |
| AOT: no IL warnings; act paths and the AppsFolder COM path exercised under NativeAOT without input | 16 |

**Follow-ups:** every P3 item maps to a task or to Decisions 5 (see "Follow-ups this plan picks up"); the GPU gate moves from P4 to Task 15; the process allowlist stays in P4.

**Step scan:** no TBD or "handle edge cases"; every test step names its tests and assertions with exact strings; code steps give signatures, files and the upstream lines whose behaviour they port; the non-obvious algorithms (shortcut hold rules, SendKeys pauses, label locate, WRatio, the foreground sequence) are written out once. Bracketed values in Task 16 are measurements recorded by earlier steps of that task.

**Type consistency:** `KeyToken`/`KeyInput`/`KeyStep` (Task 1) are used by Tasks 3, 4 and 10; `ScreenPoint`/`ScreenRect` throughout; `InjectedInput`, `MouseFlags.AbsoluteMove`, `IInputInjector`, `InputLedger.Press(name, down, up)` (Task 3) match Task 4's tests and `GuardedInjector`; `IDesktopInput` (Task 4) is what `FakeDesktop.Input` implements (Task 9) and what Tasks 10-11 call (`TypeAsync(..., afterClick, ct)`, `MultiSelectAsync(pressCtrl, count, target, ct)`, `PressKeysAsync(tokens, interval, waitTime, ct)`); `IForeground.SwitchToAsync`/`EnsureKeyboardTargetAsync` (Task 5) are used by Tasks 10-12; `ElementIdentity` (Task 6) feeds `LabelLocator.Locate(identity, storedCenter, ct)` (Task 7) and `ActionTargets` (Task 8); `LocateResult`/`LocateFailure` (Task 7) match Task 8's tests; `IActionTargets.LabelAsync/Point/CheckLabels/CheckForegroundInput` (Task 8) are used by Tasks 9-11; `ToolServices` grows `Machine`, `Input`, `Targets`, `Foreground`, `Ledger`, `InputLock`, `Time` (Task 9), `ReadBack` (10), `Apps` (12), `WaitConditions` (14); `IAppService`/`AppResult` (Task 12) are completed in Task 13; `IWindowScanner.ScanActiveAsync` (Task 12) is used by Task 14; `ITreeCapture.FindAsync` and `SnapshotService.TreeTargets` (Task 14) are used only there; `GpuGate.EnterAsync`/`GpuLease.AbandonUntil` (Task 15).

**Review Focus:** each of the five lines has its named test in Tasks 2, 3, 4, 7 and 9.

**Proportion:** the plan holds signatures, exact texts, tests and four short algorithms; tool bodies, P/Invoke plumbing and fakes are left to the implementer, guided by the cited upstream lines.
