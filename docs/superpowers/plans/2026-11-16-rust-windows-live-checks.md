# Rust Windows port: checks for the owner, by hand

Automated tests never touch the live desktop's clipboard, show toasts or inject input, so these are run by hand on
the dev PC. Build: `cargo build -p desktop-mcp-cli` → `target\debug\desktop-mcp.exe`; register
`desktop-mcp.exe serve --transport stdio` in Claude Desktop or Claude Code.

## W1

- [ ] Clipboard read: copy text, then a file in Explorer, then a screenshot (Win+Shift+S); `Clipboard` returns the
      text, the file path, and the image (scaled to at most 1568 px). Also over Chrome Remote Desktop with its
      clipboard sync on.
- [ ] Clipboard while an app holds it (e.g. Excel busy copying a large range): a write fails after about 5 s with
      the "did not answer" message, a read comes back empty (the core's Clipboard result has no error), and other
      tools keep answering meanwhile.
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
      and the next HTTPS start removes it. Closing the console window (rather than Ctrl+C) also leaves it: Windows ends
      the process before it can clean up.
- [ ] OAuth: `--oauth-client-id`/`--oauth-client-secret`, sign-in through the browser from the client.
- [ ] `--host 0.0.0.0` (or a LAN address): the Windows Firewall prompt appears once; a LAN client with the key works.
- [ ] Shell syntax error (`if (`): the result shows PowerShell's parser message and status 1.

## W3

- [ ] Snapshot over Notepad, Explorer, Edge (a long page), Settings, and an elevated window (Task Manager run as
      admin): elements listed for the first four; the elevated one listed with no elements and no error.
- [ ] Snapshot `use_vision`: the image's numbered boxes sit on the elements on a 100 % and a 150 % display; no
      yellow capture border (or note that Windows shows one here).
- [ ] Virtual desktops: windows on another desktop are absent; the "Virtual desktop: … (n of m)" line names the
      current one, including a renamed desktop.
- [ ] Window `list` and `displays` with two monitors at different scales: frames line up with what Snapshot reports.
- [ ] Wait `until=window` (open Notepad after calling it) and `until=element` with `role=Button` and a title.
- [ ] OCR (`--include-tools OCR`) on Korean and English text, with and without `region`; a language without an OCR
      pack falls back to the user's languages.
- [ ] Screenshot of each display, and of a region; the scale note is right when the image is downscaled.
- [ ] A hung window (a debugger paused on an app): Snapshot still answers within a few seconds.

## W4

- [ ] Click (single, double, right, middle, `clicks=0` hover) in Notepad and Explorer, on a 100 % and a 150 % display.
- [ ] Type with Korean and emoji text, `clear`, `caret_position` start and end, `press_enter`; typing into a password
      field reports "secure field" and never echoes the text.
- [ ] Scroll vertical and horizontal, with and without `loc`; Move, and Move with `drag` (drag a file in Explorer).
- [ ] Shortcut `ctrl+a`, `ctrl+c`/`ctrl+v`, `alt+tab`, `win+r` (then Escape), `shift+insert`; no modifier stays held
      afterwards (type in Notepad after each).
- [ ] App `launch` by Start menu name (Notepad, Calculator — a packaged app — and on a Korean Windows "메모장"),
      by full executable path; `switch` while another app has focus; `resize`, `move`; `quit` (Notepad with unsaved
      text asks to save); `force_quit`.
- [ ] Window `focus`, `move`, `resize`, `minimize`, `restore`, `maximize`, `display` across two displays of different
      scale (the frame obtained is the one asked for), `close`; `fullscreen` answers that Windows has none.
- [ ] Input toward an elevated window (Task Manager run as administrator, Registry Editor): refused with the
      "runs as administrator" message, nothing typed anywhere.
- [ ] MultiSelect three files in Explorer with ctrl; `press_ctrl=false` clicks plainly. MultiEdit two fields of a form.
- [ ] Over Chrome Remote Desktop: clicks and typing land where expected.

## W6

- [ ] `cargo build --release -p desktop-mcp-cli`, then `powershell -ExecutionPolicy Bypass -File scriptsuild-mcpb.ps1`;
      open `dist\desktop-mcp.mcpb` in Claude Desktop and install it. Settings > Extensions shows Desktop MCP with its
      17 tools; a chat can call Window `list`, Snapshot and Shell `Get-Date`.
- [ ] Turn one tool off in the extension's settings; it is gone from the chat's tools after a restart of the extension.
