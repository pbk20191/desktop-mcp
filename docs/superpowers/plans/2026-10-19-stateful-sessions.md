# Optional Sessions and Streaming Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add opt-in MCP sessions to `POST /mcp` (a client that sends `initialize` and accepts SSE gets a session, `GET` and `DELETE`), keep the stateless path untouched, and make `desktop-mcp connect` stream responses, open the session's `GET` stream and recover a lost session on its own.

**Architecture:** `HTTPApp` grows a `SessionStore` (new file `Sessions.swift`) that owns one SDK `Server` + `StatefulHTTPServerTransport` per session behind a `BudgetedTransport` wrapper, evicts by idle time, byte budget and count, and routes requests by `Mcp-Session-Id`; the stateless per-request path stays as is. Per-request cancellation inside a session uses a synthetic `x-desktop-mcp-request` header that the `CallTool` wrapper reads back through `Server.currentHandlerContext`. The bridge's `forward` moves to `URLSession.bytes(for:)` with an incremental SSE parser, remembers the `initialize` line for 404 recovery, and runs a `GET` child per session.

**Tech Stack:** Swift 6 strict concurrency, `mcp-swift-sdk` 0.12.1 (`StatefulHTTPServerTransport`, `HTTPContextProviding`, `Server.currentHandlerContext`), SwiftNIO (unchanged), `URLSession.bytes(for:)`, `URLProtocol` stubs in tests, XCTest.

**Spec:** `docs/superpowers/specs/2026-10-19-stateful-sessions-design.md`

## Global Constraints

- Swift 6 language mode, strict concurrency; no `@unchecked Sendable`; SDK pinned at `from: "0.12.1"`; no runtime dependencies.
- `macos/Sources/DesktopMCPHTTP/NIOHTTPServer.swift` and `macos/Sources/DesktopMCPCore/StdioServer.swift` are not modified.
- Build/test with `export DEVELOPER_DIR=/Applications/Xcode-16.4.app/Contents/Developer` and `export ANONYMIZED_TELEMETRY=false`; never `desktop-mcp install`/`uninstall`; never write under `~/.desktop-mcp`.
- Commit locally on the feature branch; the owner pushes. Before each commit: `git checkout -- Package.resolved` if a `swift test` run re-added pins (the committed file is the `swift build`/`resolve` state).
- Defaults copied from the spec: `sessionIdleTimeout` 1800 s (CLI `--session-timeout <seconds>`, `0` disables eviction), `sessionByteBudget` 64 MiB (67 108 864 bytes), `maxSessions` 64, synthetic header name `x-desktop-mcp-request`, session ids are UUID strings.
- Wire rules copied from the spec: no session id + `initialize` + `Accept` containing `text/event-stream` → session; no session id otherwise → stateless (unchanged, JSON); unknown session id → 404 with the SDK's JSON-RPC error body; `GET`/`DELETE` without a session id → 405 `Allow: POST`; `Last-Event-ID` stripped before the transport; `ProtocolVersionValidator` absent from both pipelines; stateful pipeline = `AcceptHeaderValidator(mode: .sseRequired)`, `ContentTypeValidator()`, `SessionValidator()`.
- Bridge rules copied from the spec: split on `\n` only (never `AsyncLineSequence`); empty SSE `data` dropped; one retry per request on 404 by replaying the remembered `initialize`; bridge-owned `GET` cancelled when the session id is cleared and at stdin EOF; forwarded `POST` children awaited at EOF; `timeoutIntervalForRequest = 0`.
- Existing behaviour that must survive: 99 tests pass today; the stateless disconnect test, the same-id concurrency test, the `connect` bridge tests and the 2026-10-19 migration README facts (apart from the lines this plan changes).

## Review Focus

1. `initialize` sent with an existing session's id (a client re-initialising on a live session): the SDK answers 400 "Session already initialized". The session must survive and keep serving. Test in Task 2.
2. Reaching `maxSessions` while every session has a tool call running: creation must answer 503 with a JSON-RPC error body, not evict a busy session. Test in Task 2.
3. A second `GET` on a session that already has one: the SDK answers 409; the first stream must keep working. Test in Task 2.
4. The bridge gets 404 for a notification (no id) while holding a session: it must replay `initialize`, resend the notification, and emit nothing. Test in Task 5.
5. A session request whose tool call is cancelled by disconnect before the handler registered its task (fast disconnect): the call must still end as `Cancelled.`, not run to completion. Test in Task 1.

---

### Task 1: Per-request cancellation handles in `ToolRegistry`

**Files:**
- Modify: `macos/Sources/DesktopMCPCore/ToolRegistry.swift` (`ToolCalls`, `StartedServer`, `call`)
- Modify: `macos/Tests/DesktopMCPCoreTests/TestSupport.swift` (add `ContextualTransport`)
- Test: `macos/Tests/DesktopMCPCoreTests/ToolRegistryTests.swift`

**Interfaces:**
- Consumes: `Server.currentHandlerContext?.httpContext: MCP.HTTPRequest?`; `MCP.HTTPContextProviding` (`func httpRequestContext(for id: ID) async -> HTTPRequest?`, an `async` requirement).
- Produces:
  ```swift
  extension StartedServer {
      /// Cancels the tool call that was started for the request carrying this handle
      /// (`x-desktop-mcp-request` header). A call that has not registered yet is cancelled
      /// as soon as it does. Idempotent; unknown handles are ignored.
      public func cancel(request handle: String) async
  }
  public enum RequestHandle {
      public static let header = "x-desktop-mcp-request"
  }
  ```
  `ToolCalls` gains `byHandle: [String: UUID]` and `cancelledEarly: Set<String>`; `register(_:id:handle:)` returns `false` when closed *or* when `handle` is in `cancelledEarly` (removing it).

- [ ] **Step 1: Write the failing tests**

In `TestSupport.swift` add a transport for tests that wraps an `InMemoryTransport` and conforms to `MCP.HTTPContextProviding`, returning for every id an `MCP.HTTPRequest(method: "POST", headers: [RequestHandle.header: handle])` with a handle the test chooses:
```swift
actor ContextualTransport: Transport, HTTPContextProviding {
    init(wrapping inner: InMemoryTransport, handle: String)
}
```
In `ToolRegistryTests.swift`:
```swift
func testCancelByRequestHandleEndsOnlyThatCall()   // start Wait 30 (handle "a") and Wait 30 (handle "b") on two servers sharing a registry; started.cancel(request: "a"); the "a" call returns Cancelled./isError within 2 s; the "b" call is still running after 1 s (observer saw no event for it)
func testCancelBeforeRegistrationStillCancels()   // started.cancel(request: "x") first, then send Wait 30 whose handle is "x": result Cancelled./isError within 2 s
func testCallsWithoutAHandleAreUnaffectedByCancelByHandle() // in-memory transport without context; cancel(request: "nope") does nothing; Wait 0 completes normally
```

- [ ] **Step 2: Run them to confirm they fail**

Run: `swift test --filter ToolRegistryTests`
Expected: compile error, `cancel(request:)` / `RequestHandle` not found.

- [ ] **Step 3: Implement**

In `call`, read `Server.currentHandlerContext?.httpContext?.header(RequestHandle.header)` before creating the task and pass it to `register`. `StartedServer.cancel(request:)` looks up `byHandle` under the lock, cancels the task outside the lock, or records `cancelledEarly` when unknown. `shutdown()` is unchanged.

- [ ] **Step 4: Run the registry tests, then the suite**

Run: `swift test --filter ToolRegistryTests` then `swift test`
Expected: PASS; total 99 + 3.

---

### Task 2: Sessions in `HTTPApp`

**Files:**
- Create: `macos/Sources/DesktopMCPCore/Sessions.swift` (`SessionStore`, `BudgetedTransport`, `Session`)
- Modify: `macos/Sources/DesktopMCPCore/HTTPApp.swift` (`Settings`, `route`, `mcpPost`, new `mcpSession*`, `convert` for `.stream`, `shutdown()`)
- Modify: `macos/Sources/DesktopMCPCore/ServePlan.swift`, `macos/Sources/desktop-mcp/DesktopMCPCommand.swift` (`--session-timeout`, `app.shutdown()` after `serve` returns)
- Modify: `macos/Tests/DesktopMCPCoreTests/TestSupport.swift` and `macos/Tests/DesktopMCPHTTPTests/TestSupport.swift` (`ReconnectingClient`)
- Test: `macos/Tests/DesktopMCPCoreTests/HTTPAppTests.swift`, `macos/Tests/DesktopMCPHTTPTests/NIOHTTPServerTests.swift`, `macos/Tests/DesktopMCPCoreTests/ConfigTests.swift`

**Interfaces:**
- Consumes: Task 1's `StartedServer.cancel(request:)`, `RequestHandle.header`; SDK `StatefulHTTPServerTransport(sessionIDGenerator:validationPipeline:retryInterval:logger:)`, `handleRequest`, `HTTPContextProviding`; conformance server `Sources/MCPConformance/Server/HTTPApp.swift` as the reference for the session map.
- Produces:
  ```swift
  // HTTPApp.swift
  public struct Settings {
      public var sessionIdleTimeout: TimeInterval = 1800   // 0 disables eviction
      public var sessionByteBudget: Int = 64 * 1024 * 1024
      public var maxSessions: Int = 64
  }
  extension HTTPApp {
      /// Ends every session (cancelling their tool calls) and stops the sweeper. Call once
      /// after `NIOHTTPServer.serve` returns; tests call it in `defer`.
      public func shutdown() async
      /// Test hook (internal): ends every session as eviction would.
      func evictAllSessions() async
  }
  // Sessions.swift
  actor SessionStore {
      init(registry: ToolRegistry, settings: HTTPApp.Settings)
      func create(initialize request: MCP.HTTPRequest) async -> MCP.HTTPResponse   // 503 when at cap and none idle
      func handle(_ request: MCP.HTTPRequest, sessionID: String) async -> MCP.HTTPResponse?  // nil when unknown
      func end(_ sessionID: String) async
      func endAll() async
      func sweep(now: Date) async            // idle eviction; called by the sweeper task
      var count: Int { get }
  }
  /// Wraps the stateful transport: counts outgoing bytes, flags over-budget, forwards HTTP context.
  actor BudgetedTransport: Transport, HTTPContextProviding
  ```
  `ServePlan.sessionTimeout: TimeInterval` (default 1800) from `--session-timeout`; `DesktopMCPConfig` is not extended.

- [ ] **Step 1: Write the failing `HTTPAppTests`**

Helpers: `initialize(_ app:accept:)` posts `{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}` with the given `Accept`; `sseData(_ response:)` reads the `.stream` body and returns the non-empty `data` payloads (reuse `SSE.dataPayloads`).
```swift
func testInitializeWithSSEAcceptCreatesASession()          // Accept "application/json, text/event-stream": 200, content-type text/event-stream, header mcp-session-id present, one payload with result.serverInfo.name == "desktop-mcp"
func testInitializeWithJSONAcceptStaysStateless()          // Accept "application/json": 200 application/json, no mcp-session-id header
func testRequestsWithoutASessionStayStateless()            // ping without session → JSON, no session header; app.sessions.count == 0 after
func testUnknownSessionIdIs404()                           // POST ping with Mcp-Session-Id "nope" → 404, body error.message contains "Session not found"
func testGetAndDeleteWithoutASessionAre405()               // GET /mcp, DELETE /mcp → 405 with allow: POST
func testSessionRoundTripAndDelete()                       // registry with RecordingObserver; initialize (clientInfo name "t") → id; tools/call Wait 0 with id → SSE payload isError false and observer event "ok Wait t"; DELETE with id → 200; ping with id → 404
func testReinitializeOnALiveSessionIs400AndKeepsIt()       // initialize again with the id → 400; ping with id → 200
func testBatchesAreRejectedInSessions()                    // "[{ping},{ping}]" with id → 400
func testProtocolVersionHeaderIsNotValidated()             // ping stateless and ping in-session with MCP-Protocol-Version "2026-07-28" → 200 both
func testLastEventIDIsStripped()                           // GET with id and Last-Event-ID "1" → 200 text/event-stream (not a resume error); a second GET → 409
func testIdleSessionsAreEvicted()                          // sessionIdleTimeout 0.2: initialize; sleep 0.5; app.sweep(now:) via internal hook or wait for the sweeper; ping with id → 404
func testByteBudgetEndsTheSession()                        // sessionByteBudget 200: initialize (response ~ >200 bytes) then one more request → next request with id → 404
func testSessionCapEvictsIdleLRUAndRefusesWhenBusy()       // maxSessions 2: three initializes → third succeeds and the first id is 404; then with two sessions each running Wait 30, a fourth initialize → 503
func testDisconnectInsideASessionCancelsOnlyThatCall()     // registry with RecordingObserver; two Shell "sleep 2; echo late" POSTs on one session via Task { app.handle }; cancel one task after 200 ms; observer sees that one end ("ok Shell" with "Command cancelled" result, or "fail Shell Cancelled." for Wait) within 1 s; the other completes after ~2 s
func testCancelledNotificationInsideASession()             // Wait 30 in a Task; POST notifications/cancelled with its id → 202; the Wait response payload is Cancelled./isError within 2 s
```
Keep every existing test; `testStreamableAcceptAndMethodRules` loses its "GET → 405 because stateless" comment but keeps the assertion (405 without a session id).

- [ ] **Step 2: Run them to confirm they fail**

Run: `swift test --filter HTTPAppTests`
Expected: compile errors (`Settings.sessionIdleTimeout`, `shutdown()` missing) or 405/JSON where sessions are expected.

- [ ] **Step 3: Implement `Sessions.swift`**

`Session` holds `started: StartedServer`, `transport: BudgetedTransport`, `createdAt`, `lastRequestAt`, `inFlight: Int`, `overBudget: Bool`. Define `struct FixedSessionIDGenerator: SessionIDGenerator { let sessionID: String }` in `Sessions.swift` (the conformance server's one is private). `SessionStore.create` builds `StatefulHTTPServerTransport(sessionIDGenerator: FixedSessionIDGenerator(sessionID: UUID().uuidString), validationPipeline: StandardValidationPipeline(validators: [AcceptHeaderValidator(mode: .sseRequired), ContentTypeValidator(), SessionValidator()]))`, wraps it in `BudgetedTransport`, calls `registry.start(on:)`, then `handleRequest(initialize)`; if the response is not a 200 stream, shuts the server down and does not store the session. Cap: before creating, if `count >= maxSessions`, evict the LRU session with `inFlight == 0`, else return `.error(statusCode: 503, .internalError("Too many sessions"))`. `handle` bumps `lastRequestAt`, increments `inFlight` for `POST`, decrements when the SDK stream finishes (see Step 4), and after the request ends a session flagged `overBudget` is ended. `sweep` ends sessions with `inFlight == 0` and `now - lastRequestAt > sessionIdleTimeout` (skipped when the timeout is 0). `BudgetedTransport.send` adds `data.count` to a counter and flags `overBudget` past the budget; `httpRequestContext(for:)` forwards to the inner transport.

- [ ] **Step 4: Implement the routing in `HTTPApp.swift`**

`route` for `/mcp`: read `Mcp-Session-Id`; `POST` without it → if body is a single `initialize` (decode `method` only) and `Accept` contains `text/event-stream` → `sessions.create`, else `mcpPost` (now with `ProtocolVersionValidator` removed); with it → `sessions.handle` or 404; `GET`/`DELETE` without it → 405 `Allow: POST`; with it → `sessions.handle` or 404. Before any session call: remove `Last-Event-ID`, set `RequestHandle.header` to a fresh UUID (overwriting a client value). `convert` gains the `.stream` case: an `AsyncStream<String>` that forwards each `Data` chunk as a `String`, whose `onTermination` (cancelled) calls `started.cancel(request: handle)` and decrements the session's `inFlight`; a finished stream decrements too. The sweeper is a `Task` started lazily on first session creation, looping `sleep(min(timeout, 60s))` then `sweep(now:)`; `shutdown()` cancels it and calls `endAll()`.

- [ ] **Step 5: CLI flag and shutdown**

`Serve` gets `@Option(name: .customLong("session-timeout"), help: "Seconds a session may stay idle before it is dropped; 0 keeps sessions until the client ends them. (default: 1800)") var sessionTimeout: Int?`; `ServePlan.CommandLineValues.sessionTimeout: Int?`, `ServePlan.sessionTimeout: TimeInterval` (CLI, else 1800); `serveHTTP` sets `settings.sessionIdleTimeout` and calls `await app.shutdown()` after `NIOHTTPServer.serve` returns (also on error). Add to `ConfigTests.testPlanPrecedenceCommandLineThenEnvironmentThenConfig` (or a new test) that `sessionTimeout` defaults to 1800 and `--session-timeout 0` yields 0.

- [ ] **Step 6: Socket-level tests with the SDK client**

In `TestSupport.swift` (both test targets) add:
```swift
/// An SDK Client that re-initialises when the server answers 404 (what the TypeScript SDK
/// clients do by spec; the Swift SDK 0.12.1 client does not).
actor ReconnectingClient {
    init(endpoint: URL, authKey: String?)
    func connect() async throws
    func callTool(name: String, arguments: [String: Value]) async throws -> (content: [MCP.Tool.Content], isError: Bool?)  // one reconnect+retry on "session expired"/404
    var sessionsSeen: Int { get }
}
```
In `NIOHTTPServerTests.swift`:
```swift
func testSDKClientGetsASessionAndTheStandaloneGetStream() // Client(streaming: true) connect; the app's session count == 1; obtain the session's StartedServer via an internal hook and `server.notify(ToolListChangedNotification.message())` → the client's onNotification handler receives it within 2 s
func testSDKClientSurvivesEvictionThroughTheReconnectingWrapper() // ReconnectingClient connect; callTool Wait 0; app.evictAllSessions(); callTool Wait 0 succeeds; sessionsSeen == 2
func testStatelessClientsStillWorkOverSockets()          // existing testSDKClientOverHTTP with streaming: false still passes unchanged
```
`start(...)` in these tests returns the `HTTPApp` too so tests can call `evictAllSessions()` and `shutdown()` in `defer`.

- [ ] **Step 7: Run the suite**

Run: `swift test`
Expected: PASS (99 + 3 + 15 + 3 + 1, no warnings).

---

### Task 3: Bridge streaming forward

**Files:**
- Modify: `macos/Sources/DesktopMCPCore/StdioBridge.swift` (`forward`, `run`, `SSE`, session config)
- Modify: `macos/Sources/desktop-mcp/ConnectCommand.swift` (call site only)
- Modify: `macos/Tests/DesktopMCPCoreTests/TestSupport.swift` (`StubHTTP` URLProtocol)
- Test: `macos/Tests/DesktopMCPCoreTests/StdioBridgeTests.swift`, `macos/Tests/DesktopMCPHTTPTests/StdioBridgeServerTests.swift`

**Interfaces:**
- Produces:
  ```swift
  extension StdioBridge {
      /// One message in; every reply is handed to `output` as soon as it is complete.
      public func forward(_ line: String, output: @Sendable (String) async -> Void) async
  }
  public struct SSEParser {           // incremental; replaces SSE.dataPayloads for the bridge
      public init()
      /// Feeds bytes; returns the non-empty `data` payloads of events completed by them.
      public mutating func feed(_ bytes: some Sequence<UInt8>) -> [String]
      /// A trailing event without its blank line, if any.
      public mutating func finish() -> String?
  }
  ```
  `SSE.dataPayloads(_:)` stays (tests use it) and is reimplemented on top of `SSEParser`. `StdioBridge.init` sets `timeoutIntervalForRequest = 0`.

- [ ] **Step 1: Write the failing tests**

In `TestSupport.swift` (Core) add `final class StubHTTP: URLProtocol` with a static, locked script: `StubHTTP.respond { request -> (status: Int, headers: [String: String], chunks: [(delay: TimeInterval, bytes: Data)]) }`; it delivers chunks with `urlProtocol(_:didLoad:)` after each delay, then `urlProtocolDidFinishLoading`. `StdioBridge(target:session:)` is built with a `URLSessionConfiguration.ephemeral` whose `protocolClasses = [StubHTTP.self]`.
In `StdioBridgeTests.swift`:
```swift
func testSSEEventsAreEmittedAsTheyArrive()      // chunks: "data: {\"id\":1}\n\n" at 0 s, "data: {\"id\":2}\n\n" at 0.3 s; collector timestamps: second line ≥ 0.25 s after the first; both lines parse
func testEmptyAndCommentEventsAreDropped()      // "id: 1\ndata: \n\n: keepalive\n\ndata: {\"id\":3}\n\n" → exactly one line
func testJSONBodyIsEmittedOnceWhenComplete()    // application/json body split across two chunks → one line equal to the whole body
func testPayloadsSplitOnLFOnly()                // a data line containing U+2028 inside a JSON string → one line, string intact
func testParserHandlesCRLFAndSplitBoundaries()  // SSEParser fed byte by byte with "\r\n" endings → same payloads as dataPayloads
```
Existing `testSSEDataIsJoinedPerEventAndCommentsAreIgnored` stays. In `StdioBridgeServerTests` the three existing tests adopt `forward(_:output:)` with a `LineCollector` and keep their assertions.

- [ ] **Step 2: Run them to confirm they fail**

Run: `swift test --filter "StdioBridgeTests|StdioBridgeServerTests"`
Expected: compile error (`forward(_:output:)`, `SSEParser`, `StubHTTP` missing).

- [ ] **Step 3: Implement**

`forward`: `let (bytes, response) = try await session.bytes(for: request)`; status/session-id handling as today (errors → the JSON-RPC `-32000` reply as today); for `text/event-stream` iterate `for try await byte in bytes`, feeding an `SSEParser` per byte buffer (collect into a `[UInt8]` and feed on each `\n` to keep it simple), emitting each payload through `singleLine` + `output`; for other content types accumulate and emit once; 202/empty → nothing. `run` calls `await forward(line, output: output)`.

- [ ] **Step 4: Run the suite**

Run: `swift test`
Expected: PASS.

---

### Task 4: Bridge session recovery, `GET` child, EOF rules

**Files:**
- Modify: `macos/Sources/DesktopMCPCore/StdioBridge.swift` (`State`, `forward`, `run`, new `listen`)
- Test: `macos/Tests/DesktopMCPHTTPTests/StdioBridgeServerTests.swift`, `macos/Tests/DesktopMCPCoreTests/StdioBridgeTests.swift`

**Interfaces:**
- Consumes: Task 2's `HTTPApp` with sessions and `evictAllSessions()` (via `@testable import DesktopMCPCore` in the HTTP test target), Task 3's `forward(_:output:)` and `SSEParser`.
- Produces: `StdioBridge.State` gains `initializeLine: String?`, `listener: Task<Void, Never>?`; `forward` retries once on 404; `run` opens the `GET` listener when a session id first appears or changes, cancels it when the id is cleared and at EOF.

- [ ] **Step 1: Write the failing tests**

`StdioBridgeServerTests` (real `HTTPApp` with sessions over `NIOHTTPServer`):
```swift
func testBridgeGetsASessionAndRecoversAfterEviction()  // forward initialize → 1 line, state.sessionID set; app.evictAllSessions(); forward tools/call Wait 0 → exactly 1 line (the Wait reply), a new session id, app session count == 1
func testSecond404IsReportedAsAnError()               // make the server refuse: evict and then set maxSessions reached with busy sessions (or point the bridge at a session-less app via a flag) → forward ping → one line with error.code -32000
func testNotificationGetting404IsReplayedSilently()   // after eviction, forward notifications/initialized → no lines, session recovered (next ping works without a further replay: count replays via a test hook on State)
func testBridgeForwardsServerNotificationsFromTheGetStream() // run(input:) with initialize on stdin; wait for the listener; `server.notify(ToolListChangedNotification.message())` on the session (internal hook) → the notification appears on the output collector within 2 s; after evict + a request, a notification on the new session also appears
func testEOFCancelsTheListenerAndWaitsForRequests()   // stdin: initialize + Wait 2; close stdin at ~0.2 s; run returns after the Wait reply (~2 s) and before 4 s; no listener task alive afterwards (state.listener == nil)
```
`StdioBridgeTests` (StubHTTP):
```swift
func testNoInitializeMeansNoReplay()  // 404 without a remembered initialize → one -32000 line, one request made
```

- [ ] **Step 2: Run them to confirm they fail**

Run: `swift test --filter "StdioBridgeServerTests|StdioBridgeTests"`
Expected: FAIL (404 reported instead of recovered; no listener).

- [ ] **Step 3: Implement**

In `forward`: if the line is a single request with `method == "initialize"`, remember it in `state` before sending. On HTTP 404 with a session id set and a remembered `initialize`: clear the id, send the `initialize` (discard its reply, store the new id), resend the original request, emit its reply; a 404 on the retry falls through to the error path. `listen(sessionID:)`: `GET target.url` with `Accept: text/event-stream` and the session header, `bytes(for:)`, `SSEParser`, each payload → `output`; on 404/405 return; on any other end return. `run`: after each `forward`, if `state.sessionID` changed, cancel the old listener and start a new one in the group; at EOF cancel the listener, then let the group wait for the `POST` children; if a `StdoutWriter` write fails (returns false) cancel the whole group.

- [ ] **Step 4: Run the suite**

Run: `swift test`
Expected: PASS.

---

### Task 5: README and final verification

**Files:**
- Modify: `macos/README.md` (lines 7-8 transport bullets, the Endpoints list around line 110, `connect` paragraph around line 102, options table for `--session-timeout`, Differences list)
- Modify: `docs/superpowers/specs/2026-10-19-stateful-sessions-design.md` status line

- [ ] **Step 1: README**

State: sessions are opt-in by `initialize` + SSE `Accept`; what `GET`/`DELETE` do; 405 without a session id, 404 for an unknown one and that clients re-initialise (TypeScript SDK does; Swift SDK 0.12.1 client does not); idle timeout, byte budget and cap with their defaults and `--session-timeout`; server-initiated messages travel only on `GET`; the priming event's empty `data`; `Last-Event-ID` unsupported; `MCP-Protocol-Version` no longer validated and 2026-07-28 clients are served statelessly without `server/discover`; `connect` streams, opens `GET`, recovers sessions, and stays alive after stdin EOF while a client-initiated stream is open.

- [ ] **Step 2: Verification**

With `DEVELOPER_DIR` set to Xcode 16.4 and `ANONYMIZED_TELEMETRY=false`: `swift build`, `swift test` (expect every test passing, no warnings), `git status` clean apart from untracked `docs/`. Manual, both logged in the report with exact commands and output:
1. `desktop-mcp serve --transport streamable-http --host 127.0.0.1 --port 18767` and, from a scratch directory, `claude mcp add --transport http desktop-mcp-test http://127.0.0.1:18767/mcp/ --header "Authorization: Bearer <key from ~/.desktop-mcp/config.toml>"`, then `claude -p "use desktop-mcp-test's Wait tool for 0 seconds and report the result"`; afterwards `claude mcp remove desktop-mcp-test`. Report whether the server log shows a session created and whether the call succeeded. If `claude` is not available, say so and skip.
2. The `connect` round trip from the previous plan (two lines, `"isError":false`), now against the session-capable server; confirm the server created one session for the bridge and the process exits on stdin EOF when fed through a pipe.
Kill the server; `pgrep -fl desktop-mcp` shows only the user's own Launch Agent.
