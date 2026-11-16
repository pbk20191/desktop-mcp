# Rust Windows port: checks for the owner, by hand

Automated tests never touch the live desktop's clipboard, show toasts or inject input, so these are run by hand on
the dev PC. Build: `cargo build -p desktop-mcp-cli` → `target\debug\desktop-mcp.exe`; register
`desktop-mcp.exe serve --transport stdio` in Claude Desktop or Claude Code.

## W1

- [ ] Clipboard read: copy text, then a file in Explorer, then a screenshot (Win+Shift+S); `Clipboard` returns the
      text, the file path, and the image (scaled to at most 1568 px).
- [ ] Clipboard write: `mode=write` with `text`, then with `paths` (a file and a folder); paste in Notepad/Explorer.
- [ ] Notification: a toast appears under "Windows PowerShell" with title, subtitle line and message; each sound
      (`Default`, `IM`, `Mail`, `Reminder`, `SMS`) plays; an unknown sound is refused with the list.
- [ ] Shell with PowerShell 7 installed: `$PSVersionTable.PSVersion` reports 7.x; no fallback note.
- [ ] Shell with PowerShell 7 hidden (rename `pwsh.exe` on PATH temporarily or run with a PATH without it and
      `ProgramFiles` pointing elsewhere): 5.1 runs and the first result starts with the install note, later ones
      do not.
- [ ] Shell `Start-Process notepad`: Notepad stays open after the call returns.
- [ ] Ctrl+C in a console running `desktop-mcp.exe serve` with nothing on stdin: exits at once with status 0.
