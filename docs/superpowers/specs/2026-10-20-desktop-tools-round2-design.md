# Desktop tools, round 2: permissions, secure fields, conditional waits, window states, app quit, Spotlight ranking

Date: 2026-10-20
Status: approved 2026-10-20 (owner chose option A for decision 2b)
Builds on: `2026-10-20-native-desktop-tools-design.md` (branch pbk/native-desktop-tools, 67dc512)
Prior art consulted: github.com/AdelElo13/mac-control-mcp (permissions_status, secure-field handling,
wait_for_*, set_window_state / move_window_to_display / list_displays, quit_app)

## Intent

The installed agent showed two kinds of weakness. It answers confidently when it cannot see (no
Accessibility → `Window list` says "No windows."), and it makes the model guess: fixed `Wait`
sleeps, no way to quit an app or fill a screen with a window, Spotlight results that bury the app
the user named. This round fixes those inside the existing tools where it can, adding one tool.

## Goals

- The model can tell, at any time, which permissions desktop-mcp holds, and a tool that needs a
  missing permission says so instead of returning an empty answer.
- Password fields never leak through desktop-mcp's output.
- The model can wait for a condition (an app, a window, a UI element) instead of a fixed time.
- Windows can be maximized, put in and out of full screen, and moved to another display.
- Apps can be quit, gracefully or by force.
- Spotlight finds apps without a scope and ranks name matches above content-only matches.

## Non-goals

- New TCC permissions. Apple Events is reported, not requested.
- Browser, Mail, Messages, Calendar or recording tools (mac-control-mcp has them; out of scope).
- OCR-augmented accessibility trees (OCR's first call takes ~17 s).
- Waiting on OCR text; waits use the accessibility tree and process list only.

## Decisions

### 1. Permissions

a. New tool `Permissions` (readOnly, idempotent), no arguments. Output, one line each:
   `Accessibility: granted|not granted`, `Screen Recording: granted|not granted`,
   `Automation (System Events): granted|not granted|not determined`, then
   `Needs Accessibility: App (resize, move), Click, Type, Scroll, Move, Shortcut, Snapshot, Desktop, Window, Menu, Wait (until)`
   and `Needs Screen Recording: Snapshot (use_vision), OCR`. Checks: `AXIsProcessTrusted()`,
   `CGPreflightScreenCaptureAccess()`, `AEDeterminePermissionToAutomateTarget` for
   `com.apple.systemevents` with `askUserIfNeeded = false`. Never prompts.
b. Tools that cannot work without Accessibility check `AXIsProcessTrusted()` first and throw
   `ToolError("Accessibility is not granted to desktop-mcp. Grant it in System Settings > Privacy & Security > Accessibility, then restart desktop-mcp.", telemetry: "Accessibility not granted")`:
   `Window`, `Menu`, `Wait` with `until` (except `app_running`/`app_quit`), `Click`, `Type`,
   `Scroll`, `Move`, `Shortcut` (CGEvent posting is dropped silently without it), `App` `resize`/`move`,
   `Desktop`. `Snapshot` keeps working (it can list apps without it) but its text starts with that
   sentence as a note when the permission is missing.

### 2. Secure fields

a. Read side (decided): an element whose role or subrole is `AXSecureTextField` never has its
   value read or emitted, anywhere (Snapshot tree, Wait results, any future element output). It is
   shown with `value: "(hidden)"` and no length. Clipboard is unaffected.
b. Write side (owner: allow, 2026-10-20): when `Type` targets a point whose element
   (`AXUIElementCopyElementAtPosition`) is a secure field, it types as usual (login automation
   works) and the result is `"Typed N characters into a secure field at (x,y)."`; the text is never
   echoed. Refusing was rejected: the model could route around it through `Shell`/osascript key
   strokes, so it would cost login automation and buy nothing.

### 3. Conditional waits

`Wait` gains `until` (optional enum `app_running`, `app_quit`, `window`, `window_gone`, `element`),
`app` (name or bundle id, as `Window`'s filter), `title` (case-insensitive substring),
`role` (exact AX role, e.g. `AXButton`) and `timeout` (seconds, default 10, clamped by
`Shell.clampTimeout`). With `until` absent, `Wait` behaves exactly as today (`duration` required).
With `until` present, `duration` is ignored.
- `app_running` / `app_quit`: `app` required; polls `NSWorkspace.runningApplications`.
- `window` / `window_gone`: `app` optional, `title` optional (at least one required); uses the
  `Window` listing; success returns the matching `Window list` line(s).
- `element`: `title` and/or `role` required, `app` optional (default: menu-bar owner); searches the
  app's windows' AX trees (depth-limited to 25, title/description/value of non-secure elements
  matched case-insensitively); success returns `<role> "<title>" at [cx,cy]`, the centre in
  `Click` points.
- Polling backs off from 100 ms to 1 s; the deadline is absolute. Timeout →
  `ToolError("Timed out after <n>s waiting for <condition>.", telemetry: "Wait timed out")`.
  Cancellation (the client cancels the call) ends the wait at once.

### 4. Window states and displays

`Window` gains modes:
- `maximize`: sets the window's frame to the `visibleFrame` of the display that contains its
  centre (menu bar and Dock excluded), converted to AX points. Not `AXZoomButton` (app-defined).
- `fullscreen` / `exit_fullscreen`: set `AXFullScreen` true / false; an app that does not support
  it → `"Could not fullscreen window <id>."`.
- `display`: new argument `display` (integer index from `displays`); moves the window to that
  display keeping its relative position and size, clamped to the target's visible frame.
- `displays` (no id needed): one line per display:
  `<index>\t<name>\t[x,y,w,h]\tvisible [x,y,w,h]\tscale <s>` plus `main` for the main one, in
  `Displays.all()` order (left to right).
Result copy follows the existing style (`Maximized <id>.`, `Moved <id> to display <n>.`).

### 5. App quit

`App` gains modes `quit` and `force_quit` (`name` required: app name or bundle id, as `switch`):
- `quit`: `NSRunningApplication.terminate()`; result `"Quit requested for <name>."` (the app may
  ask to save).
- `force_quit`: `forceTerminate()`; result `"Force-quit <name>."`.
- Refuses desktop-mcp's own process and `loginwindow`; an app not running →
  `"Application '<name>' is not running."`. Multiple processes with that name: all of them.
The tool is already annotated destructive.

### 6. Spotlight

a. `kind = application` with no `scope` searches `/Applications`, `/System/Applications` and
   `~/Applications` (recursive, so Utilities is included); this also returns
   `/Applications/Safari.app`, which on current macOS is a symlink into a system cryptex and is
   missed when the scope is `/`.
b. Words queries rank name matches first: query 1 is the name clauses only
   (`kMDItemDisplayName == "*w*"cdw` for every word); query 2 is "every word in name or content,
   and at least one word not in the name" (`… && (kMDItemDisplayName != "*w1*"cdw || …)`), so
   the two sets are disjoint. Results are query 1 newest first, then query 2 newest first, up to
   `limit`; query 2's rows are read only if query 1 did not fill `limit`. "N more not shown" uses
   both totals. Both run within the existing 10 s budget. Raw queries are unchanged.

## Constraints

As the previous round: Swift 6, no `@unchecked Sendable`, macOS 13, blocking work in `offload`,
no new dependencies, tests never touch real windows, menus, apps, pasteboard or Shortcuts;
`ANONYMIZED_TELEMETRY` is gone (telemetry disabled at the source); `spec/tools.json` and the
tool-count assertions change for `Permissions`.

## Left to the builder

File layout; how the AX permission guard is shared (one helper); the poll loop's exact backoff;
the element-search walk (reuse `Tree.swift` traversal if it fits).
