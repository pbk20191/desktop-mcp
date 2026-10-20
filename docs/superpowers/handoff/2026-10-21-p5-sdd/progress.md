# SDD ledger — plan: docs/superpowers/plans/2026-10-21-windows-p5-http.md

Spec: docs/superpowers/specs/2026-10-19-windows-port-design.md
User: "continue" + "without telemetry" (2026-10-21). Open questions take the plan's recommendations: OQ1 403 for non-loopback Origin not in --cors-origins; OQ2 JSONL audit log moves to P6; OQ3 keep legacy SSE via the SDK AppContext switch; OQ4 SessionMode Stateful.
Ruling: commit trailer "(1M context)" — costs nothing if wrong.
Preflight: preflight.md (F1-F16).
Ruling: F1-F16 adopted as proposed, except F1 is fixed in Task 1 (McpServerSetup remembers sessions under server.SessionId ?? StdioSessionKey; stdio test: two calls + one state change → exactly one notification) because it is a live P4 bug on stdio (one registry entry per call → duplicate notifications), and Task 2 adds the HTTP test — costs nothing if wrong beyond moving one step.
Task 1: dispatched (base 889435d, implementer acfce0ea01dfd829b, opus; F1 (moved in), F2, F3, F4, F7, F12, F16)
Task 1: implementer DONE_WITH_CONCERNS (commit d7472c8; publish exe+pdb only; F1 stdio test RED→GREEN)
Task 1: review: Important — ASPNETCORE_URLS + PREFERHOSTINGURLS can still move the bind (reproduced); non-IOException bind failures crash instead of the one Error line. Folded in: startup-only log filter; key rename + Task 2 stateless TTL note; multi-address port-0 doc; test disposal.
Carry to T2: stateless HTTP (null SessionId) must get SessionTtl zero.
Handoff 2026-10-21: Task 1 fix round 1 stopped mid-way for a move to a Hyper-V VM; partial edits stashed locally and saved as task-1-fix-round-1-partial.patch; redo round 1 from the findings in this ledger.
