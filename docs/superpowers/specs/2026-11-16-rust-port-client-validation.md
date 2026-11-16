# Rust port: admitting only known clients (future approach, 2026-11-16)

The owner turned the HTTP transport off by default: whatever reaches the port can drive the desktop, and the
server cannot tell which program is calling. The Unix socket (`--transport socket`) admits only this user (mode
0600 and the peer's uid by `getpeereid`), but any process of that user still gets in. This note records the
approach planned for checking *which program* is the client. Nothing here is implemented or verified yet; API
details below are to be confirmed against Apple's and Microsoft's documentation when the work starts.

## The threat: a confused deputy

An Accessibility (macOS) or UI Automation (Windows) server is far riskier than a file-system MCP server. Once the
system grants it Accessibility, it can read every window's text, type, and click: in effect drive the whole
machine. A malicious program that never passed the Accessibility prompt itself can start the already-trusted
server as a child and send it commands over stdio, or connect to its socket or port, borrowing its grant. Every
transport has this problem, stdio included: the check must be about the *client program*, not only the user.

Two consequences shape the design:

- **One signed binary.** Accessibility is granted to the executable that asks. A server run as `python main.py`
  or `node index.js` makes the grant cover `python` or `node`, and so every script they run. desktop-mcp is one
  compiled binary (Swift today, Rust here), signed with a Developer ID, so only it appears in the Accessibility
  list. Keep it that way (no interpreter, no plugin loading).
- **Verify the client at start (stdio) or on connect (socket).** Before serving, check that the program on the
  other end is an allowed, validly signed host app; refuse otherwise (stdio: exit 1; socket: close).

## Verifying the client

### Which process to check

- **stdio:** the parent process. Hosts often start servers through a launcher (`zsh -c`, `npx`, `uvx`, a wrapper
  script), so walk up the process tree past shells and launchers to the first GUI application bundle (`.app`), and
  check that one. Decide how far to walk and which intermediates are allowed (a shell started by an allowed app is
  fine; a shell started by Terminal is the user typing, which the policy must decide on).
- **Unix socket:** the connecting peer. Usually that is `desktop-mcp connect`, our own binary, started by the host:
  check the peer, then walk up from it as for stdio.
- Identify processes by **audit token** where the API allows, not by pid: a pid can be reused between looking it
  up and checking it (a race an attacker can arrange). Use pids only to walk the tree, then confirm by token.

### macOS

| Step | API | What it gives |
|---|---|---|
| Socket peer | `getsockopt(fd, SOL_LOCAL, LOCAL_PEERTOKEN, &audit_token, &len)` | The connecting process's `audit_token_t` |
| Parent | `getppid()`; further up, `proc_pidinfo(pid, PROC_PIDTBSDINFO, …)` (`pbi_ppid`) | The process tree |
| Executable path | `proc_pidpath()` (`libproc.h`) | The binary's path, e.g. `/Applications/Claude.app/Contents/MacOS/Claude` |
| Code object | `SecCodeCopyGuestWithAttributes(nil, {kSecGuestAttributeAudit: token} or {kSecGuestAttributePid: pid}, …)` | The running process's `SecCode` |
| Signature valid | `SecCodeCheckValidityWithErrors(code, flags, requirement, …)` | Not tampered with; meets a code requirement |
| Who signed it | `SecCodeCopySigningInformation(code, kSecCSSigningInformation, …)` → `kSecCodeInfoTeamIdentifier`, `kSecCodeInfoIdentifier` | Team ID and bundle id to compare with the allowlist |

A code requirement string (`anchor apple generic and certificate leaf[subject.OU] = "<team id>"` and an identifier)
checks team and identity in one call, so the allowlist can be a list of requirements.

XPC, if it is ever used: `xpc_connection_set_peer_code_signing_requirement` (macOS 12+) has the system reject
peers that do not meet a requirement before any message arrives. It needs a Mach service (a Launch Agent with
`MachServices`); the Swift port rejected XPC for `connect` earlier (stdio rules and simplicity), which this would
revisit. `xpc_connection_get_audit_token` is not public API.

Note that TCC may attribute Accessibility to the *responsible* process (often the host app that started the
server) rather than the server itself; checking the client is defence in depth either way.

### Windows

| Step | API | What it gives |
|---|---|---|
| Parent | `CreateToolhelp32Snapshot` + `Process32First/Next` (`tlhelp32.h`) → `PROCESSENTRY32.th32ParentProcessID` | The process tree |
| Executable path | `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW` | The parent's `.exe` path |
| Signature valid | `WinVerifyTrust` with `WINTRUST_ACTION_GENERIC_VERIFY_V2` (`wintrust.h`) | Authenticode signature valid |
| Who signed it | `CryptQueryObject` + `CertGetNameStringW` (`wincrypt.h`) | The signing certificate's subject, compared with the allowlist |

- Named pipes (the Windows counterpart of the socket): `GetNamedPipeClientProcessId` gives the client's pid; a
  process handle opened right away guards against pid reuse.
- UIPI (User Interface Privilege Isolation): a server at medium integrity cannot drive high-integrity (elevated)
  windows; a malicious program at medium integrity can still start the server, so the parent check applies here
  too, and the server ships as one signed `.exe` for the same reason as on macOS.

### Flow

```text
[server starts / a client connects]
  ├─ 1. find the client process (stdio: parent; socket: peer), walking past allowed launchers to the host app
  ├─ 2. its executable path
  ├─ 3. its code signature is valid (macOS: SecCode; Windows: WinVerifyTrust)
  ├─ 4. its signer (Team ID / subject) and identifier are on the allowlist
  ├─ fails ─▶ refuse (stdio: exit 1; socket: close), and say why on stderr
  └─ passes ─▶ serve
```

Open questions: where the allowlist lives (built in for known hosts, plus user approvals in `config.toml`?); how a
user approves a new host (a one-time prompt naming its signer?); unsigned and ad-hoc signed clients (developer
builds); clients run from Terminal (the user typing is legitimate, but so is a script the user did not write).

## Guardrails inside the server (defence in depth)

Even a genuine client can be steered by prompt injection, so the server limits what it will do:

- **Sensitive apps:** while a password manager (1Password and the like), System Settings, an authentication or
  security dialog, or a terminal is focused, refuse reading the screen and posting input. (Password fields are
  already never read; this widens that to whole apps.)
- **Separate reading from acting:** keep read-only tools (tree, windows, menus) apart from input tools (click,
  type, shortcuts), and allow input only into allowed apps (by bundle id or process name), configurable.
- **Destructive shortcuts:** treat quitting and closing chords (⌘Q, ⌘W) and typing into terminals with extra care
  (refuse, or require the target app to be allowed).

## HTTP

No process identity over TCP. Options remain the bearer key and OAuth; loopback alone does not identify the caller.
If HTTP stays, it could be limited to clients that first prove themselves over the socket (a token handed out per
approved client).
