# desktop-mcp for macOS (Swift)

A Swift port of [CursorTouch/MacOS-MCP](https://github.com/CursorTouch/MacOS-MCP). The tool names, parameters, output format, CLI, config file format and HTTP behaviour follow the original; the command is `desktop-mcp`, the config directory is `~/.desktop-mcp` and environment variables are prefixed `DESKTOP_MCP_` instead of `MACOS_MCP_`. Unlike the original, it needs **no Python, uv, or Homebrew packages**: it's one binary (plus one Swift runtime shim on macOS 13–15, see below).

- Uses only the frameworks that ship with macOS (Accessibility, CoreGraphics, ScreenCaptureKit, AppKit, HIToolbox, Network).
- The external dependencies (`swift-argument-parser`, the official MCP Swift SDK `mcp-swift-sdk` and `swift-log`, which it needs, SwiftNIO + NIOTransportServices + NIOSSL + NIOHTTP2) are used at build time and compiled into the binary.
- Built on Swift Concurrency (async/await). Each request runs as its own task, so a long `Wait` or `Shell` call doesn't delay other requests. In-flight work is cancelled by `notifications/cancelled` or by the HTTP client disconnecting (inside a session only the call whose request connection closed), which also kills shell child processes.
- Transports: **stdio**, a **Unix domain socket** carrying the stdio binding (what the Launch Agent offers `connect`) and **Streamable HTTP** (stateless with JSON responses by default, with opt-in sessions, see Endpoints). The protocol layer is the MCP Swift SDK (`StdioTransport`, `StatelessHTTPServerTransport` and `StatefulHTTPServerTransport`). The legacy HTTP+SSE transport is gone. HTTP runs on Network.framework through NIOTransportServices, with optional TLS from PEM files. TLS connections negotiate **HTTP/2** or HTTP/1.1 through ALPN; plain connections speak HTTP/1.1.

## Installation

```bash
macos/scripts/build-release.sh    # → macos/dist/desktop-mcp (universal, ad-hoc signed)
```

Requires Xcode 16.3 / Swift 6.1 or newer to build. Build releases with **Xcode 16** (Swift 6.1): the output is a single file, `macos/dist/desktop-mcp`, that runs on macOS 13 and later. The script picks an Xcode 16 under `/Applications` by itself; point `DEVELOPER_DIR` at one elsewhere (`DEVELOPER_DIR=/path/to/Xcode-16.4.app/Contents/Developer macos/scripts/build-release.sh`). With Swift 6.2+ toolchains (Xcode 26 and later) the compiled dependencies link against a Span runtime shim that macOS 26 ships but macOS 13–15 do not, so the script also emits `macos/dist/libswiftCompatibilitySpan.dylib`; keep the two files in the same directory in that case. `Package.swift` caps the transitive `swift-collections` dependency below 1.3 because newer manifests do not load in SwiftPM 6.1; every toolchain resolves to the same pinned versions.

Add it to your MCP client config:

```json
{
  "mcpServers": {
    "desktop-mcp": { "command": "/absolute/path/macos/dist/desktop-mcp" }
  }
}
```

With Claude Code: `claude mcp add desktop-mcp /absolute/path/macos/dist/desktop-mcp`

### Permissions

Permissions are granted to **the app that launches the binary** (Claude Desktop, Terminal, VS Code, and so on). To have desktop-mcp hold them itself, install it as a Launch Agent (see [Running as a Launch Agent](#running-as-a-launch-agent)): launchd makes it the responsible process, so System Settings lists "desktop-mcp" by name and the grant works for every client that connects over HTTP.

- System Settings › Privacy & Security › **Accessibility**: required
- System Settings › Privacy & Security › **Screen Recording**: needed only for `Snapshot(use_vision=true)`

Run `desktop-mcp permissions` to check the current state and trigger the system prompts. The grant is tied to the code signature: an ad-hoc signed binary is identified by its hash, so every rebuild needs a new grant. Sign with a stable identity to keep it (`CODESIGN_IDENTITY="My Codesign Cert" macos/scripts/build-release.sh`, using a code signing certificate made in Keychain Access, or a Developer ID).

## Tools

| Tool | Description |
|------|-------------|
| `Snapshot` | Focused window, open apps, interactive and scrollable elements with coordinates. `use_vision=true` adds a screenshot with numbered boxes |
| `Click` | left/right/middle; 0 = hover, 1 = single click, 2 = double click |
| `Type` | Clicks the coordinates, then types. Supports `clear`, `caret_position`, `press_enter` |
| `Scroll` | vertical/horizontal, `wheel_times` |
| `Move` | Moves the cursor, or drags with `drag=true` |
| `Shortcut` | e.g. `command+shift+t` |
| `Wait` | Waits for N seconds |
| `App` | launch / switch / resize / move |
| `Shell` | `bash -c`, or `osascript` for AppleScript / JXA (see below) |
| `Scrape` | Fetches a URL and converts it to Markdown (private, loopback, and other internal addresses are blocked) |
| `Desktop` | Creates a new Mission Control Space |
| `Notification` | Shows a notification banner |
| `Open` | Opens a file or URL (optionally with an app), or reveals a path in Finder |
| `Clipboard` | Reads the clipboard (text, file paths, image) or replaces it with text or files |
| `RunShortcut` | Lists or runs the user's Shortcuts (the Shortcuts app), with optional text input |

## Commands and options

```
desktop-mcp [serve] [options]      # run the server (default command)
desktop-mcp auth [options]         # generate an auth key (+ TLS cert) into ~/.desktop-mcp/config.toml
desktop-mcp permissions            # check / request permissions
desktop-mcp install [options]      # run the HTTP server as a launchd Launch Agent from login
desktop-mcp uninstall              # stop it and remove the agent
desktop-mcp connect [--url] [--socket] [--auth-key]   # stdio bridge to the agent's socket or HTTP server, for Claude Desktop
```

`serve` options. As in the original, a value given on the command line (or through its environment variable) wins, then `config.toml`, then the default.

| Option | Environment variable | Description |
|--------|----------------------|-------------|
| `--transport stdio\|streamable-http` | | Default `stdio` |
| `--host`, `--port` | | Default `localhost:8000` |
| `--socket <path>` | | Also serve the stdio binding on this Unix domain socket; what `install` sets up at `~/.desktop-mcp/mcp.sock` |
| `--auth-key` | `DESKTOP_MCP_AUTH_KEY` | Require `Authorization: Bearer <key>` on every HTTP request |
| `--oauth-client-id`, `--oauth-client-secret` | `DESKTOP_MCP_OAUTH_CLIENT_ID` / `_SECRET` | Built-in OAuth 2.0 server (PKCE S256, loopback redirect URIs only, dynamic registration disabled) |
| `--ip-allowlist` | `DESKTOP_MCP_IP_ALLOWLIST` | Comma-separated client IPs or CIDR ranges (IPv4/IPv6) |
| `--ssl-certfile`, `--ssl-keyfile` | `DESKTOP_MCP_SSL_CERTFILE` / `_KEYFILE` | HTTPS from PEM certificate and key |
| `--allow-insecure-remote` | | Allow binding a non-loopback address without auth (not recommended); otherwise refused |
| `--config` | | Config file path (default `~/.desktop-mcp/config.toml`) |
| `--exclude-tools` | `DESKTOP_MCP_EXCLUDE_TOOLS` | Tools to disable, added to `[tools] exclude` |
| `--max-timeout` | `DESKTOP_MCP_MAX_TIMEOUT` | Upper limit for the Shell timeout in seconds (default 240, 0 = unlimited) |
| `--session-timeout` | | Seconds a session may stay idle before it is dropped (default 1800, 0 = never evict). An open `GET` stream does not count as activity, and a session with a running tool call is never evicted |
| `--debug` | | Verbose logging (over HTTP also one line per session created or ended, with the reason, and per `GET` stream opened or closed) |

### Running over HTTP

```bash
desktop-mcp auth --transport streamable-http --host 127.0.0.1 --port 8000 [--with-tls]
desktop-mcp serve        # uses config.toml → http(s)://127.0.0.1:8000/mcp
```

`auth` generates a key, saves it to `config.toml` and prints the client config JSON. `--with-tls` creates a locally trusted certificate with mkcert when installed, otherwise a self-signed one with the `openssl` that ships with macOS.

### Running as a Launch Agent

```bash
desktop-mcp install                                   # streamable-http on 127.0.0.1:8000 (or the next free port)
desktop-mcp install --transport streamable-http --port 9000   # or choose; --force reinstalls
desktop-mcp uninstall
```

As in the original, `install` writes `~/Library/LaunchAgents/com.desktop-mcp.server.plist` (`RunAtLoad`, `KeepAlive`) pointing at this binary with `serve --transport … --host … --port … --socket …`, loads it with `launchctl bootstrap`, and prints the client config to connect with. Logs go to `~/.desktop-mcp/server.log` and `server.error.log`. Other `serve` settings (auth key from `auth`, TLS, allowlist, excluded tools) still come from `config.toml`; stdio cannot be installed as a service. Because launchd is the parent, the agent asks for Accessibility on its first start under its own name (see [Permissions](#permissions)). Install from the binary's permanent location, for example `~/.local/bin/desktop-mcp install`, since the plist records that path.

`install` also has the agent serve the stdio binding on a Unix domain socket, `~/.desktop-mcp/mcp.sock`: the plist's `Sockets` key makes launchd create it (mode `0600`, so only the same user can connect) and the server takes it over with `launch_activate_socket`. Every connection is its own server, with the stdio rule that stdin EOF ends it, so a client that disconnects takes nothing else down. `desktop-mcp connect` prefers the socket the plist names and pipes stdin and stdout to it unchanged; if the connection is refused there (no socket file, or nothing listening on it) it logs that to stderr and uses HTTP instead. A plist written before the socket existed names none, so `connect` goes straight to HTTP; an existing install needs `desktop-mcp install --force` once to gain the socket. While the agent is installed, launchd holds the socket and takes connections even when no agent is answering (between restarts, or a hung agent), so `connect` then waits, as the client of a hung stdio server would, instead of falling back. `--socket <path>` uses that socket with no fallback. A connection open when the agent restarts ends with it: `connect` exits with status 1 and Claude Desktop starts it again. Anything that can speak to a Unix socket works without the bridge, for example `socat STDIO UNIX-CONNECT:$HOME/.desktop-mcp/mcp.sock`.

The socket does not go through `auth_key`, the IP allowlist or OAuth: what guards it is the file mode (`0600`) and a check that the peer runs as the same user as the agent. That is no new exposure, since any process of that user can read `config.toml` and its auth key anyway, but it does mean that anything you run can use the agent's tools through it.

Claude Code connects directly (`claude mcp add --transport http desktop-mcp http://127.0.0.1:8000/mcp/ --header "Authorization: Bearer <key>"`). Claude Desktop's `claude_desktop_config.json` accepts stdio servers only and skips an HTTP entry as invalid, so give it `desktop-mcp connect` as a stdio server. Over the socket it is the plain pipe described above; over HTTP the bridge reads the agent's address from its plist and the auth key from `config.toml`, posts each stdin line to `/mcp` and writes the replies back one per line, and asks for no permissions of its own. The bridge streams each SSE event to stdout as it arrives and remembers the `initialize` line. When the server answers 404 (session evicted or the server restarted), it replays that `initialize` once, opens the new session's `GET` stream right away (before the request is resent, so a long call does not hold it up) and retries the request. If the replay is refused (503 when every session slot is busy, say), the next request tries again before it is sent, and requests go without a session until one is regained. A request whose response stream ends without its reply (the server restarted, or the session ended mid-call) gets a JSON-RPC error (`-32000`) instead of no answer; it is not retried. It also opens the session's `GET /mcp` stream and forwards server notifications to stdout, re-opening the stream after 1 s if it drops (not after a 404 or 405). At stdin EOF it closes its `GET` and waits for in-flight requests; it exits when a write to stdout fails. A client-initiated stream that never ends (`subscriptions/listen`) keeps the bridge alive after EOF. `--url` points it at another HTTP server and `--socket` at another socket; the two are exclusive, and `--auth-key` applies to HTTP only (over the socket it is ignored). `install` prints the snippet:

```json
{ "mcpServers": { "desktop-mcp": { "command": "/Users/you/.local/bin/desktop-mcp", "args": ["connect"] } } }
```

Endpoints:

- Streamable HTTP: `POST /mcp` (also `/mcp/`). By default stateless: one server per request, always `application/json`. The request's `Accept` header must include `application/json`; otherwise (including a missing `Accept`) the answer is 406. A non-JSON `Content-Type` gets 415, a JSON-RPC batch gets 400, and `GET` or `DELETE` without a session id get 405 with `Allow: POST`. `MCP-Protocol-Version` is not validated; clients of protocol 2026-07-28 are served statelessly (`server/discover` is not implemented yet).
- Sessions are opt-in. A `POST /mcp` `initialize` without a session id whose `Accept` includes `text/event-stream` gets an `Mcp-Session-Id` header. Requests carrying that header go to the session and are answered as `text/event-stream`; an SSE event with empty `data` precedes each response, and consumers must ignore empty-data events. `GET /mcp` with the header is the server-to-client stream: server-initiated messages (list-changed, elicitation, progress and so on) travel only there. `DELETE /mcp` ends the session. Requests without a session id, and an `initialize` that does not accept SSE, are served statelessly as above.
  - An unknown or evicted session id gets 404 and the client must `initialize` again. TypeScript SDK clients (Claude Code, Claude Desktop) do; the Swift SDK 0.12.1 client does not and reports "Session expired".
  - Limits: idle timeout `--session-timeout` (default 1800 s, 0 = never; an open `GET` is not activity and a running tool call is never evicted), 64 MiB sent per session (checked when a request ends and by the idle sweeper, so `GET` traffic alone is bounded too; then the session ends and the next request gets 404), and at most 64 sessions (the least recently used idle one is evicted; 503 when all are busy).
  - A client disconnect during a call cancels that call only; other requests of the session continue. `notifications/cancelled` works inside a session. A client's `Last-Event-ID` is ignored: the server itself manages re-attaching a re-opened `GET`.
- `/sse` is gone (404). A stored `transport = "sse"` in `config.toml`, or a Launch Agent plist that still says `--transport sse`, is served as streamable-http with the warning `transport "sse" is no longer supported; serving streamable-http (POST /mcp) instead. Re-run `desktop-mcp install` or `desktop-mcp auth` to update the stored setting.` The `--transport` option of `install` and `auth` accepts only `stdio|streamable-http`.
- With OAuth: `/.well-known/oauth-authorization-server`, `/oauth/authorize`, `/oauth/token` (and `/oauth/register`, which refuses).
- JSON-RPC error messages carry the SDK's prefix: an unknown tool is `-32602` with the message `Invalid params: Unknown tool: <name>` (the exact original text is in `error.data.detail`).
- A string argument that is a `data:` URL reaches the tool rebuilt by the SDK in the plain `data:<type>,<text>` form when its bytes are UTF-8 (otherwise as base64), so base64 or percent-escaped data URLs change: `Type` with `text` `data:text/html;base64,PGgxPkhpPC9oMT4=` types `data:text/html,<h1>Hi</h1>`.

Example config file:

```toml
[server]
transport = "streamable-http"
host = "127.0.0.1"
port = 8000
auth_key = "…"
ssl_certfile = "cert.pem"   # relative paths are resolved against this file
ssl_keyfile = "key.pem"

[security]
ip_allowlist = ["127.0.0.1", "10.0.0.0/8"]
oauth_client_id = "…"
oauth_client_secret = "…"

[tools]
exclude = ["Shell"]
```

## Telemetry

As in the original, anonymous usage events go to the original project's (CursorTouch) PostHog: tool name, duration, success or failure, client name and version, and an anonymous user id (`$TMPDIR/.desktop-mcp-user-id`). Tool arguments and results are never sent; for failures only a fixed error label (such as "Could not resolve hostname") or the error's type is sent, never a message that could quote a URL or hostname. **Set `ANONYMIZED_TELEMETRY=false` to turn it off.**

## Differences from the original

- **`install` finds the binary by its own path**, not by searching `$PATH` for `macos-mcp` or `uvx`, and prints the client config and the matching `claude mcp add` line afterwards.
- **`connect` talks to the agent over a Unix socket when it has one**, which the original did not offer: the socket carries the stdio binding with no HTTP, auth key or session in between, and `connect` falls back to HTTP if the socket refuses the connection.
- **`connect` is a built-in stdio-to-HTTP bridge**, replacing the `mcp-remote` (Node) bridge the original's README suggests for Claude Desktop. It streams replies, forwards server notifications and recovers from a lost session.
- **Optional MCP sessions on `POST /mcp`** (the original's HTTP transport had none), with `GET`/`DELETE`, idle eviction and size limits as described under Endpoints. `MCP-Protocol-Version` is no longer validated.
- **`Shell` with `mode: osascript` can run JXA.** Start the source with `#!/usr/bin/osascript -l JavaScript` or `// language: JavaScript` and it runs as JavaScript (`osascript -l JavaScript`); anything else is AppleScript, as in the original. Each call is its own `osascript` process, so scripts from several clients run concurrently, dialogs (`display alert`, ...) work, and a timeout kills the script.
- **`Type` doesn't depend on the input source.** The original sent US-layout key codes, so typing English while the Korean input source was active produced Hangul jamo. This port injects every character as Unicode, and while typing it temporarily switches to an ASCII layout and restores the previous input source afterwards.
- **`Click` with `clicks=2` sends a real double-click** (click state 1 followed by 2).
- **`Scrape` parses HTML with the system parser** (libxml2 through Foundation's `XMLDocument`), so broken markup is repaired the way libxml2 does it rather than the way Python's `html.parser` does, and HTML 4 entities are decoded while HTML5-only names (such as `&check;`) are left as written. The contents of `<script>` and `<style>` are dropped; the original's markdownify `strip` option kept their text.
- **Snapshot ordering:** the focused app's elements get the lowest ids (the original's order was nondeterministic). Metadata keeps non-ASCII characters as-is instead of `\uXXXX` escapes.
- **`Shortcut` rejects unknown key names with an error.** The original logged a warning and pressed only part of the combination.
- **Telemetry logs go to stderr only** (per-call lines with `--debug`). The original also `print`ed to stdout, which can corrupt the stdio protocol stream. Error text sent to telemetry is a fixed label, not the full message.
- **Browser requests must come from a loopback origin.** The original answered every `Origin` with `*`, which lets any website drive an unauthenticated `localhost` server through the visitor's browser. This port returns 403 to requests whose `Origin` is not `localhost`, `127.0.0.1` or `[::1]`, as the MCP specification requires; non-browser clients send no `Origin` and are unaffected.
- **`Scrape` re-checks every redirect** against the same private-address rules, and stops downloading after 8 MB. `Shell` output is capped at 10 MB.
- **`auth` defaults to `streamable-http`.** The original defaulted to `sse`, which the MCP specification deprecated in protocol version 2025-03-26; this port does not offer SSE at all.
- **HTTP/2 over TLS.** With `--ssl-certfile`/`--ssl-keyfile` the server offers `h2` and `http/1.1` through ALPN, so HTTP/2 clients multiplex their requests over one connection; the original (uvicorn) only speaks HTTP/1.1. Cleartext HTTP/2 (`h2c`) is not offered.
- **`/health`** is exempt from auth and the allowlist as in the original, but, also as in the original, there is no route behind it (404).

## Development

```bash
swift build
swift test
```

To try the socket without installing anything, run `desktop-mcp serve --transport streamable-http --port 0 --socket /tmp/x.sock` in one terminal and `desktop-mcp connect --socket /tmp/x.sock` (or `socat STDIO UNIX-CONNECT:/tmp/x.sock`) in another.

The package builds in Swift 6 language mode with strict concurrency checking: every value that crosses a task boundary is `Sendable`, shared state lives in actors or a `Locked` wrapper, and blocking Accessibility calls are offloaded to a GCD queue. The two retroactive `@unchecked Sendable` conformances (`AXUIElement`, `TISInputSource`) cover immutable CoreFoundation references.

`serve` is one root task: a task group whose children are telemetry delivery (each send a child task of its own), the server (stdio or HTTP) and a watcher for SIGINT/SIGTERM. A signal cancels the server child, which closes the listener and shuts its event loop group down (HTTP) or stops reading stdin (stdio); queued telemetry then gets up to three seconds to land, and the process exits by returning from `run()`. There is no `exit()` call in the code.

The MCP protocol itself (JSON-RPC framing, initialize, `tools/list`, `tools/call`, cancellation, the stdio and stateless HTTP transports) comes from the official MCP Swift SDK. The tools are registered as SDK handlers (`ToolRegistry.swift`, `StdioServer.swift`, `HTTPApp.swift`); `MCP.Value` carries the parts whose shape the protocol leaves open (tool arguments, input schemas) and keeps integers exact. Our own JSON goes through Foundation's `JSONEncoder`/`JSONDecoder` with the shared configuration in `JSONCoding` (sorted keys, slashes and non-ASCII unescaped, ISO 8601 dates).

Source layout:

- `macos/Sources/DesktopMCPCore` (depends on the MCP Swift SDK and swift-log only)
  - `JSONCoding.swift`: the shared coder configuration and the ordered Snapshot-metadata renderer
  - `ToolRegistry.swift`, `StdioServer.swift`: tool registration on the SDK `Server`, and the stdio transport
  - `HTTPApp.swift`, `OAuth.swift`, `Security.swift`: HTTP middleware (CORS, allowlist, auth), routes and OAuth, independent of the socket layer
  - `Config.swift`, `ServePlan.swift`: `config.toml` (a small TOML-subset parser) and option merging
  - `Analytics.swift`: PostHog telemetry
  - `Tools.swift`: tool definitions and schemas; `Desktop.swift`: tool behaviour
  - `Tree.swift`, `TreeConfig.swift`: accessibility tree traversal (ported from the original's `tree/service.py`)
  - `Apps.swift`: running apps, windows, dialogs; `Input.swift`: mouse and keyboard events
  - `Screenshot.swift`: capture and annotation
  - `Shell.swift`: process execution (posix_spawn plus a DispatchSource for exit, so no thread blocks)
  - `Scrape.swift`, `HTMLToMarkdown.swift`: fetching, and Markdown rendering of the tree the system HTML parser produces
- `macos/Sources/DesktopMCPHTTP/NIOHTTPServer.swift`: socket layer (NIOTransportServices / Network.framework, NIOHTTP1, NIOHTTP2, NIOSSL)
- `macos/Sources/desktop-mcp`: the CLI (ArgumentParser, async commands)
