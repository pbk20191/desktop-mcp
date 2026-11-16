# Unix Socket Connect Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Launch Agent listens on a Unix domain socket that speaks the stdio binding, and `connect` pipes stdin/stdout to it, falling back to HTTP when nothing accepts.

**Architecture:** `SocketServer` (Core) accepts connections and runs the existing `StdioServer.run` on each socket fd, so one connection is one SDK `Server` with the stdio EOF rule. `SocketBridge` (Core) is a parse-free byte pipe between stdin/stdout and the socket. `LaunchAgent`/`ServePlan`/`ConnectPlan` carry the path through the plist (`Sockets` key, `--socket`) to `serve` and `connect`.

**Tech Stack:** Swift 6, MCP Swift SDK 0.12.1 (`StdioTransport` on fds), BSD sockets, `launch_activate_socket` (`import launch`), Dispatch sources, XCTest.

**Spec:** `docs/superpowers/specs/2026-10-20-unix-socket-connect-design.md`

## Global Constraints

- Swift 6 strict concurrency; no new `@unchecked Sendable` (`Locked`/`UncheckedSendable` exist); platform stays `.macOS(.v13)`; SDK pinned at 0.12.1; no runtime dependencies.
- Build/test: `DEVELOPER_DIR=/Applications/Xcode-16.4.app/Contents/Developer swift test --package-path /Users/bagbyeong-gwan/IDE/xcodes/desktop-mcp --filter <Tests>`, with `ANONYMIZED_TELEMETRY=false` in the environment. Never run `desktop-mcp install`/`uninstall`; never write under `~/.desktop-mcp`; test socket paths live under `FileManager.default.temporaryDirectory` and must stay under 104 bytes.
- `swift test` re-adds pins to `Package.resolved`: run `git checkout -- Package.resolved` before every commit.
- Socket path: `<config dir>/mcp.sock`; `sun_path` limit → path of 104 bytes or more is `ConfigErrorText("Socket path too long (max 103 bytes): <path>")`.
- Plist `Sockets` entry: key `Listener`, `SockPathName`, `SockFamily = "Unix"`, `SockType = "stream"`, `SockPathMode = 384`.
- `serve --socket` requires `--transport streamable-http`: `ConfigErrorText("--socket requires --transport streamable-http.")`.
- Log copy: `"Socket <path>: <error>; serving HTTP only"`, `"Serving stdio over <path>"` + `" (socket from launchd)"`, `"desktop-mcp connect: <reason>; using HTTP at <url>"`, `"desktop-mcp connect: the server closed the connection"`.
- The stdio server's behaviour is not changed; `StdioServer.run` gains only `label: String = "stdin"`.
- No parsing of traffic in `SocketBridge` or `SocketServer`.
- Commits local on branch `pbk/unix-socket-connect` (off `pbk/osascript-jxa`); the owner merges and pushes.

## Review Focus

1. A `connect` started while the agent restarts (launchd queues the connection): the bridge must come alive once the new agent accepts, not hang on a half-made socket. Pinned in Task 4 (`testConnectsToAListenerThatAcceptsLate`).
2. A reply larger than the socket send buffer (a Snapshot image, megabytes) must arrive whole and promptly. Pinned in Task 2 (`testALargeReplyArrivesWhole`).
3. Bytes split across socket reads mid-message must reach stdout byte-exact, in order. Pinned in Task 4 (`testBytesSplitAcrossReadsArriveByteExact`).
4. `serve --socket` on a path whose directory does not exist, or with a leftover regular file at the path, must refuse without destroying anything. Pinned in Task 2 (`testRegularFileAtPathIsRefusedAndKept`, `testMissingDirectoryIsCannotListen`).
5. A peer that connects and sends nothing, then goes away, must not leave a server behind. Pinned in Task 2 (`testASilentPeerClosingFreesItsServer`).

---

### Task 1: Paths, plist and plans

**Files:**
- Modify: `macos/Sources/DesktopMCPCore/LaunchAgent.swift`
- Modify: `macos/Sources/DesktopMCPCore/ServePlan.swift`
- Create: `macos/Sources/DesktopMCPCore/ConnectPlan.swift`
- Test: `macos/Tests/DesktopMCPCoreTests/LaunchAgentTests.swift`, `macos/Tests/DesktopMCPCoreTests/ConfigTests.swift`, create `macos/Tests/DesktopMCPCoreTests/ConnectPlanTests.swift`

**Interfaces:**
- Consumes: `StdioBridge.Target.installed(plist:config:)` (exists), `DesktopMCPConfig.directory`, `ConfigErrorText`.
- Produces:
  - `LaunchAgent.socketName: String` = `"Listener"`; `LaunchAgent.socketURL(home: URL) -> URL` (`home/.desktop-mcp/mcp.sock`, mirroring `plistURL(home:)`); `LaunchAgent.socketURL: URL`; `LaunchAgent.validateSocketPath(_ path: String) throws` (the 104-byte rule).
  - `LaunchAgent.programArguments(executable:transport:host:port:socketPath: String?) -> [String]` (appends `"--socket", path` when non-nil).
  - `LaunchAgent.plist(programArguments:logDirectory:socketPath: String?) throws -> Data` (adds `Sockets` when non-nil).
  - `ServePlan.socketPath: String?`; `ServePlan.CommandLineValues.socketPath: String?`.
  - `ConnectPlan` enum as in spec decision 3, plus `ConnectPlan.socketPath(in arguments: [String]) -> String?` (the value after `--socket`).

- [ ] **Step 1: Write the failing tests**

In `LaunchAgentTests.swift`:
```swift
func testSocketPathIsUnderTheConfigDirectory() {
    XCTAssertEqual(LaunchAgent.socketURL(home: URL(fileURLWithPath: "/Users/x")).path, "/Users/x/.desktop-mcp/mcp.sock")
}

func testSocketPathLongerThan103BytesIsRejected() {
    XCTAssertNoThrow(try LaunchAgent.validateSocketPath(String(repeating: "a", count: 103)))
    XCTAssertThrowsError(try LaunchAgent.validateSocketPath(String(repeating: "a", count: 104))) { error in
        XCTAssertTrue("\(error)".hasPrefix("Socket path too long (max 103 bytes): "))
    }
}

func testProgramArgumentsAppendTheSocket() {
    let args = LaunchAgent.programArguments(executable: "/x", transport: "streamable-http", host: "127.0.0.1", port: 8000, socketPath: "/s.sock")
    XCTAssertEqual(args.suffix(2), ["--socket", "/s.sock"])
    XCTAssertFalse(LaunchAgent.programArguments(executable: "/x", transport: "streamable-http", host: "h", port: 1, socketPath: nil).contains("--socket"))
}

func testPlistCarriesTheLaunchdSocket() throws {
    let data = try LaunchAgent.plist(programArguments: ["/x"], logDirectory: URL(fileURLWithPath: "/tmp"), socketPath: "/s.sock")
    let plist = try XCTUnwrap(PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any])
    let sockets = try XCTUnwrap(plist["Sockets"] as? [String: [String: Any]])
    let listener = try XCTUnwrap(sockets[LaunchAgent.socketName])
    XCTAssertEqual(listener["SockPathName"] as? String, "/s.sock")
    XCTAssertEqual(listener["SockFamily"] as? String, "Unix")
    XCTAssertEqual(listener["SockType"] as? String, "stream")
    XCTAssertEqual(listener["SockPathMode"] as? Int, 384)
    XCTAssertEqual(plist["RunAtLoad"] as? Bool, true)
    let without = try LaunchAgent.plist(programArguments: ["/x"], logDirectory: URL(fileURLWithPath: "/tmp"), socketPath: nil)
    XCTAssertNil((try PropertyListSerialization.propertyList(from: without, format: nil) as? [String: Any])?["Sockets"])
}
```
In `ConfigTests.swift`:
```swift
func testSocketRequiresStreamableHTTPAndAShortPath() throws {
    var cli = ServePlan.CommandLineValues()
    cli.transport = "streamable-http"
    cli.socketPath = "/tmp/a.sock"
    XCTAssertEqual(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig()).socketPath, "/tmp/a.sock")
    cli.transport = "stdio"
    XCTAssertThrowsError(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig())) {
        XCTAssertEqual("\($0)", "--socket requires --transport streamable-http.")
    }
    cli.transport = "streamable-http"
    cli.socketPath = "/" + String(repeating: "a", count: 110)
    XCTAssertThrowsError(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig()))
    XCTAssertNil(try ServePlan.resolve(ServePlan.CommandLineValues(), environment: [:], config: DesktopMCPConfig()).socketPath)
}
```
`ConnectPlanTests.swift` (build plists with `LaunchAgent.plist(programArguments:logDirectory:socketPath:)`):
```swift
func testSocketWhenTheAgentOffersOneWithTheHTTPTargetAsFallback() throws {
    let args = LaunchAgent.programArguments(executable: "/x", transport: "streamable-http", host: "127.0.0.1", port: 8123, socketPath: "/s.sock")
    let plist = try LaunchAgent.plist(programArguments: args, logDirectory: URL(fileURLWithPath: "/tmp"), socketPath: "/s.sock")
    let plan = try ConnectPlan.installed(plist: plist, config: DesktopMCPConfig())
    XCTAssertEqual(plan, .socket(path: "/s.sock", fallback: StdioBridge.Target(url: URL(string: "http://127.0.0.1:8123/mcp")!, authKey: nil)))
}

func testHTTPWhenTheAgentPredatesTheSocket() throws {
    let args = LaunchAgent.programArguments(executable: "/x", transport: "streamable-http", host: "127.0.0.1", port: 8000, socketPath: nil)
    let plist = try LaunchAgent.plist(programArguments: args, logDirectory: URL(fileURLWithPath: "/tmp"), socketPath: nil)
    XCTAssertEqual(try ConnectPlan.installed(plist: plist, config: DesktopMCPConfig()),
                   .http(StdioBridge.Target(url: URL(string: "http://127.0.0.1:8000/mcp")!, authKey: nil)))
}

func testUnreadablePlistThrows() {
    XCTAssertThrowsError(try ConnectPlan.installed(plist: Data("nope".utf8), config: DesktopMCPConfig()))
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `… swift test --filter 'LaunchAgentTests|ConfigTests|ConnectPlanTests'`
Expected: compile errors for the new API.

- [ ] **Step 3: Implement**

`LaunchAgent.swift`: the four members of the Interfaces block; keep the existing `programArguments`/`plist` signatures as overloads calling the new ones with `nil` (the existing tests must not change). `ServePlan.swift`: the field, the `CommandLineValues` field, the two checks in `resolve` (call `LaunchAgent.validateSocketPath`), `==` includes `socketPath`. `ConnectPlan.swift`: as the spec's decision 3; `socketPath(in:)` reuses the "value after flag" idea already in `StdioBridge.Target.installed` (pull that helper out as an internal `static func value(after flag: String, in arguments: [String]) -> String?` on `StdioBridge.Target` and use it from both).

- [ ] **Step 4: Run the three test classes**

Expected: PASS, including the pre-existing `LaunchAgentTests` and `StdioBridgeTests` target tests.

- [ ] **Step 5: Commit**

```bash
git checkout -- Package.resolved
git add macos/Sources/DesktopMCPCore/LaunchAgent.swift macos/Sources/DesktopMCPCore/ServePlan.swift macos/Sources/DesktopMCPCore/ConnectPlan.swift macos/Sources/DesktopMCPCore/StdioBridge.swift macos/Tests/DesktopMCPCoreTests/
git commit -m "Carry a Unix socket path through the Launch Agent plist, serve plan and connect plan"
```

---

### Task 2: `SocketServer`

**Files:**
- Create: `macos/Sources/DesktopMCPCore/SocketServer.swift`
- Modify: `macos/Sources/DesktopMCPCore/StdioServer.swift` (the `label` parameter only)
- Test: create `macos/Tests/DesktopMCPCoreTests/SocketServerTests.swift`

**Interfaces:**
- Consumes: `StdioServer.run(_:input:output:)`, `ToolRegistry`, `DesktopMCP.makeRegistry()`, `RecordingObserver` (test support), SDK `Client` + `StdioTransport(input:output:)`.
- Produces (spec decision 7, with `launchdName` handled in Task 3):
  ```swift
  public actor SocketServer {
      public static func listen(path: String, launchdName: String?, registry: ToolRegistry) async throws -> SocketServer
      public static func listen(fileDescriptors: [FileDescriptor], registry: ToolRegistry) async throws -> SocketServer
      public var connections: Int { get }
      public func run() async
      public func shutdown() async
      public struct Busy: Error, CustomStringConvertible {}
      public struct CannotListen: Error, CustomStringConvertible { public let reason: String }
  }
  ```
  `StdioServer.run(_ registry:, input:, output:, label: String = "stdin")`.
  In this task `listen(path:launchdName:)` ignores `launchdName` (always binds); Task 3 adds the launchd call.

- [ ] **Step 1: Write the failing tests**

Test scaffolding in `SocketServerTests.swift`: `setUp` sets `signal(SIGPIPE, SIG_IGN)`; `func socketPath() -> String` returns a fresh `FileManager.default.temporaryDirectory.appendingPathComponent("dm-\(UInt32.random(in: 0...9999)).sock").path` (assert `< 104` bytes); `func client(_ path: String) throws -> (Client, FileDescriptor)` creates `socket(AF_UNIX, SOCK_STREAM, 0)`, `connect`s, builds `Client(name: "t", version: "0")` over `StdioTransport(input: fd, output: fd)` and `connect`s it. `func serving(_ registry:, path:) async throws -> (SocketServer, Task<Void, Never>)` calls `listen(path:launchdName: nil, registry:)` and starts `run()` in a task; tests end with `await server.shutdown()` and `await task.value`.

```swift
func testEachConnectionGetsItsOwnServer() async throws {
    let path = socketPath()
    let (server, run) = try await serving(await DesktopMCP.makeRegistry(), path: path)
    let (a, _) = try client(path); let (b, _) = try client(path)
    _ = try await a.listTools(); _ = try await b.listTools()
    let result = try await a.callTool(name: "Wait", arguments: ["duration": 0])
    XCTAssertEqual(result.isError, false)
    XCTAssertEqual(await server.connections, 2)
    var info = stat(); XCTAssertEqual(stat(path, &info), 0); XCTAssertEqual(info.st_mode & 0o777, 0o600)
    await server.shutdown(); await run.value
}

func testHalfCloseDrainsThenTheAgentCloses() async throws {
    // Raw fd client: send initialize + a Wait of 1 s, shutdown(fd, SHUT_WR) at once, read to EOF.
    // Assert: two responses read, the second has result.content[0].text == "Waited for 1 second(s)" (check the exact text in Desktop.swift's Wait tool), then read returns 0, and connections drops to 0 within 2 s.
}

func testFullCloseMidCallIsNoticedWhenTheReplyFails() async throws {
    // Raw fd client: initialize, Wait of 1 s, close(fd) immediately.
    // Assert: RecordingObserver sees Wait succeed (the stdio EOF rule ran it), and connections == 0 within 3 s (the reply's write failed and the server ended).
}

func testASilentPeerClosingFreesItsServer() async throws {
    // connect(), close() without sending; connections returns to 0 within 2 s.
}

func testALargeReplyArrivesWhole() async throws {
    // Register a Tool "Big" on a fresh ToolRegistry returning .text(String(repeating: "x", count: 2 * 1024 * 1024)).
    // SDK client over the socket: callTool("Big"); assert the text count, and that it took under 2 s (measure with ContinuousClock).
}

func testBusyWhenAnotherServerListens() async throws {
    // serving(path) then listen(path:launchdName:nil) again → throws SocketServer.Busy.
}

func testStaleSocketFileIsReplaced() async throws {
    // serving(path), shutdown (socket file removed by owner) → recreate: bind a raw listener at path, close it without unlink (stale file remains) → listen(path:) succeeds and a client connects.
}

func testRegularFileAtPathIsRefusedAndKept() throws {
    // write "keep" to path; listen(path:) throws CannotListen; file still reads "keep".
}

func testMissingDirectoryIsCannotListen() async {
    // listen(path: tmp + "/nope/x.sock") throws CannotListen.
}

func testShutdownCancelsCallsInFlight() async throws {
    // SDK client calls Wait 30 (in a task); after the observer-free 200 ms, server.shutdown(); observer sees "Cancelled." within 2 s; run task returns.
}
```

- [ ] **Step 2: Run the file to see it fail**

Run: `… swift test --filter SocketServerTests`
Expected: compile errors (`SocketServer` undefined).

- [ ] **Step 3: Implement `SocketServer`**

Spec decisions 5 (the bind half: `stat`/`S_ISSOCK`/probe-`connect`/unlink/`bind`/`chmod 0600`/`listen(16)`), 7, 8 and 9. Accept loop: a `@Sendable` read-source handler per listener (built in a `nonisolated static func`), yielding accepted fds into an `AsyncStream<Int32>`; the actor's `run()` consumes it, checks `getpeereid`, sets `SO_NOSIGPIPE` and `SO_SNDBUF` (1 MiB, ignore failure), sets `O_NONBLOCK` on the accepted fd, and starts a child task per connection in a `withTaskGroup` that runs `StdioServer.run(registry, input:, output:, label:)` and closes the fd afterwards. `shutdown()`: cancel the sources, close listener fds, cancel the group's children (which cancels `StdioServer.run`), wait, unlink the path if this process bound it. `connections` = live child count. Also add `label` to `StdioServer.run` and use it in its log lines.

- [ ] **Step 4: Run the file; then the full suite**

Run: `… swift test --filter SocketServerTests`, then `… swift test`
Expected: all PASS (154 existing + the new ones).

- [ ] **Step 5: Commit**

```bash
git checkout -- Package.resolved
git add macos/Sources/DesktopMCPCore/SocketServer.swift macos/Sources/DesktopMCPCore/StdioServer.swift macos/Tests/DesktopMCPCoreTests/SocketServerTests.swift
git commit -m "Serve the stdio binding over a Unix domain socket, one server per connection"
```

---

### Task 3: launchd activation and the `serve`/`install`/`uninstall` wiring

**Files:**
- Modify: `macos/Sources/DesktopMCPCore/SocketServer.swift` (the `launchdName` path)
- Modify: `macos/Sources/desktop-mcp/DesktopMCPCommand.swift` (`Serve`), `macos/Sources/desktop-mcp/LaunchAgentCommands.swift` (`Install`, `Uninstall`)
- Test: `macos/Tests/DesktopMCPCoreTests/SocketServerTests.swift`

**Interfaces:**
- Consumes: Task 1 (`LaunchAgent.socketURL`, `socketName`, `programArguments(...socketPath:)`, `plist(...socketPath:)`, `ServePlan.socketPath`), Task 2 (`SocketServer`).
- Produces: `serve --socket <path>` option; `Serve.run` starts the socket server per spec decision 6; `Install` passes `LaunchAgent.socketURL.path` to both `programArguments` and `plist` and prints `"  Socket    : <path>"` after the address line; `Uninstall` removes the file at `LaunchAgent.socketURL` when it exists and is a socket.

- [ ] **Step 1: Write the failing test**

```swift
func testAnUnknownLaunchdNameFallsBackToBinding() async throws {
    // listen(path:launchdName: "NoSuchSocket", registry:) under XCTest (not launchd): launch_activate_socket returns ENOENT/ESRCH, the path is bound, a client connects and pings.
    // Assert: await server.activatedByLaunchd == false; the socket file exists at path; after shutdown it is gone.
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `… swift test --filter SocketServerTests/testAnUnknownLaunchdNameFallsBackToBinding`
Expected: compile error (`activatedByLaunchd` undefined).

- [ ] **Step 3: Implement**

`SocketServer.listen(path:launchdName:registry:)`: with a name, call `launch_activate_socket(name, &fds, &count)` (`import launch`; the return value is the error number; `ENOENT`/`ESRCH` → bind path as in Task 2; `0` → set `O_NONBLOCK` on each fd, `free(fds)`, use them, remember "activated" for the log and do not unlink on shutdown; anything else → `CannotListen(reason: "launch_activate_socket: \(String(cString: strerror(code)))")`). `public var activatedByLaunchd: Bool`.

`Serve`: `@Option(name: .customLong("socket"), help: "Also serve the stdio binding on this Unix domain socket (what `install` sets up).") var socket: String?` → `values.socketPath`. In `run`, before `serveHTTP`: when `plan.socketPath` is set, `try await SocketServer.listen(path:, launchdName: LaunchAgent.socketName, registry:)`; on error `log("Socket \(path): \(error); serving HTTP only")` and continue; on success log `"Serving stdio over \(path)"` + `" (socket from launchd)"` when `activatedByLaunchd`. Run `socket.run()` as a task of the **outer** group (beside analytics), not of the `race`: the socket ending must never end `serve`; when its task returns on its own, log `"Socket \(path): accept loop ended; serving HTTP only"`. After the `race` ends (HTTP stopped or a signal), `await socket.shutdown()` before the analytics close. When `--debug`, `SocketServer` logs accepts/ends (add a `debug: Bool` to both `listen`s, default false).

`Install`: pass `LaunchAgent.socketURL.path` (after `LaunchAgent.validateSocketPath`) to `programArguments` and `plist`; print the socket line. `Uninstall`: after removing the plist, if `LaunchAgent.socketURL.path` exists and `S_ISSOCK`, remove it and print `"Removed \(path)"`.

- [ ] **Step 4: Build and run the Core tests**

Run: `… swift build` then `… swift test --filter SocketServerTests`
Expected: builds; PASS.

- [ ] **Step 5: Smoke by hand (no install): from the scratchpad, `ANONYMIZED_TELEMETRY=false .build/debug/desktop-mcp serve --transport streamable-http --port 0 --socket /tmp/dm-smoke.sock` in the background, then `printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"ping"}' | nc -U /tmp/dm-smoke.sock` (or `socat`), expect the pong line; kill the server; the socket file is gone.**

- [ ] **Step 6: Commit**

```bash
git checkout -- Package.resolved
git add macos/Sources/DesktopMCPCore/SocketServer.swift macos/Sources/desktop-mcp/DesktopMCPCommand.swift macos/Sources/desktop-mcp/LaunchAgentCommands.swift macos/Tests/DesktopMCPCoreTests/SocketServerTests.swift
git commit -m "Take the Launch Agent's socket from launchd; serve, install and uninstall know the socket"
```

---

### Task 4: `SocketBridge`

**Files:**
- Create: `macos/Sources/DesktopMCPCore/SocketBridge.swift`
- Modify: `macos/Sources/DesktopMCPCore/StdioBridge.swift` (`StdoutWriter.write(_ data: Data)`)
- Test: create `macos/Tests/DesktopMCPCoreTests/SocketBridgeTests.swift`

**Interfaces:**
- Consumes: Task 2 `SocketServer` (as the peer in tests), `StdoutWriter`.
- Produces (spec decision 11):
  ```swift
  public struct SocketBridge: Sendable {
      public init(path: String)
      public func connect() async throws -> Connection          // throws Refused
      public struct Refused: Error, CustomStringConvertible { public let reason: String }
      public struct Connection: Sendable {
          public func run(input: FileHandle, output: @escaping @Sendable (Data) async -> Bool) async -> Outcome
      }
      public enum Outcome: Equatable, Sendable { case finished, outputClosed, serverClosed }
  }
  ```
  `StdoutWriter.write(_ data: Data) -> Bool` (same semantics as the `String` one).

- [ ] **Step 1: Write the failing tests**

Scaffolding: `Pipes` as in `StdioServerTests` (stdin pipe the test writes, stdout pipe the test reads); `output` closure appends to a `Locked<Data>` and returns true unless the test flips a `Locked<Bool>`; a `SocketServer` on a temp path from Task 2's helpers.

```swift
func testPipesRequestsAndRepliesUntilStdinCloses() async throws {
    // serving(path); let conn = try await SocketBridge(path: path).connect()
    // run in a task with pipes; write initialize + ping lines to stdin; close stdin.
    // Assert: Outcome == .finished; stdout data splits into exactly 2 JSON lines with ids 1 and 2, in that order.
}

func testBytesSplitAcrossReadsArriveByteExact() async throws {
    // Tool "Big" (2 MiB text) on the registry; initialize + tools/call Big; close stdin.
    // Assert: .finished; stdout's second line parses as JSON and its result.content[0].text has 2 MiB of "x".
}

func testOutputClosedEndsTheBridgeAtOnce() async throws {
    // Register a Wait of 5 s; output closure returns false from its first call; write initialize + Wait(5).
    // Assert: Outcome == .outputClosed within 2 s; afterwards server.connections drops to 0 within 7 s (the reply's write fails).
}

func testServerClosingEndsTheBridgeNonZero() async throws {
    // initialize + Wait(30); after 200 ms server.shutdown(); Outcome == .serverClosed within 2 s; stdin still open (the test closes it afterwards).
}

func testNothingListeningIsRefused() async {
    // SocketBridge(path: socketPath()).connect() throws Refused with reason containing "No such file" (ENOENT text).
}

func testConnectsToAListenerThatAcceptsLate() async throws {
    // Raw listener: socket+bind+listen(16) but no accept for 500 ms, then hand the listening fd to SocketServer.listen(fileDescriptors:) and run it.
    // Bridge connect() succeeds immediately (backlog); run with initialize on stdin; the reply arrives once the server accepts.
}
```

- [ ] **Step 2: Run the file to see it fail**

Expected: compile errors (`SocketBridge` undefined).

- [ ] **Step 3: Implement**

`connect()`: `socket(AF_UNIX, SOCK_STREAM, 0)`, `SO_NOSIGPIPE`, blocking `connect` (fast for Unix sockets; run it off the cooperative pool with `Task.detached` or `DispatchQueue.global().async` + continuation); failure → `Refused(reason: String(cString: strerror(errno)))`, fd closed. `Connection.run`: two copy loops in a task group: stdin → socket (`FileHandle` readability handler, as `StdioBridge.lines(of:)` does, but yielding `Data` chunks; at EOF `shutdown(fd, SHUT_WR)`), socket → `output` (a `DispatchSource.makeReadSource` on the fd with a `@Sendable` handler reading until `EAGAIN` and yielding `Data` into an `AsyncStream`; EOF or error finishes it). Outcomes: socket stream ended after stdin EOF → `.finished`; before → `.serverClosed` (stop the stdin loop); `output` false → close the fd, `.outputClosed`. No parsing anywhere. `StdoutWriter.write(_ data: Data)`: share the loop with the `String` overload.

- [ ] **Step 4: Run the file; then the full suite**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git checkout -- Package.resolved
git add macos/Sources/DesktopMCPCore/SocketBridge.swift macos/Sources/DesktopMCPCore/StdioBridge.swift macos/Tests/DesktopMCPCoreTests/SocketBridgeTests.swift
git commit -m "Add the socket bridge: stdin and stdout piped to the agent's Unix socket"
```

---

### Task 5: `connect` wiring and README

**Files:**
- Modify: `macos/Sources/desktop-mcp/ConnectCommand.swift`
- Modify: `macos/README.md`

**Interfaces:**
- Consumes: Task 1 `ConnectPlan`, Task 4 `SocketBridge`, existing `StdioBridge`.
- Produces: `connect --socket <path>`; selection and exit codes per spec decisions 10, 12 and 13.

- [ ] **Step 1: Implement `Connect`**

`@Option(name: .customLong("socket"), help: "Connect over this Unix domain socket instead of HTTP (no fallback).") var socket: String?`; `validate()`: `--url` with `--socket` → `ValidationError("--url and --socket are exclusive.")`; `--auth-key` help gains "HTTP only". `run()`: build the plan: `--url` → `.http(target)`; `--socket` → `.socket(path:, fallback: nil)` (after `LaunchAgent.validateSocketPath`); else `ConnectPlan.installed(plist:config:)` with `authKey` applied to the fallback/HTTP target as today. `.http` → `StdioBridge.run` as today. `.socket(path, fallback)`: `do { let conn = try await SocketBridge(path: path).connect(); switch await conn.run(input: .standardInput, output: { await StdoutWriter.shared.write($0) }) { case .finished, .outputClosed: return; case .serverClosed: log("desktop-mcp connect: the server closed the connection"); throw ExitCode(1) } } catch let refused as SocketBridge.Refused { guard let fallback else { log("desktop-mcp connect: \(refused.reason)"); throw ExitCode(1) }; log("desktop-mcp connect: \(refused.reason); using HTTP at \(fallback.url.absoluteString)"); run StdioBridge(target: fallback) as today }`. Update the command's `discussion` to say the socket is preferred when the installed agent has one.

- [ ] **Step 2: Build, run the whole suite**

Run: `… swift build && … swift test`
Expected: builds, all PASS.

- [ ] **Step 3: README**

Commands block: `desktop-mcp connect [--url] [--socket] [--auth-key]`; options table: `--socket <path>` row for `serve` ("Also serve the stdio binding on this Unix domain socket; what `install` sets up at `~/.desktop-mcp/mcp.sock`"). "Running as a Launch Agent": a paragraph on the socket (`Sockets` key, `launch_activate_socket`, 0600, same-uid peers, one connection = one server with the stdio EOF rule, `install --force` once to upgrade, `connect` falls back to HTTP when the socket refuses, an agent restart ends `connect` with exit 1 and Claude Desktop restarts it, `socat STDIO UNIX-CONNECT:$HOME/.desktop-mcp/mcp.sock`). Transports line at the top mentions the socket. "Differences from the original": a bullet. Development section: how to run `serve --socket` from a terminal.

- [ ] **Step 4: Commit**

```bash
git checkout -- Package.resolved
git add macos/Sources/desktop-mcp/ConnectCommand.swift macos/README.md
git commit -m "connect prefers the agent's Unix socket, falling back to HTTP when it refuses"
```
