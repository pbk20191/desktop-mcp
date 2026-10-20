# P5 handoff (2026-10-21): resume in an isolated Hyper-V Windows VM

Work paused mid-P5 so it can continue in an isolated VM, where the live desktop checks (real
input, low-level hooks, overlays, foreground changes) can run without touching the user's session.

## State

- `windows-port` holds P0-P4 (merged). `windows-p5` = `windows-port` + the P5 plan (`889435d`) + P5 Task 1 (`d7472c8`, Streamable HTTP on Kestrel).
- P5 is executed with superpowers subagent-driven development. Plan:
  `docs/superpowers/plans/2026-10-21-windows-p5-http.md`. Spec: `docs/superpowers/specs/2026-10-19-windows-port-design.md`.
- This folder is a copy of the git-ignored SDD workspace `.superpowers/sdd/2026-10-21-windows-p5-http/`
  (ledger `progress.md`, `preflight.md` with rulings F1-F16, instructions, Task 1 brief and report).
  To resume, copy it back to `.superpowers/sdd/2026-10-21-windows-p5-http/` (also write a `plan-path` file there
  containing the plan path) and continue from the ledger.
- User decisions for P5: no telemetry; open questions take the plan's recommendations (403 for non-loopback Origin
  not in `--cors-origins`; JSONL audit log moves to P6; keep legacy SSE via the SDK's AppContext switch;
  `SessionMode` Stateful).

## Next step: P5 Task 1, fix round 1 (redo)

The fix agent was stopped mid-round; its partial edits are in `task-1-fix-round-1-partial.patch` (reference only;
also stashed in the original checkout). Redo round 1 from these review findings, then a scoped re-review:

Important
1. `ASPNETCORE_URLS` + `ASPNETCORE_PREFERHOSTINGURLS=true` still move the Kestrel bind (reproduced: `CreateSlimBuilder`
   snapshots `ASPNETCORE_*` into `WebHostOptions` before `Sources.Clear()`). After `Sources.Clear()` call
   `builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "")` and
   `UseSetting(WebHostDefaults.PreferHostingUrlsKey, "false")`. Test (non-parallel collection): set both variables to
   `http://127.0.0.2:0` / `true` (restore in `finally`) and assert the announcement is on 127.0.0.1 with no
   "Overriding address(es)" warning. Fix the Global Constraints wording.
2. Only `IOException` maps to the single `Error: Could not listen on …` line; other `SocketException`s (WSAEACCES from
   Hyper-V/WSL excluded port ranges, AddressNotAvailable) escape. Catch `IOException or SocketException`; unit-test the
   mapping.

Minor (fold in)
3. Scope the Host-category log filter to startup only; exit non-zero if a BackgroundService failure stops the host.
4. Rename `StdioSessionKey` to e.g. `NoSessionIdKey`; Task 2 must give stateless HTTP (null SessionId) SessionTtl zero.
5. Document the DNS-name / several-addresses / port 0 announcement (first listener's port), or bind the rest to it.
6. Dispose `HttpResponseMessage`s in `HttpServerTests`; `HttpRig` cancels/disposes the server on a `WaitAsync` timeout.

Then Tasks 2-10 of the P5 plan (each: implementer → task review → fix rounds → ledger), final whole-branch review,
one fix wave, merge into `windows-port`.

## Pending checks that need a real desktop (now possible in the VM)

Listed with exact commands in `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`:
- P3: InputDesktopTests (5), ForegroundServiceTests.SwitchesToTheTestWindowFromAnotherTestWindow,
  TypeSequenceDesktopTests, the by-hand act session (Notepad, App launch/kill-on-close, packaged-app exception,
  off-screen label, locked screen, Font-dialog combo drop-down).
- P4: InputMonitorDesktopTests (8, incl. RawInputReachesTheMonitorWindow, which settles Decisions 23),
  OverlayWindowDesktopTests (5), CaptureFlashDesktopTests (2), SnapshotServiceDesktopTests (now flash), and the
  by-hand control session (takeover by mouse distance and by Ctrl+Alt+Shift+Backspace, notifications/message).
- P2/P1: the WinRT clipboard test rerun with Chrome Remote Desktop and Windows Sandbox closed.

## Safety rules used so far (relax only inside the VM)

Implementers never ran anything that injects input, installs hooks, shows windows, changes the foreground,
launches apps or touches the clipboard on the user's live machine; HTTP tests bind loopback port 0 only, no
certificate store, no outbound calls. Inside the isolated VM those live checks can be run directly.
