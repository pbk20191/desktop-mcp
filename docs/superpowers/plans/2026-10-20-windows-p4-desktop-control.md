# Windows port P4: desktop control, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the AI from fighting the user for the mouse and keyboard: an input monitor on the dispatcher thread, a pure control state machine, the blue control indicator and the orange capture flash, the control gate in front of every gated tool call (it replaces P3's `DesktopInputLock`), state notifications to live sessions, the ControlStatus tool, and an optional process allowlist for injected input.

**Architecture:**
- `DesktopMcp.Core` gains `Control/`: the policy table, the status model and its two JSON forms, the process allowlist matcher and `ControlStateMachine` (pure, `TimeProvider` injected). All of it is unit-tested.
- `DesktopMcp.Desktop` gains `Control/` (`PhysicalInputFilter`, the per-event hook logic with no Win32; `InputMonitor`, the LL hooks, Raw Input window and timers on the dispatcher thread; `ControlCoordinator`, the thread-pool heartbeat loop that drives the state machine, the suppression deadline, health checks, the ledger and the checkpoints) and `Overlay/` (layout, Win2D art, click-through layer windows, the indicator and the capture flash). The dispatcher gains the follow-ups' hardening and one shared, lazily started instance.
- `DesktopMcp.Server` gains `Control/` (`ControlGate` behind an SDK call-tool filter, `ControlNotifier`, `SessionRegistry`) and the ControlStatus tool. The tools lose `DesktopInputLock`; the gate serialises gated calls.

**Tech Stack:** .NET 10 NativeAOT, CsWin32 0.3.347 (build task, `comInterop.useComSourceGenerators`), CsWinRT, Windows App SDK `Microsoft.UI.Dispatching` (and `Microsoft.UI.Composition` only if Task 1 picks it), Win2D 1.4.0 (static), ModelContextProtocol 2.2.0 (`WithRequestFilters`/`AddCallToolFilter`, `ServerCapabilities.Logging`, `McpSession.SendNotificationAsync`), xUnit 2.9.3. No new package references.

**Spec:** `docs/superpowers/specs/2026-10-19-windows-port-design.md`: §7 in full, §5 (dispatcher thread, timers, tool-call serialisation, cancellation), §6 (Input signature, capture and `WDA_EXCLUDEFROMCAPTURE`), §8 (CLI and config keys), §9, §10, §11 (P4 row), §12. Upstream is CursorTouch/Windows-MCP at `128f2960c79b74bd2630f4dbc9d4f664f4ce89b3`; paths such as `desktop/control.py:251` are relative to `src/windows_mcp/`. Every string, default, limit and timing comes from there unless the spec or the Decisions below say otherwise. P2 and P3 Decisions still bind (`docs/superpowers/plans/2026-10-19-windows-p2-desktop-read.md`, `docs/superpowers/plans/2026-10-20-windows-p3-desktop-act.md`), in particular P2 Decisions 21 (the dispatcher is first started in P4) and P3 Decisions 8, 11, 18-20 and 42.

**Scope (spec §11, P4 row):** InputMonitor, state machine, overlay, capture flash, gate, notifier, ControlStatus; plus the follow-ups' P4 items except kill-on-close job inheritance (Decisions 30). Not in P4: HTTP sessions and their 60 s TTL wiring (P5 calls `SessionRegistry` with it), target highlighting before an action (P3 follow-up, stays there), telemetry.

## Open questions for the user

The plan proceeds with each recommendation; an answer the other way changes only the tasks named.

1. **Input injected by other programs (Chrome Remote Desktop, Mouse Without Borders, AutoHotkey).** Spec §7 says the monitor ignores every event flagged `LLMHF_INJECTED`/`LLKHF_INJECTED`, as upstream does (`desktop/control_hooks.py:26, 63`). Such input then passes through unswallowed during `ai`, never moves the state to `user`, and the takeover chord typed through it does nothing; Raw Input ignores it too (null device, `control_win32.py:213`). A user who drives this PC through Chrome Remote Desktop (as during P2) could not take over. **Recommended: injected input without our `0x444D4350` signature counts as user input**, exactly like physical input (swallowed during `ai`, the chord works, keys and buttons move `ready` to `user`), and its pointer path in screen pixels counts toward the existing `mouse_takeover_pixels` key (default 500, range 40-2000; upstream accepts the key and ignores it, `infrastructure/config.py:46`). The other answer sets `ForeignInjectedPolicy.Ignore` in `ControlCoordinator.Create` (Task 11); the tests keep `Physical` either way, because it is what lets them exercise suppression without a person (Decisions 3).
2. **Desktop control without the Windows App SDK runtime.** The hooks live on the Windows App SDK dispatcher thread (spec §5), so with control enabled (the default) the server bootstraps the runtime when it starts, not lazily. Without the runtime the control state is `unavailable`, so every gated call (under `relaxed`: Click, Type, Scroll, Move, Shortcut, MultiSelect, MultiEdit, App `resize`/`switch`) is refused with `CONTROL_UNAVAILABLE`; P3 sent that input unprotected. **Recommended: fail closed** (upstream refuses to start at all when its monitor cannot start, `__main__.py:345-349`); `--no-control` runs those tools without protection, as before; the reason is logged once on stderr. The other answer would let gated calls run unprotected while unavailable for this one cause (a branch in `ControlCoordinator.BeginCallAsync`, Task 11).

## Global Constraints

- TFM `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` **10.0.22621.0**; RIDs `win-x64` and `win-arm64`.
- `TreatWarningsAsErrors`, `IsAotCompatible`, no warning suppression, **no new package references** (Task 1's WinUI 3 check happens in a throwaway copy and is never committed).
- **The publish folder for each RID contains only `desktop-mcp.exe`** (plus `.pdb`), and that exe alone runs; the probe exe has the same rule. `pwsh scripts/build-release.ps1` enforces it and must keep passing. No `.pri`, XAML or resource file may be added.
- Win32 and COM bindings come from CsWin32 0.3.347 through `NativeMethods.txt`; nothing new is declared by hand in product code. `DesktopMcp.Desktop/NativeMethods.json` stays exactly `{"$schema": "https://aka.ms/CsWin32.schema.json", "public": false, "allowMarshaling": true, "comInterop": {"useComSourceGenerators": true}}`. No built-in COM interop. Hook and window procedures are `[UnmanagedCallersOnly]` static methods passed as function pointers (as `probes/DesktopMcp.Probe/HookProbe.cs`); no delegate marshalling.
- Boundary rule (spec §3): HWND, HHOOK, `INPUT`, `MSLLHOOKSTRUCT`, `KBDLLHOOKSTRUCT`, `RAWINPUT` and COM pointers never leave `DesktopMcp.Desktop`; windows cross as `long`, points as `ScreenPoint`, rectangles as `ScreenRect`.
- **Hook callbacks are minimal and never block** (spec §5; Windows removes a hook that exceeds `LowLevelHooksTimeout`). A callback reads `Volatile` fields, updates the dispatcher-owned key and button arrays, writes at most one event to the bounded queue with `TryWrite`, and returns. It takes no lock (not even `InputLedger`'s: `Press` holds that lock across `SendInput`, which waits for the hooks), allocates nothing, logs nothing, calls no `SendInput`, UIA, capture or COM, and catches every exception (an exception escaping an `[UnmanagedCallersOnly]` method kills the process); a caught exception requests fail-open.
- **No path leaves input locked.** The hook swallows only while a suppression deadline (1 s) is in the future; only the coordinator's heartbeat on the thread pool renews it (never a dispatcher timer); a lapsed deadline makes the hook pass input and request fail-open; a stalled dispatcher thread is bounded by Windows' `LowLevelHooksTimeout`.
- **Dispatcher thread rule (spec §5):** work there is short and never blocks. Win2D rendering, capture, UIA, file, process and network work never run there; art is rendered on the thread pool under `GpuGate` and only the finished pixels are handed to the dispatcher.
- **Safety on the live machine (Decisions 3):** the always-run tests install no hook, create no visible window, inject nothing and take no foreground. Tests that do any of that carry `[Trait("Category", "Desktop")]` and are listed in Task 14 for the user to run. Desktop tests inject only into the tests' own windows (P3 Decisions 11): signed input through `GuardedInjector`, and the foreign (unsigned) input that suppression tests need through `ForeignInjector` (Task 7), which applies the same foreground and target checks.
- Seams: Win32 hooks, Raw Input, window calls, `GetAsyncKeyState`, the interactive-desktop check, Win2D, the dispatcher, the clock (`TimeProvider`, `Func<long>` timestamps in `Stopwatch` units) and MCP sessions sit behind internal interfaces or delegates, so the default run uses fakes.
- Windows App SDK initialisation: only the control start (control enabled, Open question 2) and the overlay and flash paths call `AppRuntime.EnsureInitialized()` (through the shared dispatcher). Nothing loads the graphics stack until the first indicator show or flash: `smoke-stdio.ps1` keeps failing if a run without capture loads d2d1, d3d11, dwrite or d3dcompiler_47.
- The tool contract `spec/windows/tools.json` does not change. Result shapes (spec §4): ControlStatus returns its JSON object as text and as `structuredContent`; rejections are `isError` results whose whole text is `{"code": C, "status": {...}}` (no `Error calling tool` prefix).
- Naming: `desktop-mcp`, `~/.desktop-mcp`, `DESKTOP_MCP_*`; the notifier's logger is `desktop-mcp.control`.
- Commit messages end with a blank line and `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Work happens on branch `windows-p4`.

All `dotnet` and `pwsh` commands run from `windows/`; `git` commands run from the repository root.

## Review Focus

The five conditions the spec implies but no feature test would naturally hit, most likely first. Each has a test in the task named.

1. **A tool call that hangs or a coordinator that dies while the AI holds control.** The user must get input back within about a second, never later. Tests: Task 6 `PastTheDeadlineTheHookPassesAndRequestsFailOpen`; Task 11 `AStoppedHeartbeatLetsTheDeadlineLapse`; Task 7 `SuppressionLapsesWithinOneSecondWhenTheHeartbeatStops` and `AStalledDispatcherCannotLockInput` (Desktop).
2. **The takeover chord typed with right-hand modifiers, with auto-repeat, or with other keys held.** It must take over, swallow every chord key until released (no stray Backspace reaches the app) and fire once. Tests: Task 6 `ChordWithRightHandModifiersTakesOver`, `ChordKeysStaySwallowedUntilReleased`, `AutoRepeatedBackspaceDoesNotRetrigger`.
3. **Win+L, a UAC prompt or a session switch in the middle of an AI call.** Control becomes `unavailable`, held input is released, the call stops at its next step, and after unlocking the state is `user`, then `ready` after the idle timeout, never straight back to `ai`. Tests: Task 11 `LeavingTheInputDesktopFailsOpenAndPreempts`, `RecoveryNeedsARehookAndGoesToUser`.
4. **Monitors added, removed or rearranged (negative coordinates) while the indicator shows.** The indicator must cover the new layout, not draw on stale rectangles. Tests: Task 8 `IndicatorLayoutCoversMonitorsLeftOfThePrimary`; Task 9 `IndicatorRebuildsWhenTheMonitorsChange`.
5. **A key held when a call starts, or a key-up that Windows never delivered (released on the lock screen).** The call is refused with `PHYSICAL_INPUT_HELD`, and the stale key clears after five all-up samples instead of blocking the AI forever. Tests: Task 4 `HeldPhysicalInputRejectsBegin`; Task 6 `FiveAllUpSamplesClearAMissedKeyUp`, `ADownSampleRestartsTheCount`.

---

## Decisions on upstream ambiguities

Each decision is binding for the tasks. Decisions 1-8 are the controller's, delegated by the user (2026-10-20).

1. **Upstream behaviour unless the spec or an earlier Decision says otherwise.** The state machine ports `desktop/control.py` (`_tick_locked` 230-249, `_handle` 517-559, `begin_call` 251-293, `checkpoint` 295-311, `end_call` 340-344, `start` 346-386, `_set_locked` 207-228, `_snapshot_locked` 151-172), the hooks port `desktop/control_hooks.py`, the worker ports `desktop/control_worker.py`, the missed-release check ports `desktop/control_recovery.py`, the gate ports `tools/control_notifications.py:163-296`, the policy ports `tools/control_policy.py`, the notifier ports `tools/control_notifications.py:26-160`, and the art ports `desktop/control_overlay_art.py` and `desktop/flash_overlay.py`. Each deviation below names its reason.
2. **Overlay technology** (spec §2 WinRT-first row and §7; follow-ups P4). Task 1 is a spike with a fixed decision rule: WinUI 3 (`AppWindow` plus XAML) is used only if its NativeAOT probe publish holds nothing but the exe and passes every check; else Composition plus Win2D (Windows App SDK `Microsoft.UI.Composition` visuals with Win2D-drawn surfaces in our own popup HWND) if it passes every check; else Win2D rendering into a premultiplied bitmap shown with `UpdateLayeredWindow` (upstream's mechanism, every piece already proven under NativeAOT). The checks: exe alone in the publish folder and no IL warning; click-through (`WindowFromPoint` at the layer's centre returns the window below); visible per-pixel alpha (screen pixel read with `WDA_NONE`); excluded from Windows.Graphics.Capture with `WDA_EXCLUDEFROMCAPTURE`; opacity changes without re-uploading the content; content updates run off the dispatcher thread; graphics DLLs still load only on the first layer. Visible checks show a 400x200 rectangle for about 2 s, so they run only with the user's approval; without it the result is `UpdateLayeredWindow`. Every later task uses the `IOverlayLayer` seam (Task 9), so only `LayeredWindow.cs` depends on the verdict.
3. **Safety on the live machine and testability** (controller decision 3). The hook logic is `PhysicalInputFilter`, pure and unit-tested; the Win32 part is thin. Desktop tests that suppress input do it against unsigned input that the test itself sends to its own window (`ForeignInjector`), which `ForeignInjectedPolicy.Physical` treats like physical input, keep suppression armed for at most 2 s, and disarm in `finally`. They are listed in Task 14.
4. **The control gate replaces `DesktopInputLock`** (P3 Decisions 18-20; spec §5). `DesktopInputLock`, `ToolServices.InputLock` and every `InputLock.EnterAsync` in the tools are deleted; the gate's `SemaphoreSlim(1)` serialises gated calls (and, with control disabled, every call except ControlStatus). The coordinator supplies `IActionCheckpoint` (through an `AsyncLocal` call context, Decisions 16) and `InputLedger`'s physical-key predicate (Decisions 18); a non-`ai` state releases the ledger (`control.py:188-200`).
5. **Process allowlist** (follow-ups P4; controller decision 5). New key `[control] process_allowlist`, a TOML array of process image names (default empty = every process allowed). Matching is case-insensitive on the file name; an entry without a `.` also matches `<entry>.exe`; an entry that is empty or contains `\` or `/` is a config error `control.process_allowlist entries must be process names such as "notepad.exe"`. It applies whether control is enabled or not, just before `SendInput`, to the process owning the target window: for each absolute mouse event the root window at its point, for wheel and button events without coordinates the root window under the cursor, for keyboard events the foreground window. A blocked batch sends nothing and fails with `Input blocked: the target window belongs to <name>, which is not in control.process_allowlist.` (`<name>` is `an unknown process` when the owner cannot be read; an unreadable owner never matches). A batch made only of key and button ups is never blocked, so a release can always reach Windows. App `launch`/`switch`/`resize` send no input and are not restricted. No environment variable (spec §8: only options with an upstream `envvar` get one).
6. **Kill-on-close job inheritance stays out of P4** (controller decision 6; spec §11 does not list it). The follow-up item is kept.
7. **Release verification** (controller decision 7): Task 14 as P3 Task 16. The smoke never takes control and shows nothing: the first session runs with `[control] enabled = false` and `DESKTOP_MCP_DISABLE_FLASH=1`; a second session with control on reads only ControlStatus (and Wait) and checks that the hooks started without loading the graphics stack; the probe's `overlay` step renders the art and pushes it to hidden layer windows under NativeAOT.
8. **Open questions** are at the top; the plan proceeds with the recommendations.
9. **States** (spec §7, §10 "the unreachable `takeover_pending` state is gone"): `disabled`, `unavailable`, `user`, `ready`, `ai`. Upstream's `TAKEOVER_PENDING` code and the amber `pending` indicator (`control_overlay.py:459-466`) are gone with it. Generation starts at 0 and increases on every state change (`_set_locked` 213).
10. **No hide-for-capture.** Upstream hides the indicator and releases suppression around every screenshot (`pause_for_capture`/`resume_after_capture`, `control.py:127-149`; `desktop/service.py:1366-1382`) and cancels the flash first. Spec §6 and §7 exclude every overlay window with `WDA_EXCLUDEFROMCAPTURE`, which Windows.Graphics.Capture honours, so captures never touch the overlay and suppression is not interrupted. A failed `SetWindowDisplayAffinity` is logged at warning level and the layer is used anyway (`control_overlay.py:71-74`).
11. **No `rotating` pause.** Upstream rejects new calls and preempts running ones while it reinstalls its hooks every 5 s (`control.py:264, 305`; `control_win32.py:304-315`), a race that can fail a healthy call. The new hooks are installed before the old ones are removed (as upstream does, `control.py:561-574`), so there is no gap; reinstalling never changes the state. A failed reinstall keeps the old hooks and requests fail-open.
12. **The heartbeat** (spec §7 Input-lock safety: "renewed by a heartbeat from the running tool call on the thread pool, not by a dispatcher timer"). Between calls the `ai` lease (15 s) also swallows input (`end_call` 340-344), and upstream renews from its watchdog thread (`control_worker.py:8-34`), not from the tool call. The heartbeat is the coordinator's loop on the thread pool (one long-running task): every 50 ms it drains up to 128 queued events, ticks the state machine and, while the state machine wants suppression, sets the hook's deadline to now + 1 s. If the loop stops, throws or cannot take the state lock without waiting (upstream `refresh_deadline` 20-22 uses a non-blocking acquire), renewals stop and the hook passes input within 1 s. The spec sentence is reworded in Task 14.
13. **Timers.** Spec §5 lists dispatcher timers for the idle timeout, hook reinstall, flash and missed key-up. The idle timeout is evaluated by the coordinator loop's tick (the state machine lives on the thread pool, §7); the dispatcher runs the hook reinstall (5 s), the missed-key-up sampler and dispatcher beat (0.1 s), the flash frames (30 ms, `flash_overlay.py:27`) and, while the indicator is visible, its refresh (50 ms: breathing opacity and the cursor ring, `control_overlay.py:29`).
14. **Monitor start and failure.** With control enabled, a hosted service starts the coordinator before the MCP server starts serving (as upstream's lifespan, `__main__.py:338-351`), bounded at 3 s (`control.py:370`). Unlike upstream, a failed start never stops the server: the state stays `unavailable`, the cause is logged at warning level, and the start is retried every 5 s; a missing Windows App SDK runtime is not retried (Open question 2). The first ControlStatus after startup therefore reads a settled state.
15. **Physical input before the indicator is visible** (`control.py:103-125, 525-532`): entering `ai` shows the indicator first and arms suppression only after the show is acknowledged (bounded 3 s, `control_overlay.py:269-283`) and only if no physical event, held key, takeover or fault happened since the state changed; a physical event in unarmed `ai` moves the state to `user`. A show that fails or times out is a fault (`__main__.py:294-297`), so the call gets `CONTROL_UNAVAILABLE`.
16. **Call context** (`desktop/control_context.py`). `ControlLease` carries the token, the executed-step counter and a closed flag; `lease.Enter()` sets an `AsyncLocal` in the gate's own frame, so `IActionCheckpoint` calls anywhere below it (thread-pool hops included) see the lease. Outside a gated call the checkpoint does nothing. The gate closes the lease when the call returns or is cancelled, so work that outlives the call fails its next checkpoint (`control_notifications.py:263-267`).
17. **Interruptions pass through the tools.** `ControlRejectedException` and `InputUnavailableException` are "interruptions" (`ControlErrors.IsInterruption`). `SpecTool` rethrows them instead of turning them into `Error calling tool ...`, and every broad `catch (Exception ...)` on an act path (`MultiEditTool`, `MultiSelectTool`, `AppService`) excludes them, so the gate maps them (`control_notifications.py:273-288`): `InputUnavailableException` becomes `CONTROL_UNAVAILABLE` when the state is `unavailable`, else `CONTROL_PREEMPTED`, with `executed_steps`. With control disabled, `InputUnavailableException` is an ordinary tool error.
18. **Ledger names to physical keys** (follow-ups P4: Ctrl and Shift have two names). The predicate maps `key:ctrl` and `key:vk:11` to VK 0x11 with its aliases 0xA2, 0xA3; `key:shift` and `key:vk:10` to 0x10, 0xA0, 0xA1; `key:vk:12` to 0x12, 0xA4, 0xA5; any other `key:vk:<hex>` to its VK after left/right normalisation (`control_win32.py:171-178`; `control.py:324-335`); `mouse:left`, `mouse:right`, `mouse:middle` to hook buttons 1, 2, 3 (`control.py:337-338`). It is true when any mapped key's physical down was delivered to the system (forwarded and not swallowed downstream, `control_hooks.py:46-51, 100-104`); then the ledger does not inject the up. Unknown names map to nothing (false).
19. **`InputLedger.BlockNew` takes no lock** (`control_ledger.py:21-22` is a plain flag). `_blocked` becomes a volatile field; `Press` still checks it under the lock. A press that passes the check just before a block is released by the coordinator's `ReleaseAll`.
20. **Rejection text** (`control_notifications.py:171-178`): `json.dumps({"code": C, "status": S}, ensure_ascii=False)` with Python's default separators `", "` and `": "`, status keys in the order `state, generation, wait_seconds, enabled, policy, idle_timeout_seconds`, then `executed_steps` for `CONTROL_PREEMPTED`; floats as Python prints them (`PyText.FloatRepr`). ControlStatus text is compact JSON in the same key order, as DisplayInventory (P2 Decisions 15), with the same object as `structuredContent` (spec §4).
21. **Policy classification** ports `tools/control_policy.py` exactly: a missing `mode` is `launch` for App and none for Process and Clipboard; a missing, non-string or unknown mode is direct (gated); unknown tools are direct; ControlStatus is never gated. `strict` gates everything else.
22. **Notifications** (`control_notifications.py:125-160`; spec §7): `notifications/message` with level `notice`, logger `desktop-mcp.control` and data `{"event": E, "status": S}`; `USER_CONTROL` when the state becomes `user` from `ready` or `ai`, `AI_CONTROL_AVAILABLE` when it becomes `ready` from `user`, nothing otherwise; events older than the last delivered generation are dropped; a failed send forgets the session. The server declares the `logging` capability in `initialize` (the MCP specification requires it for a server that sends `notifications/message`); sends do not depend on the client's `logging/setLevel`, as upstream (`send_log_message` ignores it). Sessions are remembered by the call-tool filter; the TTL is unlimited for stdio (P5 passes 60 s for HTTP and zero for stateless HTTP).
23. **Raw Input window** (spec §5: "a message-only window that receives Raw Input (`RIDEV_INPUTSINK`)"; upstream uses a hidden top-level window, `control_win32.py:285`). Message-only (`HWND_MESSAGE` parent) first; if the Desktop test `RawInputReachesTheMonitorWindow` fails, a hidden zero-size top-level window with no styles (upstream's) is used and spec §5 is updated.
24. **Mouse takeover** (`control.py:542-556`): the sum of `hypot(dx, dy)` of relative Raw Input motion from one non-null device, counted only for events stamped after entering `ai`, reset after a 0.5 s pause or a device change, reaching `mouse_takeover_units` (default 500). Absolute Raw Input (`MOUSE_MOVE_ABSOLUTE`) and the null device are ignored (`control_win32.py:213-217`). With Open question 1's answer, foreign injected motion sums hook-point deltas in pixels against `mouse_takeover_pixels` with the same pause and reset rules.
25. **Missed key-ups** (`control_recovery.py`): on the 0.1 s dispatcher tick, when the state is `user` or `ready`, the last input is at least the idle timeout ago and some physical key or button is recorded down, `GetAsyncKeyState` is read for all of them; five consecutive all-up samples (only the high bit means down) clear them; any change of the recorded sets restarts the count.
26. **The indicator** follows `control_overlay.py` and `control_overlay_art.py`: per monitor four edge strips (border 56 px, `_glow_alpha` fade), the notice panel with the three lines `AI is controlling this computer`, `Move the mouse a significant distance`, `or press Ctrl + Alt + Shift + Backspace to take over` (key badge in black) and its glow (pad 32), and an 88 px cursor ring; colour (45, 145, 255); breathing between alpha 140 and 255 over 3 s; the notice text itself does not breathe. The notice is left out on a monitor narrower than 232 px or too short (`control_overlay_art.py:105-107`; `control_overlay.py:170-171`). Fonts: Segoe UI Semibold for the title (hint size + 8) and Segoe UI for the hints, the hint size the largest of 24..8 that fits.
27. **The capture flash** (`flash_overlay.py`): colour #FF4500, 3.5 s, 30 ms frames, an 8 px band blurred with radius 14 (each edge strip 50 px deep), intensity `1 - |2t - 1|` for a full capture and ramp 0-0.15, hold to 0.65, fade to 1 for a region (`472-479`), quantised to 32 steps and hidden below 0.04. It is shown after every successful capture (Screenshot, and Snapshot with vision), whether control is enabled or not; a new flash replaces the previous one. Geometry: a region capture gets an outward halo around the region clipped to the virtual screen; a full or display-selected capture gets an inward halo (inset 6 px) on each captured monitor (upstream gives display selection the outward halo of the union, which falls off-screen). `DESKTOP_MCP_DISABLE_FLASH` (trimmed, case-insensitive `1`, `true`, `yes`, `on`) turns it off (`flash_overlay.py:39-41`, renamed per spec §1). The flash and its errors never affect the tool result.
28. **Overlay windows stay out of Snapshot and the label checks.** They belong to our process, so `WindowCatalog` already leaves them out of the window list and the UIA walk (P2), and they are `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, so `WindowFromPoint` and UIA hit testing pass through them and P3 Decisions 42 never sees one as covering a label. Task 9 pins both with Desktop tests.
29. **The unknown and the injected in the hook.** Our own events (signature `0x444D4350`, injected flag set) are ignored as upstream ignores all injected input. `LLMHF_LOWER_IL_INJECTED` counts as injected.
30. **Not in P4:** kill-on-close job inheritance (Decisions 6), target highlighting before acting (P3 follow-up), a `reason` field in rejections (the status shape is upstream's; the reason is logged).

## Follow-ups this plan picks up

From `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`:

| Follow-up | Where |
|---|---|
| Replace DesktopInputLock with the gate's lock; IActionCheckpoint and the physical-input predicate from the monitor | Tasks 11, 12; Decisions 4, 16, 18 |
| Ctrl and Shift have two ledger names | Task 11 (`LedgerNames`); Decisions 18 |
| Catch and log exceptions from dispatcher timer ticks | Task 2 |
| Document `TimerHandle.Dispose` off the dispatcher thread | Task 2 |
| `Func<Task>` overload and `DispatcherQueueSynchronizationContext` | Task 2 |
| Overlays: evaluate WinUI 3 / Composition plus Win2D before `UpdateLayeredWindow`; the `.pri` question | Task 1; Decisions 2 |
| Process allowlist (new config key, matched on the process, just before SendInput) | Task 5; Decisions 5 |
| Overlay click-through, `WDA_EXCLUDEFROMCAPTURE`, out of Snapshot's window list and UIA walk | Task 9; Decisions 10, 28 |
| Kill-on-close job inheritance (low priority) | Not in P4 (Decisions 6) |
| P2 Task 7: a `TryEnqueue` throw leaks the controller (no callers until P4) | Task 2 |

## File map

```
windows/
  probes/DesktopMcp.Probe/OverlayProbe.cs                     (spike, then the hidden AOT check; Tasks 1, 14)
  probes/DesktopMcp.Probe/Program.cs                          (overlay-spike, overlay steps)
  scripts/smoke-stdio.ps1, scripts/build-release.ps1          (Task 14)
  src/DesktopMcp.Core/Control/ControlPolicy.cs                (Task 3)
  src/DesktopMcp.Core/Control/ControlStatus.cs                (ControlState, ControlStatus, ControlCodes, ControlRejectedException, ControlJson; Task 3)
  src/DesktopMcp.Core/Control/ControlStateMachine.cs          (PhysicalEvent, BeginResult; Task 4)
  src/DesktopMcp.Core/Control/ProcessAllowlist.cs             (Task 5)
  src/DesktopMcp.Core/Config/{DesktopMcpConfig,ConfigFile,ServePlan}.cs   (process_allowlist; Task 5)
  src/DesktopMcp.Core/Input/PointerPath.cs                    (MouseCoordinates.Denormalize; Task 5)
  src/DesktopMcp.Desktop/NativeMethods.txt                    (Tasks 1, 5, 7, 9)
  src/DesktopMcp.Desktop/Dispatching/{DispatcherQueueThread,IDesktopDispatcher}.cs   (Task 2)
  src/DesktopMcp.Desktop/Dispatching/DispatcherQueueSynchronizationContext.cs        (Task 2)
  src/DesktopMcp.Desktop/Dispatching/DispatcherHost.cs        (Task 2)
  src/DesktopMcp.Desktop/Input/ProcessAllowlistInjector.cs    (Task 5)
  src/DesktopMcp.Desktop/Input/{DesktopInput,InputLedger}.cs  (allowlist ctor; lock-free BlockNew; Tasks 5, 11)
  src/DesktopMcp.Desktop/Control/PhysicalInputFilter.cs       (ForeignInjectedPolicy, HookVerdict, RawMouse, MissedReleaseSampler; Task 6)
  src/DesktopMcp.Desktop/Control/InputMonitor.cs              (IInputMonitor; Task 7)
  src/DesktopMcp.Desktop/Control/InteractiveDesktop.cs        (Task 7)
  src/DesktopMcp.Desktop/Control/LedgerNames.cs               (Task 11)
  src/DesktopMcp.Desktop/Control/DesktopControl.cs            (IDesktopControl, ControlLease, ControlCall, ControlErrors, DisabledControl; Task 11)
  src/DesktopMcp.Desktop/Control/ControlCoordinator.cs        (Task 11)
  src/DesktopMcp.Desktop/Overlay/OverlayLayout.cs             (LayerSpec, LayerArt, formulas; Task 8)
  src/DesktopMcp.Desktop/Overlay/OverlayArt.cs                (OverlayBitmap, Win2D renderers; Task 8)
  src/DesktopMcp.Desktop/Overlay/LayeredWindow.cs             (IOverlayLayer, IOverlayLayerFactory; Task 9)
  src/DesktopMcp.Desktop/Overlay/ControlIndicator.cs          (IControlIndicator; Task 9)
  src/DesktopMcp.Desktop/Overlay/CaptureFlash.cs              (ICaptureFlash, NoFlash; Task 10)
  src/DesktopMcp.Desktop/{DesktopMachine,Snapshots/SnapshotService}.cs (the flash after capture; Task 10)
  src/DesktopMcp.Desktop/Apps/AppService.cs                   (interruptions pass through; Task 12)
  src/DesktopMcp.Server/Control/ControlGate.cs                (ControlGateFilter; Task 12)
  src/DesktopMcp.Server/Control/{SessionRegistry,ControlNotifier}.cs      (Task 13)
  src/DesktopMcp.Server/Hosting/{StdioServer,ControlHostedService}.cs     (Tasks 12, 13)
  src/DesktopMcp.Server/Tools/ControlStatusTool.cs            (Task 12)
  src/DesktopMcp.Server/Tools/{ToolServices,ToolRegistry,ToolResults,SpecTool}.cs   (Task 12)
  src/DesktopMcp.Server/Tools/{Click,Move,Scroll,Type,Shortcut,MultiSelect,MultiEdit,App}Tool.cs (no InputLock; Task 12)
  src/DesktopMcp.Server/Tools/DesktopInputLock.cs             (deleted; Task 12)
  tests/DesktopMcp.Core.Tests/Control/{StepClock,ControlPolicyTests,ControlJsonTests,ControlStateMachineTests,ProcessAllowlistTests}.cs
  tests/DesktopMcp.Core.Tests/Config/ConfigFileTests.cs, Input/PointerPathTests.cs (additions)
  tests/DesktopMcp.Desktop.Tests/DispatcherQueueThreadTests.cs, Dispatching/DispatcherContextTests.cs
  tests/DesktopMcp.Desktop.Tests/Input/{ProcessAllowlistInjectorTests,InputLedgerTests}.cs
  tests/DesktopMcp.Desktop.Tests/Control/{PhysicalInputFilterTests,InputMonitorDesktopTests,LedgerNamesTests,ControlCoordinatorTests}.cs
  tests/DesktopMcp.Desktop.Tests/Control/ForeignInjector.cs
  tests/DesktopMcp.Desktop.Tests/Overlay/{OverlayLayoutTests,OverlayArtTests,ControlIndicatorTests,OverlayWindowDesktopTests,CaptureFlashTests}.cs
  tests/DesktopMcp.Desktop.Tests/Snapshots/SnapshotServiceTests.cs (flash addition)
  tests/DesktopMcp.Server.Tests/Control/{FakeControl,ControlGateTests,ControlNotifierTests}.cs
  tests/DesktopMcp.Server.Tests/Tools/{ControlStatusToolTests,ClickToolTests,MultiToolTests,FakeDesktop}.cs
  tests/DesktopMcp.Server.Tests/Hosting/StdioServerTests.cs (additions)
docs/superpowers/specs/2026-10-19-windows-port-design.md     (Tasks 1, 14)
docs/superpowers/plans/2026-10-19-windows-p0-followups.md    (Tasks 1, 14)
```

## Tasks

1. Overlay technology spike
2. Dispatcher hardening and the shared dispatcher
3. Core control model: policy, status, codes, JSON
4. Core control state machine
5. Process allowlist
6. Physical input filter: hook decisions, takeover chord, raw mouse, missed key-ups
7. Input monitor on the dispatcher thread
8. Overlay layout and art
9. Layer windows and the control indicator
10. Capture flash
11. Control coordinator: heartbeat, health, ledger, checkpoints
12. Control gate, ControlStatus and the tools without DesktopInputLock
13. Control notifier and sessions
14. Release verification and documentation

---

### Task 1: Overlay technology spike

Decisions 2. The deliverable is the verdict (one of `WinUI3`, `Composition`, `UpdateLayeredWindow`), recorded in spec §12 and the follow-ups, plus the probe code for the winning path only. Task 9 implements `LayeredWindow.cs` from the verdict.

**Files:**
- Create: `windows/probes/DesktopMcp.Probe/OverlayProbe.cs`
- Modify: `windows/probes/DesktopMcp.Probe/Program.cs` (`overlay-spike composition|ulw` step and the usage comment), `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (only what the winning path needs; for ULW: `CreateWindowEx`, `DestroyWindow`, `RegisterClassEx`, `UnregisterClass`, `DefWindowProc`, `UpdateLayeredWindow`, `CreateDIBSection`, `CreateCompatibleDC`, `DeleteDC`, `SelectObject`, `DeleteObject`, `GetDC`, `ReleaseDC`, `SetWindowDisplayAffinity`, `ShowWindow`, `SetWindowPos`, `IsWindowVisible`, `BLENDFUNCTION`, `WINDOW_EX_STYLE`, `WINDOW_DISPLAY_AFFINITY`), `docs/superpowers/specs/2026-10-19-windows-port-design.md` (§12 row), `docs/superpowers/plans/2026-10-19-windows-p0-followups.md` (the overlays line)

**Interfaces:**
- Consumes: `DispatcherQueueThread.Start()` (P2), `GraphicsCaptureService` (P2), `Win32WindowSystem` (P2/P3), Win2D `CanvasDevice.GetSharedDevice()`.
- Produces: the verdict (Decisions 2) and `desktop-mcp-probe overlay-spike <candidate>` printing one line per check, `overlay-spike: <candidate> <check>: OK|FAIL (<detail>)`, and a final `overlay-spike: <candidate> -> ACCEPT|REJECT`; exit code 0 only for ACCEPT.

- [ ] **Step 1: WinUI 3 check (throwaway, never committed)**

In a scratch copy of the repository (`git worktree add ../desktop-mcp-winui-spike HEAD`), in the probe project only: remove the `Microsoft.WindowsAppSDK.WinUI` `ExcludeAssets="all"` entry, add `<UseWinUI>true</UseWinUI>`, add a minimal `Application` that opens one `Microsoft.UI.Xaml.Window` without XAML markup, and run `dotnet publish probes/DesktopMcp.Probe -c Release -r win-x64 -o <scratch>/out`.
Expected verdict input: list every file in `<scratch>/out` besides `desktop-mcp-probe.exe` and `.pdb`, and the IL warnings. Any extra file (a `.pri`, `.xbf`, `Microsoft.UI.Xaml` asset) or warning rejects WinUI 3; record the list. Delete the worktree (`git worktree remove --force`).

- [ ] **Step 2: Write the two candidates in `OverlayProbe.cs`**

Both create one 400x200 layer at the primary monitor's top-left corner on the dispatcher thread with `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` and `WS_POPUP`, fill it with (45, 145, 255) at alpha 255 drawn by Win2D on the thread pool, and run the checks of Decisions 2 in this order: `graphics-not-loaded` (no d2d1.dll before the first Win2D call), `click-through` (`WindowFromPoint` at the centre is not the layer), `visible` (`WDA_NONE`; GDI `GetPixel` of the screen DC at the centre within 8 of the colour), `capture-excluded` (`WDA_EXCLUDEFROMCAPTURE`; a `GraphicsCaptureService` capture of the layer's rect has no pixel within 8 of the colour), `opacity` (set opacity 128 without new content; the screen pixel changes), `off-dispatcher` (content uploaded from a thread-pool thread without an exception), `cleanup` (window destroyed, class unregistered).
- `composition`: add `WS_EX_NOREDIRECTIONBITMAP`; a `Microsoft.UI.Composition.Compositor` created on the dispatcher thread; a `ContentIsland` with a `SpriteVisual` whose brush is a `CompositionDrawingSurface` from `CanvasComposition.CreateCompositionGraphicsDevice(compositor, CanvasDevice.GetSharedDevice())`, hosted with `Microsoft.UI.Content.DesktopChildSiteBridge.Create(compositor, Win32Interop.GetWindowIdFromWindow(hwnd))`; opacity through `SpriteVisual.Opacity`.
- `ulw`: Win2D `CanvasRenderTarget` → `GetPixelBytes()` (premultiplied BGRA) → a top-down 32-bit DIB section → `UpdateLayeredWindow(..., ULW_ALPHA)`; opacity through `UpdateLayeredWindow` with a null source DC and only the `BLENDFUNCTION` (`SourceConstantAlpha`).

- [ ] **Step 3: Build the probe under NativeAOT**

Run: `dotnet publish probes/DesktopMcp.Probe -c Release -r win-x64 -o probes/out`
Expected: no warnings; `probes/out` holds only `desktop-mcp-probe.exe` and its `.pdb`. A composition build that adds a file or a warning rejects `composition`.

- [ ] **Step 4: Run both candidates (needs the user's approval)**

Ask the user: each run shows a blue 400x200 rectangle in the primary monitor's top-left corner for about 2 s; it takes no input and no focus. With approval, run `probes/out/desktop-mcp-probe.exe overlay-spike composition`, then `... overlay-spike ulw`, hands off the mouse.
Expected: one line per check and a verdict line for each. Without approval, skip this step: the verdict is `UpdateLayeredWindow` (Decisions 2).

- [ ] **Step 5: Apply the decision rule and keep only the winner**

Verdict: `WinUI3` if Step 1 found no extra file and no warning (then stop and ask the controller: Task 9 assumes a non-XAML path); else `Composition` if every composition check said OK; else `UpdateLayeredWindow`. Delete the losing candidate's code from `OverlayProbe.cs` (and its `NativeMethods.txt` entries), keep the winner as `overlay-spike`. In spec §12 add a row `Overlay technology (P4 Task 1)` with the verdict and the measured check lines; in the follow-ups replace the "Overlays: evaluate ..." line with the verdict and its date.

- [ ] **Step 6: Commit**

```bash
git add windows/probes/DesktopMcp.Probe windows/src/DesktopMcp.Desktop/NativeMethods.txt docs/superpowers/specs/2026-10-19-windows-port-design.md docs/superpowers/plans/2026-10-19-windows-p0-followups.md
git commit -m "Choose the overlay technology with a NativeAOT probe spike" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Dispatcher hardening and the shared dispatcher

The follow-ups' dispatcher items, and one lazily started dispatcher that the monitor, the indicator and the flash share (P2 Decisions 21: P4 starts it first).

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Dispatching/DispatcherQueueSynchronizationContext.cs`, `DispatcherHost.cs`
- Modify: `windows/src/DesktopMcp.Desktop/Dispatching/DispatcherQueueThread.cs`, `IDesktopDispatcher.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Dispatching/DispatcherContextTests.cs`, `windows/tests/DesktopMcp.Desktop.Tests/DispatcherQueueThreadTests.cs` (additions, Desktop category like the file's existing real-dispatcher tests)

**Interfaces:**
- Consumes: `DispatcherQueueThread` (P2).
- Produces:
  - `IDesktopDispatcher` gains `Task InvokeAsync(Func<Task> func)` (runs `func` on the dispatcher with the dispatcher's `SynchronizationContext`, so every `await` inside resumes there; the returned task completes when `func`'s task does) and `event Action<Exception>? UnhandledException` (raised on the dispatcher thread for an exception thrown by a timer tick).
  - `StartTimerAsync` ticks are wrapped: an exception is caught, raised through `UnhandledException`, and the timer keeps running. The XML doc of `TimerHandle.Dispose` says that a dispose from another thread may let one more tick run.
  - `public sealed class DispatcherQueueSynchronizationContext : SynchronizationContext` with an internal ctor `(Func<Action, bool> tryEnqueue, Func<bool> hasThreadAccess)`; `Post` enqueues (a rejected enqueue throws `ObjectDisposedException`); `Send` runs inline with thread access and throws `NotSupportedException` otherwise; `CreateCopy` returns itself. `DispatcherQueueThread.Start` installs it on the thread in its first work item.
  - `internal static class TimerTicks { public static void Guard(Action tick, Action<Exception> report); }` (catches, reports; a throwing `report` is swallowed).
  - `public sealed class DispatcherHost` with `public static DispatcherHost Shared { get; }`, `public Task<IDesktopDispatcher> GetAsync(CancellationToken ct)` (starts the thread on first use on a thread-pool thread; concurrent callers share one start; a failed start is not cached, so the next call retries), `public bool IsStarted { get; }`, `public ValueTask ShutdownAsync()` (disposes the started dispatcher; later `GetAsync` calls throw `ObjectDisposedException`); internal ctor `(Func<IDesktopDispatcher> start)`.
  - `DispatcherQueueThread.WaitForThreadId` also shuts the controller down when `tryEnqueue` throws (P2 Task 7 leftover).

- [ ] **Step 1: Write the failing tests**

```csharp
public class DispatcherContextTests
{
    [Fact]
    public void PostEnqueuesAndSendNeedsThreadAccess()
    {
        var queued = new List<Action>();
        var context = new DispatcherQueueSynchronizationContext(work => { queued.Add(work); return true; }, () => false);
        var ran = 0;
        context.Post(_ => ran++, null);
        Assert.Equal(0, ran);
        queued.Single()();
        Assert.Equal(1, ran);
        Assert.Throws<NotSupportedException>(() => context.Send(_ => { }, null));
    }

    [Fact]
    public void ARejectedPostThrows() =>
        Assert.Throws<ObjectDisposedException>(() =>
            new DispatcherQueueSynchronizationContext(_ => false, () => false).Post(_ => { }, null));

    [Fact]
    public void ATickExceptionIsReportedNotThrown()
    {
        Exception? reported = null;
        TimerTicks.Guard(() => throw new InvalidOperationException("tick"), e => reported = e);
        Assert.Equal("tick", reported?.Message);
        TimerTicks.Guard(() => throw new InvalidOperationException("again"), _ => throw new Exception("report failed"));
    }

    [Fact]
    public async Task HostStartsOnceForConcurrentCallersAndRetriesAFailedStart()
    {
        var starts = 0;
        var host = new DispatcherHost(() => ++starts == 1 ? throw new InvalidOperationException("no runtime") : new FakeDispatcher());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetAsync(default));
        var both = await Task.WhenAll(host.GetAsync(default), host.GetAsync(default));
        Assert.Same(both[0], both[1]);
        Assert.Equal(2, starts);
        await host.ShutdownAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.GetAsync(default));
    }

    [Fact]
    public void WaitForThreadIdShutsDownWhenTheEnqueueThrows()
    {
        var shutdown = false;
        Assert.Throws<DesktopException>(() => DispatcherQueueThread.WaitForThreadId(
            _ => throw new COMException("closed", unchecked((int)0x80000013)), () => { shutdown = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(1)));
        Assert.True(shutdown);
    }
}
```

`FakeDispatcher` (same file) implements `IDesktopDispatcher` by running work inline. In `DispatcherQueueThreadTests` (Desktop): `AwaitInsideInvokeAsyncResumesOnTheDispatcher` (two awaits of `Task.Delay(10)` inside `InvokeAsync(Func<Task>)` each observe `HasThreadAccess`) and `AThrowingTickIsReportedAndTheTimerKeepsTicking` (a 20 ms timer whose first tick throws reports once through `UnhandledException`, and ticks at least twice more).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile (`DispatcherQueueSynchronizationContext` does not exist).

- [ ] **Step 3: Implement**

As the Interfaces block says. `WaitForThreadId` wraps the `tryEnqueue` call in the existing failure path (the exception becomes `The new DispatcherQueue rejected work: <message>`).

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~Dispatch"`
Expected: PASS. Then by hand (interactive session): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~DispatcherQueueThreadTests"` → PASS (no window, no input).

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Dispatching windows/tests/DesktopMcp.Desktop.Tests
git commit -m "Harden the dispatcher and share one lazily started instance" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Core control model: policy, status, codes, JSON

Decisions 9, 20, 21.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Control/ControlPolicy.cs`, `ControlStatus.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Control/ControlPolicyTests.cs`, `ControlJsonTests.cs`

**Interfaces:**
- Consumes: `PyText.FloatRepr` (P1/P2).
- Produces (namespace `DesktopMcp.Core.Control`):
  - `public enum ControlState { Disabled, Unavailable, User, Ready, Ai }` and `public static class ControlStates { public static string Wire(ControlState state); }` (`disabled`, `unavailable`, `user`, `ready`, `ai`).
  - `public sealed record ControlStatus(ControlState State, long Generation, double WaitSeconds, bool Enabled, string Policy, double IdleTimeoutSeconds)`.
  - `public static class ControlCodes { public const string UserControl = "USER_CONTROL", ControlUnavailable = "CONTROL_UNAVAILABLE", ControlPreempted = "CONTROL_PREEMPTED", PhysicalInputHeld = "PHYSICAL_INPUT_HELD"; public static string ForState(ControlState state); }` (`User` → `USER_CONTROL`, anything else → `CONTROL_UNAVAILABLE`; `control_notifications.py:229-235`).
  - `public sealed class ControlRejectedException(string code, ControlStatus status, long? executedSteps = null) : Exception` with `Code`, `Status`, `ExecutedSteps`; message `"<code>: <wire state>"` (`control.py:34`).
  - `public static class ControlJson { public static string Status(ControlStatus status); public static string Rejection(ControlRejectedException rejection); }`.
  - `public static class ControlPolicy { public static readonly IReadOnlyList<string> Policies; public static string? ModeOf(string toolName, IDictionary<string, JsonElement>? arguments); public static bool RequiresControl(string policy, string toolName, string? mode); }` (`RequiresControl` throws `ArgumentException("Unknown control policy: <p>")` for another policy, `control_policy.py:42`).

- [ ] **Step 1: Write the failing tests**

```csharp
public class ControlPolicyTests
{
    [Theory]
    [InlineData("relaxed", "Click", null, true)]
    [InlineData("relaxed", "PowerShell", null, false)]
    [InlineData("balanced", "PowerShell", null, true)]
    [InlineData("balanced", "Snapshot", null, false)]
    [InlineData("strict", "Snapshot", null, true)]
    [InlineData("strict", "ControlStatus", null, false)]
    [InlineData("relaxed", "App", "launch", false)]
    [InlineData("balanced", "App", "launch_executable", true)]
    [InlineData("relaxed", "App", "switch", true)]
    [InlineData("relaxed", "Process", "kill", false)]
    [InlineData("balanced", "Process", "kill", true)]
    [InlineData("balanced", "Clipboard", "get", false)]
    [InlineData("relaxed", "Process", null, true)]      // a missing mode counts as direct
    [InlineData("relaxed", "App", "fly", true)]         // an unknown mode counts as direct
    [InlineData("relaxed", "SomeNewTool", null, true)]  // an unknown tool counts as direct
    public void UpstreamsPolicyTable(string policy, string tool, string? mode, bool gated) =>
        Assert.Equal(gated, ControlPolicy.RequiresControl(policy, tool, mode));

    [Fact]
    public void ModeDefaultsFollowUpstream()
    {
        static IDictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.Equal("launch", ControlPolicy.ModeOf("App", null));
        Assert.Equal("launch", ControlPolicy.ModeOf("App", Args("{}")));
        Assert.Null(ControlPolicy.ModeOf("App", Args("""{"mode": null}""")));
        Assert.Null(ControlPolicy.ModeOf("Process", Args("{}")));
        Assert.Null(ControlPolicy.ModeOf("Process", Args("""{"mode": 3}""")));
        Assert.Equal("kill", ControlPolicy.ModeOf("Process", Args("""{"mode": "kill"}""")));
    }

    [Fact]
    public void AnUnknownPolicyIsAnError() =>
        Assert.Equal("Unknown control policy: loose", Assert.Throws<ArgumentException>(() => ControlPolicy.RequiresControl("loose", "Click", null)).Message);
}

public class ControlJsonTests
{
    private static readonly ControlStatus User = new(ControlState.User, 4, 9.5, true, "relaxed", 10.0);

    [Fact]
    public void StatusIsCompactWithUpstreamsKeyOrder() =>
        Assert.Equal("""{"state":"ready","generation":1,"wait_seconds":0.0,"enabled":true,"policy":"balanced","idle_timeout_seconds":12.5}""",
            ControlJson.Status(new(ControlState.Ready, 1, 0, true, "balanced", 12.5)));

    [Fact]
    public void RejectionUsesPythonsDefaultSeparators() =>
        Assert.Equal("""{"code": "USER_CONTROL", "status": {"state": "user", "generation": 4, "wait_seconds": 9.5, "enabled": true, "policy": "relaxed", "idle_timeout_seconds": 10.0}}""",
            ControlJson.Rejection(new ControlRejectedException(ControlCodes.UserControl, User)));

    [Fact]
    public void PreemptionAddsExecutedStepsLast() =>
        Assert.EndsWith("""
            "idle_timeout_seconds": 10.0, "executed_steps": 3}}
            """, ControlJson.Rejection(new ControlRejectedException(ControlCodes.ControlPreempted, User, 3)));

    [Fact]
    public void DisabledStatus() =>
        Assert.Equal("""{"state":"disabled","generation":0,"wait_seconds":0.0,"enabled":false,"policy":"relaxed","idle_timeout_seconds":10.0}""",
            ControlJson.Status(new(ControlState.Disabled, 0, 0, false, "relaxed", 10.0)));
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~DesktopMcp.Core.Tests.Control"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement the two files**

`ControlPolicy` holds upstream's `DIRECT`, `POSSIBLE`, `OTHER` and `MODES` tables (`control_policy.py:8-31`) verbatim. `ControlJson` writes with `Utf8JsonWriter` (`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, floats through `WriteRawValue(PyText.FloatRepr(...))`); `Rejection` writes the same tokens with Python's separators by hand (`, ` and `: `), since `Utf8JsonWriter` cannot.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~DesktopMcp.Core.Tests.Control"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Control windows/tests/DesktopMcp.Core.Tests/Control
git commit -m "Add the control policy table, status model and JSON forms" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Core control state machine

Spec §7 states and rules; Decisions 1, 9, 11, 15, 24. Pure and single-threaded: the coordinator (Task 11) serialises every call under its lock.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Control/ControlStateMachine.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Control/StepClock.cs`, `ControlStateMachineTests.cs`

**Interfaces:**
- Consumes: Task 3; `ControlSettings` (`DesktopMcp.Core.Config`, P1).
- Produces (namespace `DesktopMcp.Core.Control`):
  - `public enum PhysicalEventKind { Key, Pointer, RawMotion, ForeignMotion, Hotkey }`
  - `public readonly record struct PhysicalEvent(PhysicalEventKind Kind, long Timestamp, long Device = 0, int Dx = 0, int Dy = 0)` (timestamps in `TimeProvider.GetTimestamp()` units).
  - `public readonly record struct BeginResult(long Token, ControlStatus? Changed, bool NeedsIndicator)`
  - `public sealed class ControlStateMachine(ControlSettings settings, TimeProvider time)` with:
    - `ControlStatus Status { get; }` (no tick); `ControlState RawState { get; }`; `bool SuppressionWanted { get; }` (state `ai`, armed, not faulted, no takeover pending); `bool Armed { get; }`; `bool Faulted { get; }`; `long LastInput { get; }` (max of last user and last physical);
    - `bool PhysicalHeld { get; set; }` and `bool TakeoverPending { get; set; }` (copied in from the monitor by the coordinator before each call);
    - `ControlStatus? Start(long lastPhysical)`, `ControlStatus? Handle(PhysicalEvent e)`, `ControlStatus? Tick()`, `BeginResult Begin()`, `bool Arm(long generation)`, `void Checkpoint(long token, long executedSteps, bool requestClosed)`, `void End(long token)`, `ControlStatus? Fault()`, `ControlStatus? Recover()`, `ControlStatus? Stop()`;
    - a method returning `ControlStatus?` returns the new status when the state changed, else null.
  - `public const double LeaseSeconds = 15; public const double MotionPauseSeconds = 0.5;`

Rules (upstream line ranges in Decisions 1; the deviations are Decisions 9, 11, 15, 24):
- Reported state: `Disabled` when not enabled; `Unavailable` when faulted, stopped or never started; `User` when `TakeoverPending`; else the raw state. `WaitSeconds` = `max(0, LastInput + idle - now)` when the reported state is `User`, else 0.
- Tick: `ready` with recent input (a physical stamp within the idle timeout) → `user`; `user` with nothing held and `LastInput` at least idle ago → `ready`; `ai` with no active call and the lease over → `user` if recent input, else `ready`.
- Handle: always records the stamp as the last physical event (except `ForeignMotion` of zero length) and ticks first. `Hotkey` → `user` and clears `TakeoverPending`. `Key`, `Pointer`, `RawMotion`, `ForeignMotion` in `ready` or `user` → `user`; in `ai` while not armed → `user`. `RawMotion`/`ForeignMotion` in armed `ai`: path summing per Decisions 24 (`MouseTakeoverUnits` for raw, `MouseTakeoverPixels` for foreign), reaching the threshold → `user`.
- Begin (`begin_call`): when not enabled returns token 0 and changes nothing; ticks; `PhysicalHeld` → `PHYSICAL_INPUT_HELD`; faulted, stopped, `TakeoverPending` or a state other than `ready`/`ai` → `ControlCodes.ForState`; else enters `ai` (a new generation when it was `ready`, `NeedsIndicator` true), counts the active call, returns the generation as the token.
- Arm (`arm_visible`): true and armed only when the state is `ai` with that generation, nothing happened since entering `ai` (no physical event, held input, takeover or fault).
- Checkpoint: throws `CONTROL_PREEMPTED` with the current status and `executedSteps` when `requestClosed`, stopped, state not `ai`, generation ≠ token, takeover pending, faulted or not armed.
- End: active calls minus one; when none remain in `ai`, the lease ends `LeaseSeconds` from now.
- Fault → `unavailable` (sticky until Recover); Recover (only when faulted) → `user` with `LastInput` = now (`control.py:485-495`); Stop → `unavailable` for good.
- Every state change bumps the generation, disarms, resets the motion sums and records the time `ai` was entered.

- [ ] **Step 1: Write the failing tests**

`StepClock : TimeProvider` overrides `GetTimestamp()` (ticks of a settable `Now` that starts at 1,000 s, because a zero stamp means "no input yet", `control.py:231`; `TimestampFrequency` = `TimeSpan.TicksPerSecond`) and has `Advance(double seconds)` and `long Stamp()`.

```csharp
public class ControlStateMachineTests
{
    private readonly StepClock _clock = new();
    private ControlStateMachine Machine(double idle = 10, bool enabled = true) =>
        new(new ControlSettings(enabled, "relaxed", idle, 500, 500), _clock);
    private ControlStateMachine Ready() { var m = Machine(); m.Start(0); return m; }
    private PhysicalEvent Key() => new(PhysicalEventKind.Key, _clock.Stamp());
    private PhysicalEvent Raw(int dx, int dy, long device = 7) => new(PhysicalEventKind.RawMotion, _clock.Stamp(), device, dx, dy);
    private (ControlStateMachine M, long Token) InArmedAi()
    {
        var m = Ready();
        var begin = m.Begin();
        Assert.True(m.Arm(begin.Token));
        return (m, begin.Token);
    }

    [Fact]
    public void StartsReadyWithoutRecentInputAndUserWithIt()
    {
        Assert.Equal(ControlState.Ready, Ready().Status.State);
        _clock.Advance(100);
        var m = Machine();
        var recent = _clock.Stamp();
        _clock.Advance(2);
        Assert.Equal(new ControlStatus(ControlState.User, 1, 8.0, true, "relaxed", 10.0), m.Start(recent));
    }

    [Fact]
    public void PhysicalInputInReadyMovesToUserAndRejectsBegin()
    {
        var m = Ready();
        Assert.Equal(ControlState.User, m.Handle(Key())!.State);
        var error = Assert.Throws<ControlRejectedException>(() => m.Begin());
        Assert.Equal(ControlCodes.UserControl, error.Code);
        Assert.Equal(10.0, error.Status.WaitSeconds);
    }

    [Fact]
    public void UserReturnsToReadyAfterTheIdleTimeoutOnlyWithNothingHeld()
    {
        var m = Ready();
        m.Handle(Key());
        m.PhysicalHeld = true;
        _clock.Advance(10);
        Assert.Null(m.Tick());
        m.PhysicalHeld = false;
        Assert.Equal(ControlState.Ready, m.Tick()!.State);
    }

    [Fact]
    public void HeldPhysicalInputRejectsBegin()
    {
        var m = Ready();
        m.PhysicalHeld = true;
        Assert.Equal(ControlCodes.PhysicalInputHeld, Assert.Throws<ControlRejectedException>(() => m.Begin()).Code);
    }

    [Fact]
    public void BeginFromReadyEntersAiAndNeedsTheIndicatorOnce()
    {
        var m = Ready();
        var first = m.Begin();
        Assert.Equal((ControlState.Ai, 2L, true), (first.Changed!.State, first.Token, first.NeedsIndicator));
        m.Arm(first.Token);
        m.End(first.Token);
        var second = m.Begin();
        Assert.Equal((2L, (ControlStatus?)null, false), (second.Token, second.Changed, second.NeedsIndicator));
    }

    [Fact]
    public void RawPathLengthReachingTheThresholdTakesOver()
    {
        var (m, _) = InArmedAi();
        _clock.Advance(0.1);
        Assert.Null(m.Handle(Raw(300, 0)));
        _clock.Advance(0.1);
        Assert.Equal(ControlState.User, m.Handle(Raw(0, -200))!.State);   // path 500, displacement 360
    }

    [Fact]
    public void APauseOrAnotherDeviceRestartsThePath()
    {
        var (m, _) = InArmedAi();
        _clock.Advance(0.1); m.Handle(Raw(400, 0));
        _clock.Advance(0.6); Assert.Null(m.Handle(Raw(400, 0)));
        _clock.Advance(0.1); Assert.Null(m.Handle(Raw(400, 0, device: 8)));
    }

    [Fact]
    public void MotionStampedBeforeEnteringAiDoesNotCount()
    {
        var m = Ready();
        var early = Raw(600, 0);
        _clock.Advance(0.01);
        var begin = m.Begin();
        m.Arm(begin.Token);
        Assert.Null(m.Handle(early));
    }

    [Fact]
    public void KeysAreSwallowedWithoutTakeoverWhileArmedButTakeOverWhenNotArmed()
    {
        var (armed, _) = InArmedAi();
        Assert.Null(armed.Handle(Key()));
        var m = Ready();
        m.Begin();                                  // the indicator never acknowledged
        Assert.Equal(ControlState.User, m.Handle(Key())!.State);
    }

    [Fact]
    public void AnEventSinceEnteringAiPreventsArming()
    {
        var m = Ready();
        var begin = m.Begin();
        m.Handle(new PhysicalEvent(PhysicalEventKind.RawMotion, _clock.Stamp(), 7, 1, 0));
        Assert.False(m.Arm(begin.Token));
    }

    [Fact]
    public void CheckpointPreemptsAfterTakeoverWithExecutedSteps()
    {
        var (m, token) = InArmedAi();
        m.TakeoverPending = true;
        var error = Assert.Throws<ControlRejectedException>(() => m.Checkpoint(token, 3, requestClosed: false));
        Assert.Equal((ControlCodes.ControlPreempted, ControlState.User, 3L), (error.Code, error.Status.State, error.ExecutedSteps));
        Assert.Equal(ControlState.User, m.Handle(new PhysicalEvent(PhysicalEventKind.Hotkey, _clock.Stamp()))!.State);
        Assert.False(m.TakeoverPending);
    }

    [Fact]
    public void CheckpointPreemptsAClosedRequestAndAStaleToken()
    {
        var (m, token) = InArmedAi();
        Assert.Throws<ControlRejectedException>(() => m.Checkpoint(token, 0, requestClosed: true));
        Assert.Throws<ControlRejectedException>(() => m.Checkpoint(token - 1, 0, requestClosed: false));
        m.Checkpoint(token, 0, requestClosed: false);
    }

    [Fact]
    public void TheLeaseKeepsAiFifteenSecondsAfterTheLastCall()
    {
        var (m, token) = InArmedAi();
        m.End(token);
        _clock.Advance(14.9);
        Assert.Null(m.Tick());
        _clock.Advance(0.1);
        Assert.Equal(ControlState.Ready, m.Tick()!.State);
    }

    [Fact]
    public void FaultIsStickyAndRecoverGoesToUser()
    {
        var (m, token) = InArmedAi();
        Assert.Equal(ControlState.Unavailable, m.Fault()!.State);
        Assert.False(m.SuppressionWanted);
        Assert.Equal(ControlCodes.ControlUnavailable, Assert.Throws<ControlRejectedException>(() => m.Begin()).Code);
        Assert.Throws<ControlRejectedException>(() => m.Checkpoint(token, 0, false));
        Assert.Equal(new ControlStatus(ControlState.User, 4, 10.0, true, "relaxed", 10.0), m.Recover());
    }

    [Fact]
    public void DisabledNeverTicksOrGates()
    {
        var m = Machine(enabled: false);
        Assert.Null(m.Handle(Key()));
        Assert.Equal(new BeginResult(0, null, false), m.Begin());
        Assert.Equal(new ControlStatus(ControlState.Disabled, 0, 0, false, "relaxed", 10.0), m.Status);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ControlStateMachineTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `ControlStateMachine`**

As the rules above. `Stop` is final: later `Start` and `Recover` return null.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~DesktopMcp.Core.Tests.Control"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Control windows/tests/DesktopMcp.Core.Tests/Control
git commit -m "Add the pure control state machine" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Process allowlist

Decisions 5.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Control/ProcessAllowlist.cs`, `windows/src/DesktopMcp.Desktop/Input/ProcessAllowlistInjector.cs`
- Modify: `windows/src/DesktopMcp.Core/Config/DesktopMcpConfig.cs` (`ControlConfig.ProcessAllowlist`), `ConfigFile.cs`, `ServePlan.cs` (`ControlSettings.ProcessAllowlist`, an init property defaulting to empty, copied from the config), `windows/src/DesktopMcp.Core/Input/PointerPath.cs` (`MouseCoordinates.Denormalize`), `windows/src/DesktopMcp.Desktop/Input/DesktopInput.cs` (public ctor overload with the allowlist)
- Test: `windows/tests/DesktopMcp.Core.Tests/Control/ProcessAllowlistTests.cs`, `Config/ConfigFileTests.cs`, `Input/PointerPathTests.cs` (additions), `windows/tests/DesktopMcp.Desktop.Tests/Input/ProcessAllowlistInjectorTests.cs`

**Interfaces:**
- Consumes: `IInputInjector`, `InjectedInput`, `MouseFlags`, `InputBlockedException` (P3); `IWindowSystem` (P2/P3: `WindowFromPoint`, `GetAncestor`/root, `GetForegroundWindow`, `GetProcessId`, `GetProcessName`).
- Produces:
  - `public sealed class ProcessAllowlist` (Core): `public static ProcessAllowlist Everything { get; }`, `public ProcessAllowlist(IEnumerable<string> names)`, `public bool AllowsAll { get; }`, `public bool Allows(string? processName)`, `public const string InvalidEntry = "control.process_allowlist entries must be process names such as \"notepad.exe\"";`, `public static string BlockedText(string? processName)`.
  - `ControlConfig.ProcessAllowlist : List<string>` (default empty); `ControlSettings.ProcessAllowlist : IReadOnlyList<string>` (init, default `[]`).
  - `MouseCoordinates.Denormalize((int X, int Y) normalized, ScreenRect virtualScreen) : ScreenPoint` (inverse of `Normalize`, round half to even).
  - `internal sealed class ProcessAllowlistInjector(IInputInjector inner, ProcessAllowlist allowlist, IWindowSystem windows, Func<ScreenRect> virtualScreen, Func<ScreenPoint> cursor) : IInputInjector`.
  - `public DesktopInput(InputLedger ledger, IClipboard clipboard, IActionCheckpoint checkpoint, ProcessAllowlist allowlist)`; the existing 3-argument ctor uses `ProcessAllowlist.Everything`. When `AllowsAll`, no decorator is created.

- [ ] **Step 1: Write the failing tests**

```csharp
public class ProcessAllowlistTests
{
    [Theory]
    [InlineData("notepad.exe", "Notepad.EXE", true)]
    [InlineData("notepad", "notepad.exe", true)]
    [InlineData("notepad", "notepad.com", false)]
    [InlineData("code.exe", "notepad.exe", false)]
    [InlineData("code.exe", null, false)]
    public void MatchesFileNamesCaseInsensitively(string entry, string? process, bool allowed) =>
        Assert.Equal(allowed, new ProcessAllowlist([entry]).Allows(process));

    [Fact]
    public void EmptyAllowsEverything() => Assert.True(new ProcessAllowlist([]).AllowsAll);

    [Theory]
    [InlineData("")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("tools/x.exe")]
    public void PathsAndBlanksAreRejected(string entry) =>
        Assert.Equal(ProcessAllowlist.InvalidEntry, Assert.Throws<ArgumentException>(() => new ProcessAllowlist([entry])).Message);

    [Fact]
    public void BlockedTexts()
    {
        Assert.Equal("Input blocked: the target window belongs to code.exe, which is not in control.process_allowlist.", ProcessAllowlist.BlockedText("code.exe"));
        Assert.Equal("Input blocked: the target window belongs to an unknown process, which is not in control.process_allowlist.", ProcessAllowlist.BlockedText(null));
    }
}
```

`ConfigFileTests` additions: `ProcessAllowlistIsAListOfNames` (`[control]\nprocess_allowlist = ["notepad.exe", "code"]` → both entries), `ProcessAllowlistMustBeAList` (`process_allowlist = "notepad.exe"` → the existing `ListOfStrings` message for `control.process_allowlist`), `ProcessAllowlistRejectsPaths` (→ `InvalidEntry`). `ServePlanTests`: `ProcessAllowlistComesFromTheConfig`. `PointerPathTests`: `DenormaliseInvertsNormaliseOnANegativeOrigin` (for the four corners and (0,0) of `new ScreenRect(-1920, 0, 2560, 1600)`, `Denormalize(Normalize(p)) == p`).

```csharp
public class ProcessAllowlistInjectorTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1001, 1001);

    [Fact]
    public void AMouseEventOverAnAllowedProcessPasses()
    {
        var rig = new AllowlistRig(allowed: ["notepad.exe"], windowAt: _ => "notepad.exe", foreground: "code.exe");
        rig.Injector.Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove | MouseFlags.LeftDown, MouseCoordinates.Normalize(new(10, 10), Screen))]);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public void AKeyForAForegroundOutsideTheListSendsNothing()
    {
        var rig = new AllowlistRig(allowed: ["notepad.exe"], windowAt: _ => "notepad.exe", foreground: "code.exe");
        var error = Assert.Throws<InputBlockedException>(() => rig.Injector.Send([InjectedInput.Keyboard(new(0x41, '\0', false))]));
        Assert.Equal(ProcessAllowlist.BlockedText("code.exe"), error.Message);
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public void ABatchOfOnlyUpsIsNeverBlocked()
    {
        var rig = new AllowlistRig(allowed: ["notepad.exe"], windowAt: _ => "code.exe", foreground: "code.exe");
        rig.Injector.Send([InjectedInput.Keyboard(new(0x11, '\0', true)), InjectedInput.MouseAt(MouseFlags.AbsoluteMove | MouseFlags.LeftUp, (0, 0))]);
        Assert.Equal(2, rig.Sent.Count);
    }

    [Fact]
    public void AWheelWithoutCoordinatesUsesTheWindowUnderTheCursor()
    {
        var rig = new AllowlistRig(allowed: ["notepad.exe"], windowAt: p => p == new ScreenPoint(5, 5) ? "notepad.exe" : "code.exe", foreground: "code.exe", cursor: new(5, 5));
        rig.Injector.Send([InjectedInput.Wheel(120)]);
        Assert.Single(rig.Sent);
    }
}
```

`AllowlistRig` (same file) builds the injector over a recording inner injector and a fake `IWindowSystem` whose `WindowFromPoint` returns a handle per point and whose `GetProcessName` returns the given names.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Core.Tests; dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`ProcessAllowlistInjector.Send`: when every event is a key up or carries only `LeftUp`/`RightUp`/`MiddleUp` (with or without the move flags), forward. Otherwise collect the distinct target windows (absolute events: the root window at `Denormalize`d point; wheel or button events without `Absolute`: the root window at `cursor()`; keyboard: the foreground window), resolve each owner's image name, and throw `InputBlockedException(ProcessAllowlist.BlockedText(name))` for the first one not allowed, before forwarding anything. `ConfigFile` reads the key with the existing `ListOfStrings`, then constructs a `ProcessAllowlist` to validate the entries (mapping its `ArgumentException` to `ConfigException` with the same text).

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests; dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~Allowlist"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core windows/src/DesktopMcp.Desktop/Input windows/tests
git commit -m "Add the control.process_allowlist check before SendInput" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Physical input filter: hook decisions, takeover chord, raw mouse, missed key-ups

Everything the hook callbacks decide, with no Win32: `control_hooks.py`, `control_win32.py:171-218`, `control_recovery.py`, `control.py:422-431, 501-506`; Decisions 18, 24, 25, 29 and Open question 1. Every method here is called from the dispatcher thread except the setters the Interfaces block marks.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Control/PhysicalInputFilter.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Control/PhysicalInputFilterTests.cs`

**Interfaces:**
- Consumes: `PhysicalEvent`, `PhysicalEventKind`, `ControlState` (Tasks 3-4); `InputSignature.ExtraInfo` (P3).
- Produces (namespace `DesktopMcp.Desktop.Control`, internal):
  - `enum ForeignInjectedPolicy { Physical, Ignore }`, `enum HookVerdict { Pass, Swallow }`.
  - `sealed class PhysicalInputFilter(Func<long> now, long ticksPerSecond, ForeignInjectedPolicy foreign, Action blockLedger, int queueCapacity = 4096)` with:
    - hook entry points: `HookVerdict OnKey(uint vk, bool down, uint flags, nuint extraInfo)`, `void AfterKeyForwarded(uint vk, bool down, nint nextResult)`, `HookVerdict OnMouse(uint message, uint mouseData, int x, int y, uint flags, nuint extraInfo)`, `void AfterMouseForwarded(uint message, uint mouseData, nint nextResult)`, `void OnRawMouse(long device, ushort flags, int lastX, int lastY)`;
    - set from any thread (Volatile): `void SetSuppression(bool on, long deadline)`, `void PublishState(ControlState state, long lastInput, double idleSeconds)` (the sampler's idle check uses only this published `lastInput`), `void ClearTakeover()`, `void ClearFailOpen()`;
    - read from any thread: `ChannelReader<PhysicalEvent> Events`, `bool Suppressing`, `long Deadline`, `bool TakeoverPending`, `bool FailOpenRequested`, `long LastPhysical`, `bool Held` (any key or button recorded down), `bool KeyDelivered(int vk)`, `bool ButtonDelivered(int button)`;
    - `void SampleMissedReleases(Func<int, short> keyState)` (the 0.1 s dispatcher tick, Decisions 25).
  - `static class RawMouse { public static (long Device, int Dx, int Dy)? Relative(uint type, long device, ushort flags, int lastX, int lastY); }` (null for a non-mouse type, the null device or `MOUSE_MOVE_ABSOLUTE`).
  - `static class VirtualKeys { public static int Normalize(int vk); public const int Backspace = 0x08; public static readonly IReadOnlySet<int> Chord; }` (`{0x10, 0x11, 0x12, 0x08}`, `control_win32.py:160, 171-178`).

Decisions in the hook path (the tests pin them):
- An event with the injected flag (`LLKHF_INJECTED` 0x10; `LLMHF_INJECTED` 0x01 or `LLMHF_LOWER_IL_INJECTED` 0x02) whose `extraInfo` is `0x444D4350` is ours: Pass, nothing recorded. Other injected events: Pass and nothing recorded under `Ignore`; under `Physical` they are handled like physical input, and their mouse moves queue `ForeignMotion` with the delta from the previous foreign-injected point (a first point gives a zero delta).
- A physical event stamps `LastPhysical`, updates the pressed key or button arrays, and queues `Key`, `Pointer`, `Hotkey` or (from Raw Input) `RawMotion`. A full queue requests fail-open (`control.py:505-506`).
- Swallow when suppressing and `now() <= deadline`; when suppressing and the deadline has passed: Pass and request fail-open. Fail-open sets `FailOpenRequested`, turns suppression off and calls `blockLedger` (which must not lock, Decisions 19).
- Chord (`control_hooks.py:78-91`): a Backspace down that was not already down, while suppressing and no takeover is pending, with every chord key in the normalised pressed set: quarantine the pressed chord keys, set `TakeoverPending`, turn suppression off, call `blockLedger`, queue `Hotkey`, Swallow. A quarantined key is swallowed until its up; the up leaves quarantine and queues `Key`.
- Delivery (`AfterKeyForwarded`/`AfterMouseForwarded`): a forwarded down whose `CallNextHookEx` result is 0 marks the key or button delivered; an up clears it. Buttons: left 1, right 2, middle 3, XBUTTON1 4, XBUTTON2 5 (`control_win32.py:181-192`).
- Any exception inside an entry point is caught and requests fail-open (the method returns Pass).

- [ ] **Step 1: Write the failing tests**

```csharp
public class PhysicalInputFilterTests
{
    private const uint KeyInjected = 0x10;
    private long _now = 1_000;
    private int _blocks;
    private PhysicalInputFilter Filter(ForeignInjectedPolicy foreign = ForeignInjectedPolicy.Physical) =>
        new(() => _now, TimeSpan.TicksPerSecond, foreign, () => _blocks++);
    private static List<PhysicalEvent> Drain(PhysicalInputFilter f) { var list = new List<PhysicalEvent>(); while (f.Events.TryRead(out var e)) list.Add(e); return list; }

    [Fact]
    public void OurSignatureIsIgnored()
    {
        var f = Filter();
        f.SetSuppression(true, _now + 10_000_000);
        Assert.Equal(HookVerdict.Pass, f.OnKey(0x41, true, KeyInjected, InputSignature.ExtraInfo));
        Assert.Empty(Drain(f));
        Assert.Equal(0, f.LastPhysical);
    }

    [Theory]
    [InlineData(ForeignInjectedPolicy.Physical, HookVerdict.Swallow, 1)]
    [InlineData(ForeignInjectedPolicy.Ignore, HookVerdict.Pass, 0)]
    public void ForeignInjectedInputFollowsThePolicy(ForeignInjectedPolicy policy, HookVerdict verdict, int events)
    {
        var f = Filter(policy);
        f.SetSuppression(true, _now + 10_000_000);
        Assert.Equal(verdict, f.OnKey(0x41, true, KeyInjected, 0));
        Assert.Equal(events, Drain(f).Count);
    }

    [Fact]
    public void KeysAreQueuedAndSwallowedWhileTheDeadlineIsAhead()
    {
        var f = Filter();
        f.SetSuppression(true, _now + 1);
        Assert.Equal(HookVerdict.Swallow, f.OnKey(0x41, true, 0, 0));
        Assert.Equal(PhysicalEventKind.Key, Assert.Single(Drain(f)).Kind);
        Assert.True(f.Held);
    }

    [Fact]
    public void PastTheDeadlineTheHookPassesAndRequestsFailOpen()
    {
        var f = Filter();
        f.SetSuppression(true, _now - 1);
        Assert.Equal(HookVerdict.Pass, f.OnMouse(0x200, 0, 5, 5, 0, 0));
        Assert.True(f.FailOpenRequested);
        Assert.False(f.Suppressing);
        Assert.Equal(1, _blocks);
    }

    [Fact]
    public void ChordWithRightHandModifiersTakesOver()
    {
        var f = Filter();
        f.SetSuppression(true, _now + 10_000_000);
        foreach (var vk in new uint[] { 0xA3, 0xA4, 0xA1 }) f.OnKey(vk, true, 0, 0);   // RCtrl, LAlt, RShift
        Assert.Equal(HookVerdict.Swallow, f.OnKey(0x08, true, 0, 0));
        Assert.True(f.TakeoverPending);
        Assert.False(f.Suppressing);
        Assert.Equal(PhysicalEventKind.Hotkey, Drain(f)[^1].Kind);
    }

    [Fact]
    public void ChordKeysStaySwallowedUntilReleased()
    {
        var f = TookOver();
        Assert.Equal(HookVerdict.Swallow, f.OnKey(0x08, false, 0, 0));
        Assert.Equal(HookVerdict.Swallow, f.OnKey(0xA3, false, 0, 0));
        Assert.Equal(HookVerdict.Pass, f.OnKey(0x41, true, 0, 0));   // suppression is off, A is not quarantined
    }

    [Fact]
    public void AutoRepeatedBackspaceDoesNotRetrigger()
    {
        var f = TookOver();
        f.ClearTakeover();
        f.SetSuppression(true, _now + 10_000_000);
        Drain(f);
        f.OnKey(0x08, true, 0, 0);                                    // repeat while still down and quarantined
        Assert.False(f.TakeoverPending);
    }

    [Fact]
    public void WithoutSuppressionTheChordIsOrdinaryInput()
    {
        var f = Filter();
        foreach (var vk in new uint[] { 0x11, 0x12, 0x10 }) f.OnKey(vk, true, 0, 0);
        Assert.Equal(HookVerdict.Pass, f.OnKey(0x08, true, 0, 0));
        Assert.False(f.TakeoverPending);
    }

    [Fact]
    public void ADownIsDeliveredOnlyWhenForwardedAndNotSwallowedDownstream()
    {
        var f = Filter();
        f.OnKey(0xA2, true, 0, 0); f.AfterKeyForwarded(0xA2, true, 0);
        f.OnKey(0x41, true, 0, 0); f.AfterKeyForwarded(0x41, true, 1);
        Assert.True(f.KeyDelivered(0xA2));
        Assert.False(f.KeyDelivered(0x41));
        f.OnMouse(0x20B, 0x0002_0000, 0, 0, 0, 0); f.AfterMouseForwarded(0x20B, 0x0002_0000, 0);   // XBUTTON2 down
        Assert.True(f.ButtonDelivered(5));
    }

    [Fact]
    public void AFullQueueFailsOpen()
    {
        var f = new PhysicalInputFilter(() => _now, TimeSpan.TicksPerSecond, ForeignInjectedPolicy.Physical, () => _blocks++, queueCapacity: 2);
        for (var i = 0; i < 3; i++) f.OnMouse(0x200, 0, i, 0, 0, 0);
        Assert.True(f.FailOpenRequested);
    }

    [Fact]
    public void RawMouseKeepsOnlyRelativeMotionFromARealDevice()
    {
        Assert.Equal((7L, 3, -4), RawMouse.Relative(0, 7, 0, 3, -4));
        Assert.Null(RawMouse.Relative(0, 0, 0, 3, -4));       // null device
        Assert.Null(RawMouse.Relative(0, 7, 1, 3, -4));       // MOUSE_MOVE_ABSOLUTE
        Assert.Null(RawMouse.Relative(1, 7, 0, 3, -4));       // keyboard
    }

    [Fact]
    public void FiveAllUpSamplesClearAMissedKeyUp()
    {
        var f = Filter();
        f.OnKey(0x41, true, 0, 0);
        f.PublishState(ControlState.User, _now - 20 * TimeSpan.TicksPerSecond, 10);
        for (var i = 0; i < 4; i++) { f.SampleMissedReleases(_ => 0); _now += TimeSpan.TicksPerSecond / 10; }
        Assert.True(f.Held);
        f.SampleMissedReleases(_ => 0);
        Assert.False(f.Held);
    }

    [Fact]
    public void ADownSampleRestartsTheCount()
    {
        var f = Filter();
        f.OnKey(0x41, true, 0, 0);
        f.PublishState(ControlState.Ready, _now - 20 * TimeSpan.TicksPerSecond, 10);
        for (var i = 0; i < 4; i++) { f.SampleMissedReleases(_ => 0); _now += TimeSpan.TicksPerSecond / 10; }
        f.SampleMissedReleases(_ => unchecked((short)0x8000)); _now += TimeSpan.TicksPerSecond / 10;
        for (var i = 0; i < 4; i++) { f.SampleMissedReleases(_ => 0); _now += TimeSpan.TicksPerSecond / 10; }
        Assert.True(f.Held);
    }

    [Fact]
    public void NoSamplingDuringAi()
    {
        var f = Filter();
        f.OnKey(0x41, true, 0, 0);
        f.PublishState(ControlState.Ai, _now - 20 * TimeSpan.TicksPerSecond, 10);
        for (var i = 0; i < 6; i++) { f.SampleMissedReleases(_ => 0); _now += TimeSpan.TicksPerSecond / 10; }
        Assert.True(f.Held);
    }

    private PhysicalInputFilter TookOver()
    {
        var f = Filter();
        f.SetSuppression(true, _now + 10_000_000);
        foreach (var vk in new uint[] { 0xA3, 0xA4, 0xA1, 0x08 }) f.OnKey(vk, true, 0, 0);
        return f;
    }
}
```

Timestamps here are `TimeSpan` ticks (`ticksPerSecond` = `TimeSpan.TicksPerSecond`); production passes `Stopwatch.GetTimestamp` and `Stopwatch.Frequency`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `PhysicalInputFilter`, `RawMouse` and `VirtualKeys`**

Pressed, delivered and quarantine state are fixed `bool[256]` arrays plus `bool[6]` for buttons and a pressed count, written only on the dispatcher thread and read elsewhere with `Volatile.Read`. The queue is `Channel.CreateBounded<PhysicalEvent>(new BoundedChannelOptions(queueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true })` written with `TryWrite` (a false return is the overflow). `SampleMissedReleases` ports `StaleInputRecovery.sample` (signature of the recorded sets; reset on change; samples 0.1 s apart; clear pressed, delivered and quarantine entries after five all-up samples).

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~PhysicalInputFilterTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Control windows/tests/DesktopMcp.Desktop.Tests/Control
git commit -m "Add the physical input filter for the low-level hooks" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Input monitor on the dispatcher thread

The Win32 side of `InputMonitor` (spec §5, §7): `WH_KEYBOARD_LL` and `WH_MOUSE_LL` installed on the shared dispatcher, a Raw Input window with `RIDEV_INPUTSINK` (Decisions 23), the 5 s hook reinstall (Decisions 11) and the 0.1 s missed-key-up and beat tick (Decisions 13, 25); plus the interactive-desktop check. Upstream: `control_win32.py:221-339`, `control.py:561-574`.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Control/InputMonitor.cs`, `InteractiveDesktop.cs`
- Modify: `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (`SetWindowsHookEx`, `UnhookWindowsHookEx`, `CallNextHookEx`, `KBDLLHOOKSTRUCT`, `MSLLHOOKSTRUCT`, `RegisterRawInputDevices`, `GetRawInputData`, `RAWINPUT`, `RAWINPUTHEADER`, `RAWINPUTDEVICE`, `GetAsyncKeyState`, `WTSQuerySessionInformation`, `WTSFreeMemory`, `WTS_CONNECTSTATE_CLASS`, `HWND_MESSAGE` and the window calls Task 1 did not add)
- Create (tests): `windows/tests/DesktopMcp.Desktop.Tests/Control/ForeignInjector.cs`, `InputMonitorDesktopTests.cs`

**Interfaces:**
- Consumes: `DispatcherHost` (Task 2), `PhysicalInputFilter` (Task 6), `DesktopSession` (P2).
- Produces (internal):
  - `interface IInputMonitor : IAsyncDisposable { PhysicalInputFilter Filter { get; } Task StartAsync(CancellationToken ct); Task StopAsync(); long LastBeat { get; } long LastRehook { get; } void RequestRehook(); }` (`LastBeat`: timestamp of the last 0.1 s tick; `LastRehook`: timestamp of the last successful reinstall).
  - `sealed class InputMonitor(DispatcherHost host, PhysicalInputFilter filter, Func<long> now) : IInputMonitor`. One instance per process may be started (the hook procedures are static and route to `s_current`); a second start throws `InvalidOperationException("Another input monitor is running.")`.
  - `static class InteractiveDesktop { public static bool IsAvailable(); }`: WTS connect state of the current session is `WTSActive` and the input desktop is `Default` (`control_win32.py:221-253`); never throws.
  - Tests: `ForeignInjector(nint window)`: sends unsigned (`dwExtraInfo` 0) keyboard and absolute mouse events with hand-declared `SendInput`, after the same foreground and target checks as `GuardedInjector`, which it reuses.

Start (on the dispatcher through `InvokeAsync`, bounded 3 s by `StartAsync`): register a window class `DesktopMcp.InputMonitor` with an `[UnmanagedCallersOnly]` window procedure (WM_INPUT → `GetRawInputData` → `RawMouse.Relative` → `filter.OnRawMouse`; every exception caught), create the message-only window, register mouse Raw Input (usage page 1, usage 2, `RIDEV_INPUTSINK`), install both hooks with `GetModuleHandle(null)`, start the 5 s reinstall timer and the 0.1 s tick (`filter.SampleMissedReleases(GetAsyncKeyState)`, then `LastBeat = now()`). Reinstall: install both new hooks, then unhook the old ones; on failure keep the old ones and `RequestRehook()` sets a flag that the next tick retries and the filter's fail-open is requested. `RequestRehook()` (from the coordinator after a fault) reinstalls on the next tick. Stop (dispatcher): stop timers, unhook, remove Raw Input (`RIDEV_REMOVE`), destroy the window, unregister the class; a fail-safe also turns suppression off first. Every hook procedure: `code < 0` → `CallNextHookEx`; else `filter.OnKey`/`OnMouse`; Swallow → return 1; else `r = CallNextHookEx(...)`, `AfterKeyForwarded`/`AfterMouseForwarded`, return `r`.

- [ ] **Step 1: Write the Desktop tests**

Every test `[Trait("Category", "Desktop")]`, run with `ActiveTestWindow` (P3) in front; the monitor's filter uses `ForeignInjectedPolicy.Physical`; suppression is armed for at most 2 s and turned off in `finally`; the user keeps hands off the mouse and keyboard.
- `HooksAndRawInputStartAndStopOnTheDispatcher`: `StartAsync` completes in under 3 s; `LastBeat` advances; `StopAsync` leaves no hook (a foreign key sent afterwards reaches the test window's edit).
- `RawInputReachesTheMonitorWindow`: a test-only counter (internal `InputMonitor.RawMessages`) increases after `ForeignInjector` sends a relative move of 1 px and back (raw input with the null device still arrives as WM_INPUT). If it does not, switch the window to Decisions 23's fallback and note it.
- `ForeignInputIsSwallowedOnlyWhileSuppressing`: `SetSuppression(true, now + 1 s)` and a renewal every 100 ms from the test; a foreign `A` key down/up does not reach the edit; after `SetSuppression(false, 0)` the next `A` does.
- `SuppressionLapsesWithinOneSecondWhenTheHeartbeatStops`: arm once with a 1 s deadline and stop renewing; a foreign key sent 1.2 s later reaches the edit and `FailOpenRequested` is true.
- `AStalledDispatcherCannotLockInput`: arm with renewals; block the dispatcher with `InvokeAsync(() => Thread.Sleep(3000))`; a foreign key sent during the stall reaches the edit within `LowLevelHooksTimeout` (read from `HKCU\Control Panel\Desktop`, default 1000 ms when absent) plus 500 ms.
- `ForeignInjectedAndPhysicalChordTakesOver`: arm; foreign Ctrl, Alt, Shift downs and Backspace down set `TakeoverPending`; the edit received none of them (no stray Backspace); ups are swallowed until each key is up.
- `HooksAreReinstalledEveryFiveSeconds`: `LastRehook` advances within 6 s and foreign input keeps being observed across the reinstall.
- `InteractiveDesktopIsAvailableInThisSession`: `InteractiveDesktop.IsAvailable()` is true.

- [ ] **Step 2: Build to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile (`InputMonitor` does not exist).

- [ ] **Step 3: Implement `InputMonitor`, `InteractiveDesktop` and `ForeignInjector`**

As above. The hook and window procedures live in `InputMonitor` as `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]` statics; `s_current` is set before the hooks are installed and cleared after they are removed.

- [ ] **Step 4: Run the default suite, then the Desktop tests by hand**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests`
Expected: PASS (the new tests are filtered out).

Run (interactive session, hands off; the test window appears and takes the foreground; input is swallowed for at most 2 s at a time; the dispatcher stall test makes input lag for 3 s): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~InputMonitorDesktopTests"`
Expected: PASS. A guard failure (the foreground was taken) sends nothing: rerun.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop windows/tests/DesktopMcp.Desktop.Tests
git commit -m "Add the input monitor with low-level hooks and Raw Input on the dispatcher" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Overlay layout and art

Decisions 26-27. Geometry and formulas are pure; the bitmaps come from Win2D on the thread pool under `GpuGate`.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Overlay/OverlayLayout.cs`, `OverlayArt.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Overlay/OverlayLayoutTests.cs`, `OverlayArtTests.cs` (the Win2D tests carry the Desktop category: they need the runtime and a GPU device, and show nothing)

**Interfaces:**
- Consumes: `ScreenRect`, `ScreenPoint`, `PixelSize` (P2); `GpuGate` (P3); `AppRuntime.EnsureInitialized()`.
- Produces (namespace `DesktopMcp.Desktop.Overlay`):
  - `public sealed record OverlayBitmap(int Width, int Height, byte[] Pixels)` (premultiplied BGRA, top-down, `Pixels.Length == Width * Height * 4`).
  - `internal enum ArtKind { EdgeTop, EdgeBottom, EdgeLeft, EdgeRight, NoticeGlow, Notice, CursorRing, FlashEdge }`
  - `internal sealed record LayerArt(ArtKind Kind, int Width, int Height, (byte R, byte G, byte B) Color, int Extent = 0, bool Inward = true)`
  - `internal sealed record LayerSpec(string Name, ScreenRect Bounds, LayerArt Art, bool Breathes)`
  - `internal static class OverlayLayout` with constants `Border = 56`, `CursorSize = 88`, `NoticeGlowPad = 32`, `BreathPeriodSeconds = 3.0`, `BreathMinAlpha = 140`, `IndicatorBlue = (45, 145, 255)`, `FlashColor = (0xFF, 0x45, 0x00)`, `FlashDuration` 3.5 s, `FlashFrame` 30 ms, `FlashBand = 8`, `FlashBlur = 14`, `FlashDepth = 50`, `FlashInset = 6`; texts `NoticeTitle`, `NoticeMouseHint`, `NoticeShortcut`, `NoticeHint`; and:
    - `IReadOnlyList<LayerSpec> Indicator(IReadOnlyList<ScreenRect> monitors, Func<int, PixelSize?> noticeSize)` (edges, notice glow and notice per monitor, in upstream's order, `control_overlay.py:137-194`; throws `InvalidOperationException("no display available for AI control indicator")`, `"invalid display geometry for AI control indicator"`, `"display too small for AI control indicator"`);
    - `LayerSpec CursorRing(ScreenPoint cursor)`;
    - `IReadOnlyList<LayerSpec> Flash(ScreenRect? region, IReadOnlyList<ScreenRect> capturedMonitors, ScreenRect virtualScreen)` (Decisions 27);
    - `int GlowAlpha(int depth, int extent)` (`control_overlay_art.py:28-32`), `byte BreathOpacity(double elapsedSeconds)` (`22-25`), `double FlashIntensity(double t, bool fullScreen)` (`flash_overlay.py:472-479`), `byte? FlashOpacity(double t, bool fullScreen)` (quantised to 32 steps; null below 0.04).
  - `internal sealed class OverlayArt` with `public static OverlayArt Shared { get; }`, `public PixelSize? NoticeSize(int screenWidth)` (cached per width; measures with `CanvasTextLayout`), `public Task<OverlayBitmap> RenderAsync(LayerArt art, CancellationToken ct)` (thread pool, `GpuGate.Shared.EnterAsync("Control indicator", 3 s, ct)`).

- [ ] **Step 1: Write the failing tests**

```csharp
public class OverlayLayoutTests
{
    private static PixelSize? Notice(int width) => width - 32 >= 200 ? new PixelSize(600, 120) : null;

    [Fact]
    public void EachMonitorGetsFourEdgesAndTheNotice()
    {
        var layers = OverlayLayout.Indicator([new ScreenRect(0, 0, 1920, 1080)], Notice);
        Assert.Equal(["0_top", "0_bottom", "0_left", "0_right", "0_notice_glow", "0_notice"], layers.Select(l => l.Name));
        Assert.Equal(new ScreenRect(0, 0, 1920, 56), layers[0].Bounds);
        Assert.Equal(new ScreenRect(0, 56, 56, 1024), layers[2].Bounds);
        Assert.Equal(new ScreenRect(628, 56, 1292, 240), layers[4].Bounds);   // 600x120 notice centred below the border, 32 px glow pad
        Assert.False(layers[5].Breathes);
    }

    [Fact]
    public void IndicatorLayoutCoversMonitorsLeftOfThePrimary()
    {
        var layers = OverlayLayout.Indicator([new ScreenRect(-1280, -200, 0, 824), new ScreenRect(0, 0, 2560, 1600)], Notice);
        Assert.Equal(new ScreenRect(-1280, -200, 0, -144), layers.Single(l => l.Name == "0_top").Bounds);
        Assert.Equal(new ScreenRect(-56, -144, 0, 768), layers.Single(l => l.Name == "0_right").Bounds);
    }

    [Fact]
    public void ANarrowMonitorHasNoNotice() =>
        Assert.DoesNotContain(OverlayLayout.Indicator([new ScreenRect(0, 0, 220, 600)], Notice), l => l.Name.Contains("notice"));

    [Fact]
    public void NoMonitorIsAnError() =>
        Assert.Equal("no display available for AI control indicator",
            Assert.Throws<InvalidOperationException>(() => OverlayLayout.Indicator([], Notice)).Message);

    [Fact]
    public void UpstreamsFormulas()
    {
        Assert.Equal(240, OverlayLayout.GlowAlpha(0, 56));
        Assert.Equal(0, OverlayLayout.GlowAlpha(55, 56));
        Assert.Equal(255, OverlayLayout.BreathOpacity(0));
        Assert.Equal(140, OverlayLayout.BreathOpacity(1.5));
        Assert.Equal(0.5, OverlayLayout.FlashIntensity(0.25, fullScreen: true));
        Assert.Equal(1.0, OverlayLayout.FlashIntensity(0.5, fullScreen: false));
        Assert.Null(OverlayLayout.FlashOpacity(0.01, fullScreen: true));
    }

    [Fact]
    public void ARegionFlashSurroundsTheRegionAndAFullFlashSitsInsideEachMonitor()
    {
        var screen = new ScreenRect(0, 0, 1920, 1080);
        var region = OverlayLayout.Flash(new ScreenRect(100, 100, 300, 200), [screen], screen);
        Assert.Equal(new ScreenRect(50, 50, 350, 100), region[0].Bounds);   // the top band, 50 px above the region
        var full = OverlayLayout.Flash(null, [screen], screen);
        Assert.Equal(new ScreenRect(6, 6, 1914, 56), full[0].Bounds);
        Assert.All(full, l => Assert.True(l.Art.Inward));
    }

    [Fact]
    public void TheCursorRingIsCentredOnTheCursor() =>
        Assert.Equal(new ScreenRect(56, 56, 144, 144), OverlayLayout.CursorRing(new ScreenPoint(100, 100)).Bounds);
}
```

`OverlayArtTests` (Desktop): `EdgeBitmapsFadeInwardsFromTheScreenEdge` (top edge 400x56: the pixel at row 0 has alpha 240 and premultiplied blue `(255*240/255)` in B within 1; row 55 has alpha 0), `TheNoticeFitsAndHasTheBadge` (`NoticeSize(1920)` is not null and narrower than 1888; the rendered notice has an opaque black pixel inside the badge's row band), `TheCursorRingIsClearInTheCentreAndFadesOut` (centre alpha 0, corners alpha 0, some ring pixel alpha above 100), `AFlashEdgeIsOrange` (the most opaque pixel of a `FlashEdge` is within 8 of #FF4500 after un-premultiplying).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`OverlayLayout` ports the geometry of `control_overlay.py:137-210` and `flash_overlay.py:509-528` (flash: four strips per rectangle, each `FlashDepth` deep, outward strips outside the region and inward strips inside the monitor minus the inset, all clipped to the virtual screen). `OverlayArt` ports `control_overlay_art.py` with Win2D on a `CanvasRenderTarget` (DPI 96): edges as lines with `GlowAlpha` and the corner rule (`35-59`); the cursor aura per pixel (`62-76`) written with `SetPixelBytes`; the notice as a rounded panel (radius 22, colour (8, 42, 88) at 80 % with the 8 px fade) with three `CanvasTextLayout`s and the black badge (radius 6) per `103-177`; the notice glow as a `GaussianBlurEffect` ring (`180-194`); the flash band as an 8 px stroked rectangle blurred with `GaussianBlurEffect` (`BlurAmount = FlashBlur`) under the sharp band (`flash_overlay.py:310-346`). `GetPixelBytes()` returns premultiplied BGRA already.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~OverlayLayoutTests"`
Expected: PASS. By hand: `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~OverlayArtTests"` → PASS (renders offscreen only).

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Overlay windows/tests/DesktopMcp.Desktop.Tests/Overlay
git commit -m "Add the overlay layout and the Win2D indicator and flash art" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Layer windows and the control indicator

The click-through layer from Task 1's verdict, and `ControlIndicator` (`control_overlay.py:213-479`) on the shared dispatcher. Decisions 10, 13, 15, 26, 28.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Overlay/LayeredWindow.cs`, `ControlIndicator.cs`
- Modify: `windows/src/DesktopMcp.Desktop/NativeMethods.txt` (whatever Task 1's verdict still needs)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Overlay/ControlIndicatorTests.cs`, `OverlayWindowDesktopTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 8; `IDisplayProvider` (P2), `DesktopSession.CursorPosition` (P2).
- Produces (internal):
  - `interface IOverlayLayer : IDisposable { ScreenRect Bounds { get; } bool IsVisible { get; } void SetContent(OverlayBitmap bitmap); void SetOpacity(byte opacity); void MoveTo(ScreenPoint topLeft); void Show(); void Hide(); }` (dispatcher thread only; `Show` raises `InvalidOperationException("AI control indicator window could not be shown")` when the window is not visible afterwards, `control_overlay.py:90-104`).
  - `interface IOverlayLayerFactory { IOverlayLayer Create(ScreenRect bounds, string name); }` and `sealed class LayeredWindowFactory : IOverlayLayerFactory` (the verdict's implementation: class `DesktopMcp.Overlay.<name>`, `WS_POPUP` with `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` (plus `WS_EX_NOREDIRECTIONBITMAP` for Composition), `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` with a warning on failure, `ShowWindow(SW_SHOWNA)` plus `SetWindowPos(HWND_TOPMOST, ..., SWP_NOACTIVATE | SWP_SHOWWINDOW)`; ULW: content into a DIB once, opacity with a null source DC).
  - `interface IControlIndicator : IAsyncDisposable { Task ShowAsync(long generation, CancellationToken ct); void Hide(long generation); bool Healthy { get; } }` (`ShowAsync` completes when every layer is visible; an older generation never re-shows over a newer hide, `control_overlay.py:248-249`).
  - `sealed class ControlIndicator(DispatcherHost host, IOverlayLayerFactory layers, OverlayArt art, Func<IReadOnlyList<ScreenRect>> monitors, Func<ScreenPoint> cursor, Func<long> now, ILogger? log = null) : IControlIndicator`.

Behaviour: `ShowAsync` renders the indicator art for the current monitors on the thread pool (cached by monitor layout), then on the dispatcher creates the layers (once per layout), sets content, shows them and starts the 50 ms refresh timer; the refresh sets every breathing layer's opacity to `BreathOpacity(elapsed)`, moves the ring to the cursor, records `LastRefresh`, and when the monitor list differs from the built one, rebuilds (old layers disposed after the new ones show). `Hide` stops the timer and hides every layer (layers are kept for the next show). `Healthy` is false after any exception on the dispatcher path (sticky) or when a visible indicator's `LastRefresh` is older than 1 s (`control_overlay.py:469-479`). Dispose destroys all windows.

- [ ] **Step 1: Write the failing tests**

`ControlIndicatorTests` (default run) use a `FakeLayerFactory` recording created layers and their calls, an inline `FakeDispatcher` host, a fake `OverlayArt` render returning 1x1 bitmaps, and a manual clock:

```csharp
public class ControlIndicatorTests
{
    [Fact]
    public async Task ShowCreatesEveryLayerAndTheRingAndShowsThem()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        Assert.Equal(7, rig.Layers.Count);                     // 4 edges, notice glow, notice, ring
        Assert.All(rig.Layers, l => Assert.True(l.IsVisible));
    }

    [Fact]
    public async Task AnOlderShowNeverOverridesANewerHide()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        rig.Indicator.Hide(5);
        await rig.Indicator.ShowAsync(4, default);
        Assert.DoesNotContain(rig.Layers, l => l.IsVisible);
    }

    [Fact]
    public async Task IndicatorRebuildsWhenTheMonitorsChange()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        rig.Monitors = [new ScreenRect(-1280, 0, 0, 1024), new ScreenRect(0, 0, 1920, 1080)];
        rig.TickRefresh();
        Assert.Contains(rig.Layers, l => l.IsVisible && l.Bounds == new ScreenRect(-1280, 0, 0, 56));
        Assert.All(rig.Layers.Where(l => l.Disposed), l => Assert.False(l.IsVisible));
    }

    [Fact]
    public async Task TheRingFollowsTheCursorAndEdgesBreathe()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        rig.Cursor = new ScreenPoint(500, 400);
        rig.Advance(1.5); rig.TickRefresh();
        Assert.Equal(new ScreenPoint(456, 356), rig.Ring.Position);
        Assert.Equal((byte)140, rig.Layers[0].Opacity);
        Assert.Null(rig.Layers.Single(l => l.Name == "0_notice").Opacity);   // the notice text does not breathe
    }

    [Fact]
    public async Task AFailingLayerMakesTheIndicatorUnhealthyAndShowThrows()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)], failShow: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Indicator.ShowAsync(1, default));
        Assert.False(rig.Indicator.Healthy);
    }
}
```

`OverlayWindowDesktopTests` (Desktop; the indicator appears on the screen for under 2 s each; hands off): `LayersAreClickThrough` (an indicator shown over `ActiveTestWindow`: `WindowFromPoint` at a point inside a visible edge layer returns the test window's root), `LayersAreExcludedFromCapture` (a `GraphicsCaptureService` capture of a top edge layer's rect has no pixel within 8 of (45, 145, 255) over a white test window), `LayersAreNotInTheWindowCatalog` (with the indicator shown, `DesktopMachine.CreateDefault()`'s window scan lists no window of class `DesktopMcp.Overlay.*`), `ALabelUnderTheIndicatorIsNotCovered` (with a layer over the test window's button, `ActionTargets`' covered check of P3 Decisions 42 accepts the button's point), `LayersNeverTakeTheForeground` (the foreground stays the test window after `ShowAsync`).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `LayeredWindow.cs` per the verdict and `ControlIndicator`**

The window procedure is an `[UnmanagedCallersOnly]` static returning `DefWindowProc` (WM_NCHITTEST → `HTTRANSPARENT` as a second guard). Rendering happens before any dispatcher call; the dispatcher work is window creation, content upload, show and the timer.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~ControlIndicatorTests"`
Expected: PASS. By hand (interactive session; a blue border and notice appear for under 2 s per test): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~OverlayWindowDesktopTests"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop windows/tests/DesktopMcp.Desktop.Tests
git commit -m "Add click-through layer windows and the control indicator" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Capture flash

Decisions 27; `flash_overlay.py:72-133, 487-587`; `desktop/service.py:1382`.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Overlay/CaptureFlash.cs`
- Modify: `windows/src/DesktopMcp.Desktop/DesktopMachine.cs` (`ICaptureFlash Flash`, default `CaptureFlash.CreateDefault()`; an internal factory overload for tests), `windows/src/DesktopMcp.Desktop/Snapshots/SnapshotService.cs` (after a successful capture: `_machine.Flash.Show(captureRect, capturedMonitors)`)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Overlay/CaptureFlashTests.cs`, `Snapshots/SnapshotServiceTests.cs` (additions)

**Interfaces:**
- Consumes: Tasks 2, 8, 9.
- Produces:
  - `public interface ICaptureFlash { void Show(ScreenRect? region, IReadOnlyList<ScreenRect> capturedMonitors); }` (returns at once; never throws).
  - `public sealed class NoFlash : ICaptureFlash { public static NoFlash Instance { get; } }`
  - `internal sealed class CaptureFlash(DispatcherHost host, IOverlayLayerFactory layers, OverlayArt art, Func<ScreenRect> virtualScreen, Func<string, string?> environment, TimeProvider time, ILogger? log = null) : ICaptureFlash`; `public static ICaptureFlash CreateDefault()`; `internal const string DisableVariable = "DESKTOP_MCP_DISABLE_FLASH"`; `internal static bool Disabled(string? value)`.

Behaviour: `Disabled(environment(DisableVariable))` → nothing. Otherwise, on the thread pool: render the flash layers' art (cached per size), then on the dispatcher cancel the running flash (dispose its layers), create the new layers hidden with content, and run a 30 ms timer that sets every layer's opacity to `FlashOpacity(elapsed / 3.5 s, fullScreen)` (hidden when null) and disposes the layers at 3.5 s. Every failure is logged at debug level and dropped.

- [ ] **Step 1: Write the failing tests**

```csharp
public class CaptureFlashTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("on", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheDisableVariable(string? value, bool disabled) => Assert.Equal(disabled, CaptureFlash.Disabled(value));

    [Fact]
    public async Task AFullCaptureFadesInAndOutAndCloses()
    {
        var rig = new FlashRig();
        rig.Flash.Show(null, [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Settled();
        rig.Advance(0.875); Assert.All(rig.Layers, l => Assert.Equal((byte)128, l.Opacity));   // t = 0.25: intensity 0.5
        rig.Advance(2.7); Assert.All(rig.Layers, l => Assert.True(l.Disposed));
    }

    [Fact]
    public async Task ANewFlashReplacesTheRunningOne()
    {
        var rig = new FlashRig();
        rig.Flash.Show(new ScreenRect(10, 10, 100, 100), [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Settled();
        var first = rig.Layers.ToList();
        rig.Flash.Show(null, [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Settled();
        Assert.All(first, l => Assert.True(l.Disposed));
    }

    [Fact]
    public void ADisabledFlashCreatesNothing()
    {
        var rig = new FlashRig(env: "1");
        rig.Flash.Show(null, [new ScreenRect(0, 0, 1920, 1080)]);
        Assert.Empty(rig.Layers);
    }

    [Fact]
    public async Task AFailureNeverEscapes()
    {
        var rig = new FlashRig(failCreate: true);
        rig.Flash.Show(null, [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Settled();
    }
}
```

`FlashRig` reuses Task 9's `FakeLayerFactory`, an inline dispatcher and a manual `TimeProvider` whose advance fires the 30 ms timer. `SnapshotServiceTests` additions: `AVisionCaptureShowsTheFlashForTheRegion` (the fake flash records `(region, monitors)` once after the capture), `ACaptureFailureShowsNoFlash`, `NoVisionNoFlash`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `CaptureFlash` and the Snapshot wiring**

`capturedMonitors` are the monitors the capture area intersects (the list `SnapshotService` already computes for the deadline).

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~CaptureFlashTests|FullyQualifiedName~SnapshotServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop windows/tests/DesktopMcp.Desktop.Tests
git commit -m "Show the capture flash after every screenshot" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Control coordinator: heartbeat, health, ledger, checkpoints

The thread-pool side (`desktop/control.py`, `control_worker.py`): the heartbeat loop of Decisions 12, health checks, fault and recovery, the ledger (Decisions 4, 18, 19), the call context (Decisions 16) and start/stop (Decisions 14). Spec §7 "Input-lock safety".

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Control/LedgerNames.cs`, `DesktopControl.cs`, `ControlCoordinator.cs`
- Modify: `windows/src/DesktopMcp.Desktop/Input/InputLedger.cs` (volatile `_blocked`, lock-free `BlockNew`)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/Control/LedgerNamesTests.cs`, `ControlCoordinatorTests.cs`, `Input/InputLedgerTests.cs` (addition)

**Interfaces:**
- Consumes: Tasks 3, 4, 6, 7, 9; `InputLedger`, `IActionCheckpoint`, `InputUnavailableException` (P3); `ControlSettings` (P1, Task 5).
- Produces (namespace `DesktopMcp.Desktop.Control`):
  - `public interface IDesktopControl : IAsyncDisposable` with `ControlSettings Settings { get; }`, `InputLedger Ledger { get; }`, `IActionCheckpoint Checkpoint { get; }`, `ControlStatus Status()` (ticks when enabled, like `status()`, `control.py:174-186`), `Task<ControlLease> BeginCallAsync(CancellationToken ct)` (throws `ControlRejectedException`), `ControlRejectedException Interrupted(Exception interruption, ControlLease? lease)` (Decisions 17), `Task StartAsync(CancellationToken ct)`, `Task StopAsync()`, `event Action<ControlStatus>? StateChanged` (raised off the caller's lock, in generation order).
  - `public sealed class ControlLease : IDisposable` with `long Token`, `long ExecutedSteps`, `bool Closed`, `IDisposable Enter()` (sets `ControlCall.Current` in the caller's frame; dispose restores), `Dispose()` (closes the lease and ends the call once).
  - `public static class ControlCall { public static ControlLease? Current { get; } }` (an `AsyncLocal`).
  - `public static class ControlErrors { public static bool IsInterruption(Exception e); }` (true for `ControlRejectedException` and `InputUnavailableException`).
  - `public sealed class DisabledControl(ControlSettings settings) : IDesktopControl` (state `disabled`, generation 0; `BeginCallAsync` returns a lease with token 0 whose checkpoints do nothing; an enabled ledger with no predicate; start and stop touch nothing).
  - `public sealed class ControlCoordinator : IDesktopControl` with `public static IDesktopControl Create(ControlSettings settings)` (`DisabledControl` when not enabled; else the real monitor, indicator and `InteractiveDesktop.IsAvailable`, `ForeignInjectedPolicy.Physical` per Open question 1) and an internal ctor `(ControlSettings settings, IInputMonitor monitor, IControlIndicator indicator, Func<bool> interactiveDesktop, TimeProvider time, ILogger? log = null)`; internal test hooks `Task RunOnceAsync()` (one heartbeat iteration) and `bool HeartbeatPaused { set; }`.
  - `internal static class LedgerNames { public static IReadOnlyList<int> Keys(string ledgerName); public static int? Button(string ledgerName); }`.

Behaviour (one `Lock` guards the state machine; the hook never takes it):
- Heartbeat loop (one long-running task from `StartAsync` to `StopAsync`), every 50 ms: if the lock cannot be taken at once, skip renewal this round (`refresh_deadline` 20-22). Under the lock: copy `Filter.Held` and `Filter.TakeoverPending` into the machine; drain up to 128 events into `Handle`; `Tick`; when `Filter.FailOpenRequested`, `Fault` and `monitor.RequestRehook()`; every 250 ms the health check: `interactiveDesktop()`, `indicator.Healthy`, and `now - monitor.LastBeat <= 1 s` (a failure faults); when faulted, recovery after `monitor.LastRehook` is later than the fault and health passed (`control.py:450-499`): `ReleaseAll`, `Pending` empty, `Ledger.Enable()`, `ClearFailOpen`, `Recover`. Then publish: `Filter.SetSuppression(machine.SuppressionWanted, now + 1 s)` and `Filter.PublishState(...)`. Outside the lock: apply each change (below).
- A change to a non-`ai` state: `Ledger.BlockNew()`, `Ledger.ReleaseAll()` (a failed release faults, `control.py:188-200`), `indicator.Hide(generation)`, `StateChanged`. A change to `ai`: `StateChanged` only (the show is the caller's).
- `BeginCallAsync`: under the lock `Ledger.Enable()` (an `InvalidOperationException` faults and rejects `CONTROL_UNAVAILABLE`) and `machine.Begin()`; when `NeedsIndicator`, outside the lock `indicator.ShowAsync(generation)` bounded 3 s (a failure or timeout faults, then rejects with `CONTROL_UNAVAILABLE`), then under the lock `machine.Arm` and an immediate `SetSuppression`. Cancellation after `Begin` succeeded ends the call before rethrowing.
- `Checkpoint.Checkpoint()`: with no `ControlCall.Current`, nothing; else under the lock `machine.Checkpoint(lease.Token, lease.ExecutedSteps, lease.Closed)`. `RecordStep()` increments `ExecutedSteps` of the current lease.
- `Interrupted`: a `ControlRejectedException` is returned unchanged; an `InputUnavailableException` becomes `CONTROL_UNAVAILABLE` when `Status()` is `unavailable`, else `CONTROL_PREEMPTED`, with the lease's executed steps.
- Ledger predicate: `name => LedgerNames.Keys(name).Any(Filter.KeyDelivered) || LedgerNames.Button(name) is { } b && Filter.ButtonDelivered(b)`.
- `StartAsync` (Decisions 14): `interactiveDesktop()` false or a monitor start failure → stays `unavailable`, logs, retries every 5 s from the heartbeat loop (`InvalidOperationException` from `AppRuntime` is not retried); success → `machine.Start(Filter.LastPhysical)`, `Ledger.Enable()`. `StopAsync`: `machine.Stop()`, suppression off, `Ledger.BlockNew()`, stop the loop, `monitor.StopAsync()`, `indicator.DisposeAsync()`, `Ledger.ReleaseAll()` (errors logged), `StateChanged`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class LedgerNamesTests
{
    [Theory]
    [InlineData("key:ctrl", new[] { 0x11, 0xA2, 0xA3 })]
    [InlineData("key:vk:11", new[] { 0x11, 0xA2, 0xA3 })]
    [InlineData("key:shift", new[] { 0x10, 0xA0, 0xA1 })]
    [InlineData("key:vk:10", new[] { 0x10, 0xA0, 0xA1 })]
    [InlineData("key:vk:12", new[] { 0x12, 0xA4, 0xA5 })]
    [InlineData("key:vk:a3", new[] { 0x11, 0xA2, 0xA3 })]
    [InlineData("key:vk:41", new[] { 0x41 })]
    [InlineData("mouse:left", new int[0])]
    [InlineData("nonsense", new int[0])]
    public void BothNamesOfAKeyMapToTheSamePhysicalKeys(string name, int[] keys) => Assert.Equal(keys, LedgerNames.Keys(name));

    [Theory]
    [InlineData("mouse:left", 1)]
    [InlineData("mouse:right", 2)]
    [InlineData("mouse:middle", 3)]
    [InlineData("key:ctrl", null)]
    public void Buttons(string name, int? button) => Assert.Equal(button, LedgerNames.Button(name));
}
```

`InputLedgerTests` addition: `BlockNewDoesNotWaitForAPressInProgress` (a `Press` whose `sendDown` blocks on a gate; `BlockNew()` from another thread returns within 100 ms; after the gate opens, a new `Press` throws `InputUnavailableException`).

`ControlCoordinatorTests` (default run) use `ControlRig`: a `FakeMonitor` whose `Filter` is a real `PhysicalInputFilter` over the rig's clock, a `LastBeat` that follows the clock unless a test sets it, a settable `LastRehook` (0 by default), and a `Start` that can fail; a `FakeIndicator` (show succeeds, fails or never completes; records shows and hides; settable `Healthy`); an `interactive` flag; a manual `TimeProvider` (Desktop tests' `ManualTime`); a ledger press helper recording injected ups; heartbeat iterations driven by `RunOnceAsync()`:

```csharp
public class ControlCoordinatorTests
{
    [Fact]
    public async Task BeginShowsTheIndicatorBeforeArmingSuppression()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Indicator.HoldShow();
        var begin = rig.Control.BeginCallAsync(default);
        await rig.Indicator.ShowStarted;
        Assert.False(rig.Monitor.Filter.Suppressing);
        rig.Indicator.CompleteShow();
        using var lease = await begin;
        Assert.True(rig.Monitor.Filter.Suppressing);
        Assert.Equal(ControlState.Ai, rig.Control.Status().State);
    }

    [Fact]
    public async Task AFailedIndicatorRejectsWithControlUnavailable()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Indicator.FailShow();
        var error = await Assert.ThrowsAsync<ControlRejectedException>(() => rig.Control.BeginCallAsync(default));
        Assert.Equal((ControlCodes.ControlUnavailable, ControlState.Unavailable), (error.Code, error.Status.State));
    }

    [Fact]
    public async Task TakeoverReleasesHeldInputExceptKeysTheUserHolds()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Control.Ledger.Press("key:ctrl", () => { }, () => rig.Ups.Add("ctrl"));
        rig.Control.Ledger.Press("key:vk:10", () => { }, () => rig.Ups.Add("shift"));
        rig.PhysicalDownDelivered(0xA0);                      // a physical left Shift down that reached the system (OnKey + AfterKeyForwarded with result 0 while suppression was briefly off)
        rig.RawMove(600, 0);
        await rig.Control.RunOnceAsync();
        Assert.Equal(["ctrl"], rig.Ups);
        Assert.Empty(rig.Control.Ledger.Pending);
        using (lease.Enter())
            Assert.Equal(ControlCodes.ControlPreempted, Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint).Code);
    }

    [Fact]
    public async Task TheHeartbeatRenewsTheDeadlineOnlyWhileArmed()
    {
        var rig = await ControlRig.StartedAsync();
        await rig.Control.RunOnceAsync();
        Assert.False(rig.Monitor.Filter.Suppressing);
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Advance(0.5); await rig.Control.RunOnceAsync();
        Assert.Equal(rig.Stamp() + rig.Seconds(1), rig.Monitor.Filter.Deadline);
    }

    [Fact]
    public async Task AStoppedHeartbeatLetsTheDeadlineLapse()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Control.HeartbeatPaused = true;
        rig.Advance(1.1);
        Assert.Equal(HookVerdict.Pass, rig.Monitor.Filter.OnKey(0x41, true, 0, 0));
        rig.Control.HeartbeatPaused = false;
        await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);   // the lapse was a fault
    }

    [Fact]
    public async Task LeavingTheInputDesktopFailsOpenAndPreempts()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Control.Ledger.Press("mouse:left", () => { }, () => rig.Ups.Add("left"));
        rig.Interactive = false;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        Assert.Equal(["left"], rig.Ups);
        Assert.False(rig.Monitor.Filter.Suppressing);
        using (lease.Enter()) Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint);
    }

    [Fact]
    public async Task RecoveryNeedsARehookAndGoesToUser()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Interactive = false;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        rig.Interactive = true;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        rig.Monitor.LastRehook = rig.Stamp();
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.User, rig.Control.Status().State);
        rig.Advance(10); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);
    }

    [Fact]
    public async Task AStaleDispatcherBeatFailsOpen()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Monitor.LastBeat = rig.Stamp() - rig.Seconds(1.5);
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
    }

    [Fact]
    public async Task CheckpointsOutsideACallDoNothingAndAClosedLeasePreempts()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Control.Checkpoint.Checkpoint();
        var lease = await rig.Control.BeginCallAsync(default);
        var scope = lease.Enter();
        rig.Control.Checkpoint.RecordStep();
        lease.Dispose();
        var error = Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint);
        Assert.Equal(1L, error.ExecutedSteps);
        scope.Dispose();
    }

    [Fact]
    public async Task InputUnavailableMapsToPreemptedOrUnavailable()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        Assert.Equal(ControlCodes.ControlPreempted, rig.Control.Interrupted(new InputUnavailableException("AI input is unavailable"), lease).Code);
        rig.Interactive = false; rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlCodes.ControlUnavailable, rig.Control.Interrupted(new InputUnavailableException("AI input is unavailable"), lease).Code);
    }

    [Fact]
    public async Task AFailedStartStaysUnavailableAndRetries()
    {
        var rig = ControlRig.Create(monitorStartFailures: 1);
        await rig.Control.StartAsync(default);
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        rig.Advance(5); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);
    }

    [Fact]
    public async Task StateChangesAreRaisedInGenerationOrder()
    {
        var rig = await ControlRig.StartedAsync();
        var seen = new List<long>();
        rig.Control.StateChanged += s => seen.Add(s.Generation);
        using (await rig.Control.BeginCallAsync(default)) { }
        rig.RawMove(600, 0); await rig.Control.RunOnceAsync();
        Assert.Equal(seen.Order(), seen);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task DisabledControlNeverTouchesTheDesktop()
    {
        var control = new DisabledControl(new ControlSettings(false, "relaxed", 10, 500, 500));
        await control.StartAsync(default);
        using var lease = await control.BeginCallAsync(default);
        using (lease.Enter()) control.Checkpoint.Checkpoint();
        Assert.Equal(ControlState.Disabled, control.Status().State);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Desktop.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `LedgerNames`, `DesktopControl.cs`, `ControlCoordinator` and the ledger change**

As the Behaviour list says. The real loop waits with `Task.Delay(50 ms, time, ct)`; `RunOnceAsync` runs exactly one iteration's body.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter "FullyQualifiedName~Control|FullyQualifiedName~InputLedgerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Desktop windows/tests/DesktopMcp.Desktop.Tests
git commit -m "Add the control coordinator with the suppression heartbeat and checkpoints" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Control gate, ControlStatus and the tools without DesktopInputLock

`tools/control_notifications.py:163-296` as an SDK call-tool filter (spec §5 serialisation, §7 rules), the ControlStatus tool (`tools/control_status.py`), and the server wiring. Decisions 4, 14, 17, 20, 21.

**Files:**
- Create: `windows/src/DesktopMcp.Server/Control/ControlGate.cs`, `windows/src/DesktopMcp.Server/Hosting/ControlHostedService.cs`, `windows/src/DesktopMcp.Server/Tools/ControlStatusTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolServices.cs`, `ToolRegistry.cs`, `ToolResults.cs`, `SpecTool.cs`, `ClickTool.cs`, `MoveTool.cs`, `ScrollTool.cs`, `TypeTool.cs`, `ShortcutTool.cs`, `MultiSelectTool.cs`, `MultiEditTool.cs`, `AppTool.cs`, `windows/src/DesktopMcp.Desktop/Apps/AppService.cs`, `windows/src/DesktopMcp.Server/Hosting/StdioServer.cs`
- Delete: `windows/src/DesktopMcp.Server/Tools/DesktopInputLock.cs`
- Test: `windows/tests/DesktopMcp.Server.Tests/Control/FakeControl.cs`, `ControlGateTests.cs`, `windows/tests/DesktopMcp.Server.Tests/Tools/ControlStatusToolTests.cs`, `ClickToolTests.cs` (remove `InputToolsRunOneAtATime`), `MultiToolTests.cs`, `FakeDesktop.cs`, `windows/tests/DesktopMcp.Server.Tests/Hosting/StdioServerTests.cs`

**Interfaces:**
- Consumes: Tasks 3, 11; `ToolSpecCatalog`, `SpecTool` (P0/P1).
- Produces:
  - `public sealed class ControlGate(IDesktopControl control, TimeProvider time)` with `public ValueTask<CallToolResult> InvokeAsync(string toolName, IDictionary<string, JsonElement>? arguments, Func<CancellationToken, ValueTask<CallToolResult>> next, CancellationToken ct)` and `public static TimeSpan RecheckInterval` (0.1 s).
  - `public static class ControlGateFilter { public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(ControlGate gate, Action<McpServer> remember); }`.
  - `ToolResults.Rejection(ControlRejectedException e)` (isError, the single text `ControlJson.Rejection(e)`) and `ToolResults.Object(string json)` (text plus the parsed object as `structuredContent`).
  - `public sealed class ControlStatusTool(Tool protocolTool, IDesktopControl control) : SpecTool`.
  - `ToolServices`: gains `required IDesktopControl Control`; loses `InputLock`; `Ledger` must be `Control.Ledger`; `CreateDefault()` uses `new DisabledControl(<default settings>)` and `ProcessAllowlist.Everything` (tests never start a monitor); new `public static ToolServices Create(ServePlan plan)` uses `ControlCoordinator.Create(plan.Control)`, `new ProcessAllowlist(plan.Control.ProcessAllowlist)` and `new DesktopInput(control.Ledger, clipboard, control.Checkpoint, allowlist)`.
  - `ControlHostedService(IDesktopControl control, ILogger log) : IHostedService` (start and stop the control; registered before `AddMcpServer`, so it starts first and stops last).

Gate algorithm (`on_call_tool` 204-296; the remember call is Task 13's):
1. `ControlStatus` → `next`.
2. Control disabled → hold the lock for `next`, then return its result; an `InputUnavailableException` escaping it becomes `ToolResults.Error(name, message)`.
3. `!ControlPolicy.RequiresControl(policy, name, ControlPolicy.ModeOf(name, arguments))` → `next`.
4. `Status()` not `ready`/`ai` → `ToolResults.Rejection(ControlCodes.ForState(...))`; wait for the lock in `RecheckInterval` slices with `ct`, re-checking after each slice.
5. `BeginCallAsync` (a rejection → its result); `using lease.Enter()`; `control.Checkpoint.Checkpoint()` once; `next`; an interruption (`ControlErrors.IsInterruption`) → `ToolResults.Rejection(control.Interrupted(e, lease))`; `finally` dispose the lease, release the lock.

- [ ] **Step 1: Write the failing tests**

`FakeControl : IDesktopControl` (tests): a settable `Current` status (`Status()` returns it; `Settings.Enabled` follows it), a `BeginCallAsync` that returns a lease (counting `EndedCalls` when one is disposed) or throws a configured rejection, `Interrupted` returning a `ControlRejectedException` unchanged, a recording checkpoint, a real `InputLedger`, and a `RaiseStateChanged(status)` helper.

```csharp
public class ControlGateTests
{
    private static readonly ControlStatus Ready = new(ControlState.Ready, 1, 0, true, "relaxed", 10);
    private static readonly ControlStatus User = new(ControlState.User, 2, 9.5, true, "relaxed", 10);

    [Fact]
    public async Task AGatedCallInUserStateIsRejectedAtOnce()
    {
        var control = new FakeControl { Current = User };
        var ran = false;
        var result = await new ControlGate(control, TimeProvider.System).InvokeAsync("Click", null, _ => { ran = true; return Ok(); }, default);
        Assert.False(ran);
        Assert.True(result.IsError);
        Assert.Equal("""{"code": "USER_CONTROL", "status": {"state": "user", "generation": 2, "wait_seconds": 9.5, "enabled": true, "policy": "relaxed", "idle_timeout_seconds": 10.0}}""",
            ToolCalls.Text(result));
    }

    [Theory]
    [InlineData("ControlStatus")]
    [InlineData("Wait")]
    [InlineData("Snapshot")]
    public async Task UngatedToolsRunWhileTheUserHasControl(string tool)
    {
        var result = await new ControlGate(new FakeControl { Current = User }, TimeProvider.System).InvokeAsync(tool, null, _ => Ok(), default);
        Assert.False(result.IsError ?? false);
    }

    [Fact]
    public async Task GatedCallsRunOneAtATime()
    {
        var control = new FakeControl { Current = Ready };
        var gate = new ControlGate(control, TimeProvider.System);
        var release = new TaskCompletionSource();
        var first = gate.InvokeAsync("Click", null, async _ => { await release.Task; return await Ok(); }, default).AsTask();
        var secondRan = false;
        var second = gate.InvokeAsync("Type", null, _ => { secondRan = true; return Ok(); }, default).AsTask();
        await Task.Delay(150);
        Assert.False(secondRan);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondRan);
    }

    [Fact]
    public async Task AWaitingCallIsRejectedWhenTheUserTakesOver()
    {
        var control = new FakeControl { Current = Ready };
        var gate = new ControlGate(control, TimeProvider.System);
        var release = new TaskCompletionSource();
        var first = gate.InvokeAsync("Click", null, async _ => { await release.Task; return await Ok(); }, default).AsTask();
        var second = gate.InvokeAsync("Click", null, _ => Ok(), default).AsTask();
        await Task.Delay(50);
        control.Current = User;
        var rejected = await second.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.StartsWith("{\"code\": \"USER_CONTROL\"", ToolCalls.Text(rejected));
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task APreemptionInsideTheToolBecomesControlPreempted()
    {
        var control = new FakeControl { Current = Ready };
        var result = await new ControlGate(control, TimeProvider.System).InvokeAsync("Type", null,
            _ => throw new ControlRejectedException(ControlCodes.ControlPreempted, User, 4), default);
        Assert.EndsWith("\"executed_steps\": 4}}", ToolCalls.Text(result));
    }

    [Fact]
    public async Task DisabledControlSerialisesEveryCallExceptControlStatus()
    {
        var control = new FakeControl { Current = new(ControlState.Disabled, 0, 0, false, "relaxed", 10) };
        var gate = new ControlGate(control, TimeProvider.System);
        var release = new TaskCompletionSource();
        var wait = gate.InvokeAsync("Wait", null, async _ => { await release.Task; return await Ok(); }, default).AsTask();
        var statusRan = false;
        await gate.InvokeAsync("ControlStatus", null, _ => { statusRan = true; return Ok(); }, default);
        var snapshot = gate.InvokeAsync("Snapshot", null, _ => Ok(), default).AsTask();
        await Task.Delay(100);
        Assert.True(statusRan);
        Assert.False(snapshot.IsCompleted);
        release.SetResult();
        await Task.WhenAll(wait, snapshot);
    }

    [Fact]
    public async Task ACancelledCallReleasesTheLockAndEndsTheCall()
    {
        var control = new FakeControl { Current = Ready };
        var gate = new ControlGate(control, TimeProvider.System);
        using var cancel = new CancellationTokenSource();
        var call = gate.InvokeAsync("Click", null, async ct => { await Task.Delay(Timeout.Infinite, ct); return await Ok(); }, cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, control.EndedCalls);
        Assert.False((await gate.InvokeAsync("Click", null, _ => Ok(), default)).IsError ?? false);
    }

    private static ValueTask<CallToolResult> Ok() => ValueTask.FromResult(ToolResults.Text("ok"));
}
```

`ControlStatusToolTests`: `ReturnsTheStatusAsTextAndStructuredContent` (text equals `ControlJson.Status(...)`; `structuredContent` has `state == "ready"` and `idle_timeout_seconds == 10.0`, not wrapped in `result`). `MultiToolTests` additions: `MultiSelectDoesNotSwallowAPreemption` and `MultiEditDoesNotSwallowAPreemption` (a `FakeInput` throwing `ControlRejectedException` at the second item: the exception escapes the tool, it is not turned into `Stopped after ...`). `SpecToolTests` (in `ToolResultsTests.cs`): `InterruptionsAreRethrown`. `StdioServerTests` additions: `GatedCallsGoThroughTheFilter` (a server over in-memory pipes with `ToolServices.CreateDefault() with { Control = new FakeControl { Current = User } }`: Click returns the USER_CONTROL rejection, Wait succeeds, ControlStatus returns the fake status), `InitializeDeclaresTheLoggingCapability`, `TheControlStartsBeforeServingAndStopsAfter` (the fake control records start before the first request and stop after the host stops).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Server.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

- The gate and filter as above; the filter reads `request.Params.Name` and `request.Params.Arguments`, calls `remember(request.Server)`, and passes `ct => next(request, ct)`.
- Tools: delete every `using (await services.InputLock.EnterAsync(...))` block (keep its body).
- `SpecTool.InvokeAsync`: `catch (Exception e) when (ControlErrors.IsInterruption(e)) { throw; }` before the generic catch. Add `&& !ControlErrors.IsInterruption(e)` to the broad filters in `MultiEditTool` (two), `MultiSelectTool` and `AppService` (lines 100, 132, 174 of P3).
- `ToolRegistry`: `"ControlStatus" => new ControlStatusTool(protocolTool, services.Control)`.
- `StdioServer.RunAsync(plan, ct)` uses `ToolServices.Create(plan)`; the internal overload registers `ControlHostedService` for `services.Control` before `AddMcpServer`, sets `options.Capabilities = new() { Logging = new() }`, adds `.WithRequestFilters(f => f.AddCallToolFilter(ControlGateFilter.Create(new ControlGate(services.Control, services.Time), remember)))` (Task 13 supplies `remember`; until then a no-op), and keeps the ledger release on stop.

- [ ] **Step 4: Run the tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: PASS (`ContractTests` unchanged; no test starts a monitor).

- [ ] **Step 5: Commit**

```bash
git add windows/src windows/tests
git commit -m "Gate tool calls by the control policy and add ControlStatus" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Control notifier and sessions

Spec §7 `ControlNotifier` and Sessions; `tools/control_notifications.py:19-160`; Decisions 22.

**Files:**
- Create: `windows/src/DesktopMcp.Server/Control/SessionRegistry.cs`, `ControlNotifier.cs`
- Modify: `windows/src/DesktopMcp.Server/Hosting/StdioServer.cs` (registry with an unlimited TTL; `remember`; the notifier started after the control and closed before it)
- Test: `windows/tests/DesktopMcp.Server.Tests/Control/ControlNotifierTests.cs`, `Hosting/StdioServerTests.cs` (addition)

**Interfaces:**
- Consumes: `IDesktopControl.StateChanged` (Task 11), `ControlGateFilter` (Task 12).
- Produces:
  - `public interface IControlSession { ValueTask NotifyAsync(LoggingMessageNotificationParams message, CancellationToken ct); }` and `internal sealed class McpServerSession(McpServer server) : IControlSession` (`SendNotificationAsync(NotificationMethods.LoggingMessageNotification, message, <source-generated JsonTypeInfo>, ct)`).
  - `public sealed class SessionRegistry(TimeProvider time, TimeSpan? ttl)` (`null` = unlimited; `TimeSpan.Zero` = never notify): `void Remember(object key, IControlSession session)` (refreshes last seen), `IReadOnlyList<(object Key, IControlSession Session)> Live()` (drops expired ones), `void Forget(object key)`.
  - `public sealed class ControlNotifier(IDesktopControl control, SessionRegistry sessions, ILogger log) : IAsyncDisposable` with `void Start()` (reads the initial status, subscribes) and `DisposeAsync()` (unsubscribes, waits for sends in flight).
  - `public static string? ControlNotifier.EventFor(ControlState previous, ControlState current)`.

Behaviour: each `StateChanged` is queued to one consumer task; a status whose generation is not above the last handled one is dropped; `EventFor` decides; the message is `Level = LoggingLevel.Notice`, `Logger = "desktop-mcp.control"`, `Data` = `{"event": E, "status": <the ControlStatus object, same keys and order as ControlJson.Status>}`; each live session is sent on the thread pool; a failed send forgets that session and logs at debug level.

- [ ] **Step 1: Write the failing tests**

```csharp
public class ControlNotifierTests
{
    [Theory]
    [InlineData(ControlState.Ready, ControlState.User, "USER_CONTROL")]
    [InlineData(ControlState.Ai, ControlState.User, "USER_CONTROL")]
    [InlineData(ControlState.User, ControlState.Ready, "AI_CONTROL_AVAILABLE")]
    [InlineData(ControlState.Ai, ControlState.Ready, null)]
    [InlineData(ControlState.Ready, ControlState.Ai, null)]
    [InlineData(ControlState.Ai, ControlState.Unavailable, null)]
    [InlineData(ControlState.Unavailable, ControlState.User, null)]
    public void UpstreamsEvents(ControlState previous, ControlState current, string? expected) =>
        Assert.Equal(expected, ControlNotifier.EventFor(previous, current));

    [Fact]
    public async Task ATakeoverReachesEveryLiveSessionWithUpstreamsShape()
    {
        var rig = new NotifierRig(ttl: null);
        var a = rig.Session("a"); var b = rig.Session("b");
        rig.Control.RaiseStateChanged(new(ControlState.Ai, 2, 0, true, "relaxed", 10));
        rig.Control.RaiseStateChanged(new(ControlState.User, 3, 10, true, "relaxed", 10));
        await rig.Drained();
        var message = Assert.Single(a.Sent);
        Assert.Equal((LoggingLevel.Notice, "desktop-mcp.control"), (message.Level, message.Logger));
        Assert.Equal("""{"event":"USER_CONTROL","status":{"state":"user","generation":3,"wait_seconds":10.0,"enabled":true,"policy":"relaxed","idle_timeout_seconds":10.0}}""",
            message.Data!.Value.GetRawText());
        Assert.Single(b.Sent);
    }

    [Fact]
    public async Task OlderGenerationsAreDropped()
    {
        var rig = new NotifierRig(ttl: null);
        var a = rig.Session("a");
        rig.Control.RaiseStateChanged(new(ControlState.User, 5, 10, true, "relaxed", 10));
        rig.Control.RaiseStateChanged(new(ControlState.Ready, 4, 0, true, "relaxed", 10));
        await rig.Drained();
        Assert.Single(a.Sent);
    }

    [Fact]
    public async Task AFailedSendForgetsTheSession()
    {
        var rig = new NotifierRig(ttl: null);
        rig.Session("a", fail: true);
        rig.Control.RaiseStateChanged(new(ControlState.User, 2, 10, true, "relaxed", 10));
        await rig.Drained();
        Assert.Empty(rig.Sessions.Live());
    }

    [Fact]
    public async Task ExpiredAndStatelessSessionsGetNothing()
    {
        var rig = new NotifierRig(ttl: TimeSpan.FromSeconds(60));
        var a = rig.Session("a");
        rig.Advance(61);
        rig.Control.RaiseStateChanged(new(ControlState.User, 2, 10, true, "relaxed", 10));
        await rig.Drained();
        Assert.Empty(a.Sent);
        var stateless = new SessionRegistry(TimeProvider.System, TimeSpan.Zero);
        stateless.Remember("s", new RecordingSession());
        Assert.Empty(stateless.Live());
    }
}
```

`StdioServerTests` addition: `TheClientReceivesUserControlNotifications` (in-memory server with a `FakeControl`; the client calls Wait once so the filter remembers the session; the fake raises `ready` → `user`; the client's `notifications/message` handler receives logger `desktop-mcp.control` and `event` `USER_CONTROL` within 5 s).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Server.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

As above. The `Data` JSON is built with `Utf8JsonWriter` from the same writer `ControlJson.Status` uses (expose `ControlJson.WriteStatus(Utf8JsonWriter, ControlStatus)`), parsed to a `JsonElement`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Server.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Control windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Server.Tests
git commit -m "Notify live sessions when the user takes or returns control" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Release verification and documentation

The P4 checkpoint, as P3 Task 16 (Decisions 7).

**Files:**
- Modify: `windows/probes/DesktopMcp.Probe/OverlayProbe.cs`, `Program.cs` (`overlay` step), `windows/scripts/smoke-stdio.ps1`, `windows/scripts/build-release.ps1`, `docs/superpowers/specs/2026-10-19-windows-port-design.md`, `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`

**Interfaces:**
- Consumes: every task.
- Produces: the hidden AOT overlay check, the smoke's control checks, the measured P4 results in the spec, the follow-ups without the closed P4 items, and the by-hand checklist.

- [ ] **Step 1: Add the hidden overlay check to the probe and build-release**

`desktop-mcp-probe overlay`: through the product's `OverlayArt` and `LayeredWindowFactory`, render the indicator for the real monitors and one flash, create every layer, push its content and set an opacity, **never show a layer**, read back that each window has the click-through ex-styles and `GetWindowDisplayAffinity` is `WDA_EXCLUDEFROMCAPTURE`, destroy them; print `overlay: <n> layers, <bytes> bytes rendered, hidden -> OK`. `build-release.ps1` runs it after the `uia` step: `AOT overlay check OK: the NativeAOT probe rendered and uploaded the indicator and flash without showing them`.

- [ ] **Step 2: Update the smoke test**

In `smoke-stdio.ps1`: the first session's config becomes `[control]\nenabled = false` and the process gets `DESKTOP_MCP_DISABLE_FLASH=1` (so the act-tool calls take no control and the captures flash nothing); it adds `ControlStatus` → not isError, text `{"state":"disabled","generation":0,"wait_seconds":0.0,"enabled":false,"policy":"relaxed","idle_timeout_seconds":10.0}`. The second session (the config excluding Registry) keeps control on: `ControlStatus` → `state` is `ready` or `user` (also `unavailable` with `-SkipCapture`, the no-runtime host), `enabled` true, `policy` `relaxed`; `Wait {duration: 0}` → OK (ungated); `ControlStatus` again → same `enabled`; then the graphics-stack check (the hooks and the dispatcher must not load d2d1, d3d11, dwrite or d3dcompiler_47), and the server exits within 5 s of stdin closing. The header comment says the control session reads status only: it never takes control, shows nothing and injects nothing; its hooks pass every event through. Append `, control session OK (<state>)` to the `smoke OK` line.

- [ ] **Step 3: Run the full default suite**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project. Record the counts.

- [ ] **Step 4: Publish both RIDs and run the checks**

Run (interactive session): `pwsh scripts/build-release.ps1`
Expected: no publish warnings; both folder lines; the `smoke OK` line with the act-tool checks and the control session; `AOT activation check OK`; `AOT UIA check OK`; `AOT overlay check OK`. Record both exe sizes and the module counts.

- [ ] **Step 5: Record the results in the spec**

In `docs/superpowers/specs/2026-10-19-windows-port-design.md`:
1. §5 The dispatcher thread: the timer list per Decisions 13 (the idle timeout moves to the coordinator; add the 50 ms indicator refresh); Raw Input window per Decisions 23's outcome. Tool-call serialisation: replace the P3 sentence with `Since P4 the gate's lock replaces P3's input lock (P4 plan, Decisions 4).`
2. §7: the Input-lock safety sentence becomes `The deadline is renewed by the control coordinator's heartbeat on the thread pool (every 50 ms while the state is ai and the indicator is visible), not by a dispatcher timer, so a stuck coordinator releases the user's input within a second (P4 plan, Decisions 12).`; the overlay row names Task 1's verdict; add the process allowlist and Open question 1's answer to the rules; config keys `process_allowlist`.
3. §8 CLI and configuration: `[control] process_allowlist` (P4 plan, Decisions 5).
4. §9: `P4 results (<date>, Windows build <build>): default suite <n> (Core <a>, Server <b>, Desktop non-desktop <c>); Desktop suite <n> by hand or pending; build-release: win-x64 <size> bytes, win-arm64 <size> bytes; stdio smoke OK under NativeAOT with the control session (<state>) and no control taken; AOT overlay check OK (hidden); by-hand control session: <result or pending>.` Add the hooks, overlay, takeover and input-lock safety Desktop tests to the Desktop integration row as done.
5. §10: under Bug fixes or Implementation: input injected by other programs counts as user input (Open question 1's answer); the indicator stays visible during captures (excluded from them) instead of being hidden; the flash also on display-selected captures inside each monitor; `mouse_takeover_pixels` applies to foreign injected motion; the `logging` capability; the process allowlist; and `- **P4 decisions**: ... listed in the P4 plan's "Decisions on upstream ambiguities" (docs/superpowers/plans/2026-10-20-windows-p4-desktop-control.md).` In Installation: with desktop control enabled the runtime is bootstrapped at startup, and without it gated tools return `CONTROL_UNAVAILABLE` (Open question 2's answer).
6. §11 P4 row Outcome: `Protection against fighting the user (done; results in §9)`.
7. §12: the "LL hooks swallow input" row gets the measured Desktop results; the overlay row from Task 1 stays.

- [ ] **Step 6: Update the follow-ups list**

Replace the P4 section with `All P4 items are done (see docs/superpowers/plans/2026-10-20-windows-p4-desktop-control.md), except:` followed by kill-on-close job inheritance (kept as is) and the pending checks of Step 7; add a `## P4 leftovers` section for minor review findings.

- [ ] **Step 7: List the by-hand checks for the user**

Add to the follow-ups' P4 section (they show the indicator, swallow input or take the foreground, so none ran unattended):
- The Desktop suite: `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath=`, including `InputMonitorDesktopTests` (input swallowed for up to 2 s at a time; the dispatcher stall test lags input for 3 s), `OverlayWindowDesktopTests` and `OverlayArtTests`, plus the P3 tests still pending. Hands off the mouse and keyboard.
- By-hand control session through the AOT exe over stdio with control on (the user saves their work and watches). Expected results in brackets:
  1. `Click {loc: <a point on the desktop background>}` [the blue border, notice and cursor ring appear on every monitor before the click; ControlStatus `ai`].
  2. Move the mouse briefly by hand within 15 s [nothing moves: swallowed]; then keep moving it about 5 cm [the indicator disappears; a `notifications/message` with `USER_CONTROL`; ControlStatus `user` with `wait_seconds` near 10].
  3. Wait 10 s hands off [`AI_CONTROL_AVAILABLE`; ControlStatus `ready`].
  4. `Type {loc: <Notepad's edit>, text: 'a long text of about two hundred characters ...'}` and press Ctrl+Alt+Shift+Backspace during it [typing stops; the result is `CONTROL_PREEMPTED` with `executed_steps`; no Backspace reached Notepad; no key stays held].
  5. Start a gated call, then lock the screen (Win+L) [`CONTROL_PREEMPTED` or `CONTROL_UNAVAILABLE`; after unlocking, ControlStatus `user`, then `ready`].
  6. `Screenshot` [the orange flash for 3.5 s; neither the flash nor the indicator in the PNG]; then with `DESKTOP_MCP_DISABLE_FLASH=1` [no flash].
  7. With `[control] process_allowlist = ["notepad.exe"]`: `Click` on Notepad [works]; `Click` on another app [`Input blocked: the target window belongs to ...`; nothing clicked].
  8. From Chrome Remote Desktop (Open question 1): during `ai`, the remote mouse and the chord take over.
  9. `--control-policy balanced`: `PowerShell` is gated (the indicator appears).
- `pwsh scripts/build-release.ps1` again on the final branch.

- [ ] **Step 8: Commit**

```bash
git add windows/probes windows/scripts docs/superpowers/specs/2026-10-19-windows-port-design.md docs/superpowers/plans/2026-10-19-windows-p0-followups.md
git commit -m "Smoke-test desktop control under NativeAOT without taking control and record the P4 results" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Self-review

**Spec coverage (§11 P4 row, §7, §5, the follow-ups and the controller's decisions):**

| Requirement | Task |
|---|---|
| InputMonitor: LL hooks and Raw Input on the dispatcher; physical input only; swallow while suppressing | 6, 7 (Decisions 23, 29; Open question 1) |
| ControlStateMachine with `TimeProvider`; states disabled/unavailable/user/ready/ai | 4 (Decisions 9) |
| ControlGate: policy classification, desktop lock with 0.1 s re-check, 15 s lease, checkpoints, rejection results | 3, 4, 11, 12 (Decisions 4, 16, 17, 20, 21) |
| ControlOverlay: blue border on every monitor, notice with the three strings, cursor ring, click-through, `WDA_EXCLUDEFROMCAPTURE`; technology evaluated, `.pri` checked | 1, 8, 9 (Decisions 2, 10, 26, 28) |
| Capture flash #FF4500 for 3.5 s; `DESKTOP_MCP_DISABLE_FLASH` | 8, 10 (Decisions 27) |
| ControlNotifier: `notifications/message`, level notice, logger `desktop-mcp.control`, `USER_CONTROL`/`AI_CONTROL_AVAILABLE`; sessions with TTL | 13 (Decisions 22) |
| User first; takeover by 500 raw units (0.5 s reset) or Ctrl+Alt+Shift+Backspace (swallowed until released); `CONTROL_PREEMPTED` with `executed_steps`; ledger releases | 4, 6, 11 (Decisions 18, 24) |
| Back to `ready` after the idle timeout with nothing held; missed key-up after five samples 0.1 s apart | 4, 6, 7 (Decisions 25) |
| `unavailable`: no active session, input desktop not Default, hook or overlay failure, queue overflow (4096) | 6, 7, 11 (Decisions 14, 15) |
| ControlStatus `{state, generation, wait_seconds, enabled, policy, idle_timeout_seconds}`, never gated | 3, 12 |
| Input-lock safety: 1 s deadline renewed by a thread-pool heartbeat; stalled dispatcher bounded by `LowLevelHooksTimeout`; both integration tests | 6, 7, 11 (Decisions 12) |
| Dispatcher: timers per §5, `ShutdownQueueAsync` after hooks and windows; follow-ups' timer exceptions, `Func<Task>`, sync context | 2, 7, 9, 11 (Decisions 13) |
| `--no-control`: every call takes the lock | 12 |
| Process allowlist (new config key, process match, before SendInput) | 5 (Decisions 5) |
| Overlay out of Snapshot's window list and UIA walk; P3 Decisions 42 not tripped | 9 (Decisions 28) |
| Contract unchanged; no IL warnings; single exe; graphics stack lazy | 12 (`ContractTests`), 14 |

**Follow-ups:** every P4 item maps to a task or to Decisions 6 (see "Follow-ups this plan picks up").

**Step scan:** no TBD or "handle edge cases"; every test step names its tests with exact strings and values; code steps give signatures, files, upstream line ranges and the rules their behaviour follows; the algorithms the tests do not fully fix (the state machine's rules, the hook decisions, the coordinator loop, the gate) are written out once. Bracketed values in Task 14 are measurements recorded by that task. Task 1's verdict is the only open choice, and it is confined to `LayeredWindow.cs` behind `IOverlayLayer`.

**Type consistency:** `ControlState`, `ControlStatus`, `ControlCodes`, `ControlRejectedException`, `ControlJson` (Task 3) are used by Tasks 4, 11, 12, 13; `PhysicalEvent`/`PhysicalEventKind`/`BeginResult` and `ControlStateMachine.Begin/Arm/Checkpoint/End/Fault/Recover/Stop/SuppressionWanted/PhysicalHeld/TakeoverPending` (Task 4) are what `ControlCoordinator` calls (Task 11); `ControlSettings.ProcessAllowlist` and `ProcessAllowlist` (Task 5) feed `ToolServices.Create` (Task 12); `PhysicalInputFilter.SetSuppression/PublishState/Held/TakeoverPending/FailOpenRequested/ClearFailOpen/KeyDelivered/ButtonDelivered/Events/LastPhysical` (Task 6) match Tasks 7 and 11 (the rig's `Filter.Deadline` is an internal read-only property added with `SetSuppression`); `IInputMonitor.LastBeat/LastRehook/RequestRehook` (Task 7) match Task 11's health and recovery; `DispatcherHost` (Task 2) is used by Tasks 7, 9, 10; `OverlayBitmap`, `LayerArt`, `LayerSpec`, `OverlayLayout`, `OverlayArt` (Task 8) are used by Tasks 9, 10, 14; `IOverlayLayer`/`IOverlayLayerFactory`/`IControlIndicator` (Task 9) by Tasks 10, 11, 14; `IDesktopControl`, `ControlLease.Enter`, `ControlCall`, `ControlErrors.IsInterruption`, `DisabledControl`, `ControlCoordinator.Create` (Task 11) by Tasks 12, 13; `ControlGate`, `ControlGateFilter.Create(gate, remember)` (Task 12) by Task 13; `ICaptureFlash`/`NoFlash` (Task 10) by `DesktopMachine`.

**Review Focus:** each of the five lines has its named tests in Tasks 4, 6, 7, 8, 9 and 11.

**Proportion:** the plan holds signatures, exact texts, tests, the decision rule of the spike and four short rule lists; window plumbing, Win2D drawing code, fakes and rigs are left to the implementer, guided by the cited upstream lines.
