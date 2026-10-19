import Foundation
import MCP

public struct HTTPRequest: Sendable {
    public var method: String
    /// Path without the query string, e.g. "/messages/".
    public var path: String
    public var query: [String: String]
    /// Header names lowercased.
    public var headers: [String: String]
    public var body: Data
    /// The peer's IP address, when known.
    public var remoteAddress: String?

    public init(method: String, target: String, headers: [(String, String)], body: Data = Data(), remoteAddress: String? = nil) {
        self.method = method.uppercased()
        let components = URLComponents(string: target)
        self.path = components?.percentEncodedPath.removingPercentEncoding ?? target
        if path.isEmpty { path = "/" }
        var query: [String: String] = [:]
        // Decoded by hand: URLComponents keeps a form-encoded "+" instead of turning it into a space.
        for item in components?.percentEncodedQueryItems ?? [] {
            let name = Self.formDecode(item.name)
            if query[name] == nil { query[name] = Self.formDecode(item.value ?? "") }
        }
        self.query = query
        var lowered: [String: String] = [:]
        for (name, value) in headers {
            let key = name.lowercased()
            lowered[key] = lowered[key].map { "\($0), \(value)" } ?? value
        }
        self.headers = lowered
        self.body = body
        self.remoteAddress = remoteAddress
    }

    public func header(_ name: String) -> String? { headers[name.lowercased()] }

    static func formDecode(_ text: String) -> String {
        text.replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? text
    }
}

public struct HTTPResponse: Sendable {
    public enum Body: Sendable {
        case data(Data)
        /// Written chunk by chunk until the stream finishes. When the client disconnects the
        /// writer stops consuming, and the stream's termination handler cleans up.
        case stream(AsyncStream<String>)
    }

    public var status: Int
    public var headers: [(String, String)]
    public var body: Body

    public init(status: Int, headers: [(String, String)] = [], body: Body = .data(Data())) {
        self.status = status
        self.headers = headers
        self.body = body
    }

    public static func json<T: Encodable>(_ value: T, status: Int = 200, headers: [(String, String)] = []) -> HTTPResponse {
        HTTPResponse(status: status, headers: [("content-type", "application/json")] + headers,
                     body: .data(Data(JSONCoding.encodeString(value).utf8)))
    }

    /// `{"error": "…"}`, the body of every refusal.
    public static func error(_ message: String, status: Int, headers: [(String, String)] = []) -> HTTPResponse {
        json(ErrorBody(error: message), status: status, headers: headers)
    }

    struct ErrorBody: Encodable {
        var error: String
    }

    public static func text(_ text: String, status: Int, headers: [(String, String)] = []) -> HTTPResponse {
        HTTPResponse(status: status, headers: [("content-type", "text/plain; charset=utf-8")] + headers,
                     body: .data(Data(text.utf8)))
    }

    public func header(_ name: String) -> String? {
        headers.first { $0.0.lowercased() == name.lowercased() }?.1
    }

    /// The body as text, for `.data` bodies.
    public var bodyText: String? {
        if case .data(let data) = body { return String(decoding: data, as: UTF8.self) }
        return nil
    }
}

/// The HTTP side of the server, independent of the socket layer: CORS, IP allowlist, bearer /
/// OAuth authentication, OAuth endpoints, and the streamable-HTTP MCP endpoint (stateless by
/// default, with opt-in sessions for clients that send `initialize` and accept SSE).
/// Middleware runs in the original's order: OPTIONS, CORS, allowlist, auth, routes.
public final class HTTPApp: Sendable {
    public struct Settings: Sendable {
        public var authKey: String?
        public var ipAllowlist: [IPNetwork]?
        public var oauth: OAuthService?
        /// Seconds a session may go without a request before it is ended; 0 disables eviction.
        public var sessionIdleTimeout: TimeInterval = 1800
        /// Bytes a session's server may send before the session is ended (between requests):
        /// the SDK's stateful transport keeps every message it sent until the session ends.
        public var sessionByteBudget: Int = 64 * 1024 * 1024
        /// Live sessions at most; one more evicts the least recently used idle one.
        public var maxSessions: Int = 64

        public init(authKey: String? = nil, ipAllowlist: [IPNetwork]? = nil, oauth: OAuthService? = nil) {
            self.authKey = authKey
            self.ipAllowlist = ipAllowlist
            self.oauth = oauth
        }
    }

    static let publicPaths: Set<String> = [
        "/health", "/.well-known/oauth-authorization-server", "/oauth/register", "/oauth/authorize", "/oauth/token",
    ]

    let registry: ToolRegistry
    let settings: Settings
    let sessions: SessionStore

    public init(registry: ToolRegistry, settings: Settings) {
        self.registry = registry
        self.settings = settings
        self.sessions = SessionStore(registry: registry, settings: settings)
    }

    /// Ends every session (cancelling their tool calls) and stops the sweeper. Call once
    /// after `NIOHTTPServer.serve` returns; tests call it in `defer`. Idempotent.
    public func shutdown() async {
        await sessions.shutdown()
    }

    /// Test hook: ends every session as eviction would.
    func evictAllSessions() async {
        await sessions.endAll()
    }

    /// Test hook: the server of a live session, to send it server-initiated messages.
    func sessionServer(id: String) async -> MCP.Server? {
        await sessions.server(for: id)
    }

    public func handle(_ request: HTTPRequest) async -> HTTPResponse {
        // A browser names the page that made the request. Only pages served from this machine
        // may talk to the server; otherwise any website could drive an unauthenticated loopback
        // server through the visitor's browser (the MCP spec requires this check).
        let origin = request.header("origin")
        if let origin, !Self.isAllowedOrigin(origin) {
            return .error("Forbidden: origin '\(origin)' is not allowed", status: 403)
        }
        if request.method == "OPTIONS" {
            return HTTPResponse(status: 200, headers: [
                ("content-length", "0"),
                ("access-control-allow-origin", origin ?? "*"),
                ("access-control-allow-methods", "*"),
                ("access-control-allow-headers", "*"),
                ("vary", "origin"),
            ])
        }
        var response = await guarded(request)
        if let origin {
            response.headers.append(("access-control-allow-origin", origin))
            response.headers.append(("vary", "origin"))
        }
        return response
    }

    /// Loopback http(s) origins on any port.
    static func isAllowedOrigin(_ origin: String) -> Bool {
        guard let components = URLComponents(string: origin),
              let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = components.host, !host.isEmpty else { return false }
        return SecurityUtil.isLoopbackHost(host.trimmingCharacters(in: CharacterSet(charactersIn: "[]")))
    }

    private func guarded(_ request: HTTPRequest) async -> HTTPResponse {
        if let allowlist = settings.ipAllowlist, !allowlist.isEmpty, request.path != "/health" {
            guard let address = request.remoteAddress else {
                return .error("Forbidden: missing client address", status: 403)
            }
            guard IPNetwork.parseAddress(address) != nil else {
                return .error("Forbidden: invalid client address '\(address)'", status: 403)
            }
            if !allowlist.contains(where: { $0.contains(address) }) {
                return .error("Forbidden: \(address) is not in the IP allowlist", status: 403)
            }
        }
        if let denied = await authenticate(request) { return denied }
        return await route(request)
    }

    /// nil when the request may proceed.
    private func authenticate(_ request: HTTPRequest) async -> HTTPResponse? {
        guard settings.authKey != nil || settings.oauth != nil else { return nil }
        if Self.publicPaths.contains(request.path) { return nil }
        let header = request.header("authorization") ?? ""
        let token = header.hasPrefix("Bearer ") ? String(header.dropFirst(7)) : nil

        if let key = settings.authKey {
            guard let token else {
                return .error("Missing or invalid Authorization header. Expected: Bearer <token>",
                              status: 401, headers: [("www-authenticate", "Bearer")])
            }
            if SecurityUtil.constantTimeEquals(token, key) { return nil }
            if let oauth = settings.oauth, await oauth.validate(token) { return nil }
            return .error("Invalid authentication token", status: 401, headers: [("www-authenticate", "Bearer")])
        }
        if let oauth = settings.oauth, let token, await oauth.validate(token) { return nil }
        return .error("Unauthorized", status: 401)
    }

    private func route(_ request: HTTPRequest) async -> HTTPResponse {
        if let oauth = settings.oauth, let response = await oauth.handle(request) {
            return response
        }
        switch request.path {
        case "/mcp", "/mcp/":
            if let sessionID = request.header(HTTPHeaderName.sessionID) {
                return await mcpSession(request, sessionID: sessionID)
            }
            guard request.method == "POST" else {
                // No session: no standalone server-to-client stream, nothing to delete. SDK
                // clients read 405 on GET as "no stream" and stop asking.
                return Self.convert(.error(statusCode: 405, .invalidRequest("Method Not Allowed"),
                                           extraHeaders: ["Allow": "POST"]))
            }
            // Sessions are opt-in: only an `initialize` that accepts SSE gets one.
            if Self.isInitialize(request.body), request.header("accept")?.contains("text/event-stream") == true {
                return await mcpSessionCreate(request)
            }
            return await mcpPost(request)
        default:
            return .text("Not Found", status: 404)
        }
    }

    // MARK: Streamable HTTP transport (stateless)

    /// One SDK server on one stateless transport per request: JSON-RPC ids from different
    /// clients never meet, and nothing outlives the response.
    private func mcpPost(_ request: HTTPRequest) async -> HTTPResponse {
        let transport = StatelessHTTPServerTransport(validationPipeline: StandardValidationPipeline(validators: [
            // No OriginValidator: `handle` has already checked the origin.
            AcceptHeaderValidator(mode: .jsonOnly),
            ContentTypeValidator(),
            // No ProtocolVersionValidator: SDK 0.12.1 rejects any version newer than it knows
            // (2026-07-28 clients included); `initialize` negotiates the version instead.
        ]))
        let started: StartedServer
        do {
            started = try await registry.start(on: transport)
        } catch {
            log("Could not start an MCP server for an HTTP request: \(error)")
            return Self.convert(.error(statusCode: 500, .internalError("Could not start the MCP server")))
        }
        // The socket layer cancels this task when the client disconnects: stop the tool call.
        let response = await withTaskCancellationHandler {
            await transport.handleRequest(MCP.HTTPRequest(
                method: request.method, headers: request.headers,
                body: request.body.isEmpty ? nil : request.body, path: request.path))
        } onCancel: {
            Task { await started.shutdown() }
        }
        await started.shutdown()
        return Self.convert(response)
    }

    // MARK: Streamable HTTP transport (sessions)

    /// A single `initialize` request; only the method is decoded.
    static func isInitialize(_ body: Data) -> Bool {
        struct Probe: Decodable { var method: String? }
        return (try? JSONDecoder().decode(Probe.self, from: body))?.method == "initialize"
    }

    /// The request as a session's transport sees it: without `Last-Event-ID` (resumability is
    /// not offered, and the SDK would take the client's first `GET` for a resume of a finished
    /// stream instead of the standalone one), and with a fresh `RequestHandle` naming the tool
    /// call it may start, replacing any value the client sent.
    static func sessionRequest(_ request: HTTPRequest, handle: String) -> MCP.HTTPRequest {
        var headers = request.headers
        headers["last-event-id"] = nil
        headers[RequestHandle.header] = handle
        return MCP.HTTPRequest(method: request.method, headers: headers,
                               body: request.body.isEmpty ? nil : request.body, path: request.path)
    }

    private func mcpSessionCreate(_ request: HTTPRequest) async -> HTTPResponse {
        let handle = UUID().uuidString
        let response = await sessions.create(initialize: Self.sessionRequest(request, handle: handle))
        guard SessionStore.isStream(response),
              let id = response.headers.first(where: { $0.key.lowercased() == HTTPHeaderName.sessionID.lowercased() })?.value
        else { return Self.convert(response) }
        return Self.convert(response, ended: requestEnded(sessionID: id, handle: handle))
    }

    private func mcpSession(_ request: HTTPRequest, sessionID: String) async -> HTTPResponse {
        let handle = UUID().uuidString
        guard let response = await sessions.handle(Self.sessionRequest(request, handle: handle), sessionID: sessionID) else {
            // What the SDK answers for a session it has ended; clients re-initialise on 404.
            return Self.convert(.error(statusCode: 404, .invalidRequest("Not Found: Session not found or expired")))
        }
        // Only POST streams are in flight; the standalone GET stream is not a request.
        return Self.convert(response, ended: request.method == "POST" ? requestEnded(sessionID: sessionID, handle: handle) : nil)
    }

    /// Reports the end of a `POST` response stream to its session. `cancelled`: the client
    /// went away first, so the tool call the request started is cancelled.
    private func requestEnded(sessionID: String, handle: String) -> @Sendable (_ cancelled: Bool) async -> Void {
        let sessions = sessions
        return { cancelled in
            await sessions.requestEnded(sessionID: sessionID, handle: handle, cancelled: cancelled)
        }
    }

    /// A session response for the socket layer. A stream is forwarded chunk by chunk. When it
    /// finishes, `ended(false)` runs before the client sees the end, so a session ended for its
    /// byte budget is gone by the client's next request. When the socket layer stops reading
    /// (the client disconnected, the server is stopping), `ended(true)`. One of the two, once.
    static func convert(_ response: MCP.HTTPResponse, ended: (@Sendable (_ cancelled: Bool) async -> Void)?) -> HTTPResponse {
        guard case .stream(let events, _) = response else { return convert(response) }
        let (stream, continuation) = AsyncStream<String>.makeStream()
        let reported = Locked(false)
        /// True for the first caller only.
        @Sendable func claim() -> Bool {
            reported.withLock { done in
                defer { done = true }
                return !done
            }
        }
        let pump = Task {
            do {
                for try await chunk in events { continuation.yield(String(decoding: chunk, as: UTF8.self)) }
            } catch {
                // The transport ended the stream with an error: the client sees it close.
            }
            if claim() { await ended?(false) }
            continuation.finish()
        }
        continuation.onTermination = { termination in
            // Claimed before the pump is cancelled: a pump cut short must not report a finish.
            if case .cancelled = termination, claim() { Task { await ended?(true) } }
            pump.cancel()
        }
        return HTTPResponse(status: response.statusCode,
                            headers: response.headers.map { ($0.key.lowercased(), $0.value) },
                            body: .stream(stream))
    }

    static func convert(_ response: MCP.HTTPResponse) -> HTTPResponse {
        if case .stream = response {
            // The stateless transport answers with JSON only; a stream here is a bug, not a crash.
            log("The MCP transport returned a stream, which the stateless endpoint does not serve")
            return convert(.error(statusCode: 500, .internalError("Unexpected streaming response")))
        }
        return HTTPResponse(status: response.statusCode,
                            headers: response.headers.map { ($0.key.lowercased(), $0.value) },
                            body: .data(response.bodyData ?? Data()))
    }
}
