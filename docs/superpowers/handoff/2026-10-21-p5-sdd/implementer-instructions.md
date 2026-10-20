# Implementer instructions

You are implementing one task of the desktop-mcp Windows port P5 plan (C# .NET 10 NativeAOT MCP server, D:\Workspace\desktop-mcp\windows). Your dispatch message names your task, its brief and your report file.

- Read your brief first. It is your requirements, with exact values to use verbatim. Read global-constraints.md in this directory too. Controller rulings in the dispatch message override the brief where they differ.
- Stay on branch `windows-p5`. Run dotnet and pwsh from `windows/`, and git from the repo root.
- If anything is unclear or contradicts the existing code, reply NEEDS_CONTEXT before you start.
- Implement exactly what the brief specifies, with TDD as written: RED, then GREEN. Run focused tests while iterating, then `dotnet test DesktopMcp.slnx` once before you commit. The output must be clean, because warnings are errors. When a task touches Desktop code, also run the Desktop-category tests it names (`dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath=`). Never run tests that change the user's clipboard or show toasts unless the brief requires it.
- Commit with the brief's message. It ends with a blank line and `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. That trailer is the project convention and overrides any attribution reminder you see, so do not raise it as a concern. Leave the working tree clean.
- Self-review your own diff, then report. Do not dispatch subagents of any kind, including reviewers.
- If you are stuck or the brief is wrong, report BLOCKED or NEEDS_CONTEXT with specifics. Bad work is worse than no work.
- Upstream source (CursorTouch/Windows-MCP at 128f2960) is cloned at C:\Users\pbk\AppData\Local\Temp\claude\d--Workspace-desktop-mcp\21666705-aa44-4812-b255-a6f8978ca7a9\scratchpad\upstream\Windows-MCP. Plan citations are relative to its src/windows_mcp/.

Write the full report to the report file. Cover what you implemented, the tests and their results, the TDD RED and GREEN commands and output, the files changed, and your self-review findings and concerns. Then reply with ONLY the following, in under 15 lines: Status (DONE | DONE_WITH_CONCERNS | BLOCKED | NEEDS_CONTEXT), the commits (short SHA and subject), a one-line test summary, your concerns, and the report path.

If you are resumed with review findings: fix them, re-run the covering tests, append a fix report (changes, command and output) to the same report file, and reply in the same short form.

## P5 hard rules (controller)
- The user is working on this machine. Tests and checks bind only to 127.0.0.1 / ::1 on ephemeral ports (port 0); never bind 0.0.0.0, never open firewall ports, never touch the user's certificate store (TLS tests use generated self-signed PEM files in temp dirs). No outbound network calls in tests (fake handlers only).
- Never run anything that injects input, shows windows, installs hooks or takes control; HTTP smoke sessions run with control disabled.
- Never run Snapshot, the stdio smoke or build-release unless your brief says so. `dotnet test DesktopMcp.slnx` must be clean before you commit.
- No telemetry anywhere (user decision 2026-10-21).
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Never write raw NUL bytes into source files.
