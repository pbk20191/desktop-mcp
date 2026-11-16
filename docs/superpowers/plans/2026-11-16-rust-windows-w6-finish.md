# Rust Windows W6: packaging and docs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Claude Desktop extension (`.mcpb`) for Windows, a
pinned Windows `tools/list`, and the READMEs saying what the Windows port is.

**Architecture:** no new tools (`Registry` and `Process` were dropped by the owner: `Shell` covers them). The `.mcpb`
script mirrors `macos/scripts/build-mcpb.sh` in PowerShell.

**Spec:** `docs/superpowers/specs/2026-11-16-rust-port-windows-design.md` (Tools; Packaging)

## Global Constraints

- W1–W4 constraints hold; tests check normal behaviour only (owner), one or two per task; the full suite and parity
  run once at the end.
- macOS unchanged.

---

### Task 1: the Windows tools/list, pinned

**Files:** Test `crates/windows/tests/tools_list.rs` with `crates/windows/tests/snapshots/tools.json` (`UPDATE_SNAPSHOTS=1` refreshes, as core's).
- [ ] Write the test, create the snapshot, review it, commit `Pin the Windows tools/list`.

### Task 2: the Claude Desktop extension

**Files:** Create `scripts/build-mcpb.ps1`.
- `pwsh scripts/build-mcpb.ps1 [exe]` (default `target\release\desktop-mcp.exe`): stages `server\desktop-mcp.exe`
  and `manifest.json` (as macOS's: binary server, `serve --transport stdio`, `DESKTOP_MCP_INCLUDE_TOOLS=OCR`,
  `compatibility.platforms = ["win32"]`, tools from the binary's own `tools/list`), zips to `dist\desktop-mcp.mcpb`.
- [ ] Run it against the debug build; check the archive holds the two files and the manifest names the Windows
  tools; commit `Package the Windows build as a Claude Desktop extension`.

### Task 3: docs

**Files:** Modify root `README.md` (the port table: Windows is Rust on the core, status), `crates/README.md` (Windows
tools list, `.mcpb`), the live checks (W6: installing the `.mcpb` in Claude Desktop).
- [ ] Run the full suite, parity and the macOS check once; commit `Say what the Windows port does`; push.
