# Rust Windows W6: Registry, Process, packaging and docs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Windows-only `Registry` and `Process` tools, a Claude Desktop extension (`.mcpb`) for Windows, a
pinned Windows `tools/list`, and the READMEs saying what the Windows port is.

**Architecture:** both tools are platform tools in `crates/windows` (as `Screenshot`), on the Win32 registry and
process APIs (no PowerShell), with core-style arguments taken from `spec/windows/tools.json`'s shapes. The `.mcpb`
script mirrors `macos/scripts/build-mcpb.sh` in PowerShell.

**Spec:** `docs/superpowers/specs/2026-11-16-rust-port-windows-design.md` (Platform tools; Packaging)

## Global Constraints

- W1–W4 constraints hold; tests check normal behaviour only (owner), one or two per task; the full suite and parity
  run once at the end.
- Tests touch only a scratch registry key under `HKCU\Software\desktop-mcp-test-<pid>` (deleted after) and a child
  process the test starts itself; nothing on the desktop.
- macOS unchanged.

---

### Task 1: Registry

**Files:** Create `crates/windows/src/registry_tool.rs`; Modify `crates/windows/src/platform.rs` (tool, `TOOLS`); Test `crates/windows/tests/registry.rs`

- Arguments: `mode` `get` | `set` | `delete` | `list` (required), `path` (`HKCU:\Software\X` or `HKEY_CURRENT_USER\Software\X`;
  hives HKCU, HKLM, HKCR, HKU, HKCC), `name` (a value; empty or absent is the key's default value for get/set),
  `value` (string; DWord/QWord decimal or 0x hex, Binary hex bytes, MultiString one item per line), `type`
  `String` (default) | `ExpandString` | `Binary` | `DWord` | `MultiString` | `QWord`.
- `get`: `<name> (<type>): <value>`. `set`: creates the key as needed, `Set <name> in <path>.`. `delete`: with `name`
  the value, without it the key and its subkeys (`RegDeleteTreeW`). `list`: `Values:` lines `<name> (<type>): <value>`,
  then `Subkeys:` names.
- [ ] **Test** `set_get_list_delete_round_trip` on the scratch key (String, DWord, MultiString), then the key is gone.
- [ ] RED, implement, GREEN; commit `Read and write the registry on Windows`.

### Task 2: Process

**Files:** Create `crates/windows/src/process_tool.rs`; Modify `platform.rs`; Test `crates/windows/tests/process_tool.rs`

- `list`: name, pid, memory (working set, MB), CPU (total CPU seconds since start; Windows has no instant percentage
  without sampling), optionally only names containing `name`, sorted by `sort_by` `memory` (default) | `cpu` | `name`,
  at most `limit` (default 20). `kill`: by `pid` (preferred) or every process named `name`; `force=false` asks their
  windows to close and ends the rest, `force=true` ends them; never this server.
- [ ] **Test** `lists_and_kills_a_child` (a `ping -n 30` child started without a window: listed by name with its pid; killed by pid; it exits).
- [ ] RED, implement, GREEN; commit `List and end processes on Windows`.

### Task 3: the Windows tools/list, pinned

**Files:** Test `crates/windows/tests/tools_list.rs` with `crates/windows/tests/snapshots/tools.json` (`UPDATE_SNAPSHOTS=1` refreshes, as core's).
- [ ] Write the test, create the snapshot, review it, commit `Pin the Windows tools/list`.

### Task 4: the Claude Desktop extension

**Files:** Create `scripts/build-mcpb.ps1`.
- `pwsh scripts/build-mcpb.ps1 [exe]` (default `target\release\desktop-mcp.exe`): stages `server\desktop-mcp.exe`
  and `manifest.json` (as macOS's: binary server, `serve --transport stdio`, `DESKTOP_MCP_INCLUDE_TOOLS=OCR`,
  `compatibility.platforms = ["win32"]`, tools from the binary's own `tools/list`), zips to `dist\desktop-mcp.mcpb`.
- [ ] Run it against the debug build; check the archive holds the two files and the manifest names the Windows
  tools; commit `Package the Windows build as a Claude Desktop extension`.

### Task 5: docs

**Files:** Modify root `README.md` (the port table: Windows is Rust on the core, status), `crates/README.md` (Windows
tools list, `.mcpb`), the live checks (W6: Registry and Process by hand, installing the `.mcpb` in Claude Desktop).
- [ ] Run the full suite, parity and the macOS check once; commit `Say what the Windows port does`; push.
