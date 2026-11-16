# Rust Windows W5: desktop control — Implementation Plan (dropped)

> **Dropped by the owner on 2026-11-16** after Tasks 1–5: desktop control is not ported (see the spec's Control section).
> Those commits were reverted; this plan is kept as the record of what was tried.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the AI from fighting the user for the mouse and keyboard on Windows: low-level hooks on the main
message loop, a pure control state machine, a heartbeat on the thread pool that alone keeps input suppressed, the
blue control indicator and the orange capture flash (Direct2D, layered, click-through, excluded from capture), a
gate in front of every gated tool call, `notifications/message` on state changes, the `ControlStatus` tool and the
`[control]` settings.

**Architecture:** the C# port's P4 (on `windows-port`: `docs/superpowers/plans/2026-10-20-windows-p4-desktop-control.md`,
whose Decisions 9–29 port upstream `desktop/control*.py` and are the behavioural reference), rebuilt on this port's
pieces: hooks and the Raw Input window live on the one message loop (main thread, W1); the heartbeat is a
thread-pool timer (W1 `pool`); the overlay draws with Direct2D into a DIB shown with `UpdateLayeredWindow`; the gate
is a core hook around tool dispatch (spec "Gate placement"). `core` stays free of control: it only offers the hook,
the logging capability and session peers.

**Tech Stack:** `windows` 0.62 (`Win32_UI_WindowsAndMessaging` hooks, `Win32_UI_Input` Raw Input,
`Win32_Graphics_Gdi`, Direct2D/DirectWrite), rmcp 3.10 (`notify_logging_message`), W1–W4 modules.

**Spec:** `docs/superpowers/specs/2026-11-16-rust-port-windows-design.md` (Control; Gate placement)

## Decisions (recorded here; the owner approved porting control in this project)

1. **Upstream behaviour via the C# port's P4 Decisions 9–29** unless listed below: states `disabled`, `unavailable`,
   `user`, `ready`, `ai`; generation +1 per change; idle timeout 10 s; lease 15 s after a call; suppression deadline
   1 s renewed only by the heartbeat (50 ms); queue 4096; takeover chord Ctrl+Alt+Shift+Backspace (swallowed until
   released); mouse takeover 500 units (40–2000), reset after 0.5 s pause; missed key-ups cleared after five all-up
   samples; codes `USER_CONTROL`, `CONTROL_UNAVAILABLE`, `CONTROL_PREEMPTED`, `PHYSICAL_INPUT_HELD`; rejection text
   `{"code": C, "status": {...}}` with Python's separators; notifications `notifications/message`, level `notice`,
   logger `desktop-mcp.control`, data `{"event": "USER_CONTROL" | "AI_CONTROL_AVAILABLE", "status": {...}}`.
2. **Foreign injected input counts as user input (my call; C# Open question 1's recommendation).** The owner works
   through Chrome Remote Desktop, whose input is injected: it must be able to take over. Our own input carries the
   signature `0x444D4350` in `dwExtraInfo` (added to W4's `SendInput`) and is ignored; unsigned injected input is
   treated like physical input; its pointer path in pixels counts against `mouse_takeover_pixels` (500).
3. **Fail closed (my call; C# Open question 2's recommendation).** With control enabled and the monitor not running,
   gated calls get `CONTROL_UNAVAILABLE`; `--no-control` (or `[control] enabled = false`) runs everything unprotected.
4. **Overlay:** Direct2D rendered into a 32-bit premultiplied DIB, shown with `UpdateLayeredWindow`
   (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`), excluded from capture
   with `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`; rendering on the pool, `UpdateLayeredWindow` on the main
   thread. No WinUI, no Composition (no App SDK).
5. **Policies for this port's tools:** `relaxed` gates Click, Type, Scroll, Move, Shortcut, MultiSelect, MultiEdit,
   App (every mode), Window (every mode but `list`/`displays`), Clipboard `write`; `strict` gates every tool but
   ControlStatus. Ungated calls run concurrently; gated calls are serialised by the gate.
6. **Process allowlist** (C# P4 Decisions 5) is left out of W5: it is independent of control and can follow later.
7. **Checkpoints:** a preempted call's later input is dropped by `post` (the lease is closed); the gate turns the
   call's result into `CONTROL_PREEMPTED` with `executed_steps` (events posted before preemption).

## Global Constraints

- W1–W4 constraints hold. **Hook callbacks never block:** read atomics, push at most one event into a bounded
  lock-free queue, return; no allocation, no locks, no logging, no `SendInput`/UIA/COM; panics caught (abort is fatal).
- **No path leaves input locked:** the hook swallows only while the deadline (an `AtomicU64` of `QueryPerformanceCounter`
  ticks) is in the future; only the heartbeat renews it; a stalled main thread is bounded by Windows'
  `LowLevelHooksTimeout`.
- **Tests never install hooks, show windows or inject input.** The state machine, the hook filter, the policy and
  the JSON are pure and unit-tested; the hook, Raw Input and overlay windows are live checks.
- **Test diet (owner, 2026-11-16):** each task runs only its own test file; the workspace suite and parity run once,
  before the phase review.
- macOS output unchanged (core hooks default to nothing).

## Review Focus

1. **The heartbeat stops (a panic, a hung pool) while the AI holds control** — the user gets input back within ~1 s
   (Task 3 `a_lapsed_deadline_passes_input`).
2. **The takeover chord with right-hand modifiers, auto-repeat, or other keys held** — takes over once, swallows the
   chord keys until released (Task 3 tests).
3. **Win+L / UAC / session switch mid-call** — `unavailable`, held input released, then `user`, then `ready` after
   the idle timeout, never straight back to `ai` (Task 2 test `leaving_the_input_desktop_fails_open`).
4. **Monitors rearranged while the indicator shows** — layout rebuilt for the new monitors (Task 6, pure layout test
   with negative coordinates).
5. **A key held when a call starts / a key-up never delivered** — `PHYSICAL_INPUT_HELD`, and a stale key clears after
   five all-up samples (Task 3).

---

### Task 1: core — a gate around tool calls, the logging capability, session peers

**Files:** Modify `crates/core/src/platform.rs`, `crates/core/src/mcp.rs` (implement `call_tool` around the router
instead of `#[tool_handler]`'s, keeping behaviour); Test `crates/core/tests/platform.rs`

**Interfaces:**
- Produces:
  ```rust
  /// What the gate decided for one call.
  pub enum Admission { Run(Box<dyn std::any::Any>), Refuse(CallToolResult) }
  pub trait CallGate: 'static {
      /// Before the call: admit (with a lease, held until `finish`) or refuse. `peer` lets the gate notify the client later.
      fn admit(&self, tool: &str, arguments: &serde_json::Map<String, serde_json::Value>, peer: rmcp::Peer<rmcp::RoleServer>) -> Pin<Box<dyn Future<Output = Admission>>>;
      /// After the call (or its cancellation): the result as the client sees it.
      fn finish(&self, lease: Box<dyn std::any::Any>, result: CallToolResult) -> CallToolResult;
  }
  // Platform gains: fn gate(&self) -> Option<Rc<dyn CallGate>> { None }   and   fn logging(&self) -> bool { false }
  ```
- With no gate, `tools/call` and `initialize` are byte-identical to today (macOS parity).

- [ ] **Step 1: Tests:** `a_gate_refuses_and_wraps` (a test gate refusing `Shell` with a fixed result; admitting `Wait` and appending to its text in `finish`); `logging_capability_when_asked` (initialize with a platform whose `logging()` is true lists `logging`).
- [ ] **Step 2–4:** RED, implement, GREEN (core `platform` and `mcp` tests only).
- [ ] **Step 5:** Commit `Let a platform gate tool calls and declare logging`.

### Task 2: control model — policy, status, state machine (pure)

**Files:** Create `crates/windows/src/control/{mod,policy,status,machine}.rs`; Test `crates/windows/tests/control_machine.rs`

**Interfaces:**
- `policy::gated(policy: Policy, tool: &str, arguments: &Map<String, Value>) -> bool` (Decision 5).
- `status::Status { state, generation, wait_seconds, enabled, policy, idle_timeout_seconds }` with `to_json()` (compact,
  that key order) and `rejection(code, &Status, executed_steps: Option<u64>) -> String` (Python separators).
- `machine::Machine` (clock injected as `Fn() -> Duration`): `tick()`, `physical(event)`, `takeover()`,
  `begin_call() -> Result<Lease, Code>`, `end_call(lease)`, `overlay_shown()`, `fault()`, `desktop_lost()`,
  `desktop_back()`, `wants_suppression() -> bool`, `status()`; transitions per C# P4 Decisions 9, 12, 14, 15.

- [ ] **Step 1: Tests:** `ready_to_ai_and_back_with_the_lease` (begin → ai; end → ai until 15 s; then ready); `physical_input_takes_over` (in ai → user, call gets USER_CONTROL; ready again after 10 s idle); `held_input_refuses_begin` (PHYSICAL_INPUT_HELD); `leaving_the_input_desktop_fails_open` (→ unavailable, then user on return, never ai); `suppression_waits_for_the_indicator` (no suppression in ai before `overlay_shown`); `rejection_json_matches_upstream` (exact string for USER_CONTROL with generation 3); `relaxed_and_strict_policies`.
- [ ] **Step 2–4:** RED, implement, GREEN. **Step 5:** Commit `Model desktop control: policy, status and states`.

### Task 3: the hook filter (pure) and the input signature

**Files:** Create `crates/windows/src/control/filter.rs`; Modify `crates/windows/src/input.rs` (`dwExtraInfo = SIGNATURE` on every INPUT); Test `crates/windows/tests/control_filter.rs`

**Interfaces:**
- `pub const SIGNATURE: usize = 0x444D4350;`
- `Filter::decide(&mut self, event: HookEvent, now: u64, deadline: u64) -> Verdict` where `HookEvent` is a key or
  button down/up or a move (vk/button, injected flag, extra info, point) and `Verdict { swallow: bool, report: Option<Physical>, takeover: bool }`;
  key-ups inherit their down's verdict; our signature is ignored; foreign injected counts as physical (Decision 2);
  chord detection with left/right modifiers; mouse pixel path for foreign moves.
- `MissedKeyUps::sample(&mut self, all_up: bool) -> bool` (true after five all-up samples in a row).

- [ ] **Step 1: Tests:** `a_lapsed_deadline_passes_input`; `ours_is_ignored_foreign_counts`; `chord_with_right_hand_modifiers_takes_over`; `chord_keys_stay_swallowed_until_released`; `auto_repeated_backspace_does_not_retrigger`; `a_swallowed_down_swallows_its_up_even_after_the_deadline`; `five_all_up_samples_clear_a_missed_key_up`; `signed_inputs_carry_the_signature` (W4 `inputs()` sets `dwExtraInfo`).
- [ ] **Step 2–4:** RED, implement, GREEN. **Step 5:** Commit `Decide hook events and sign our own input`.

### Task 4: the monitor — hooks and Raw Input on the main loop

**Files:** Create `crates/windows/src/control/monitor.rs` (`WH_KEYBOARD_LL`/`WH_MOUSE_LL` installed on the main
thread, reinstalled every 5 s new-before-old; a message-only window with `RIDEV_INPUTSINK` Raw Input for relative
mouse motion; a bounded SPSC queue of 4096 events; the deadline atomic; fail-open flag). No automated test (hooks);
`cargo build` and the live checks.

- [ ] **Step 1:** Implement with every callback wrapped in `catch_unwind`, returning `CallNextHookEx` unless swallowing.
- [ ] **Step 2:** Build; commit `Watch physical input with low-level hooks`.

### Task 5: the coordinator — heartbeat, health, release

**Files:** Create `crates/windows/src/control/coordinator.rs`; Modify `crates/windows/src/desktop.rs` (`post` drops
input when the call's lease is closed and counts executed steps); Test `crates/windows/tests/control_coordinator.rs`

**Interfaces:**
- A periodic pool timer (50 ms): drain the queue into the machine, tick, set the deadline to now+1 s while
  `wants_suppression()`, check the input desktop (`OpenInputDesktop` name `Default`) and the hooks' health, release
  held modifiers when leaving `ai`, publish state changes to the notifier; a panic or a missed lock stops renewal.
- `Coordinator::begin(tool) -> Result<CallLease, (Code, Status)>`, `end(lease)`, `status()`, `subscribe() -> watch`.

- [ ] **Step 1: Tests** (with a fake clock and a fake monitor feeding events): `a_stopped_heartbeat_lets_the_deadline_lapse`; `takeover_mid_call_preempts_and_counts_steps`.
- [ ] **Step 2–4:** RED, implement, GREEN. **Step 5:** Commit `Coordinate control from a thread-pool heartbeat`.

### Task 6: the indicator and the capture flash

**Files:** Create `crates/windows/src/overlay/{layout,art,layer,indicator,flash}.rs`; Modify capture callers
(Snapshot `use_vision`, Screenshot, OCR) to flash after a capture; Test `crates/windows/tests/overlay.rs` (pure layout)

**Interfaces:** layout from the displays (edge strips 56 px, notice panel, cursor ring 88 px, colour (45,145,255),
breathing 140–255 over 3 s; flash #FF4500, 3.5 s, 30 ms frames, inward halo per captured display or outward around a
region), per C# P4 Decisions 26–27; `DESKTOP_MCP_DISABLE_FLASH`; layers excluded from capture (a layer that cannot be
excluded is not shown for the flash).

- [ ] **Step 1: Tests (pure):** `layout_covers_monitors_left_of_the_primary`; `notice_is_left_out_on_a_narrow_monitor`; `flash_intensity_curve`.
- [ ] **Step 2–4:** RED, implement, GREEN. **Step 5:** Commit `Show the control indicator and the capture flash`.

### Task 7: the gate, ControlStatus, notifications and settings

**Files:** Create `crates/windows/src/control/gate.rs`, `crates/windows/src/control/status_tool.rs`; Modify
`crates/windows/src/platform.rs` (gate, logging, ControlStatus in `TOOLS`), `crates/core/src/config.rs` and
`crates/core/src/plan.rs` (`[control]`: `enabled`, `policy`, `idle_timeout_seconds`, `mouse_takeover_units`,
`mouse_takeover_pixels`; `--no-control`; `DESKTOP_MCP_*` only where upstream has an envvar), `crates/cli/src/main.rs`
(start the monitor and coordinator before serving, when enabled); Test `crates/windows/tests/control_gate.rs`,
`crates/core/tests/config.rs`

- [ ] **Step 1: Tests:** `ungated_calls_are_not_held`; `a_refused_call_returns_the_rejection_json`; `control_status_reports_json_and_structured_content`; `config_reads_the_control_section` (core); `user_control_is_notified_once_per_generation`.
- [ ] **Step 2–4:** RED, implement, GREEN; then once: workspace suite, parity, macOS check.
- [ ] **Step 5:** Commit `Gate desktop calls behind control and report it`.

### Task 8: live checks

- [ ] Add the W5 section to `docs/superpowers/plans/2026-11-16-rust-windows-live-checks.md`: indicator on every
  monitor while the AI acts; takeover by mouse and by the chord (physical and over Chrome Remote Desktop); idle
  return to ready; Win+L mid-call; the flash after Screenshot/Snapshot; the indicator and flash absent from
  captures; `--no-control`; ControlStatus and notifications in Claude Code. Commit.
