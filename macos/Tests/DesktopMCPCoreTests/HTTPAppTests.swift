import CryptoKit
import struct MCP.ToolListChangedNotification
import enum MCP.Value
import XCTest
@testable import DesktopMCPCore

final class HTTPAppTests: XCTestCase {
    func app(authKey: String? = nil, allowlist: [String]? = nil, oauth: OAuthService? = nil,
             registry: ToolRegistry? = nil, configure: (inout HTTPApp.Settings) -> Void = { _ in }) async throws -> HTTPApp {
        var settings = HTTPApp.Settings(authKey: authKey, ipAllowlist: try allowlist.map(IPNetwork.parseAllowlist), oauth: oauth)
        configure(&settings)
        let registry = if let registry { registry } else { await DesktopMCP.makeRegistry() }
        return HTTPApp(registry: registry, settings: settings)
    }

    func post(_ path: String, _ body: String, headers: [(String, String)] = [], remote: String? = "127.0.0.1") -> HTTPRequest {
        HTTPRequest(method: "POST", target: path, headers: [("Content-Type", "application/json")] + headers,
                    body: Data(body.utf8), remoteAddress: remote)
    }

    static let ping = #"{"jsonrpc":"2.0","id":1,"method":"ping"}"#
    static let json = [("Accept", "application/json")]

    // MARK: Streamable HTTP

    func testStreamableAnswersJSON() async throws {
        let app = try await app()
        for path in ["/mcp", "/mcp/"] {
            let response = await app.handle(post(path, Self.ping, headers: Self.json))
            XCTAssertEqual(response.status, 200, path)
            XCTAssertEqual(response.header("content-type"), "application/json", path)
            XCTAssertEqual(try JSON.parse(response.bodyText!)["result"], .object([:]), path)
        }
    }

    func testStreamableAcceptAndMethodRules() async throws {
        let app = try await app()
        let sseOnly = await app.handle(post("/mcp", Self.ping, headers: [("Accept", "text/event-stream")]))
        XCTAssertEqual(sseOnly.status, 406)
        let wrongType = await app.handle(HTTPRequest(method: "POST", target: "/mcp",
                                                     headers: [("Content-Type", "text/plain")] + Self.json,
                                                     body: Data(Self.ping.utf8)))
        XCTAssertEqual(wrongType.status, 415)
        // Without a session id there is no stream to open and nothing to delete.
        let get = await app.handle(HTTPRequest(method: "GET", target: "/mcp", headers: []))
        XCTAssertEqual(get.status, 405)
        XCTAssertEqual(get.header("allow"), "POST")
        XCTAssertEqual(try JSON.parse(get.bodyText!)["error"]?["code"], -32600)
        let delete = await app.handle(HTTPRequest(method: "DELETE", target: "/mcp", headers: []))
        XCTAssertEqual(delete.status, 405)
        let missing = await app.handle(HTTPRequest(method: "GET", target: "/sse", headers: []))
        XCTAssertEqual(missing.status, 404)
    }

    func testStreamableNotificationsAndBadBodies() async throws {
        let app = try await app()
        let notification = await app.handle(post("/mcp", #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#,
                                                 headers: Self.json))
        XCTAssertEqual(notification.status, 202)
        let bad = await app.handle(post("/mcp", "{nope", headers: Self.json))
        XCTAssertEqual(bad.status, 400)
        let empty = await app.handle(post("/mcp", "[]", headers: Self.json))
        XCTAssertEqual(empty.status, 400)
        let batch = await app.handle(post("/mcp", "[\(Self.ping),{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}]",
                                          headers: Self.json))
        XCTAssertEqual(batch.status, 400)
    }

    func testStreamableDisconnectCancelsTheToolCall() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let app = try await app(registry: registry)
        let body = #"{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"Shell","arguments":{"command":"sleep 2; echo late"}}}"#
        // The socket layer runs `handle` in a task and cancels it when the peer closes.
        let call = post("/mcp", body, headers: Self.json)
        let request = Task { await app.handle(call) }
        try await Task.sleep(for: .milliseconds(200))
        let cancelled = Date()
        request.cancel()
        _ = await request.value
        XCTAssertLessThan(Date().timeIntervalSince(cancelled), 1)
        // The call's outcome lands within a second, long before `sleep 2` would have ended. Shell
        // turns a cancelled command into its result ("Command cancelled"), so it reports success.
        let deadline = Date().addingTimeInterval(1)
        while await observer.events.isEmpty, Date() < deadline {
            try await Task.sleep(for: .milliseconds(20))
        }
        let events = await observer.events
        XCTAssertEqual(events, ["ok Shell -"])
        XCTAssertLessThan(Date().timeIntervalSince(cancelled), 1.5)
    }

    func testConcurrentClientsWithTheSameIdGetTheirOwnResponses() async throws {
        let app = try await app()
        let wait = #"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":1}}}"#
        let call = post("/mcp", wait, headers: Self.json)
        let waiting = Task { await app.handle(call) }
        try await Task.sleep(for: .milliseconds(100))
        let pingStarted = Date()
        let ping = await app.handle(post("/mcp", Self.ping, headers: Self.json))
        XCTAssertLessThan(Date().timeIntervalSince(pingStarted), 0.5)
        XCTAssertEqual(try JSON.parse(ping.bodyText!)["result"], .object([:]))
        let waited = await waiting.value
        XCTAssertEqual(waited.status, 200)
        let result = try JSON.parse(waited.bodyText!)["result"]
        XCTAssertEqual(result?["isError"], false, waited.bodyText!)
        let text = result?["content"]?.arrayValue?.first?["text"]?.stringValue
        XCTAssertTrue(text?.hasPrefix("Waited for 1") == true, waited.bodyText!)
    }

    func testHealthStaysPublic() async throws {
        let app = try await app(authKey: "s3cret")
        // /health skips auth (and the allowlist); no route answers it yet, so it is a 404, not a 401.
        let health = await app.handle(HTTPRequest(method: "GET", target: "/health", headers: []))
        XCTAssertNotEqual(health.status, 401)
    }

    // MARK: Sessions

    static let sse = "application/json, text/event-stream"
    static let initializeBody = #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"#

    func sendInitialize(_ app: HTTPApp, accept: String = sse) async -> HTTPResponse {
        await app.handle(post("/mcp", Self.initializeBody, headers: [("Accept", accept)]))
    }

    /// A session created by `initialize`, its stream read to the end.
    func session(_ app: HTTPApp) async throws -> String {
        let response = await sendInitialize(app)
        XCTAssertEqual(response.status, 200)
        let id = try XCTUnwrap(response.header("mcp-session-id"))
        _ = try await sseData(response)
        return id
    }

    func inSession(_ id: String, _ body: String, method: String = "POST", extra: [(String, String)] = []) -> HTTPRequest {
        HTTPRequest(method: method, target: "/mcp",
                    headers: [("Content-Type", "application/json"), ("Accept", Self.sse), ("Mcp-Session-Id", id)] + extra,
                    body: Data(body.utf8), remoteAddress: "127.0.0.1")
    }

    static func call(_ id: Int, _ tool: String, _ arguments: String) -> String {
        #"{"jsonrpc":"2.0","id":\#(id),"method":"tools/call","params":{"name":"\#(tool)","arguments":\#(arguments)}}"#
    }

    func testInitializeWithSSEAcceptCreatesASession() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let response = await sendInitialize(app)
        XCTAssertEqual(response.status, 200)
        XCTAssertEqual(response.header("content-type"), "text/event-stream")
        XCTAssertNotNil(response.header("mcp-session-id"))
        let payloads = try await sseData(response)
        XCTAssertEqual(payloads.count, 1)
        XCTAssertEqual(payloads.first?["result"]?["serverInfo"]?["name"], "desktop-mcp")
        let count = await app.sessions.count
        XCTAssertEqual(count, 1)
    }

    func testInitializeWithJSONAcceptStaysStateless() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let response = await sendInitialize(app, accept: "application/json")
        XCTAssertEqual(response.status, 200)
        XCTAssertEqual(response.header("content-type"), "application/json")
        XCTAssertNil(response.header("mcp-session-id"))
        let count = await app.sessions.count
        XCTAssertEqual(count, 0)
    }

    func testRequestsWithoutASessionStayStateless() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let response = await app.handle(post("/mcp", Self.ping, headers: [("Accept", Self.sse)]))
        XCTAssertEqual(response.status, 200)
        XCTAssertEqual(response.header("content-type"), "application/json")
        XCTAssertNil(response.header("mcp-session-id"))
        let count = await app.sessions.count
        XCTAssertEqual(count, 0)
    }

    func testUnknownSessionIdIs404() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let response = await app.handle(inSession("nope", Self.ping))
        XCTAssertEqual(response.status, 404)
        let message = try JSON.parse(response.bodyText!)["error"]?["message"]?.stringValue
        XCTAssertTrue(message?.contains("Session not found") == true, response.bodyText!)
    }

    func testGetAndDeleteWithoutASessionAre405() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        for method in ["GET", "DELETE"] {
            let response = await app.handle(HTTPRequest(method: method, target: "/mcp", headers: [("Accept", Self.sse)]))
            XCTAssertEqual(response.status, 405, method)
            XCTAssertEqual(response.header("allow"), "POST", method)
        }
    }

    func testSessionRoundTripAndDelete() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let app = try await app(registry: registry)
        defer { Task { await app.shutdown() } }
        let id = try await session(app)

        let waited = try await sseData(await app.handle(inSession(id, Self.call(2, "Wait", #"{"duration":0}"#))))
        XCTAssertEqual(waited.count, 1)
        XCTAssertEqual(waited.first?["result"]?["isError"], false)
        let events = await observer.events
        XCTAssertEqual(events, ["ok Wait t"])

        let deleted = await app.handle(inSession(id, "", method: "DELETE"))
        XCTAssertEqual(deleted.status, 200)
        let after = await app.handle(inSession(id, Self.ping))
        XCTAssertEqual(after.status, 404)
        let count = await app.sessions.count
        XCTAssertEqual(count, 0)
    }

    func testReinitializeOnALiveSessionIs400AndKeepsIt() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        let again = await app.handle(inSession(id, Self.initializeBody))
        XCTAssertEqual(again.status, 400)
        let ping = await app.handle(inSession(id, Self.ping))
        XCTAssertEqual(ping.status, 200)
        _ = try await sseData(ping)
    }

    func testBatchesAreRejectedInSessions() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        let batch = await app.handle(inSession(id, "[\(Self.ping),{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}]"))
        XCTAssertEqual(batch.status, 400)
    }

    func testProtocolVersionHeaderIsNotValidated() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let future = ("MCP-Protocol-Version", "2026-07-28")
        let stateless = await app.handle(post("/mcp", Self.ping, headers: Self.json + [future]))
        XCTAssertEqual(stateless.status, 200, stateless.bodyText ?? "")
        let id = try await session(app)
        let inSession = await app.handle(inSession(id, Self.ping, extra: [future]))
        XCTAssertEqual(inSession.status, 200, inSession.bodyText ?? "")
        _ = try await sseData(inSession)
    }

    func testLastEventIDIsStripped() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        // A resume would fail (400) or replay a finished POST stream; this must be the standalone stream.
        let get = await app.handle(inSession(id, "", method: "GET", extra: [("Last-Event-ID", "1")]))
        XCTAssertEqual(get.status, 200, get.bodyText ?? "")
        XCTAssertEqual(get.header("content-type"), "text/event-stream")
        let second = await app.handle(inSession(id, "", method: "GET"))
        XCTAssertEqual(second.status, 409)
    }

    func testIdleSessionsAreEvicted() async throws {
        let app = try await app { $0.sessionIdleTimeout = 0.2 }
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        try await Task.sleep(for: .milliseconds(500))
        await app.sessions.sweep(now: Date())
        let ping = await app.handle(inSession(id, Self.ping))
        XCTAssertEqual(ping.status, 404)
    }

    func testAnOpenGetIsNotActivity() async throws {
        let app = try await app { $0.sessionIdleTimeout = 0.2 }
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        try await Task.sleep(for: .milliseconds(150))
        let get = await app.handle(inSession(id, "", method: "GET"))
        XCTAssertEqual(get.status, 200)
        try await Task.sleep(for: .milliseconds(100))
        // 250 ms since the last POST, 100 ms since the GET: idle.
        await app.sessions.sweep(now: Date())
        let ping = await app.handle(inSession(id, Self.ping))
        XCTAssertEqual(ping.status, 404)
    }

    func testSweepSkipsASessionThatBecameBusy() async throws {
        // The default timeout keeps the real sweeper out of the way; `now` is pushed past it.
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        // Idle when a sweep took its snapshot, then a tool call started before the sweep got to it.
        let wait = await app.handle(inSession(id, Self.call(2, "Wait", #"{"duration":30}"#)))
        XCTAssertEqual(wait.status, 200)
        let ended = await app.sessions.endIfIdle(id, now: Date().addingTimeInterval(3600))
        XCTAssertFalse(ended)
        let count = await app.sessions.count
        XCTAssertEqual(count, 1)
    }

    func testTheSweeperEvictsOnItsOwn() async throws {
        let app = try await app { $0.sessionIdleTimeout = 0.2 }
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        try await Task.sleep(for: .milliseconds(600))
        let ping = await app.handle(inSession(id, Self.ping))
        XCTAssertEqual(ping.status, 404)
    }

    func testAReopenedGetResumesTheStandaloneStream() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        let first = await app.handle(inSession(id, "", method: "GET"))
        guard case .stream(let firstBody) = first.body else { return XCTFail("not a stream") }
        let reader = Task { for await _ in firstBody {} }
        try await Task.sleep(for: .milliseconds(100))
        reader.cancel()
        try await Task.sleep(for: .milliseconds(100))

        // The SDK still holds the first stream; without a resume this would be 409 for good.
        let second = await app.handle(inSession(id, "", method: "GET", extra: [("Last-Event-ID", "bogus")]))
        XCTAssertEqual(second.status, 200, second.bodyText ?? "")
        XCTAssertEqual(second.header("content-type"), "text/event-stream")
        guard case .stream(let secondBody) = second.body else { return XCTFail("not a stream") }
        let server = await app.sessionServer(id: id)
        try await XCTUnwrap(server).notify(ToolListChangedNotification.message())
        let received = Task { () -> String? in
            var text = ""
            for await chunk in secondBody {
                text += chunk
                if let payload = SSE.dataPayloads(text).first(where: { $0.contains("list_changed") }) { return payload }
            }
            return nil
        }
        let timeout = Task { try await Task.sleep(for: .seconds(2)); received.cancel() }
        let payload = await received.value
        timeout.cancel()
        XCTAssertEqual(try payload.map { try JSON.parse($0)["method"] }, "notifications/tools/list_changed")
    }

    func testEventsStillBufferedWhenAGetReaderLeavesAreReplayed() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        let first = await app.handle(inSession(id, "", method: "GET"))
        guard case .stream(let firstBody) = first.body else { return XCTFail("not a stream") }
        let (primed, primedContinuation) = AsyncStream<Void>.makeStream()
        let reader = Task {
            var iterator = firstBody.makeAsyncIterator()
            _ = await iterator.next() // the priming event
            primedContinuation.yield()
            // Stops reading, like a socket writer stuck on a slow client...
            try? await Task.sleep(for: .seconds(60))
            // ...then asks again once cancelled, as NIOHTTPServer's consumer does.
            _ = await iterator.next()
        }
        for await _ in primed { break }
        let sessionServer = await app.sessionServer(id: id)
        let server = try XCTUnwrap(sessionServer)
        for _ in 0..<3 { try await server.notify(ToolListChangedNotification.message()) }
        try await Task.sleep(for: .milliseconds(100))
        reader.cancel()
        await reader.value
        try await Task.sleep(for: .milliseconds(100))

        // None of the three left the server: the re-opened GET must replay all of them.
        let second = await app.handle(inSession(id, "", method: "GET"))
        XCTAssertEqual(second.status, 200, second.bodyText ?? "")
        guard case .stream(let secondBody) = second.body else { return XCTFail("not a stream") }
        let received = Task { () -> Int in
            var text = ""
            func count() -> Int { SSE.dataPayloads(text).filter { $0.contains("list_changed") }.count }
            for await chunk in secondBody {
                text += chunk
                if count() >= 3 { break }
            }
            return count()
        }
        let timeout = Task { try await Task.sleep(for: .seconds(2)); received.cancel() }
        let count = await received.value
        timeout.cancel()
        XCTAssertEqual(count, 3)
    }

    func testCancelledWhileTheSessionAnswersEndsTheCall() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let app = try await app(registry: registry)
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        // The socket layer cancels the task running `handle` when the client goes away before
        // the response is written.
        let request = inSession(id, Self.call(2, "Wait", #"{"duration":30}"#))
        let call = Task { await app.handle(request) }
        call.cancel()
        let response = await call.value
        XCTAssertEqual(response.status, 499)
        try await observer.waitFor(error: "Cancelled.", timeout: 2)
        // The request no longer counts as in flight: the session can go idle.
        let ended = await app.sessions.endIfIdle(id, now: Date().addingTimeInterval(3600))
        XCTAssertTrue(ended)
    }

    func testByteBudgetEndsTheSession() async throws {
        let app = try await app { $0.sessionByteBudget = 200 }
        defer { Task { await app.shutdown() } }
        // The initialize reply alone (instructions included) passes 200 bytes: the session
        // serves that reply to the end, then ends.
        let id = try await session(app)
        let ping = await app.handle(inSession(id, Self.ping))
        XCTAssertEqual(ping.status, 404)
    }

    func testTheSweepEndsASessionOverBudgetFromGetTrafficAlone() async throws {
        let app = try await app { $0.sessionByteBudget = 20_000 }
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        let get = await app.handle(inSession(id, "", method: "GET"))
        XCTAssertEqual(get.status, 200)
        // Server-initiated messages only: no request ends, so only the sweep can notice.
        let sessionServer = await app.sessionServer(id: id)
        let server = try XCTUnwrap(sessionServer)
        for _ in 0..<400 { try await server.notify(ToolListChangedNotification.message()) }
        var count = await app.sessions.count
        XCTAssertEqual(count, 1)
        // Not idle by the clock (the timeout is 30 minutes): ended for the budget.
        await app.sessions.sweep(now: Date())
        count = await app.sessions.count
        XCTAssertEqual(count, 0)
    }

    func testSessionCapEvictsIdleLRUAndRefusesWhenBusy() async throws {
        let app = try await app { $0.maxSessions = 2 }
        defer { Task { await app.shutdown() } }
        let first = try await session(app)
        let second = try await session(app)
        let third = try await session(app)
        let gone = await app.handle(inSession(first, Self.ping))
        XCTAssertEqual(gone.status, 404)

        // Both remaining sessions are busy: none may be evicted for a new one.
        // A session answers with a stream at once; the call runs until the stream ends.
        var waits: [HTTPResponse] = []
        for id in [second, third] {
            waits.append(await app.handle(inSession(id, Self.call(2, "Wait", #"{"duration":30}"#))))
        }
        XCTAssertEqual(waits.map(\.status), [200, 200])
        let refused = await sendInitialize(app)
        XCTAssertEqual(refused.status, 503)
        let count = await app.sessions.count
        XCTAssertEqual(count, 2)
    }

    func testDisconnectInsideASessionCancelsOnlyThatCall() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let app = try await app(registry: registry)
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        // The dropped call is a Wait: its reply never reaches anyone, and Wait reports its
        // cancellation to the observer as "Cancelled." (Shell's "Command cancelled" is a result
        // the observer does not see).
        let dropped = await app.handle(inSession(id, Self.call(2, "Wait", #"{"duration":30}"#)))
        let kept = await app.handle(inSession(id, Self.call(3, "Shell", #"{"command":"sleep 2; echo late"}"#)))
        // The socket layer consumes the body in a task and cancels it when the peer closes.
        guard case .stream(let droppedBody) = dropped.body else { return XCTFail("not a stream") }
        let reader = Task { for await _ in droppedBody {} }
        let keptPayloads = Task { try await sseData(kept, timeout: 10) }
        try await Task.sleep(for: .milliseconds(200))
        let cancelled = Date()
        reader.cancel()

        let deadline = Date().addingTimeInterval(1)
        while await observer.events.isEmpty, Date() < deadline {
            try await Task.sleep(for: .milliseconds(20))
        }
        let events = await observer.events
        XCTAssertEqual(events, ["fail Wait t"])
        let errors = await observer.errors
        XCTAssertEqual(errors, ["Cancelled."])
        XCTAssertLessThan(Date().timeIntervalSince(cancelled), 1.5)

        let payloads = try await keptPayloads.value
        let text = payloads.first?["result"]?["content"]?.arrayValue?.first?["text"]?.stringValue
        XCTAssertTrue(text?.contains("late") == true, "\(payloads)")
        XCTAssertGreaterThan(Date().timeIntervalSince(cancelled), 1.5)
    }

    func testCancelledNotificationInsideASession() async throws {
        let app = try await app()
        defer { Task { await app.shutdown() } }
        let id = try await session(app)
        let wait = await app.handle(inSession(id, Self.call(7, "Wait", #"{"duration":30}"#)))
        let payloads = Task { try await sseData(wait) }
        try await Task.sleep(for: .milliseconds(200))
        let sent = Date()
        let cancel = await app.handle(inSession(id, #"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}"#))
        XCTAssertEqual(cancel.status, 202)
        let result = try await payloads.value.first?["result"]
        XCTAssertLessThan(Date().timeIntervalSince(sent), 2)
        XCTAssertEqual(result?["isError"], true)
        XCTAssertEqual(result?["content"]?.arrayValue?.first?["text"], "Cancelled.")
    }

    // MARK: Middleware

    func testBearerAuth() async throws {
        let app = try await app(authKey: "s3cret")
        let none = await app.handle(post("/mcp", Self.ping))
        XCTAssertEqual(none.status, 401)
        XCTAssertEqual(none.header("www-authenticate"), "Bearer")
        let wrong = await app.handle(post("/mcp", Self.ping, headers: [("Authorization", "Bearer nope")]))
        XCTAssertEqual(wrong.status, 401)
        XCTAssertEqual(try JSON.parse(wrong.bodyText!)["error"], "Invalid authentication token")
        let right = await app.handle(post("/mcp", Self.ping, headers: [("Authorization", "Bearer s3cret"), ("Accept", "application/json")]))
        XCTAssertEqual(right.status, 200)
    }

    func testOptionsAndCORS() async throws {
        let app = try await app()
        let preflight = await app.handle(HTTPRequest(method: "OPTIONS", target: "/mcp", headers: []))
        XCTAssertEqual(preflight.status, 200)
        XCTAssertEqual(preflight.header("access-control-allow-origin"), "*")

        // A page served from this machine may call the server; its origin is echoed back.
        let local = await app.handle(post("/mcp", Self.ping, headers: [("Origin", "http://localhost:3000"), ("Accept", "application/json")]))
        XCTAssertEqual(local.status, 200)
        XCTAssertEqual(local.header("access-control-allow-origin"), "http://localhost:3000")

        // Any other website is refused before auth or routing, preflight included, even though
        // the server itself requires no auth on loopback.
        let foreign = await app.handle(post("/mcp", Self.ping, headers: [("Origin", "https://x.example")]))
        XCTAssertEqual(foreign.status, 403)
        XCTAssertNil(foreign.header("access-control-allow-origin"))
        let foreignPreflight = await app.handle(HTTPRequest(method: "OPTIONS", target: "/mcp", headers: [("Origin", "https://x.example")]))
        XCTAssertEqual(foreignPreflight.status, 403)
        let nullOrigin = await app.handle(post("/mcp", Self.ping, headers: [("Origin", "null")]))
        XCTAssertEqual(nullOrigin.status, 403)
    }

    func testQueryDecodesFormEncoding() {
        let request = HTTPRequest(method: "GET", target: "/oauth/authorize?state=a+b%2Bc&x=%E2%9C%93", headers: [])
        XCTAssertEqual(request.query["state"], "a b+c")
        XCTAssertEqual(request.query["x"], "✓")
    }

    func testOAuthMetadataAdvertisesTheHostClientsUsed() async {
        let wildcard = OAuthService(issuer: "https://0.0.0.0:8000", clientID: "c", clientSecret: "s")
        let seen = await wildcard.handle(HTTPRequest(method: "GET", target: "/.well-known/oauth-authorization-server",
                                                     headers: [("Host", "mac.local:8000")]))
        XCTAssertEqual(try JSON.parse(seen!.bodyText!)["token_endpoint"], "https://mac.local:8000/oauth/token")

        let fixed = OAuthService(issuer: "https://127.0.0.1:8000", clientID: "c", clientSecret: "s")
        let kept = await fixed.handle(HTTPRequest(method: "GET", target: "/.well-known/oauth-authorization-server",
                                                  headers: [("Host", "mac.local:8000")]))
        XCTAssertEqual(try JSON.parse(kept!.bodyText!)["issuer"], "https://127.0.0.1:8000")
    }

    func testIPAllowlist() async throws {
        let app = try await app(allowlist: ["10.0.0.0/8"])
        let outside = await app.handle(post("/mcp", Self.ping, remote: "192.168.0.2"))
        XCTAssertEqual(outside.status, 403)
        XCTAssertEqual(try JSON.parse(outside.bodyText!)["error"], "Forbidden: 192.168.0.2 is not in the IP allowlist")
        let v5 = await app.handle(post("/mcp", Self.ping, remote: nil)).status
        XCTAssertEqual(v5, 403)
        let v6 = await app.handle(post("/mcp", Self.ping, headers: [("Accept", "application/json")], remote: "10.9.9.9")).status
        XCTAssertEqual(v6, 200)
    }

    // MARK: OAuth

    func testOAuthAuthorizationCodeFlowWithPKCE() async throws {
        let oauth = OAuthService(issuer: "http://127.0.0.1:8000", clientID: "cid", clientSecret: "csecret")
        let app = try await app(authKey: "static", oauth: oauth)

        let metadata = await app.handle(HTTPRequest(method: "GET", target: "/.well-known/oauth-authorization-server", headers: []))
        XCTAssertEqual(try JSON.parse(metadata.bodyText!)["token_endpoint"], "http://127.0.0.1:8000/oauth/token")
        let v7 = await app.handle(HTTPRequest(method: "POST", target: "/oauth/register", headers: [])).status
        XCTAssertEqual(v7, 403)

        let verifier = "verifier-0123456789-abcdefghijklmnopqrstuvwxyz"
        let challenge = SecurityUtil.base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
        let redirect = "http://localhost:3333/callback"
        func authorize(_ extra: String = "") async -> HTTPResponse {
            await app.handle(HTTPRequest(method: "GET", target:
                "/oauth/authorize?response_type=code&client_id=cid&redirect_uri=\(redirect)&code_challenge=\(challenge)&code_challenge_method=S256&state=xyz" + extra,
                headers: []))
        }
        let redirected = await authorize()
        XCTAssertEqual(redirected.status, 302)
        let location = try XCTUnwrap(redirected.header("location"))
        XCTAssertTrue(location.hasPrefix("\(redirect)?code="), location)
        XCTAssertTrue(location.hasSuffix("&state=xyz"), location)
        let code = try XCTUnwrap(URLComponents(string: location)?.queryItems?.first { $0.name == "code" }?.value)

        func exchange(_ code: String, verifier: String, secret: String = "csecret") async -> HTTPResponse {
            let form = "grant_type=authorization_code&code=\(code)&client_id=cid&redirect_uri=\(redirect)&code_verifier=\(verifier)&client_secret=\(secret)"
            return await app.handle(HTTPRequest(method: "POST", target: "/oauth/token",
                                                headers: [("Content-Type", "application/x-www-form-urlencoded")], body: Data(form.utf8)))
        }
        let badVerifier = await exchange(code, verifier: "wrong")
        XCTAssertEqual(try JSON.parse(badVerifier.bodyText!)["error_description"], "PKCE verification failed")

        let second = await authorize()
        let code2 = try XCTUnwrap(URLComponents(string: second.header("location")!)?.queryItems?.first { $0.name == "code" }?.value)
        let token = await exchange(code2, verifier: verifier)
        XCTAssertEqual(token.status, 200)
        let accessToken = try XCTUnwrap(try JSON.parse(token.bodyText!)["access_token"]?.stringValue)
        let v8 = await exchange(code2, verifier: verifier).status
        XCTAssertEqual(v8, 400, "codes are single-use")

        let call = await app.handle(post("/mcp", Self.ping, headers: [("Authorization", "Bearer \(accessToken)"), ("Accept", "application/json")]))
        XCTAssertEqual(call.status, 200)
    }

    func testOAuthRejections() async throws {
        let oauth = OAuthService(issuer: "http://127.0.0.1:8000", clientID: "cid", clientSecret: "csecret")
        func authorize(_ query: String) async -> HTTPResponse? {
            await oauth.handle(HTTPRequest(method: "GET", target: "/oauth/authorize?" + query, headers: []))
        }
        let base = "response_type=code&client_id=cid&code_challenge=abc&code_challenge_method=S256"
        let remote = await authorize(base + "&redirect_uri=https://evil.example/cb")
        XCTAssertEqual(remote?.status, 400)
        let plain = await authorize("response_type=code&client_id=cid&code_challenge=abc&code_challenge_method=plain&redirect_uri=http://127.0.0.1/cb")
        XCTAssertEqual(plain?.status, 400)
        let client = await authorize("response_type=code&client_id=other&code_challenge=abc&redirect_uri=http://127.0.0.1/cb")
        XCTAssertEqual(client?.status, 400)
        let token = await oauth.handle(HTTPRequest(method: "POST", target: "/oauth/token", headers: [("Content-Type", "application/json")],
                                                   body: Data(#"{"grant_type":"password"}"#.utf8)))
        XCTAssertEqual(token?.status, 400)
        let v9 = await oauth.validate("made-up")
        XCTAssertFalse(v9)
    }

    func testOAuthTokensExpire() async throws {
        let clock = Clock()
        let oauth = OAuthService(issuer: "http://127.0.0.1:8000", clientID: "cid", clientSecret: "csecret", now: { clock.now })
        let verifier = "v"
        let challenge = SecurityUtil.base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
        let redirected = await oauth.handle(HTTPRequest(method: "GET", target:
            "/oauth/authorize?response_type=code&client_id=cid&redirect_uri=http://127.0.0.1/cb&code_challenge=\(challenge)", headers: []))
        let code = URLComponents(string: redirected!.header("location")!)!.queryItems!.first { $0.name == "code" }!.value!
        let body = #"{"grant_type":"authorization_code","code":"\#(code)","client_id":"cid","redirect_uri":"http://127.0.0.1/cb","code_verifier":"v","client_secret":"csecret"}"#
        let token = await oauth.handle(HTTPRequest(method: "POST", target: "/oauth/token", headers: [("Content-Type", "application/json")], body: Data(body.utf8)))
        let accessToken = try JSON.parse(token!.bodyText!)["access_token"]!.stringValue!
        let v10 = await oauth.validate(accessToken)
        XCTAssertTrue(v10)
        clock.now = clock.now.addingTimeInterval(OAuthService.tokenLifetime + 1)
        let v11 = await oauth.validate(accessToken)
        XCTAssertFalse(v11)
    }
}

struct StreamTimeout: Error, CustomStringConvertible {
    let seconds: TimeInterval
    var description: String { "the stream did not end within \(seconds) s" }
}

/// The non-empty `data` payloads of a `.stream` body, read until the stream ends. A stream
/// still open after `timeout` stops being read (which cancels it) and fails the test.
func sseData(_ response: HTTPResponse, timeout: TimeInterval = 10,
             file: StaticString = #filePath, line: UInt = #line) async throws -> [Value] {
    guard case .stream(let stream) = response.body else {
        XCTFail("not a stream: \(response.status) \(response.bodyText ?? "")", file: file, line: line)
        return []
    }
    let text = try await withThrowingTaskGroup(of: String?.self) { group in
        group.addTask {
            var text = ""
            for await chunk in stream { text += chunk }
            return text
        }
        group.addTask {
            try await Task.sleep(for: .seconds(timeout))
            return nil
        }
        let first = try await group.next() ?? nil
        group.cancelAll()
        return first
    }
    guard let text else {
        XCTFail(StreamTimeout(seconds: timeout).description, file: file, line: line)
        throw StreamTimeout(seconds: timeout)
    }
    return try SSE.dataPayloads(text).map { try JSON.parse($0) }
}

final class Clock: @unchecked Sendable {
    var now = Date()
}
