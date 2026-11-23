# Wait: check again when the platform reports a change (design, 2026-11-23)

## Why

`Wait` polls: it checks at once, then after 0.1 s, doubling to 1 s between checks. A window that opens during a
long wait is seen up to 1 s late, and every element check walks the app's whole tree again. macOS reports most of
what a wait reads (AXObserver notifications, NSWorkspace launch and quit notifications), so the wait can check as
soon as something changed, and the polling stays as the fallback for apps that report nothing.

## What the spike showed (throwaway `crates/macos/examples/ax_observe.rs`, 60 s, 10 apps)

- Every app accepted every notification it was asked for: Chrome, Electron apps (VS Code, Claude, GitHub),
  IntelliJ, Finder, TextEdit, System Settings.
- Opening a window sends AXWindowCreated (with its title) within a millisecond; closing one sends
  AXUIElementDestroyed and AXMainWindowChanged. The destroyed element answers nothing, so a notification only means
  "look again".
- Noise: AXValueChanged about once a second from a terminal (Claude Code's spinner) and 158 a minute from Finder;
  Chromium apps (Chrome, Code, Claude) send an AXCreated and AXUIElementDestroyed pair for an untitled window on
  every focus change; notifications come in bursts.

## Decisions (owner: "go to full improvement")

- Polling stays as it is (0.1 s doubling to 1 s). A reported change makes the next check come after a short
  settle instead: 50 ms for app and window waits, 250 ms for element waits (a tree walk). Checks are not made more
  often than that, however noisy the app.
- Window waits listen for AXWindowCreated, AXUIElementDestroyed, AXTitleChanged, AXMainWindowChanged,
  AXFocusedWindowChanged, AXWindowMiniaturized and AXWindowDeminiaturized, on the named app's processes, or on
  every app's when only a title is given.
- Element waits listen for AXCreated, AXUIElementDestroyed, AXLayoutChanged, AXTitleChanged, AXWindowCreated,
  AXMainWindowChanged and AXFocusedWindowChanged on the named app, or on the menu bar's owner (re-chosen when
  another app is activated). Not AXValueChanged: too frequent, and each would cost a walk. A static text whose
  value changes in place is still found by the polling.
- App waits listen for NSWorkspace's launch and quit notifications.
- Apps launched or quit during a wait gain or lose their observer at NSWorkspace's notification; an app that is
  not ready to accept notifications yet is tried again at the next one.
- Registering an observer messages the app, so the app element's messaging timeout is set first (as for reads,
  0.5 s), and a timeout stops that app's registration.

## Where the code is

- `crates/core/src/wait.rs`: `Watch` (what a wait reads), `Signal` (raised by the platform), `Changes` (a wait's
  watching, dropped with the wait), and `poll` racing the backoff sleep against the signal. Settle times in
  `crates/core/src/tuning.rs`.
- `Desktop::watch`, defaulting to no watching (Windows, test fakes).
- `crates/macos/src/observer.rs`: the AXObservers (run loop sources on the main run loop) and the NSWorkspace
  observers (blocks run on the main thread); everything is removed when the wait ends.

## Windows (added 2026-11-23)

UI Automation events only (the owner ruled out `SetWinEventHook`), in `crates/windows/src/observer.rs`:

- Every wait: WindowOpened and WindowClosed on the desktop root's subtree. Windows lists the apps that have a
  window, so these also cover app waits.
- Window waits add Name and WindowVisualState changes of the root's children (the top-level windows).
- Element waits add StructureChanged and Name changes on the subtrees of the windows the search walks
  (`windows_list::element_windows`, shared with `find_element`). A window opened later is seen by the opened event
  (a check), not observed itself.
- Window events are not filtered by app: few, and a check costs a window list.
- Handlers are added and removed on the pool (multithreaded apartment), each watch with its own automation object
  so `RemoveAllEventHandlers` removes exactly its handlers; UI Automation calls them on its own threads, so the
  core `Signal` is thread-safe (`AtomicBool` and a locked waker). The first moments of a wait, before the handlers
  are in, are covered by the polling.
- Type-checked from macOS (`cargo check --target x86_64-pc-windows-msvc`); not yet run on Windows.
