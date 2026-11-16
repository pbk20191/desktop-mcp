# XPC endpoint on the Launch Agent, and an XPC mode for `connect`

Date: 2026-10-20
Status: superseded by 2026-10-20-unix-socket-connect-design.md (the owner chose a Unix domain socket: the draft MCP spec blesses stdio framing over any byte stream, and the SDK StdioTransport takes file descriptors)
Builds on: `2026-10-19-stateful-sessions-design.md` (merged into pbk/osascript-jxa at e86ddb5)

## Intent

Claude Desktop can only spawn stdio servers, so it runs `desktop-mcp connect`, which today
bridges stdin/stdout to the Launch Agent over Streamable HTTP with a session. That works, but
every piece of the HTTP session machinery exists only to rebuild what a plain connection gives
for free: the session id, the 404 replay of `initialize`, the standalone `GET` stream for
server-initiated messages, the idle timeout and byte budget, the per-request cancel handle.

For a stdio client on the same machine a Mach XPC connection is that plain connection. launchd
already owns the agent; it can own a Mach service name for it too, so a client reaches the agent
by name with no port, no auth key, no HTTP round trip. One XPC connection is one MCP session:
the agent runs one SDK `Server` on it, as the stdio server does on a pipe, and the connection
dropping is the session ending. The low-level C API (`xpc_connection_create_mach_service`,
macOS 10.7) is used, not `NSXPCConnection` (Objective-C protocols and reply blocks do not fit
a message stream) and not `XPCListener`/`XPCSession` (macOS 14; the package targets 13).

HTTP stays for everything else: Claude Code and other HTTP clients, remote use, `--url`.

## Goals

- `serve --transport streamable-http` can also listen on a Mach service, when told its name.
  Every accepted connection gets its own SDK `Server` from the shared `ToolRegistry`, the same
  server the stdio transport builds, with the same handlers, telemetry and tool cancellation.
- `connect` talks XPC to the installed agent when the agent offers it, and HTTP otherwise.
  Claude Desktop's config snippet (`desktop-mcp connect`, no arguments) does not change.
- A connection that drops cancels that client's in-flight tool calls, at once.
- The bridge survives an agent restart (launchd relaunches it under `KeepAlive`): the Mach
  connection comes back on its own, the bridge replays the client's `initialize` so the new
  server is initialised, and every request that was in flight gets a JSON-RPC error instead
  of silence.
- An agent that was installed before this change (plist without the Mach service) keeps
  working with the new `connect`: it falls back to HTTP.
- Optional peer check: a code signing requirement the agent enforces on every XPC peer.

## Non-goals

- Replacing the HTTP sessions or the HTTP `connect` bridge. Both stay as they are.
- XPC without HTTP (`serve --transport stdio` plus XPC, or an XPC-only agent). The agent
  always serves HTTP; XPC is added to it.
- The macOS 14 `XPCListener`/`XPCSession` API, `NSXPCConnection`, or an entitlement-based
  peer check. The C API with a code signing requirement string covers it on macOS 12+.
- A Unix domain socket endpoint. It would give the same connection-is-session model for
  future Linux/Windows ports; the owner chose XPC for its launchd integration and peer check.
  This document does not preclude adding a socket later.
- Changing what the stdio server or the HTTP paths do.
- Testing against the real launchd. Tests use anonymous XPC connections and endpoints
  in-process; the owner verifies the Mach service by reinstalling the agent.

## Anti-goals

- Any MCP semantics in the XPC layer beyond what the stdio server already tracks (request
  ids to drain at EOF and to fail on interruption). No parsing of methods or results.
- `@unchecked Sendable` (the existing `Locked`/`UncheckedSendable` wrappers excepted). The
  XPC objects are non-Sendable; they stay inside the actor or queue that owns them.
- A crash loop: an XPC failure in the agent (name not registered with launchd, bad
  requirement string at runtime) must not take the HTTP server down with it.

## Decisions

### Names and the plist

1. **Mach service name**: `LaunchAgent.machServiceName = "com.desktop-mcp.server.xpc"`.
   The label `com.desktop-mcp.server` stays what it is.
2. **`LaunchAgent.plist(...)` adds `MachServices: { "com.desktop-mcp.server.xpc": true }`**
   next to the existing keys (`RunAtLoad` and `KeepAlive` stay: the agent serves HTTP from
   login, and a Mach service alongside them is fine). `LaunchAgent.programArguments` gains
   `--xpc-service com.desktop-mcp.server.xpc` after the existing `serve` arguments. `install`
   always writes both; there is no flag to leave XPC out. `install --force` is what an
   existing user runs to get the new plist (`bootout` + `bootstrap`, as today).
3. **Discovery by `connect`**: a new `ConnectPlan` (Core, `ConnectPlan.swift`) replaces the
   direct use of `StdioBridge.Target.installed`:
   `ConnectPlan.installed(plist: Data, config: DesktopMCPConfig) throws -> ConnectPlan`,
   where `enum ConnectPlan: Equatable, Sendable { case xpc(service: String), case http(StdioBridge.Target) }`.
   It is `.xpc` when `ProgramArguments` carries `--xpc-service <name>`, `.http` (built as
   today) otherwise. `StdioBridge.Target.installed` becomes the `.http` half and keeps its
   tests.

### `serve`

4. **Options**: `serve --xpc-service <name>` (String, default nil) and
   `serve --xpc-requirement <string>` (String, default nil). `ServePlan` gains
   `xpcService: String?` and `xpcRequirement: String?`, command line only (no config.toml
   key, no environment variable, like `--session-timeout`). `resolve` rejects
   `--xpc-service` with `transport == "stdio"` (`ConfigErrorText("--xpc-service requires --transport streamable-http.")`)
   and `--xpc-requirement` without `--xpc-service`
   (`ConfigErrorText("--xpc-requirement requires --xpc-service.")`).
5. **Lifecycle**: with `xpcService` set, `Serve.run` starts the XPC listener before the HTTP
   server and runs both until the first ends or a signal arrives. The listener is started
   with `XPCServer.listen(machService:requirement:registry:)` (below). A listener that
   cannot be registered (launchd does not own the name, because `serve` was started from a
   terminal) is logged once, `"XPC service <name> is not registered with launchd (run as the installed Launch Agent); serving HTTP only"`,
   and `serve` carries on with HTTP. A requirement string the system rejects is a startup
   error (`CLIError`), since it is a configuration mistake, and `serve` exits 1 before
   binding HTTP. When `serve` ends (signal, HTTP failure) every XPC peer's server is shut
   down, which cancels its tool calls, before the process exits.
6. **Log lines**: at start `"Serving XPC on <name>"` plus `" (peer requirement set)"` when
   one is set; with `--debug`, one line per connection accepted and per connection ended
   (with `interrupted`/`invalid`).

### `XPCServer` (Core, `XPCServer.swift`)

7. **Shape**: `public actor XPCServer`. Two constructors:
   `static func listen(machService: String, requirement: String?, registry: ToolRegistry) async throws -> XPCServer`
   and `static func listen(anonymous registry: ToolRegistry, requirement: String? = nil) async throws -> XPCServer`
   with `public func endpoint() -> xpc_endpoint_t` on the anonymous one (tests connect with
   `xpc_connection_create_from_endpoint`). `public func shutdown() async` cancels the listener,
   shuts down every peer server and waits for them. `public var connections: Int` (test hook).
   The Mach service listener is `xpc_connection_create_mach_service(name, queue, XPC_CONNECTION_MACH_SERVICE_LISTENER)`;
   the anonymous one is `xpc_connection_create(nil, queue)`. `requirement` is applied to the
   listener connection with `xpc_connection_set_peer_code_signing_requirement` before it is
   activated; a non-zero return throws `ConfigErrorText("Invalid --xpc-requirement: <errno text>")`.
   Peers that fail the requirement are dropped by the system (their messages never arrive).
8. **Not registered**: when the listener's event handler receives
   `XPC_ERROR_CONNECTION_INVALID` (launchd has no such service), `listen(machService:)`
   throws `XPCServer.NotRegistered` (a public `Error`), which `serve` turns into the log line
   of decision 5. The handler sees this error promptly after activation; `listen` waits for
   either that error or a short settle (it resolves as "registered" when nothing arrives
   within 200 ms, since a registered listener is silent until a client connects). The builder
   may find a sharper signal; the contract is "throws when not registered, returns otherwise".
9. **Per connection**: the listener's event handler receives a peer `xpc_connection_t`. The
   actor wraps it in an `XPCPeerTransport` (below), sets the peer's event handler, activates
   it, and starts a server with `registry.start(on: transport)`, keeping the `StartedServer`
   keyed by the peer. When the transport's incoming stream ends (`XPC_ERROR_CONNECTION_INVALID`
   for the peer), the actor calls `started.shutdown()` (cancels that client's tool calls)
   and forgets the peer. `XPC_ERROR_CONNECTION_INTERRUPTED` cannot occur on a peer
   connection; it is treated like `INVALID` if it does.
10. **`XPCPeerTransport: Transport`** (private to `XPCServer.swift`, an actor):
    `send(_ data: Data)` builds an XPC dictionary with one entry, key `"message"`, value
    `xpc_data` of the bytes (one JSON-RPC message or batch, no trailing newline), and calls
    `xpc_connection_send_message`. `receive()` returns an `AsyncThrowingStream<Data, Error>`
    fed from the peer's event handler: an incoming dictionary's `"message"` data is copied
    into a `Data` inside the handler and yielded; a message without that key is ignored; an
    error event finishes the stream. `disconnect()` cancels the peer connection
    (`xpc_connection_cancel`) and finishes the stream. `connect()` is a no-op (the actor
    activated the connection already). The transport does not track request ids: the SDK
    `Server` answers what it was asked, and a dropped peer is cancelled, not drained.
11. **Threads**: every XPC object (listener, peers, messages) is touched only on one private
    serial `DispatchQueue` owned by the `XPCServer` actor, which is also the connections'
    target queue, or inside the actor that holds it. The event handler closures capture no
    XPC object other than the one they are installed on, and hand out only `Data`.
    `xpc_connection_send_message` is thread-safe and keeps per-connection order, so sends
    need no serialisation of their own.

### `XPCBridge` (Core, `XPCBridge.swift`), and `connect`

12. **Selection in `connect`**: `--url` (as today) forces HTTP; a new `--xpc-service <name>`
    forces XPC by that name; given neither, `ConnectPlan.installed` decides from the plist.
    `--url` and `--xpc-service` together is a `ValidationError`. `--auth-key` is ignored in
    XPC mode (XPC has no bearer token; the Mach service is visible only inside the user's
    login session). The help text says so.
13. **Shape**: `public struct XPCBridge: Sendable` with
    `public enum Target: Sendable { case machService(String); case endpoint(UncheckedSendable<xpc_endpoint_t>) }`
    (the endpoint case is for tests) and
    `public func run(input: FileHandle, output: @escaping @Sendable (String) async -> Bool) async -> Int32`
    returning the exit code. It reads stdin with `StdioBridge.lines(of:)` (made
    `package`/internal-shared, not duplicated), sends each line as a `"message"` dictionary,
    and writes each incoming `"message"` as one line to `output` (through
    `StdioBridge.singleLine`, so a payload with raw newlines still fits the framing).
14. **Request tracking**: the bridge tracks unanswered request ids exactly as
    `StdoutWatchingTransport` does (requests add, responses and `notifications/cancelled`
    remove). That logic moves out of `StdioServer.swift` into an internal
    `JSONRPCEnvelope` in `JSONCoding.swift`, used by both. The bridge also remembers the
    first `initialize` line it forwards.
15. **Lifecycle**:
    - stdin EOF: no more sends; the bridge waits until the unanswered set is empty (bounded
      by nothing, as the stdio server), then cancels the connection and returns 0. If the
      set is already empty it returns at once.
    - `output` returns false (stdout gone): cancel the connection, return 0.
    - `XPC_ERROR_CONNECTION_INTERRUPTED` (the agent exited and launchd will relaunch it): every
      unanswered request gets `{"jsonrpc":"2.0","id":…,"error":{"code":-32000,"message":"desktop-mcp connect: the server restarted before replying"}}`
      on `output` and is forgotten; the remembered `initialize` is sent again (its reply,
      matched by id, is swallowed, not written); the bridge then continues. Nothing is sent
      twice otherwise. The same connection object is reused: the system reconnects on the
      next send.
    - `XPC_ERROR_CONNECTION_INVALID` (no such service, the requirement rejected us, or the
      peer cancelled): every unanswered request gets the same error with message
      `"desktop-mcp connect: the XPC service <name> is unavailable"`, the message is logged
      to stderr, and `run` returns 1. `connect` exits with that code.
16. **`connect` wiring**: `Connect.run` builds a `ConnectPlan` (from the flags or the plist),
    runs `StdioBridge` for `.http` exactly as today, or `XPCBridge` for `.xpc`, and throws
    `ExitCode(code)` when the XPC bridge returns non-zero. `SIGPIPE` handling stays.

### Shared

17. **`StdoutWatchingTransport`'s `Envelope`** becomes `JSONRPCEnvelope` (internal,
    `JSONCoding.swift`): `id`, `method`, `cancelledRequest`, `static func all(in: Data)`.
    Behaviour unchanged; `StdioServerTests` keep passing.
18. **README**: the Commands table gains the two `serve` options and the `connect` option;
    "Running as a Launch Agent" explains the Mach service, that `install --force` is needed
    once to get it, and the fallback to HTTP; "Differences from the original" gains a line.
    The memory file for SDK follow-ups is not affected (no SDK workaround here).

## Client-visible changes

- `install` writes a plist with `MachServices` and `--xpc-service`; `install --force` on an
  existing install changes what `connect` uses from HTTP to XPC. Nothing in the printed
  client config changes.
- `connect` against a new agent: no `Mcp-Session-Id`, no `GET` stream, no HTTP at all. Replies
  arrive in the same one-line-per-message form. Against an old agent: unchanged.
- `serve` run from a terminal with `--xpc-service` logs the "not registered" line and serves
  HTTP; without the flag nothing changes.

## Constraints

- Swift 6 strict concurrency; no new `@unchecked Sendable` beyond `UncheckedSendable` for the
  test endpoint; SDK 0.12.1; package platform stays `.macOS(.v13)`; no runtime dependencies.
  `import XPC` only in `XPCServer.swift`, `XPCBridge.swift` and their tests.
- Tests run with `DEVELOPER_DIR=/Applications/Xcode-16.4.app/Contents/Developer`,
  `ANONYMIZED_TELEMETRY=false`; never `install`/`uninstall`; never write under
  `~/.desktop-mcp`; never create a Mach service listener in tests (anonymous only).
- Commits local on a feature branch off pbk/osascript-jxa; the owner merges and pushes.
- `swift test` re-adds pins to `Package.resolved`; restore it before committing.

## Left to the builder

- Whether `XPCServer` and `XPCPeerTransport` are one actor or two, and which queue is the
  connections' target queue; how the 200 ms "registered" settle of decision 8 is written.
- How the bridge waits for the unanswered set to drain at EOF (an `AsyncStream` of events, a
  continuation, a `CheckedContinuation`).
- File layout of `ConnectPlan` (its own file or beside `StdioBridge.Target`).
- Test doubles: the anonymous listener is the double; an SDK `Client` over an in-process
  `Transport` built on `xpc_connection_create_from_endpoint` is welcome but not required.

## Verification the builder must show

- `XPCServer.listen(anonymous:)` + a test client over its endpoint: `initialize`, `tools/list`,
  `tools/call` of `Wait`; replies arrive in order; a second client on the same listener gets
  its own `initialize` (two servers, `connections == 2`).
- Cancelling the client connection while `Wait` runs ends the tool call within a second
  (`RecordingObserver` sees a failure with `Cancelled.`) and `connections` drops to 1.
- `XPCBridge` end to end: stdin pipe → bridge → anonymous `XPCServer` → stdout pipe, with
  the SDK `Client` over `StdioTransport` on the pipes: `initialize`, `tools/call`, EOF drains
  the reply before exit, exit code 0.
- Bridge with `output` returning false: returns 0 within a second with a call still running.
- Bridge against an invalid endpoint (listener shut down before the first send): a request
  gets the `-32000` "unavailable" error line, `run` returns 1.
- `ConnectPlan.installed` on a plist with and without `--xpc-service`; `ServePlan.resolve`
  rejects the two invalid combinations of decision 4; `LaunchAgent.plist` contains
  `MachServices`; `LaunchAgent.programArguments` contains `--xpc-service`.
- `XPCServer.listen(anonymous:requirement:)` with a syntactically invalid requirement throws
  `ConfigErrorText`; with `identifier "com.example.nobody"` a client's `initialize` gets no
  reply within a second (dropped by the system), with no requirement it does.
- Owner, by hand after `install --force`: `launchctl print gui/$UID/com.desktop-mcp.server`
  lists the Mach service; `desktop-mcp connect` answers `initialize` from stdin;
  `launchctl kickstart -k` mid-session makes an in-flight request fail with `-32000` and the
  next request succeed.
