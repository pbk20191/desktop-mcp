# Optional sessions on Streamable HTTP, and a streaming `connect` bridge

Date: 2026-10-19
Status: implemented on pbk/sessions
Builds on: `2026-10-19-mcp-swift-sdk-migration-design.md` (merged as 0595b80 on pbk/osascript-jxa)

## Intent

The SDK migration left Streamable HTTP stateless: every `POST /mcp` is served by a fresh SDK
`Server`, no session id is issued, responses are plain JSON. That is correct for the
2026-07-28 protocol (sessions and the `initialize` handshake are removed there) and it works
with today's clients, which treat sessions as optional.

What it cannot do, for the clients that exist today (protocol 2025-03-26 to 2025-11-25), is
anything the server initiates: `tools/list_changed`, elicitation, sampling, and progress
notifications during a long call. The owner is not yet convinced desktop-mcp will never need
those with current clients, and wants the door open without giving up the stateless path.

So: keep the stateless path exactly as it is, and add sessions as an opt-in that a client
takes by sending `initialize`. Make the `connect` bridge able to live in that world: stream
responses instead of buffering them, and recover a lost session on its own so Claude Desktop
never notices.

## Goals

- A client that sends `initialize` without a session id (and accepts `text/event-stream`)
  gets a session: `Mcp-Session-Id` on the response, SSE responses to its `POST`s, `GET /mcp`
  for server-initiated messages, `DELETE /mcp` to end it. **In SDK 0.12.1 every
  server-initiated message, progress notifications included, goes to the `GET` stream, not
  to the `POST` response it relates to** (`Transport.send` carries no request id). A client
  that wants them must open `GET`.
- A request without a session id that is not `initialize` is served statelessly, exactly as
  today (JSON response, per-request `Server`). 2026-07-28 clients therefore keep working.
- A lost session (idle eviction, server restart, byte budget) never strands a client: SDK
  clients re-initialise on 404 by spec; `connect` re-initialises on their behalf.
- Client disconnect still cancels the in-flight tool call, in both modes.
- Memory per session is bounded.
- `connect` forwards streamed responses event by event, so a `subscriptions/listen`
  (2026-07-28) or an SSE `POST` response reaches the stdio client as it arrives; and once it
  holds a session it opens the `GET` stream and forwards its events too, so server-initiated
  messages reach Claude Desktop.
- Telemetry gains `clientInfo` on every tool call made inside a session (today HTTP calls
  have it only when `initialize` shares the request body).

## Non-goals

- Using the new capabilities yet. No tool emits progress or asks for elicitation in this
  work; the plumbing is the deliverable.
- Cross-call *application* state (a persistent shell, "click element 12 from the last
  snapshot"). The 2026-07-28 spec says to do that with server-minted handles passed as tool
  arguments, not with protocol sessions; it is a separate design if ever wanted.
- 2026-07-28 features beyond accepting its version header (`server/discover`, `resultType`,
  `_meta` fields, MRTR). Those arrive with an SDK release that supports the revision.
- SSE resumability (`Last-Event-ID`): not offered to clients (their header is stripped, and
  2026-07-28 removes the feature); used internally to re-attach a re-opened `GET`. The id it
  resumes from is that of the last event the socket layer took and wrote (recorded when the
  writer asks for the next chunk; a priming event's when taken): response streams are pulled
  from the SDK chunk by chunk, so events still waiting when a reader leaves are replayed.
- Changing the stdio server.

## Anti-goals

- Making the stateless path slower or different. Stateless requests must not touch the
  session map or take a lock the session path holds.
- A `connect` bridge that parses MCP semantics beyond what the recovery needs (the
  `initialize` line and JSON-RPC ids).
- Modifying the SDK or vendoring a patched copy. Everything is a wrapper or an app-level
  decision.

## Decisions

### Server

1. **Hybrid dispatch in `HTTPApp`, following the SDK's conformance server
   (`Sources/MCPConformance/Server/HTTPApp.swift`).** For `POST /mcp`:
   - `Mcp-Session-Id` present and known → that session's `StatefulHTTPServerTransport`.
   - `Mcp-Session-Id` present and unknown → 404 with the JSON-RPC error body the SDK produces
     (`Not Found: Session not found or expired`). Clients re-initialise.
   - No session id and the body is a single `initialize` request → create a session (fresh
     `Server` + `StatefulHTTPServerTransport` with a `FixedSessionIDGenerator`), hand it the
     request, keep it in the map.
   - No session id and anything else → today's stateless path, unchanged.
   A session is created only when the `initialize` request's `Accept` includes
   `text/event-stream`; otherwise the request is served statelessly (a JSON `initialize`
   reply, no session), so clients that never asked for SSE see no change.
   `GET /mcp` and `DELETE /mcp`: 405 with `Allow: POST` when no `Mcp-Session-Id` is sent
   (SDK clients read 405 as "no stream" and stop retrying), 404 for an unknown id.
   `HTTPApp` strips `Last-Event-ID` from every request before the transport sees it:
   resumability is not supported (2026-07-28 removes it), and letting it through makes the
   SDK treat a client's first `GET` as a resume of a finished `POST` stream instead of the
   standalone stream, which breaks delivery of server-initiated messages.

2. **Session identity and lifetime.** Session ids are UUIDs. A session ends on `DELETE`, when
   idle longer than `Settings.sessionIdleTimeout` (default 30 minutes; CLI flag
   `--session-timeout <seconds>`, `0` disables eviction), when its byte budget is exceeded
   (decision 4), when the session cap is reached (below), or when the server stops. **Idle**
   means: no `POST` in flight and no `POST` received for the timeout; an open `GET` stream
   does not count as activity (otherwise an SDK client with `streaming: true` would never
   be idle), and a session is never evicted while a tool call is running. **Cap:**
   `Settings.maxSessions` (default 64); creating one more evicts the least recently used
   idle session, or fails with 503 if none is idle. Ending a session calls
   `StartedServer.shutdown()` (cancels in-flight tool calls) and removes it from the map.
   Eviction runs from a single sweeper task started with the app, not from a timer per
   session. Note for the README: the TypeScript SDK (Claude Code, Claude Desktop) re-sends
   `initialize` on 404 as the spec requires; the Swift SDK's client (0.12.1) does not, it
   surfaces "Session expired".

3. **Validation pipeline for sessions:** `AcceptHeaderValidator(mode: .sseRequired)`,
   `ContentTypeValidator()`, `SessionValidator()`. No `OriginValidator` (the app checks
   origins first) and **no `ProtocolVersionValidator`** on either path: SDK 0.12.1 only knows
   versions up to 2025-11-25 and would reject `MCP-Protocol-Version: 2026-07-28` with 400.
   The stateless pipeline drops `ProtocolVersionValidator` too. Version negotiation happens
   in `initialize` where it still exists; otherwise the server serves what it can.

4. **Byte budget per session.** SDK 0.12.1's stateful transport stores every outgoing message
   for resumability and never prunes. A `private actor BudgetedTransport: Transport` wraps
   the stateful transport, counts bytes passed to `send`, and when the total passes
   `Settings.sessionByteBudget` (default 64 MiB) it marks the session as over budget; the app
   ends that session after the current request completes. The client's next request gets
   404 and re-initialises. A `Snapshot` with `use_vision` is a few MB, so this is dozens of
   screenshots per session, not a nuisance.

5. **Cancellation on client disconnect, per request, inside a shared session.** For a
   session request the transport returns `.stream` immediately, so `app.handle` has already
   returned when the client disconnects; the task cancellation the NIO layer sends is not the
   signal. The signal is the termination of the `AsyncStream<String>` that `HTTPApp` builds
   when converting the SDK stream: NIO cancels its consumer on close, and the stream's
   `onTermination(.cancelled)` fires. `NIOHTTPServer.swift` changes only to make that true over HTTP/1.1 (no pipelining handler, so a FIN is read; see sdk-notes).
   To know *which* tool call to cancel: before handing a session request to the transport,
   `HTTPApp` sets a synthetic header `x-desktop-mcp-request: <uuid>` on the
   `MCP.HTTPRequest`, overwriting any client-supplied value. The SDK stores that request
   and exposes it to handlers through `Server.currentHandlerContext?.httpContext`
   (`HandlerContext.id` is package-private, so the JSON-RPC id is not available; the header
   is the handle). The `BudgetedTransport` wrapper must conform to `HTTPContextProviding`
   and forward `httpRequestContext(for:)`, or the context is nil. The `CallTool` wrapper
   reads the header and registers its task in the in-flight map under that uuid; on
   termination `HTTPApp` calls `StartedServer.cancel(request: uuid)`. A cancel can arrive
   before the handler registers, so the map keeps a "cancelled early" marker per uuid that
   cancels the task on registration. The stateless path keeps using `shutdown()`.
   The 2025 Streamable HTTP spec says a disconnect SHOULD NOT be read as cancellation;
   desktop-mcp deliberately does (it did before the migration too): an orphaned `Shell`
   driving the user's desktop is worse than a lost response.

6. **`notifications/cancelled` works inside a session** without any new code: the session's
   `Server` sees both the request and the cancellation and cancels the handler task; the
   wrapper returns the `Cancelled.` result as today.

7. **The SSE priming event.** The stateful transport sends an SSE event with empty `data`
   before each response. Any consumer of our SSE output must ignore events whose `data` is
   empty. `connect` does (decision 11); the README says so for other consumers.
   Server-initiated messages that nobody listens to (no `GET` open) are only stored by the
   transport and count toward the byte budget; that is accepted.

8. **Telemetry.** The session's `initializeHook` stores `Client.Info` once; every tool call in
   that session reports it. Nothing else changes.

9. **`install` / `auth` / printed client config** do not change: clients connect to the same
   `/mcp`. The README explains the opt-in: send `initialize` to get a session; do not, to stay
   stateless.

### Bridge (`desktop-mcp connect`)

10. **Streaming forward.** `StdioBridge.forward(_:output:)` uses `URLSession.bytes(for:)`.
    It reads the body byte by byte, splits on `\n` only (never `AsyncLineSequence`, which
    also splits on CR, NEL, U+2028, U+2029 that JSON strings may contain unescaped), and for
    `text/event-stream` responses emits each event's `data` as soon as its terminating blank
    line arrives; for JSON responses it emits the whole body once; 202 and empty bodies emit
    nothing. Each emitted payload is written as one line (`singleLine` as today).

11. **Empty SSE payloads are dropped** (the priming event, keep-alive comments).

12. **Session recovery.** The bridge remembers the first `initialize` line it forwards and
    the `Mcp-Session-Id` it was given. When a request gets 404 with a session id set, the
    bridge clears the id, re-sends the remembered `initialize`, stores the new id, re-sends
    the original request, and emits only the original request's reply (the replayed
    `initialize` reply is discarded; the stdio client already had its own). One retry per
    request; a second 404 is reported as today's JSON-RPC `-32000` error. If no `initialize`
    was ever seen (a 2026-07-28 client), there is nothing to replay and the 404 is reported.
    When the replay itself fails (503, a transport error), the request's 404 is reported and
    `State.needsSession` is set: a session-less request is never answered 404, so without it
    the bridge would stay session-less (and `GET`-less) for the rest of the run. The next
    request of any kind except a client `initialize` runs the recovery (shared, replaying the
    remembered `initialize`) before it is sent, and is sent without a session if that fails
    too; success clears the flag. Still one attempt per request.

12a. **The bridge opens the `GET` stream.** Once it holds a session id, the bridge opens
    `GET /mcp` (with `Accept: text/event-stream`, no `Last-Event-ID`) and forwards every
    non-empty `data` payload to stdout as one line, so server-initiated messages reach the
    stdio client. On 405 or 404 it does not retry until the session id changes; after a
    recovery (12) it opens a new `GET` for the new session from inside `State.recover`, as
    soon as the new id is stored and before the original request is resent (a recovering
    30 s `Wait` must not leave the new session without a `GET` for 30 s); the follow-up
    after the request is then a no-op for that id. The `GET` is the bridge's own child and
    is cancelled whenever the session id is cleared.

13. **Long streams and shutdown.** `URLSessionConfiguration.timeoutIntervalForRequest` for the
    bridge becomes `0` (no inactivity timeout), because a `GET` or `subscriptions/listen`
    stream may stay silent for hours; the server's SSE keep-alives are not relied on. At
    stdin EOF, `run` cancels its own `GET` child immediately and waits for the forwarded
    `POST` children to finish, as today (a `tools/call` response stream ends when the call
    does). The bridge cannot tell a never-ending client-initiated `POST` stream
    (`subscriptions/listen`) from a slow tool call without reading method names, which the
    anti-goal forbids, so such a stream keeps the bridge alive after EOF until it ends or
    until a stdout write fails, at which point every child is cancelled. Documented.

14. **Interleaving stays impossible**: every emitted line goes through `StdoutWriter.shared`
    as today, one call per complete payload.

### Test-side client

16. **A reconnecting client wrapper for tests.** SDK 0.12.1's `Client` does not re-send
    `initialize` on 404 (it clears the session id and throws "Session expired"), although
    the 2025 spec requires clients to. desktop-mcp ships no Swift client, so the fix lives in
    the test target: `ReconnectingClient` in `macos/Tests/DesktopMCPCoreTests/TestSupport.swift`
    (duplicated in the HTTP test target if needed) wraps `Client`, and on that error creates
    a fresh `HTTPClientTransport`, calls `connect(transport:)` again and retries the request
    once. Session eviction and recovery tests use it so they assert "the client sees no
    failure", which is what the TypeScript SDK clients (Claude Code, Claude Desktop)
    experience. When a later SDK release re-initialises on its own, delete the wrapper; the
    owner keeps a memory note to that effect, and an upstream proposal to the SDK is a
    separate, optional task.

### Protocol version header

15. Dropping `ProtocolVersionValidator` (decision 3) is the only 2026-07-28 accommodation in
    this work. The README states that 2026-07-28 clients are served statelessly and that
    `server/discover` is not implemented yet.

## Client-visible changes

- A client that sends `initialize` now receives `Mcp-Session-Id` and SSE responses; one that
  does not sees no change. `connect`'s behaviour toward Claude Desktop is unchanged apart
  from streaming (replies may now arrive before a slow request's reply, as they already
  could because requests are forwarded concurrently).
- `GET /mcp` and `DELETE /mcp` now exist for sessions. Without an `Mcp-Session-Id` they are
  405 with `Allow: POST`, as before; with an unknown id, 404.
- An unknown `MCP-Protocol-Version` header no longer yields 400.

## Constraints

- Swift 6 strict concurrency; no `@unchecked Sendable`; SDK pinned at 0.12.1; no runtime
  dependencies; `NIOHTTPServer.swift` unchanged except for FIN detection over HTTP/1.1; stdio server unchanged.
- Tests run with Xcode 16.4 (`DEVELOPER_DIR=/Applications/Xcode-16.4.app/Contents/Developer`),
  `ANONYMIZED_TELEMETRY=false`; never `install`/`uninstall`; never write under
  `~/.desktop-mcp`.
- Commits local on a feature branch; the owner pushes.
- Known SwiftPM wart: `swift test` re-adds 13 pins to `Package.resolved` and `swift build`
  removes them; the committed file is the build/resolve state. Restore it before committing.

## Left to the builder

- Session map structure (actor vs `Locked`), sweeper interval, and how the stateless path
  avoids the session actor entirely.
- Whether the byte budget wrapper also counts incoming bytes (it need not).
- Test doubles for SSE: a small in-process HTTP server (reuse `NIOHTTPServer` with a test
  `HTTPApp`) is preferred over mocking `URLSession`.
- File layout; the session code may live in a new `Sessions.swift` beside `HTTPApp.swift`.

## Verification the builder must show

- SDK `Client` over `HTTPClientTransport(streaming: true)`: connect (session issued), call
  `Wait`, `DELETE`, then a request with the old id → 404, reconnect → new session. The
  `GET` the client opens must be the standalone stream (a server-initiated
  `notifications/message` sent via `server.notify` arrives on the client).
- `GET /mcp` without a session id → 405; `initialize` with `Accept: application/json` only
  → JSON reply and no `Mcp-Session-Id`.
- Session cap: with `maxSessions = 2`, a third `initialize` evicts the idle LRU session.
- Same `HTTPApp`: a request without `initialize` and without a session id → JSON, no
  session header (stateless path intact); a request with `MCP-Protocol-Version: 2026-07-28`
  → served.
- Idle eviction: with a 1-second timeout, a session disappears and its next request is 404.
- Byte budget: with a tiny budget, two large responses end the session; the next request is
  404 and re-initialisation works.
- Disconnect inside a session: two concurrent `Shell` calls on one session; cancel the
  connection of one; the observer sees that one end within 1 s and the other complete.
- `notifications/cancelled` inside a session yields `Cancelled.`.
- Bridge: against a real `HTTPApp` with sessions, `initialize` then `tools/call` through
  `forward`; evict the session; the next `tools/call` through the bridge succeeds (one
  replayed `initialize`, invisible to the stdio side); the stdio side sees exactly one reply
  per request and no empty lines.
- Bridge streaming: an SSE response whose two events are separated by a 300 ms server-side
  delay is emitted as two lines ~300 ms apart; a JSON response is emitted once.
- Bridge `GET`: after `initialize` through the bridge, a `server.notify` on the session
  shows up on the bridge's stdout as one line; after a forced eviction and recovery, a
  notification on the new session also arrives.
- Bridge EOF: with the bridge's `GET` open and no `POST` in flight, closing stdin ends
  `run` within 1 s; with a `Wait 2` in flight, `run` ends after the reply (about 2 s).
- Manual: `desktop-mcp serve` + Claude Code `claude mcp add --transport http` round trip
  (session mode) and the `connect` round trip from the previous spec (stateless or session,
  whichever the bridge gets), both logged in the report.
