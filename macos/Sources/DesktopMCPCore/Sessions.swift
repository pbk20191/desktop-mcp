import Foundation
import Logging
import MCP

/// The id a session's transport hands out: chosen by the store before the transport exists, so
/// the store can file the session under it.
struct FixedSessionIDGenerator: SessionIDGenerator {
    let sessionID: String
    func generateSessionID() -> String { sessionID }
}

/// One MCP session: its own SDK server on its own stateful transport, kept between requests.
struct Session {
    let started: StartedServer
    let transport: BudgetedTransport
    let createdAt: Date
    /// When the last `POST` arrived or finished: idle eviction counts from here.
    var lastRequestAt: Date
    /// `POST`s whose response has not ended. A session with any is never evicted.
    var inFlight: Int
    /// Copied from the transport whenever a request ends; an over-budget session takes no new
    /// requests and ends once its last one finishes.
    var overBudget = false
}

/// The live sessions, after the SDK's conformance server (`MCPConformance/Server/HTTPApp.swift`):
/// one `StatefulHTTPServerTransport` and one `Server` per session id. On top of that it evicts
/// idle sessions, caps their number, and ends a session whose transport has sent too much
/// (SDK 0.12.1 keeps every outgoing message for resumability and never prunes).
actor SessionStore {
    private let registry: ToolRegistry
    private let settings: HTTPApp.Settings
    private var sessions: [String: Session] = [:]
    /// Sessions being created: they count toward the cap while their server starts.
    private var creating = 0
    private var sweeper: Task<Void, Never>?
    private var closed = false

    init(registry: ToolRegistry, settings: HTTPApp.Settings) {
        self.registry = registry
        self.settings = settings
    }

    var count: Int { sessions.count }

    /// Starts a session for an `initialize` request and hands the request to it. The
    /// initialize response stream counts as in flight: the caller reports its end through
    /// `requestEnded`. 503 when the cap is reached and every session is busy.
    func create(initialize request: MCP.HTTPRequest) async -> MCP.HTTPResponse {
        guard !closed else { return .error(statusCode: 503, .internalError("The server is shutting down")) }
        while sessions.count + creating >= settings.maxSessions {
            let idle = sessions.filter { $0.value.inFlight == 0 }
            guard let oldest = idle.min(by: { $0.value.lastRequestAt < $1.value.lastRequestAt })?.key else {
                return .error(statusCode: 503, .internalError("Too many sessions"))
            }
            await end(oldest)
        }
        creating += 1
        let id = UUID().uuidString
        let transport = BudgetedTransport(
            wrapping: StatefulHTTPServerTransport(
                sessionIDGenerator: FixedSessionIDGenerator(sessionID: id),
                validationPipeline: StandardValidationPipeline(validators: [
                    // No OriginValidator: `HTTPApp.handle` has checked the origin. No
                    // ProtocolVersionValidator: it rejects versions newer than the SDK knows.
                    AcceptHeaderValidator(mode: .sseRequired),
                    ContentTypeValidator(),
                    SessionValidator(),
                ])),
            budget: settings.sessionByteBudget)
        let started: StartedServer
        do {
            started = try await registry.start(on: transport)
        } catch {
            creating -= 1
            log("Could not start an MCP server for a session: \(error)")
            return .error(statusCode: 500, .internalError("Could not start the MCP server"))
        }
        creating -= 1
        guard !closed else {
            // `shutdown()` ran while the server was starting: it has already ended the others.
            await started.shutdown()
            return .error(statusCode: 503, .internalError("The server is shutting down"))
        }
        let now = Date()
        // Filed before the request is handled, so the end of its stream finds the session.
        sessions[id] = Session(started: started, transport: transport, createdAt: now, lastRequestAt: now, inFlight: 1)
        startSweeper()
        let response = await transport.inner.handleRequest(request)
        if !Self.isStream(response), sessions.removeValue(forKey: id) != nil {
            // Refused by the transport (a bad Accept header, say): nothing to keep.
            await started.shutdown()
        }
        return response
    }

    /// Hands a request to its session; nil when the session is unknown (or over budget, which
    /// the client must treat the same way: re-initialise). A `POST` answered with a stream
    /// stays in flight until the caller reports its end through `requestEnded`.
    func handle(_ request: MCP.HTTPRequest, sessionID id: String) async -> MCP.HTTPResponse? {
        guard var session = sessions[id], !session.overBudget else { return nil }
        let counted = request.method == "POST"
        session.lastRequestAt = Date()
        if counted { session.inFlight += 1 }
        sessions[id] = session
        let response = await session.transport.inner.handleRequest(request)
        if request.method == "DELETE", response.statusCode == 200 {
            await end(id)
        } else if counted, !Self.isStream(response) {
            await requestEnded(sessionID: id, handle: nil, cancelled: false)
        }
        return response
    }

    /// The response to a counted request has ended. `cancelled`: the client went away before
    /// it did, so the tool call it started (named by `handle`) is cancelled; the session lives on.
    func requestEnded(sessionID id: String, handle: String?, cancelled: Bool) async {
        guard var session = sessions[id] else { return }
        session.inFlight -= 1
        session.lastRequestAt = Date()
        sessions[id] = session
        if cancelled, let handle { await session.started.cancel(request: handle) }
        let overBudget = await session.transport.overBudget
        guard var current = sessions[id] else { return }
        current.overBudget = current.overBudget || overBudget
        sessions[id] = current
        // Ended only between requests: never in the middle of another response stream.
        if current.overBudget, current.inFlight == 0 { await end(id) }
    }

    /// Ends a session: cancels its tool calls, stops its server (which terminates the
    /// transport, so its open streams finish) and forgets it.
    func end(_ id: String) async {
        guard let session = sessions.removeValue(forKey: id) else { return }
        await session.started.shutdown()
    }

    func endAll() async {
        for id in Array(sessions.keys) { await end(id) }
    }

    /// Ends the sessions idle for longer than the timeout; an open `GET` stream is not
    /// activity, a running tool call is.
    func sweep(now: Date) async {
        let timeout = settings.sessionIdleTimeout
        guard timeout > 0 else { return }
        let idle = sessions.filter { $0.value.inFlight == 0 && now.timeIntervalSince($0.value.lastRequestAt) > timeout }
        for id in idle.keys { await end(id) }
    }

    /// Stops the sweeper, ends every session and refuses new ones.
    func shutdown() async {
        closed = true
        sweeper?.cancel()
        sweeper = nil
        await endAll()
    }

    /// The server of a live session (test hook).
    func server(for id: String) -> MCP.Server? { sessions[id]?.started.server }

    /// One sweeper for all sessions, started with the first one.
    private func startSweeper() {
        guard sweeper == nil, !closed, settings.sessionIdleTimeout > 0 else { return }
        let interval = min(settings.sessionIdleTimeout, 60)
        sweeper = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(interval))
                guard let self, !Task.isCancelled else { return }
                await self.sweep(now: Date())
            }
        }
    }

    static func isStream(_ response: MCP.HTTPResponse) -> Bool {
        if case .stream = response { return true }
        return false
    }
}

/// Wraps a session's stateful transport for its server: counts the bytes the server sends
/// (the transport keeps every one of them) and forwards the HTTP context, without which tool
/// calls could not see the request handle that `HTTPApp` cancels them by.
actor BudgetedTransport: Transport, HTTPContextProviding {
    let inner: StatefulHTTPServerTransport
    private let budget: Int
    private var bytesSent = 0

    init(wrapping inner: StatefulHTTPServerTransport, budget: Int) {
        self.inner = inner
        self.budget = budget
    }

    var overBudget: Bool { bytesSent > budget }

    nonisolated var logger: Logger { inner.logger }
    func connect() async throws { try await inner.connect() }
    func disconnect() async { await inner.disconnect() }

    func send(_ data: Data) async throws {
        bytesSent += data.count
        try await inner.send(data)
    }

    /// `receive()` is synchronous but the inner transport's is isolated to that actor, so a
    /// task forwards the inner stream.
    func receive() -> AsyncThrowingStream<Data, Swift.Error> {
        let (stream, continuation) = AsyncThrowingStream<Data, Swift.Error>.makeStream()
        let inner = inner
        let pump = Task {
            do {
                for try await data in await inner.receive() { continuation.yield(data) }
                continuation.finish()
            } catch {
                continuation.finish(throwing: error)
            }
        }
        continuation.onTermination = { _ in pump.cancel() }
        return stream
    }

    func httpRequestContext(for id: ID) async -> MCP.HTTPRequest? {
        await inner.httpRequestContext(for: id)
    }
}
