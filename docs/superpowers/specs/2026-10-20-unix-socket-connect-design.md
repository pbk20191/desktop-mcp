# A Unix domain socket on the Launch Agent, and a socket mode for `connect`

Date: 2026-10-20
Status: approved by the owner on 2026-10-20 (with the stdio-binding rulings in decisions 9, 12 and 13)
Builds on: `2026-10-19-stateful-sessions-design.md` (merged into pbk/osascript-jxa at e86ddb5)
Supersedes: `2026-10-20-xpc-connect-design.md` (XPC; kept as a record of why not)

## Intent

Claude Desktop can only spawn stdio servers, so it runs `desktop-mcp connect`, which bridges
stdin/stdout to the Launch Agent over Streamable HTTP with a session. That works, but the
session machinery (session id, 404 replay, standalone `GET` stream, idle timeout, byte budget,
per-request cancel handle) exists only to rebuild what a plain connection gives for free.

The draft MCP specification's stdio page says so itself: the wire format (one newline-delimited
JSON-RPC message per line over a reliable bidirectional byte stream) "works unchanged over Unix
domain sockets, TCP connections, or any similar channel", and custom transports on such streams
SHOULD reuse it; only launch, stderr, shutdown-by-closing-the-stream and process restart need
channel-specific equivalents.

So the agent gets a Unix domain socket, and the socket is treated exactly as the stdio binding
treats the standard streams. One accepted connection is one MCP session: the agent runs one SDK
`Server` on it through the same `StdoutWatchingTransport` the stdio server uses (the SDK
`StdioTransport` takes file descriptors; a socket is one), the client closing its side is the
stdio EOF, and the agent closing the connection is the server exiting. `connect` becomes a byte
pipe between stdin/stdout and the socket, with the stdio binding's channel-specific equivalents
and nothing more: launch is `connect()`, stderr is stderr, shutdown is closing the stream, and a
server restart is what it is in stdio, the server going away and the client (Claude Desktop)
dealing with it.

launchd can own the socket (`Sockets` in the plist), so the agent needs no port, token or HTTP
round trip for same-machine stdio clients, and any tool that speaks stdio framing over a socket
(`socat STDIO UNIX-CONNECT:…`, a future client) can reach the agent without the bridge.

HTTP stays for everything else: Claude Code and other HTTP clients, remote use, `--url`.

## Goals

- `serve --transport streamable-http --socket <path>` also listens on that Unix domain socket.
  Every accepted connection gets its own SDK `Server` from the shared `ToolRegistry`, with the
  same handlers, telemetry, tool cancellation and EOF rule as stdio: the client closing its
  side ends input, the requests already read are answered, then the agent closes.
- Under launchd the agent takes the socket launchd created (`launch_activate_socket`), so the
  listening socket survives agent restarts (connections made during a restart wait in the
  backlog). Run from a terminal, `serve` binds the path itself.
- `connect` uses the socket when the installed agent offers one, and HTTP otherwise. Claude
  Desktop's config snippet (`desktop-mcp connect`, no arguments) does not change. An agent
  installed before this change keeps working with the new `connect`, and so does a new
  `connect` whose `connect()` is refused (old agent still loaded): it falls back to HTTP.
- The bridge behaves like a stdio server process would: when the agent closes the connection
  (restart, shutdown), the bridge closes stdout and exits non-zero, as a server that died.
  No reconnect, no replay, no synthesised error replies: the stdio client handles a server
  that went away the way it handles any stdio server that went away.
- Nothing socket-related can crash-loop the agent under `KeepAlive`: a socket the agent cannot
  bind or activate is logged, and HTTP serves on.

## Non-goals

- Replacing the HTTP sessions or the HTTP `connect` bridge. Both stay as they are.
- A socket without HTTP (`serve --transport stdio --socket`, or a socket-only agent). The
  agent always serves HTTP; the socket is added to it.
- Telling a client's half-close apart from its full close or death. Both read as EOF, as on
  stdin, and the stdio EOF rule applies: the requests already read are answered, and a client
  that is gone is noticed when the write fails (`EPIPE`), which ends the server and cancels
  its remaining calls. The HTTP sessions keep their immediate cancel-on-disconnect; this
  transport does what stdio does.
- Judging whether the agent is alive behind a launchd-held socket. launchd accepts
  `connect()` whether or not the agent ever accepts the connection; a bridge whose
  `initialize` is never answered hangs like a stdio server that hangs, and the stdio client's
  own timeout handles it. The agent therefore takes care never to be in that state for long
  (decision 6), and the owner's reinstall verifies it.
- Peer identity beyond the user: the socket file is `0600` in `~/.desktop-mcp`, and the agent
  rejects peers whose effective uid differs from its own (`getpeereid`). A code signing check
  of the peer (`LOCAL_PEERTOKEN` audit token → `SecCodeCopyGuestWithAttributes` →
  `SecCodeCheckValidity`) is possible on this transport and may be a later
  `--socket-requirement`; not in this work.
- TCP, or a socket path from config.toml or an environment variable: command line only, like
  `--session-timeout`; `install` writes it into the plist.
- Changing what the stdio server or the HTTP paths do, or the SDK.
- Testing against the real launchd. `launch_activate_socket` is exercised only by the owner's
  reinstall; tests bind paths under a temporary directory.

## Anti-goals

- Any parsing of the traffic in the bridge or the socket layer. Not even line splitting: bytes
  in, bytes out. The stdio server already understands the framing.
- `@unchecked Sendable` (the existing `Locked`/`UncheckedSendable` wrappers excepted).
- Any new framing. One message per line, `\n` terminated, exactly the stdio binding.

## Decisions

### Names and the plist

1. **Socket path**: `LaunchAgent.socketURL(home:)` = `<config dir>/mcp.sock`, that is
   `~/.desktop-mcp/mcp.sock` (honouring `$HOME` as the config directory does), with
   `LaunchAgent.socketURL` as the `$HOME` convenience like `plistURL`. `sun_path` holds 104
   bytes; a path of 104 bytes or more is a `ConfigErrorText("Socket path too long (max 103 bytes): <path>")`
   wherever a path is taken (`serve`, `connect`, `install`).
2. **Plist**: `LaunchAgent.plist(programArguments:logDirectory:socketPath:)` adds
   ```
   Sockets = { Listener = { SockPathName = <path>; SockFamily = "Unix"; SockType = "stream"; SockPathMode = 384 } }
   ```
   (`384` is `0600`; plists have no octal) next to the existing keys; `RunAtLoad` and
   `KeepAlive` stay. `LaunchAgent.programArguments(executable:transport:host:port:socketPath:)`
   appends `--socket <path>`. `LaunchAgent.socketName = "Listener"` is the key `serve` passes
   to `launch_activate_socket`. `install` always writes both; there is no opt-out.
   `install --force` is what an existing user runs to get the new plist (`bootout` +
   `bootstrap`, as today). `uninstall` removes a leftover socket file at the path if the plist
   named one and the file is a socket (launchd removes its own; a terminal-run `serve` removes
   what it bound).
3. **Discovery by `connect`**: a new `ConnectPlan` in Core (`ConnectPlan.swift`):
   ```swift
   public enum ConnectPlan: Equatable, Sendable {
       case socket(path: String, fallback: StdioBridge.Target?)
       case http(StdioBridge.Target)
       public static func installed(plist: Data, config: DesktopMCPConfig) throws -> ConnectPlan
   }
   ```
   `.socket` when `ProgramArguments` carries `--socket <path>`, with `fallback` the HTTP target
   built from the same plist exactly as `StdioBridge.Target.installed` does today (which
   stays, as the `.http` builder). `.http` otherwise.

### `serve`

4. **Option**: `serve --socket <path>` (String, default nil). `ServePlan` gains
   `socketPath: String?`, command line only. `resolve` rejects it with `transport == "stdio"`
   (`ConfigErrorText("--socket requires --transport streamable-http.")`) and applies the
   length check of decision 1.
5. **Taking the socket**: `SocketServer.listen(path:launchdName:registry:)` (below) first calls
   `launch_activate_socket(name)` (`import launch`: the SDK ships `launch.h` as its own clang
   module; the function returns the error number itself, not -1 with `errno`; the fd array is
   `malloc`ed and must be freed; the fds are blocking and get `O_NONBLOCK` before use).
   Success with one or more fds: those are the listeners (all of them). `ENOENT`/`ESRCH` (not
   under launchd, or no such key): bind the path itself. To bind: `stat` the path; a file
   that is not a socket (`S_ISSOCK` false) → `SocketServer.CannotListen` ("not a socket"), never
   unlinked; a socket file → `connect()` to it; a successful connect means another server is
   live there → `SocketServer.Busy`; `ECONNREFUSED` → unlink the stale file. Then `bind`,
   `chmod 0600` at once (no `umask`: it is process-wide and would race other threads' file
   creation; the uid check of decision 8 covers the gap), `listen` with backlog 16, remember
   that this process owns the file. Any other failure is `SocketServer.CannotListen(errno)`.
   `EALREADY` from `launch_activate_socket` (already activated) is `CannotListen` too.
6. **Lifecycle in `Serve.run`**: with `socketPath` set, the socket server is started before
   HTTP. If `listen` throws, the error is logged once as
   `"Socket <path>: <error>; serving HTTP only"` and `serve` continues with HTTP alone, never
   exits over it (the "no crash loop" goal). When launchd handed over fds but `listen` still
   failed, those fds are closed before continuing so that they do not leak. Closing them does
   not stop launchd: it keeps its own copy of the listener and goes on queueing connections
   that nobody accepts, so a bridge then hangs as the non-goal on launchd-held sockets
   describes (the owner's reinstall check is what catches it). For the same reason an
   `accept` failure other than a fatal one (`EMFILE`, `ENFILE`, `ENOBUFS`, `ENOMEM`, or any
   other errno) does not end the socket task: it is logged once, the listener's read source is suspended for 250 ms and
   then resumed. Only an error that cannot clear (`EBADF`, `EINVAL`, `ENOTSOCK`,
   `EOPNOTSUPP`) ends it.
   Once listening it logs `"Serving stdio over <path>"` plus `" (socket from launchd)"` when
   activated. The socket task ending on its own (accept failing permanently) is logged the
   same way and does not end `serve`; only HTTP ending or a signal does. On exit every
   accepted connection's server is shut down (cancelling its tool calls), the listeners
   closed, and the socket file unlinked when this process bound it. With `--debug`, one line
   per connection accepted (with the peer pid from `LOCAL_PEERPID`) and per connection ended.

### `SocketServer` (Core, `SocketServer.swift`)

7. **Shape**:
   ```swift
   public actor SocketServer {
       public static func listen(path: String, launchdName: String?, registry: ToolRegistry) async throws -> SocketServer
       public static func listen(fileDescriptors: [FileDescriptor], registry: ToolRegistry) async throws -> SocketServer  // tests, and launchd fds
       public var connections: Int { get }      // test hook
       public func run() async                  // accept loop; returns when shut down
       public func shutdown() async             // close listeners, shut every connection's server down, unlink if owned
       public struct Busy: Error; public struct CannotListen: Error { let reason: String }
   }
   ```
   `launchdName == nil` skips `launch_activate_socket` (tests).
8. **Accepting**: the listening fds are non-blocking; readiness comes from a
   `DispatchSource.makeReadSource` per listener whose handler `accept`s in a loop until
   `EAGAIN` and yields each new fd into an `AsyncStream<FileDescriptor>` the actor consumes.
   The handler is a `@Sendable` closure built outside actor isolation and captures only the
   listener fd and the continuation (`DispatchSourceHandler` is not `@Sendable`; an inline
   closure in an actor method would be inferred actor-isolated and trap under Swift 6's
   executor check when Dispatch runs it). Each accepted fd: `getpeereid`; a uid other than
   `geteuid()` is closed at once and logged. Otherwise the actor sets `SO_NOSIGPIPE` and
   `SO_SNDBUF` = 1 MiB (best effort; the default 8 KiB with the SDK's 10 ms sleep on `EAGAIN`
   would move a Snapshot image at under 1 MB/s) on it and starts a child task that runs
   `StdioServer.run(registry, input: fd, output: fd, label: "<path> peer <pid>")` and then
   closes the fd. Connections are tracked by fd in the actor; `shutdown` cancels their tasks
   (which `StdioServer.run` turns into `started.shutdown()`: the tool calls are cancelled)
   and waits for them.
9. **`StdioServer.run` on a socket**: unchanged in behaviour (owner's ruling: the stdio
   binding's rules apply to the socket as they are). The client closing its side, half or
   full, reads as EOF; the requests already read are answered; the server stops; the actor
   closes the fd, which the client sees as EOF. A write failing (`EPIPE`, `ECONNRESET`: the
   client went away) ends the server loop as it does on a closed stdout, and
   `StartedServer.shutdown()` cancels that client's remaining tool calls. `StdioServer.run`
   gains only `label: String = "stdin"` for its log lines.

### `SocketBridge` (Core, `SocketBridge.swift`), and `connect`

10. **Selection in `connect`**: `--url` (as today) forces HTTP; a new `--socket <path>` forces
    the socket with no HTTP fallback; both together is a `ValidationError`. Given neither,
    `ConnectPlan.installed` decides. `--auth-key` applies only to HTTP (used by the fallback
    target too); the help text says so.
11. **Shape**:
    ```swift
    public struct SocketBridge: Sendable {
        public init(path: String)
        /// Connects; `Refused` when nothing accepts at `path`. Nothing has been read from `input` yet.
        public func connect() async throws(Refused) -> Connection   // or an equivalent two-step API
        public struct Refused: Error { let reason: String }          // ENOENT, ECONNREFUSED, ... as text
        public struct Connection: Sendable {
            /// Copies bytes both ways until one side ends. Returns when done.
            public func run(input: FileHandle, output: @escaping @Sendable (Data) async -> Bool) async -> Outcome
        }
        public enum Outcome: Equatable, Sendable { case finished, outputClosed, serverClosed }
    }
    ```
    No parsing: `input`'s bytes go to the socket as they arrive, the socket's bytes go to
    `output` as they arrive. `StdoutWriter` gains `write(_ data: Data) -> Bool`.
12. **Lifecycle** (owner's ruling: the stdio binding's channel-specific equivalents, nothing
    else):
    - **Launch** = `connect()`, before anything is read from stdin. `ENOENT`/`ECONNREFUSED`
      (or any connect failure) → `Refused`, at once.
    - **Shutdown by closing the stream** = stdin EOF → `shutdown(SHUT_WR)` on the socket;
      keep copying socket → stdout until the socket hits EOF (the agent answers what it read,
      then closes) → `.finished`.
    - **stdout gone** (`output` returns false) = the client went away → close the socket
      fully → `.outputClosed`. The agent notices on its next write.
    - **Process restart** = the socket hits EOF or errors while stdin is still open (the agent
      restarted, shut down, or dropped us) → stop reading stdin, close stdout, `.serverClosed`.
      This is what a stdio server dying looks like to its client: EOF on its stdout. Nothing is
      resent, nothing is synthesised.
    - **A launchd-held socket with no agent behind it** is not detected (non-goal): the bridge
      sends and waits, as a client of a hung stdio server would.
13. **`connect` wiring**: `Connect.run` builds the `ConnectPlan`. `.http` runs `StdioBridge`
    as today. `.socket(path, fallback)` calls `SocketBridge.connect()`; `Refused` with a
    `fallback` present logs `"desktop-mcp connect: <reason>; using HTTP at <url>"` to stderr
    and runs `StdioBridge` on the fallback with the same stdin (nothing has been read from it,
    so nothing is lost); `Refused` with `--socket` (no fallback) logs the reason and exits 1.
    A connection's `.finished` and `.outputClosed` exit 0; `.serverClosed` logs
    `"desktop-mcp connect: the server closed the connection"` and exits 1 (a stdio server that
    died exits non-zero too). `SIGPIPE` handling stays.

### Shared

14. **README**: Commands table gains `serve --socket` and `connect --socket`; "Running as a
    Launch Agent" explains the socket, `launch_activate_socket`, that `install --force` is
    needed once, the HTTP fallback at connect time, that an agent restart ends `connect` (Claude
    Desktop restarts it), and the `socat` one-liner; "Differences from the original" gains a
    line.
15. **Known cost, accepted**: the SDK `StdioTransport` polls (`EAGAIN` → 10 ms sleep, no
    readiness wait), so every open socket connection wakes the agent about 100 times a second
    while idle, for as long as Claude Desktop keeps the bridge running. The stdio server pays
    the same today. Recorded in the SDK follow-ups memory as a thing to re-check on an SDK
    bump; a readiness-driven fd transport of our own is the fix if it ever matters.

## Client-visible changes

- `install` writes a plist with `Sockets` and `--socket`; `install --force` on an existing
  install switches what `connect` uses from HTTP to the socket. The printed client config is
  unchanged.
- `connect` against a new agent: no HTTP, no session id, no `GET` stream; replies arrive in
  the same one-line form. Against an old agent (connect refused): HTTP, as today, with one
  stderr line. When the agent restarts, `connect` exits 1, where the HTTP bridge used to
  recover in place; Claude Desktop treats it as a stdio server that exited.
- `serve --socket` from a terminal binds the path itself, so a developer can test the socket
  without launchd; `~/.desktop-mcp/mcp.sock` appears while it runs.

## Constraints

- Swift 6 strict concurrency; no new `@unchecked Sendable`; SDK 0.12.1; package platform
  stays `.macOS(.v13)`; no runtime dependencies. `DispatchSource` handlers and the
  `launch_activate_socket` call live in `SocketServer.swift` (`import launch`).
- Tests run with `DEVELOPER_DIR=/Applications/Xcode-16.4.app/Contents/Developer`,
  `ANONYMIZED_TELEMETRY=false`; never `install`/`uninstall`; never write under
  `~/.desktop-mcp`; socket paths in tests live under `FileManager.temporaryDirectory`
  (kept short: `sun_path` is 104 bytes).
- Commits local on a feature branch off pbk/osascript-jxa; the owner merges and pushes.
- `swift test` re-adds pins to `Package.resolved`; restore it before committing.

## Left to the builder

- Whether the bridge's socket I/O uses `FileHandle` readability handlers, `DispatchIO`, or
  NIOTS; it must not block the cooperative pool. The copy loops are the whole bridge.
- The `DispatchSource`/`AsyncStream` seam of decision 8, and how listener fds from launchd and
  from `bind` share one code path.
- File layout of `ConnectPlan` (its own file or beside `StdioBridge.Target`), and whether
  `SocketBridge.connect()` is a throwing function or an `init` that throws.

## Verification the builder must show

- `SocketServer.listen(path:)` under a temp dir + SDK `Client` over `StdioTransport(input: fd, output: fd)`
  on a connected socket: `initialize`, `tools/list`, `tools/call` of `Wait`; two clients get two
  servers (`connections == 2`); the socket file is `0600`.
- Client `shutdown(SHUT_WR)` after a request: the reply arrives, then EOF from the agent;
  `connections` drops. Client `shutdown(SHUT_WR)` mid-`Wait`: the `Wait` completes and its
  reply arrives (the stdio EOF rule). Client fully closing mid-`Wait` with a long wait: once
  the reply's write fails the server ends and `connections` drops (observed within the wait's
  duration plus a second; the test uses a short wait).
- Peer uid check: not testable in-process (same uid); the code path is reviewed, not tested.
- `listen(path:)` on a path with a live listener → `Busy`; on a stale socket file → listens;
  on a regular file → `CannotListen`, file untouched.
- A 2 MiB `tools/call` reply over the socket completes in well under a second.
- `SocketBridge` end to end: stdin pipe → bridge → `SocketServer` → stdout pipe, driven by the
  SDK `Client` over `StdioTransport` on the pipes: `initialize`, `tools/call`, EOF yields
  `.finished` after the reply is written; a reply split across socket reads reaches stdout
  byte-exact.
- Bridge with `output` returning false → `.outputClosed` within a second with a call still
  running, and the server's `connections` drops once that call's reply fails to write.
- Bridge with the server shut down mid-`Wait` → `.serverClosed`, stdout closed, nothing
  written for the `Wait` beyond what the server sent (the cancelled handler's `Cancelled.`
  result may or may not get out first; either is accepted).
- `SocketBridge.connect()` with nothing listening → `Refused` at once.
- `ConnectPlan.installed` on plists with and without `--socket`; `ServePlan.resolve` rejects
  `--socket` with stdio and an over-long path; `LaunchAgent.plist` contains the `Sockets`
  dictionary with mode `384`; `programArguments` contains `--socket`.
- Owner, by hand after `install --force`: `launchctl print gui/$UID/com.desktop-mcp.server`
  lists the socket; `desktop-mcp connect` answers `initialize` from stdin;
  `socat STDIO UNIX-CONNECT:$HOME/.desktop-mcp/mcp.sock` does too; `launchctl kickstart -k`
  mid-session ends `connect` with exit 1 and the stderr line, and a fresh `connect` works.
