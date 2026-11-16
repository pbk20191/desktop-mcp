# Rust port: only known hosts may start desktop-mcp, and it can ask for its permissions (design, 2026-11-16)

Builds on `2026-11-16-rust-port-client-validation.md` (the threat and the APIs). This document records what the
owner decided and what gets built first.

## Why

Started from Claude Desktop, desktop-mcp is launched through Claude's `disclaimer` helper, which gives up
responsibility for its child. macOS therefore holds **desktop-mcp itself** responsible for Accessibility and
Screen Recording: the user grants them to the bundled binary
(`~/Library/Application Support/Claude/Claude Extensions/<id>/server/desktop-mcp`), not to Claude. Observed on
this Mac:

```
Claude (com.anthropic.claudefordesktop)
 └ Claude.app/Contents/Helpers/disclaimer --pgroup -- …/desktop-mcp serve --transport stdio
    └ …/desktop-mcp serve --transport stdio
```

Once granted, any program of the same user can start that binary the same way and drive the desktop through it,
although it never passed the permission prompts itself (a confused deputy). A same-user attacker cannot read the
screen or post input on their own; desktop-mcp must not hand them that. So, before serving over stdio,
desktop-mcp checks who started it.

## Goals

- Over stdio, serve only when started by a known AI host app, verified by code signature.
- Let the model ask for the two permissions from inside the host (`.mcpb` users have no terminal).
- Say plainly on stderr why a start was refused, naming the parent's identifier and Team ID, so a missing host
  can be added.

## Non-goals

- Socket and `connect` (already refused in the Rust port) and HTTP (needs a key or OAuth) are untouched. The
  socket can reuse this check later.
- Approving new hosts at run time (a dialog plus a Keychain entry): a later step.
- Stopping an attacker who starts an allowed host itself (for example `claude -p "…"`): the owner accepted
  relying on the host's own approval gates.
- Windows. The policy part is platform-free so the Windows port can supply its own process and signature lookup.

## Anti-goals

- A switch an attacker could flip: no command-line flag, environment variable or `config.toml` key disables or
  extends the check. The allowlist is compiled into the signed binary, where changing it breaks the signature.
- Accepting any descendant of a host: every command Claude Code runs (an `npm install` script included) descends
  from `claude`, so walking up until a host appears would let them all in.

## Decisions (owner's)

- **Built-in allowlist only.** New hosts are added by a new build.
- **Experimental mode exists only as a build option**, a Cargo feature (named `any-client`) on the CLI crate. A
  binary built with it skips the check and says so loudly on stderr at every start. Release builds and the
  `.mcpb` never enable it. Development builds are ad-hoc signed, so macOS keeps their permissions apart from the
  release binary's.
- **Direct parent (approach A).** The parent must be an allowed host; or a known launcher whose own parent is an
  allowed host. Nothing else is walked. Rejected: walking the ancestry (approach B), for the anti-goal above.
- **Permissions tool gets `request` (approach 가).** Rejected: prompting at every start (a prompt at each launch).

## Approaches considered

| | How | Trade-off | Picked |
|---|---|---|---|
| A | Direct parent, one known-launcher hop | Strict; a host that starts servers through `sh -c` or `npx` is refused until listed | yes |
| B | Walk up to the first allowed host | Flexible; admits anything a host runs | no |
| 가 | `Permissions(request=true)` | Prompts only when asked | yes |
| 나 | Prompt at start | Prompt at every launch | no |

## The check

Runs once at `serve` start, only for `--transport stdio`, before any byte is read from stdin.

1. `parent = getppid()`. If it is 1 (orphaned, or started by launchd), refuse.
2. Look up the parent's code: `SecCodeCopyGuestWithAttributes` with its pid; check validity
   (`SecCodeCheckValidity`) against each allowlist requirement (`SecRequirementCreateWithString`).
3. Matches a **host** → serve. Matches a **launcher** → repeat step 2 once for the launcher's parent (from
   `proc_pidinfo(PROC_PIDTBSDINFO).pbi_ppid`), which must match a host. Anything else → refuse.
4. **Against pid reuse:** after the signature checks, read `getppid()` again (and, for the launcher hop, the
   launcher's parent again); if either changed, refuse. A live parent's pid cannot be reused, and a dead parent
   leaves us reparented to launchd.
5. Refusal: exit 1 and write one line to stderr:
   `desktop-mcp: refused to serve: started by <path> (identifier <id>, team <team or "unsigned">), which is not a
   known MCP host` (for the launcher case, both processes). Nothing is written to stdout.

The identifier and Team ID for the message come from `SecCodeCopySigningInformation`; the path from
`proc_pidpath`.

### The allowlist (compiled in)

Every requirement is `anchor apple generic` plus the Developer ID markers (`certificate 1[field.1.2.840.113635.100.6.2.6]`
and `certificate leaf[field.1.2.840.113635.100.6.1.13]`), the Team ID (`certificate leaf[subject.OU]`) and the
identifier. App Store–signed apps use `certificate leaf[field.1.2.840.113635.100.6.1.9]` instead of the
Developer ID markers. Values read from the apps installed on this Mac:

| Host | Role | Identifier | Team ID | Seen starting a server |
|---|---|---|---|---|
| Claude Code CLI | host | `com.anthropic.claude-code` | Q6L2SF6YDW | yes (direct parent) |
| Claude Desktop | host | `com.anthropic.claudefordesktop` | Q6L2SF6YDW | yes, through `disclaimer` |
| Claude's helper | launcher | `disclaimer` | Q6L2SF6YDW | yes |
| ChatGPT / Codex app | host | `com.openai.codex` | 2DC432GLL2 | no |
| Codex CLI (inside the app) | host | `codex` | 2DC432GLL2 | no |
| GitHub Copilot CLI | host | `copilot` | VEKTX9H2N7 | no |
| GitHub Copilot for Xcode | host | `com.github.CopilotForXcode`, `com.github.CopilotForXcode.ExtensionService` | VEKTX9H2N7 (or App Store) | no |
| Gemini | host | `com.google.GeminiMacOS` | EQHXZ8M8AV | no |
| Microsoft Copilot | host | `com.microsoft.copilot-mac` | UBF8T346G9 (or App Store) | no |

Hosts that run as scripts (an npm-installed Codex or Gemini CLI appears as `node`, maybe under `sh`) are not
supported: `node` and shells are neither hosts nor launchers, since anything can run them. The refusal message
says so when the parent is an interpreter or a shell (builder's call). The README says the same.

Hosts marked "no" are listed on the owner's request; whether they start servers directly is unverified, and a
refusal message tells us what to add. Claude Code started from inside Claude Desktop
(`…/Claude/claude-code/<version>/…/claude.app`) is also launched through `disclaimer`; it is signed as
`com.anthropic.claude-code` (Q6L2SF6YDW) like the CLI, so the same entry covers it after the launcher hop.

## Requesting permissions

- `Permissions` gains `request` (boolean, default false). With `request=true`, for each permission not granted:
  Accessibility → `AXIsProcessTrustedWithOptions` with the prompt option; Screen Recording →
  `CGRequestScreenCaptureAccess`. When the system shows no prompt (already denied once), open the matching System
  Settings pane (`x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility` and
  `…?Privacy_ScreenCapture`). The reply is the usual report, then one line per permission saying what was done
  (prompted, opened Settings, already granted) and that the host must restart the server after granting.
  The `request=false` behaviour and wording stay as they are.
- Tools that fail for a missing permission add to their message: call Permissions with request=true. Builder's
  call: this changes the shared messages only (`permissions::ACCESSIBILITY_MESSAGE`, `ocr::CAPTURE_ERROR` and the
  Screen Recording message Snapshot uses); no per-tool audit, and a capture that silently comes back blank is not
  detected.
- `desktop-mcp permissions` (CLI) does the same as `request=true` and prints the report, for terminal users.
- Automation (System Events) is still only reported, never requested.
- **Whose permission it is** (builder's call, recorded): macOS asks on behalf of the process it holds responsible.
  Under Claude Desktop (through `disclaimer`) that is desktop-mcp itself; when a host starts desktop-mcp directly
  (Claude Code in a terminal), it is that host or its terminal. The tool and the CLI prompt for whatever the
  current process is attributed to, and the reply says so ("macOS asks for the app that started desktop-mcp, or
  for desktop-mcp itself under Claude Desktop"). Running `desktop-mcp permissions` in a terminal therefore helps
  terminal hosts, not the `.mcpb` binary; `.mcpb` users use the tool. Prompting for the bundled binary from a
  terminal (re-launching it with responsibility disclaimed) is not done: it needs private API.

## Where the code goes

- `crates/core`: the policy, free of platform calls: the allowlist entries (role, requirement), and the decision
  from what the platform reports about the parent (and the launcher's parent).
- `crates/macos`: process lookup (`getppid`, `proc_pidinfo`, `proc_pidpath`) and signature checks
  (`objc2-security`: SecCode, SecRequirement), plus the permission requests.
- `crates/cli`: runs the check before `serve_stdio`; the `any-client` feature; the `permissions` subcommand.

## Testing (kept small, as the owner prefers)

- Core: the decision for host, launcher→host, launcher→other, other, unsigned.
- Live, on this Mac: started by this terminal's shell → refused with the message; started by Claude Code
  (this session's MCP config or `claude mcp add`) → serves; the `.mcpb` in Claude Desktop → serves.
- `scripts/parity.sh` and any test that spawns the binary build it with `--features any-client` (the test runner
  is not a host).

## Left to the builder

- The exact requirement strings (generated from the table).
- Wording of the experimental-mode warning.
- Whether the launcher hop compares `pbi_ppid` from `proc_pidinfo` or another call, as long as step 4 holds.

## Later: approving new hosts (owner's leaning, 2026-11-16, not decided in full)

Desktop-mcp is to be published as open source for any MCP host, so a compiled-in list alone will be too narrow.
Asked whether an unknown host should be approved by a dialog at start or by a list written ahead of time, the
owner leaned to the dialog. Notes for whoever designs it:

- Show the parent's signer (Team ID, identifier) and ask once; `CFUserNotificationDisplayAlert` works from a
  process with no app bundle. A start that waits on the dialog may hit the host's own start-up timeout.
- Keep approvals in the Keychain, with access limited to desktop-mcp's code signature, not in `config.toml`,
  which any program of this user could add itself to.
- Unsigned or ad-hoc signed hosts can only be approved by code hash, so they are asked again after each update.
- Open: hosts that run inside a terminal (an npm-installed CLI appears as `node` under a shell under Terminal or
  iTerm). Approving the terminal approves everything run in it. The owner had no answer yet and put the question
  off, with the whole approval step, to move on to the Windows port.
