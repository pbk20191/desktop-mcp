# Migrate the MCP protocol layer to the official Swift SDK

Date: 2026-10-19
Status: approved in conversation; builder check done, answers folded in

## Intent

desktop-mcp currently carries its own MCP implementation: JSON-RPC framing, a `JSONValue`
type, protocol-version negotiation, `initialize` / `tools/list` / `tools/call` dispatch,
per-request task tracking with `notifications/cancelled`, a legacy SSE transport and a
stateless Streamable HTTP transport. That is roughly 1,200 lines whose only job is to track
the MCP specification.

Replace it with the official SDK, `modelcontextprotocol/swift-sdk` (package name
`mcp-swift-sdk`, product `MCP`), so that spec upkeep moves to the SDK and the project can
later adopt the parts of the protocol it does not have today (server-initiated messages,
elicitation, sampling, resources) without writing them by hand.

The socket layer is deliberately not touched. The SDK's HTTP server transports are
framework-agnostic: they expose `handleRequest(HTTPRequest) async -> HTTPResponse` and
never open a socket, so the existing Network.framework server (NIOTransportServices with
NIOSSL and NIOHTTP2) stays as it is.

## Goals

- Every MCP message that leaves or enters the process is produced or parsed by the SDK.
- Behaviour visible to clients over `stdio` and Streamable HTTP is unchanged except as
  listed under "Client-visible changes".
- The hand-rolled protocol files are deleted, not kept alongside.
- Existing tests that cover protocol behaviour are replaced by tests that drive the SDK's
  `Client` against the real server, so coverage is end-to-end rather than unit-level.

## Non-goals

- No new tools, no tool behaviour changes, no changes to the AX, screenshot, input or
  shell code.
- No change to the launchd install/uninstall flow or to the `connect` bridge's design.
- No app bundle, no menu bar UI, no code-signing changes. Those are separate topics.
- Not yet using elicitation, sampling, resources or server-initiated notifications. See
  decision 2 for why that waits.

## Anti-goals

- A thin wrapper that re-implements SDK types "to keep the old API". If the SDK has a
  type for it, use the SDK's type. Pre-processing request bodies in `HTTPApp` to paper
  over SDK behaviour counts as such a wrapper.
- Pinning the SDK to a git branch. Pin a released tag. (The SDK's own transitive
  `swift-docc-plugin` branch pin is accepted; see decision 11.)
- Breaking `desktop-mcp connect`, which Claude Desktop depends on.

## Decided by the project owner

1. **The legacy SSE transport is removed.** The `/sse` + `/messages/` endpoints
   (protocol 2024-11-05) go away. `sse` is no longer an accepted value for `--transport`
   anywhere: the `serve` command, the `install` command, `Config.transports`, and the
   client-config printout. The SDK does not implement this transport and no supported
   client needs it. `connect` already refused it.

2. **Streamable HTTP stays stateless.** Use `StatelessHTTPServerTransport`. Stateful was
   considered and rejected for this migration because SDK 0.12.1's
   `StatefulHTTPServerTransport` (a) makes every request on an evicted or restarted
   session a 404 that the `connect` bridge cannot recover from without re-sending
   `initialize`, (b) stores every outgoing message per session with no API to disable or
   prune the event store, and (c) keeps running a tool after the client disconnects.
   Server-initiated messages are revisited when the SDK exposes control over its event
   store.

## Decided in design (owner reviewed; override if wrong)

3. **One `Server` and one `StatelessHTTPServerTransport` per HTTP request.** The
   stateless transport keys its response waiters by JSON-RPC request id. Two independent
   clients both sending `id: 1` concurrently through one shared transport would collide,
   so the transport is not shared: `HTTPApp` builds a `Server` from the shared tool
   registry, starts it on a fresh transport, handles the one request, and stops both. This
   also matches today's semantics, where each incoming message is dispatched independently
   and `clientInfo` is only known when `initialize` is in the same body. The SDK `Server`
   runs in non-strict mode so `tools/call` without a preceding `initialize` keeps working.
   The builder measures the per-request cost once; if it is not negligible, pool
   transports per connection instead, never globally.

4. **The NIO server stays. Only `HTTPApp` changes.** `NIOHTTPServer.swift` keeps its
   `bind` / `serve` / `write` surface. Inside `HTTPApp`, after the existing middleware
   (OPTIONS, CORS, IP allowlist, bearer/OAuth auth, OAuth routes, `/health`), the `/mcp`
   route converts desktop-mcp's `HTTPRequest` into `MCP.HTTPRequest`, calls the
   transport, and converts `MCP.HTTPResponse` back. Name clashes between desktop-mcp's
   `HTTPRequest`/`HTTPResponse` and the SDK's are resolved by module-qualifying the SDK
   types; renaming desktop-mcp's types is acceptable if it reads better.

5. **Client disconnect still cancels the tool call.** Today a client hanging up mid-call
   cancels the handler task and, for `Shell`, kills the child process
   (`HTTPAppTests.testStreamableDisconnectCancelsTheToolCall`). The SDK does not do this
   on its own. When the NIO side observes the connection closing, `HTTPApp` must stop the
   request's `Server` and transport so the SDK cancels the pending handler task. The
   existing test is kept and must pass.

6. **Existing auth stays in front of the transport.** The SDK ships
   `BearerTokenValidator`, `ProtectedResourceMetadataValidator` and `OriginValidator` as
   pipeline validators. They are not adopted in this migration; desktop-mcp's
   `authenticate` and `OAuthService` keep running before the request reaches the
   transport. The SDK transport is constructed with an explicit validation pipeline of
   `AcceptHeaderValidator(mode: .jsonOnly)`, `ContentTypeValidator` and
   `ProtocolVersionValidator` only. **Do not use the SDK's default pipeline**: it includes
   `OriginValidator.localhost()`, which would reject the remote hosts desktop-mcp supports
   with TLS and an auth key.

7. **`stdio` uses the SDK's `StdioTransport`.** `MCPServer.runStdio`, `MCPSession` and
   the custom newline splitter go away. The process must still exit when stdin closes or
   when stdout is closed by the client. The SDK transport ends its receive loop on stdin
   EOF but swallows EPIPE on stdout, so the builder wraps stdout-closed detection and
   makes shutdown cancellation-aware for SIGTERM, rather than keeping the old session
   code.

8. **Tool definitions keep their shape; the schema type changes.** `DesktopMCP.Tool`
   (name, description, `inputSchema`, `annotations`, async handler returning content)
   remains the registration unit. `inputSchema` becomes `MCP.Value`; the schema helper
   functions in `Tools.swift` are kept and retyped. `ToolContent` becomes
   `MCP.Tool.Content`. `ToolArguments` keeps its typed accessors over `[String: Value]`.
   `MCPServer` as an actor is replaced by a small registry (tools in registration order,
   exclude-by-name) plus a function that builds an SDK `Server` with `ListTools` and
   `CallTool` handlers installed. Tool errors thrown by a handler become a
   `CallTool.Result` with `isError: true` and the message as text; a cancelled request
   still produces today's "Cancelled." `isError` result, because the SDK sends nothing for
   a cancelled handler and the HTTP response would otherwise never complete.
   `resources/list` and `prompts/list` get empty handlers so they return empty lists as
   today; the capabilities are not advertised.

   `JSONValue.swift` and `JSONRPC.swift` are deleted as protocol code. `JSONCoding` (the
   Codable encoder/decoder helper) is used by Analytics, OAuth, Desktop,
   LaunchAgentCommands and the HTTP response helpers and stays. If `JSONValue` is still
   needed by non-protocol code after the migration, retype that code to `MCP.Value`
   instead of keeping two value types.

9. **Analytics hooks move to SDK extension points.** `clientInfo` for PostHog comes from
   the `initializeHook` passed to `Server.start(transport:initializeHook:)`. Tool success
   and failure observation wraps the `CallTool` handler. `ToolCallObserver` stays as the
   protocol `PostHogAnalytics` conforms to.

10. **Per-request concurrency and cancellation come from the SDK.** The SDK runs each
    request in its own task and honours `notifications/cancelled`; a long `Wait` or
    `Shell` call must still not block `ping`. Keep the test that proves this.

11. **`connect` keeps its design; mechanical edits are allowed.** It forwards bytes
    between stdio and the HTTP endpoint and does not parse MCP. Allowed changes: retyping
    its JSON handling if `JSONValue` goes away, and dropping empty SSE `data:` payloads
    should the server ever send them. Its tests stay, pointed at the new server. With the
    stateless transport the server answers POSTs with plain JSON, which the bridge already
    handles.

12. **Toolchain floor rises to swift-tools-version 6.1.** SDK 0.12.1 declares tools 6.1,
    which needs Xcode 16.3 or later. Xcode 16.4 (Swift 6.1.2) and Xcode 27.1 are
    installed. `Package.swift` moves to `// swift-tools-version:6.1`. The
    `swift-collections` cap `"1.1.0"..<"1.3.0"` **stays**: it is what lets Xcode 16.4's
    SwiftPM load the graph. Its comment is rewritten to say so; `macos/scripts/build-release.sh`
    exists and the comment's reference to it is correct. The macOS 13 deployment target
    is unchanged; the SDK supports it.

13. **SDK version and transitive dependencies.** Depend on
    `https://github.com/modelcontextprotocol/swift-sdk.git` `from: "0.12.1"`. Transitive
    additions are `swift-system`, `swift-log`, `mattt/eventsource`, and, through the
    SDK's own manifest, `swift-docc-plugin` on `branch: "main"` plus
    `swift-docc-symbolkit`. The branch pin is the SDK's, not ours, and is accepted; note
    it in the `Package.swift` comment. All of it compiles into the binary; nothing is
    installed at runtime. The builder confirms the graph resolves with Xcode 16.4 before
    writing any code.

14. **Protocol version list.** `MCPServer.supportedProtocolVersions` is deleted. The SDK
    negotiates versions.

15. **Old `sse` settings do not crash existing installs.** A `config.toml` with
    `transport = "sse"` or a Launch Agent plist installed with `--transport sse` is
    treated as `streamable-http` with a warning on stderr. A hard error would crash-loop an
    already installed agent under `KeepAlive`. The CLI's `--transport` option itself does
    not accept `sse`; only stored settings get the lenient treatment. `install` rewrites
    the plist, so a reinstall clears the warning.

## Client-visible changes forced by the SDK (accepted; document in README)

- POST `/mcp` always answers with `application/json`. Today a request with
  `Accept: text/event-stream` got an SSE response; that is gone. A request whose `Accept`
  does not include `application/json` gets 406.
- JSON-RPC batch bodies get 400. The SDK's stateless transport only accepts a single
  message, and batches were removed from the MCP specification in 2025-06-18 anyway.
- `GET /mcp` and `DELETE /mcp` return 405 with `Allow: POST`, as today.
- A string argument that matches the `data:` URL pattern is decoded by `MCP.Value` into
  `.data`, and `ToolArguments` rebuilds it as plain `data:<type>,<text>` (base64 when the bytes
  are not UTF-8). Base64 or percent-escaped data URLs therefore change: `Type` with `text`
  `data:text/html;base64,PGgxPkhpPC9oMT4=` types `data:text/html,<h1>Hi</h1>`. Accepted;
  no code change.

## Constraints

- Swift 6 language mode, strict concurrency. The SDK is actor-based and `Sendable`
  throughout; nothing in the migration may introduce `@unchecked Sendable` to bridge it.
- No runtime dependencies for users: a single executable remains the deliverable.
- Telemetry stays opt-in exactly as today; the PostHog key and event names do not change.
  Tests run with `ANONYMIZED_TELEMETRY=false` and a scratch `HOME`.
- No commits or pushes as part of this work unless the owner asks.
- Tests run with `swift test` from the repository root and must pass with Xcode 16.4's
  toolchain (`DEVELOPER_DIR=/Applications/Xcode-16.4.app`).

## What is left to the builder

- File layout of the new protocol glue (one file or several, names).
- Whether desktop-mcp's `HTTPRequest`/`HTTPResponse` are renamed or module-qualified.
- How `ToolArguments` validates types against the schema (today it throws typed errors;
  keep equivalent messages).
- Logging: the SDK takes a `swift-log` `Logger`; wire it to the existing `log()` helper
  or leave it no-op, builder's choice, as long as `--debug` still shows request traffic.
- README edits: the transport table loses the SSE row, the install/serve examples drop
  `sse`, and the Streamable HTTP section describes the JSON-only response.
- The contract tests (`ContractTests.swift`, `spec/tools.json`) are rewritten against the
  SDK `Client` in whatever form keeps them readable.

## Verification the builder must show

- `swift build` and `swift test` pass with `DEVELOPER_DIR=/Applications/Xcode-16.4.app`.
- An end-to-end test using the SDK `Client` with `HTTPClientTransport` against the NIO
  server: initialize, list tools, call `Wait`, call a tool that errors and see
  `isError: true`.
- An end-to-end stdio test using the SDK `Client` against an in-process `StdioTransport`
  over pipe file descriptors. Spawning the real executable is not required.
- A test that a `ping` completes while a long tool call is in flight, and that
  `notifications/cancelled` stops the in-flight call and yields the "Cancelled." result.
- The existing disconnect test: a client that hangs up mid-call causes the `Shell` child
  process to be killed.
- `desktop-mcp connect` against a running server still round-trips `initialize` and
  `tools/call`, one line per response, notifications silent.
- A config file with `transport = "sse"` serves as streamable-http with a warning.
- `grep -rn "sse" macos/Sources` returns nothing transport-related.
