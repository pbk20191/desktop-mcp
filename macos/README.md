# desktop-mcp for macOS (Swift)

A Swift port of [CursorTouch/MacOS-MCP](https://github.com/CursorTouch/MacOS-MCP). The tool names, parameters, output format, CLI, config file format and HTTP behaviour follow the original; the command is `desktop-mcp`, the config directory is `~/.desktop-mcp` and environment variables are prefixed `DESKTOP_MCP_` instead of `MACOS_MCP_`. Unlike the original, it needs **no Python, uv, or Homebrew packages**: it's one binary (plus one Swift runtime shim on macOS 13–15, see below).

- Uses only the frameworks that ship with macOS (Accessibility, CoreGraphics, ScreenCaptureKit, AppKit, HIToolbox, Network).
- The external dependencies (`swift-argument-parser`, SwiftNIO + NIOTransportServices + NIOSSL + NIOHTTP2) are used at build time and compiled into the binary.
- Built on Swift Concurrency (async/await). Each request runs as its own task, so a long `Wait` or `Shell` call doesn't delay other requests. In-flight work is cancelled by `notifications/cancelled` (stdio, SSE) or by the HTTP client disconnecting, which also kills shell child processes.
- Transports: **stdio**, **Streamable HTTP** (stateless) and the deprecated **HTTP+SSE** (protocol 2024-11-05, kept for older clients). HTTP runs on Network.framework through NIOTransportServices, with optional TLS from PEM files. TLS connections negotiate **HTTP/2** or HTTP/1.1 through ALPN; plain connections speak HTTP/1.1.

## Installation

```bash
macos/scripts/build-release.sh    # → macos/dist/desktop-mcp (universal, ad-hoc signed)
```

Requires Xcode 16 / Swift 6.0 or newer to build. Build releases with **Xcode 16** (Swift 6.1): the output is a single file, `macos/dist/desktop-mcp`, that runs on macOS 13 and later. The script picks an Xcode 16 under `/Applications` by itself; point `DEVELOPER_DIR` at one elsewhere (`DEVELOPER_DIR=/path/to/Xcode-16.4.app/Contents/Developer macos/scripts/build-release.sh`). With Swift 6.2+ toolchains (Xcode 26 and later) the compiled dependencies link against a Span runtime shim that macOS 26 ships but macOS 13–15 do not, so the script also emits `macos/dist/libswiftCompatibilitySpan.dylib`; keep the two files in the same directory in that case. `Package.swift` caps the transitive `swift-collections` dependency below 1.3 because newer manifests do not load in SwiftPM 6.1; every toolchain resolves to the same pinned versions.

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
| `Shell` | `bash -c`, or AppleScript / JXA run in process (see below) |
| `Scrape` | Fetches a URL and converts it to Markdown (private, loopback, and other internal addresses are blocked) |
| `Desktop` | Creates a new Mission Control Space |
| `Notification` | Shows a notification banner |

## Commands and options

```
desktop-mcp [serve] [options]      # run the server (default command)
desktop-mcp auth [options]         # generate an auth key (+ TLS cert) into ~/.desktop-mcp/config.toml
desktop-mcp permissions            # check / request permissions
desktop-mcp install [options]      # run the HTTP server as a launchd Launch Agent from login
desktop-mcp uninstall              # stop it and remove the agent
desktop-mcp connect [--url] [--auth-key]   # stdio bridge to the HTTP server, for Claude Desktop
```

`serve` options. As in the original, a value given on the command line (or through its environment variable) wins, then `config.toml`, then the default.

| Option | Environment variable | Description |
|--------|----------------------|-------------|
| `--transport stdio\|sse\|streamable-http` | | Default `stdio` |
| `--host`, `--port` | | Default `localhost:8000` |
| `--auth-key` | `DESKTOP_MCP_AUTH_KEY` | Require `Authorization: Bearer <key>` on every HTTP request |
| `--oauth-client-id`, `--oauth-client-secret` | `DESKTOP_MCP_OAUTH_CLIENT_ID` / `_SECRET` | Built-in OAuth 2.0 server (PKCE S256, loopback redirect URIs only, dynamic registration disabled) |
| `--ip-allowlist` | `DESKTOP_MCP_IP_ALLOWLIST` | Comma-separated client IPs or CIDR ranges (IPv4/IPv6) |
| `--ssl-certfile`, `--ssl-keyfile` | `DESKTOP_MCP_SSL_CERTFILE` / `_KEYFILE` | HTTPS from PEM certificate and key |
| `--allow-insecure-remote` | | Allow binding a non-loopback address without auth (not recommended); otherwise refused |
| `--config` | | Config file path (default `~/.desktop-mcp/config.toml`) |
| `--exclude-tools` | `DESKTOP_MCP_EXCLUDE_TOOLS` | Tools to disable, added to `[tools] exclude` |
| `--max-timeout` | `DESKTOP_MCP_MAX_TIMEOUT` | Upper limit for the Shell timeout in seconds (default 240, 0 = unlimited) |
| `--debug` | | Verbose logging |

### Running over HTTP

```bash
desktop-mcp auth --transport streamable-http --host 127.0.0.1 --port 8000 [--with-tls]
desktop-mcp serve        # uses config.toml → http(s)://127.0.0.1:8000/mcp
```

`auth` generates a key, saves it to `config.toml` and prints the client config JSON. `--with-tls` creates a locally trusted certificate with mkcert when installed, otherwise a self-signed one with the `openssl` that ships with macOS.

### Running as a Launch Agent

```bash
desktop-mcp install                                   # streamable-http on 127.0.0.1:8000 (or the next free port)
desktop-mcp install --transport sse --port 9000       # or choose; --force reinstalls
desktop-mcp uninstall
```

As in the original, `install` writes `~/Library/LaunchAgents/com.desktop-mcp.server.plist` (`RunAtLoad`, `KeepAlive`) pointing at this binary with `serve --transport … --host … --port …`, loads it with `launchctl bootstrap`, and prints the client config to connect with. Logs go to `~/.desktop-mcp/server.log` and `server.error.log`. Other `serve` settings (auth key from `auth`, TLS, allowlist, excluded tools) still come from `config.toml`; stdio cannot be installed as a service. Because launchd is the parent, the agent asks for Accessibility on its first start under its own name (see [Permissions](#permissions)). Install from the binary's permanent location, for example `~/.local/bin/desktop-mcp install`, since the plist records that path.

Claude Code connects directly (`claude mcp add --transport http desktop-mcp http://127.0.0.1:8000/mcp/ --header "Authorization: Bearer <key>"`). Claude Desktop's `claude_desktop_config.json` accepts stdio servers only and skips an HTTP entry as invalid, so give it `desktop-mcp connect` as a stdio server: the bridge reads the agent's address from its plist and the auth key from `config.toml`, posts each stdin line to `/mcp` and writes the replies back one per line, and asks for no permissions of its own. `--url` and `--auth-key` point it at another server. `install` prints the snippet:

```json
{ "mcpServers": { "desktop-mcp": { "command": "/Users/you/.local/bin/desktop-mcp", "args": ["connect"] } } }
```

Endpoints:

- Streamable HTTP: `POST /mcp`. Stateless; responds with an SSE stream when `Accept` includes `text/event-stream`, otherwise with JSON.
- SSE (deprecated): `GET /sse`, then `POST /messages/?session_id=…`. `serve` logs a deprecation warning when this transport is selected.
- With OAuth: `/.well-known/oauth-authorization-server`, `/oauth/authorize`, `/oauth/token` (and `/oauth/register`, which refuses).

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

[osascript]                 # this port only: Apple Events from Shell osascript-mode scripts
deny_apps = ["com.apple.systemevents"]   # bundle IDs that scripts may not send events to

[tools]
exclude = ["Shell"]
```

## Telemetry

As in the original, anonymous usage events go to the original project's (CursorTouch) PostHog: tool name, duration, success or failure, client name and version, and an anonymous user id (`$TMPDIR/.desktop-mcp-user-id`). Tool arguments and results are never sent; for failures only a fixed error label (such as "Could not resolve hostname") or the error's type is sent, never a message that could quote a URL or hostname. **Set `ANONYMIZED_TELEMETRY=false` to turn it off.**

## Differences from the original

- **`install` finds the binary by its own path**, not by searching `$PATH` for `macos-mcp` or `uvx`, and prints the client config and the matching `claude mcp add` line afterwards.
- **`connect` is a built-in stdio-to-HTTP bridge**, replacing the `mcp-remote` (Node) bridge the original's README suggests for Claude Desktop.
- **`Shell` with `mode: osascript` runs the script in process through OSAKit** instead of spawning `osascript`. The output is the same (results rendered the way osascript prints them, errors as `start:end: execution error: message (number)`, `log` lines appended, status 1 on error), with three differences. A timeout or an MCP cancellation stops the script within a fraction of a second, including `delay`, loops and Apple Events still waiting for a reply, through the OSA active and send procs. Every Apple Event the script sends is checked against `[osascript] deny_apps` in `config.toml`; a denied application fails the statement with a privilege error (-10004). And JXA is available: start the source with `#!/usr/bin/osascript -l JavaScript` or `// language: JavaScript`. Scripts run concurrently on a dedicated queue, one language instance each. One limitation: StandardAdditions' own dialogs (`display dialog`/`alert`, `choose from list`) need an application host and fail here with -1713, so address them to System Events (`tell application "System Events" to display alert …`); the error text says so.
- **`Type` doesn't depend on the input source.** The original sent US-layout key codes, so typing English while the Korean input source was active produced Hangul jamo. This port injects every character as Unicode, and while typing it temporarily switches to an ASCII layout and restores the previous input source afterwards.
- **`Click` with `clicks=2` sends a real double-click** (click state 1 followed by 2).
- **`Scrape` parses HTML with the system parser** (libxml2 through Foundation's `XMLDocument`), so broken markup is repaired the way libxml2 does it rather than the way Python's `html.parser` does, and HTML 4 entities are decoded while HTML5-only names (such as `&check;`) are left as written. The contents of `<script>` and `<style>` are dropped; the original's markdownify `strip` option kept their text.
- **Snapshot ordering:** the focused app's elements get the lowest ids (the original's order was nondeterministic). Metadata keeps non-ASCII characters as-is instead of `\uXXXX` escapes.
- **`Shortcut` rejects unknown key names with an error.** The original logged a warning and pressed only part of the combination.
- **Telemetry logs go to stderr only** (per-call lines with `--debug`). The original also `print`ed to stdout, which can corrupt the stdio protocol stream. Error text sent to telemetry is a fixed label, not the full message.
- **Browser requests must come from a loopback origin.** The original answered every `Origin` with `*`, which lets any website drive an unauthenticated `localhost` server through the visitor's browser. This port returns 403 to requests whose `Origin` is not `localhost`, `127.0.0.1` or `[::1]`, as the MCP specification requires; non-browser clients send no `Origin` and are unaffected.
- **`Scrape` re-checks every redirect** against the same private-address rules, and stops downloading after 8 MB. `Shell` output is capped at 10 MB.
- **`auth` defaults to `streamable-http`.** The original defaulted to `sse`, which the MCP specification deprecated in protocol version 2025-03-26. The SSE transport is still available with `--transport sse` for clients that have not migrated.
- **HTTP/2 over TLS.** With `--ssl-certfile`/`--ssl-keyfile` the server offers `h2` and `http/1.1` through ALPN, so HTTP/2 clients multiplex their requests over one connection; the original (uvicorn) only speaks HTTP/1.1. Cleartext HTTP/2 (`h2c`) is not offered.
- **`/health`** is exempt from auth and the allowlist as in the original, but, also as in the original, there is no route behind it (404).

## Development

```bash
swift build
swift test
```

The package builds in Swift 6 language mode with strict concurrency checking: every value that crosses a task boundary is `Sendable`, shared state lives in actors or a `Locked` wrapper, and blocking Accessibility calls are offloaded to a GCD queue. The two retroactive `@unchecked Sendable` conformances (`AXUIElement`, `TISInputSource`) cover immutable CoreFoundation references.

`serve` is one root task: a task group whose children are telemetry delivery (each send a child task of its own), the server (stdio or HTTP) and a watcher for SIGINT/SIGTERM. A signal cancels the server child, which closes the listener and shuts its event loop group down (HTTP) or stops reading stdin (stdio); queued telemetry then gets up to three seconds to land, and the process exits by returning from `run()`. There is no `exit()` call in the code.

Everything on the wire is `Codable` and goes through Foundation's `JSONEncoder`/`JSONDecoder` (`JSONCoding`: sorted keys, slashes and non-ASCII unescaped, ISO 8601 dates). Incoming messages decode into `JSONRPCMessage`, which classifies them (request, notification, response, invalid) and types the parameters of the methods the server implements; results are plain `Encodable` structs. `JSONValue` is kept for the parts whose shape the protocol leaves open (tool arguments, input schemas, request ids) and keeps integers exact, so ids above 2^53 round-trip.

Source layout:

- `macos/Sources/DesktopMCPCore` (no third-party dependencies)
  - `JSONRPC.swift`, `JSONValue.swift`: the Codable message types, the dynamic JSON value, and the shared coder configuration
  - `MCPServer.swift`: JSON-RPC dispatch (`MCPServer`) and per-client sessions (`MCPSession`: per-request tasks, cancellation)
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
