# Rust Windows W4: acting on the desktop — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On Windows, `Click`, `Type`, `Scroll`, `Move`, `Shortcut`, `App`, the `Window` actions and the
Windows-only `MultiSelect` and `MultiEdit` act on the desktop through `SendInput` and Win32, with the core's
arguments and Windows wording.

**Architecture:** the core's input logic already drives a platform through `Desktop::post(InputEvent)`; Windows maps
each event to `SendInput`. Two macOS assumptions in the core become platform data: the editing chords Type uses
(select all, line start, line end) and the key names Shortcut accepts. Window actions use Win32 (`SetWindowPos`,
`ShowWindow`, `WM_CLOSE`, a foreground-lock-safe focus); App launches from the shell's Apps folder
(`FOLDERID_AppsFolder`, which holds both desktop and packaged apps). Input into a window of higher integrity
(an elevated app) is refused with a message instead of being dropped silently by UIPI.

**Tech Stack:** `windows` 0.62 (`Win32_UI_Input_KeyboardAndMouse`, `Win32_UI_WindowsAndMessaging`,
`Win32_UI_Shell` incl. `Common`/`PropertiesSystem`, `Win32_Security`, UI Automation).

**Spec:** `docs/superpowers/specs/2026-11-16-rust-port-windows-design.md` (`Desktop` on Windows: Input, Windows, apps)

**Reference (behaviour):** the C# port on `windows-port`: `windows/src/DesktopMcp.Desktop/Input/{SendInputInjector,
DesktopInput,ProcessIntegrity}.cs`, `Apps/{AppCatalog,AppLauncher,AppService}.cs`, `Actions/*.cs`,
`Tree/WindowSystem.cs`.

## Global Constraints

- Everything from W1–W3 holds (one message loop; pool for blocking/cross-process work; WinRT first; minimal tests).
- **Automated tests never inject input, show windows, launch apps or touch the clipboard.** Tests are pure
  (event → `INPUT` mapping, key names, chords, catalog matching) or act on a window the test creates and never shows.
- Coordinates are W3's physical screen pixels; `SendInput` absolute moves use `MOUSEEVENTF_VIRTUALDESK` normalized
  over the virtual screen.
- macOS behaviour and `tools/list` stay byte-identical (core defaults are macOS's).
- `Window` `fullscreen`/`exit_fullscreen` have no Windows equivalent for arbitrary apps: they answer false with a
  message saying so (the macOS-only modes stay in the shared schema).

## Review Focus

1. **Input aimed at an elevated window** — refused with "runs as administrator" before any event is posted (Task 2).
2. **Typing non-BMP text (emoji) and Korean** — reaches the target as typed (`KEYEVENTF_UNICODE` surrogate pairs;
   Task 2 test `unicode_text_becomes_surrogate_pairs`).
3. **A modifier left held** — every Shortcut and Type ends with modifiers released (core `ReleaseModifiers` mapped).
4. **Focusing a window of another app while the user is typing elsewhere** — the foreground lock is respected or
   worked around without leaving a stuck Alt key (Task 3).
5. **Moving a window between displays of different scale** — the frame asked for is the frame obtained (Task 3
   test on a hidden window; mixed-DPI as a live check).

---

### Task 1: core — editing chords and key names per platform

**Files:** Modify `crates/core/src/input.rs`, `crates/core/src/mcp.rs` (`Desktop` defaults), macOS `post` for the new
modifier; Test `crates/core/tests/input.rs`

**Interfaces:**
- Produces:
  ```rust
  // input.rs
  pub struct EditingKeys { pub select_all: &'static [&'static str], pub line_start: &'static [&'static str], pub line_end: &'static [&'static str] }
  pub const MAC_EDITING: EditingKeys;            // command+a, command+left, command+right (today's behaviour)
  pub enum Modifier { …, Meta }                  // the Windows key; macOS posts it as command
  // mcp.rs, trait Desktop (default methods, so macOS and the fakes compile unchanged):
  fn editing_keys(&self) -> &'static EditingKeys { &MAC_EDITING }
  /// Key names this platform accepts besides `KEYS` (Windows: win, windows, insert, printscreen, …).
  fn extra_keys(&self) -> &'static [&'static str] { &[] }
  ```
  `type_at` uses `desktop.editing_keys()`; `shortcut` validates against `KEYS` + `extra_keys()`; `win`/`windows`/`super`
  parse as `Modifier::Meta`.

- [ ] **Step 1: Tests:** `type_uses_the_platform_chords` (a fake desktop whose `editing_keys` are ctrl+a/ctrl+home/ctrl+end: `Type` with `clear` and `caret_position=start` posts those chords, not command); `extra_keys_are_accepted` (`Shortcut "win+r"` refused by the default fake, accepted by a fake with `extra_keys = ["win","r"]`… `r` is already a key: `win` alone is the extra).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** core suite PASS; macOS check compiles.
- [ ] **Step 5:** Commit `Let a platform give its editing chords and extra key names`.

### Task 2: input with `SendInput`

**Files:** Create `crates/windows/src/input.rs`, `crates/windows/src/integrity.rs`; Modify `crates/windows/src/desktop.rs` (`post`, `cursor_position`, `secure_input_at`, `select_typing_layout`, `editing_keys`, `extra_keys`); Test `crates/windows/tests/input.rs`

**Interfaces:**
- Produces:
  ```rust
  /// The `INPUT`s for one event; `screen` is the virtual screen (x, y, width, height) for absolute coordinates.
  pub fn inputs(event: &InputEvent, screen: (i32, i32, i32, i32)) -> Vec<INPUT>;
  pub fn virtual_key(name: &str) -> Option<VIRTUAL_KEY>;   // KEYS + Windows extras
  pub const EXTRA_KEYS: &[&str];                           // win, windows, super, insert, printscreen, scrolllock, pause, numlock, menu, apps, f13–f24, browserback…
  pub const EDITING: EditingKeys;                          // ctrl+a, ctrl+home, ctrl+end
  // integrity.rs
  /// Whether the process owning the window at (x, y) (or the foreground window) runs at a higher integrity level
  /// than this one, which UIPI would make ignore our input.
  pub fn input_blocked_at(point: Option<(i32, i32)>) -> bool;
  ```
- Mapping: `command`/`cmd`/`control`/`ctrl` → `VK_CONTROL`, `option`/`alt` → `VK_MENU`, `Meta` → `VK_LWIN`,
  `fn` → nothing; `Move`/`Drag` absolute with `MOUSEEVENTF_MOVE | ABSOLUTE | VIRTUALDESK`; buttons left/right/middle;
  `Wheel` = `WHEEL_DELTA` × notches (`MOUSEEVENTF_WHEEL` / `HWHEEL`, sign as the core's); `Text` = one
  `KEYEVENTF_UNICODE` down/up per UTF-16 unit; `ReleaseModifiers` = key-ups for both sides of ctrl, shift, alt, win.
- `post` sends on the main thread (SendInput is quick). Before Click/Type/Scroll/Move/Shortcut events, `post` is
  preceded by the core's `require_accessibility`, which on Windows becomes the integrity check: `accessibility_trusted`
  stays true, and `post` of the first event of a call refuses when `input_blocked_at` says so (surface: the call fails
  with "The window at (x,y) runs as administrator; desktop-mcp cannot send input to it unless it runs as
  administrator too.").
- `secure_input_at(x, y)`: UIA `ElementFromPoint` → `IsPassword` (on the pool).

- [ ] **Step 1: Tests (pure):** `keys_map_to_virtual_keys` (ctrl, alt, win, a, f12, enter, pageup, printscreen); `click_is_move_down_up_at_normalized_coordinates` (virtual screen (0,0,2560,1600): `Down` at (1280,800) → absolute ≈ 32768,32768 with VIRTUALDESK); `unicode_text_becomes_surrogate_pairs` (`"가😀"` → 6 inputs, the emoji as two units); `wheel_signs_follow_the_core` (vertical +2 → +240); `own_process_is_not_blocked` (`input_blocked_at(None)` is false when the test's own console/terminal is foreground… use the test process: a helper `integrity_of(pid)` equals for our pid and itself).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Send input with SendInput, refusing elevated targets`.

### Task 3: window actions

**Files:** Modify `crates/windows/src/windows_list.rs` (`Win32Windows` actions), Create `crates/windows/src/foreground.rs`; Test `crates/windows/tests/window_actions.rs`

**Interfaces:**
- `focus`: restore if iconic, then `foreground::bring(hwnd)`: `SetForegroundWindow`; when refused,
  `AllowSetForegroundWindow(ASFW_ANY)` + a zero-length `SendInput` of `VK_MENU` down/up (the documented
  "last input" rule) and retry; never `AttachThreadInput`. Returns whether the window is foreground afterwards.
- `set_minimized`: `ShowWindow(SW_MINIMIZE | SW_RESTORE)`. `close`: `PostMessageW(WM_CLOSE)`.
- `move_to`/`resize`/`set_frame`: the asked frame is the visible (DWM extended) frame; the invisible borders
  (`GetWindowRect` − extended bounds) are added back for `SetWindowPos(SWP_NOZORDER | SWP_NOACTIVATE)`; a window that
  is not resizable (`WS_THICKFRAME` absent) answers `FrameResult::NotResizable` when the size must change.
- `set_full_screen`: false (no system full screen for arbitrary windows).
- Windows are found again by `number` (the HWND) and must still belong to the listed pid.

- [ ] **Step 1: Tests** on a window the test creates **without `WS_VISIBLE`**: `set_frame_lands_where_asked` (overlapped window, set_frame to (100,100,400,300) → extended frame equals that within 1 px); `close_posts_wm_close` (a window whose proc records `WM_CLOSE`, pumped by the test thread); `not_resizable_is_reported` (`WS_POPUP` without thick frame, size change → `NotResizable`).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Arrange windows on Windows`.

### Task 4: App — launch, switch, resize, move, quit

**Files:** Create `crates/windows/src/apps.rs`; Modify `crates/windows/src/desktop.rs` (`launch_app`, `activate_app`, `change_front_window`, `terminate`, `find_application`); Test `crates/windows/tests/apps.rs`

**Interfaces:**
- Produces:
  ```rust
  pub struct AppEntry { pub name: String, pub parsing_name: String }   // from FOLDERID_AppsFolder
  pub fn catalog() -> Vec<AppEntry>;                                     // blocking; SHGetKnownFolderItem + EnumItems
  pub fn best_match<'a>(entries: &'a [AppEntry], query: &str) -> Option<&'a AppEntry>; // exact (ignore case) > prefix > contains; shortest name wins ties
  ```
- `launch_app(name)`: an executable path → `ShellExecuteExW`; otherwise `best_match` in the catalog →
  `ShellExecuteExW("shell:AppsFolder\<parsing_name>")`; false when nothing matches.
- `activate_app(name)`: the front window of the app (`TopWindow::is_app`) → `foreground::bring`.
- `change_front_window(change)`: the focused top window → `set_frame` (move/resize as `FrameChange` says).
- `terminate(pids, force)`: `force` → `TerminateProcess`; else `WM_CLOSE` to each of their top windows. Never this
  process or `explorer.exe`'s shell (the core already excludes the server).
- Windows `App` text: "Manages Windows applications … 'launch' (opens an app by its Start menu name, or an executable
  path) … 'quit' (asks its windows to close) … 'force_quit' (ends its processes)".

- [ ] **Step 1: Tests:** `best_match_prefers_exact_then_prefix` (pure: `["Notepad", "Notepad++", "Microsoft Edge"]`: "notepad" → Notepad; "edge" → Microsoft Edge; "zzz" → None); `catalog_is_readable` (read-only: non-empty, every entry has a name and parsing name).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit `Launch, switch and quit applications on Windows`.

### Task 5: MultiSelect and MultiEdit, tool text and the tool list

**Files:** Create `crates/windows/src/multi_tools.rs`; Modify `crates/windows/src/platform.rs` (`WindowsPlatform` holds the desktop; text for App, Shortcut, Type; `TOOLS`), `crates/cli/src/main.rs`, `crates/parity/tests/tools.rs` (input/window refusal tests run on Windows when they post nothing); Test `crates/windows/tests/desktop.rs`

**Interfaces:**
- `MultiSelect { locs: [[x, y], …], press_ctrl: bool = true }`: with `press_ctrl`, ctrl held across the clicks
  (down before, up after, released even on error); answers "Selected N items." / "Clicked N locations.".
- `MultiEdit { locs: [[x, y, "text"], …] }`: the core's `type_at` for each (clear first), answers one line per field.
- (`labels` of the Windows-MCP schema are not offered: element ids belong to a snapshot the server does not keep.)
- `TOOLS` (W4): `App, Click, Clipboard, Move, MultiEdit, MultiSelect, Notification, Scrape, Screenshot, Scroll,
  Shell, Shortcut, Snapshot, Type, Wait, Window` (+ `OCR` opt-in).
- Shortcut Windows text: examples `ctrl+c`, `ctrl+v`, `alt+tab`, `win+r`; "use ctrl, not command".

- [ ] **Step 1: Tests:** `w4_tools_are_listed` (names = the W4 list; Shortcut text mentions `win+r`); `multi_tools_refuse_bad_locations` (MultiSelect with `[[1]]` and MultiEdit with `[[1,2]]` answer errors before any input — the desktop's `post` is not reached: run with a recording `Desktop` wrapper).
- [ ] **Step 2:** Run — FAIL. **Step 3:** Implement. **Step 4:** Run — PASS; parity (now including the input/window refusal tests on Windows) green; macOS check compiles.
- [ ] **Step 5:** Commit `Offer the input, app and multi tools on Windows`.

### Task 6: live checks

**Files:** Modify `docs/superpowers/plans/2026-11-16-rust-windows-live-checks.md` (W4)

- [ ] **Step 1:** Add: Click/double/right click in Notepad and Explorer; Type with Korean, emoji, `clear`,
  `caret_position` start/end, `press_enter`; Scroll both ways; Move and drag a file; Shortcut `ctrl+a`, `alt+tab`,
  `win+r` (then Escape); App launch by Start menu name (Notepad, Calculator — a packaged app), switch, resize, move,
  quit, force_quit; Window focus/move/resize/minimize/restore/maximize/display/close across two displays of different
  scale; input refused toward an elevated window; MultiSelect files with ctrl; MultiEdit two fields.
- [ ] **Step 2:** Commit `List the W4 checks for the owner`.
