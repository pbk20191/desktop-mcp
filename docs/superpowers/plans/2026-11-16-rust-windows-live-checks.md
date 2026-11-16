# Rust Windows port: checks for the owner, by hand

Automated tests never touch the live desktop's clipboard, show toasts or inject input, so these are run by hand on
the dev PC. Build: `cargo build -p desktop-mcp-cli` → `target\debug\desktop-mcp.exe`; register
`desktop-mcp.exe serve --transport stdio` in Claude Desktop or Claude Code.

## W1

- [ ] Clipboard read: copy text, then a file in Explorer, then a screenshot (Win+Shift+S); `Clipboard` returns the
      text, the file path, and the image (scaled to at most 1568 px). Also over Chrome Remote Desktop with its
      clipboard sync on.
- [ ] Clipboard while an app holds it (e.g. Excel busy copying a large range): the call fails after about 5 s with
      the "did not answer" message, and other tools keep answering meanwhile.
- [ ] Clipboard write: `mode=write` with `text`, then with `paths` (a file and a folder); paste in Notepad/Explorer.
- [ ] Notification: a toast appears under "Windows PowerShell" with title, subtitle line and message; each sound
      (`Default`, `IM`, `Mail`, `Reminder`, `SMS`) plays; an unknown sound is refused with the list.
- [ ] Shell with PowerShell 7 installed: `$PSVersionTable.PSVersion` reports 7.x; no fallback note.
- [ ] Shell with PowerShell 7 hidden (rename `pwsh.exe` on PATH temporarily or run with a PATH without it and
      `ProgramFiles` pointing elsewhere): 5.1 runs and the first result starts with the install note, later ones
      do not.
- [ ] Shell `Start-Process notepad`: Notepad stays open after the call returns.
- [ ] Ctrl+C in a console running `desktop-mcp.exe serve` with nothing on stdin: exits at once with status 0.

## W2

- [ ] Claude Desktop or Code connected to `http://127.0.0.1:<port>/mcp` with `DESKTOP_MCP_ALLOW_HTTP=1` and a
      bearer key (`--auth-key`); a request without the key gets 401.
- [ ] HTTPS with a certificate the client trusts (`mkcert localhost 127.0.0.1`); the client validates it.
- [ ] While HTTPS runs, `certutil -user -csp "Microsoft Software Key Storage Provider" -key` lists one
      `desktop-mcp-tls-<pid>-…` key; after Ctrl+C it is gone. Kill the server with Task Manager instead: the key stays,
      and the next HTTPS start removes it.
- [ ] OAuth: `--oauth-client-id`/`--oauth-client-secret`, sign-in through the browser from the client.
- [ ] `--host 0.0.0.0` (or a LAN address): the Windows Firewall prompt appears once; a LAN client with the key works.
- [ ] Shell syntax error (`if (`): the result shows PowerShell's parser message and status 1.
