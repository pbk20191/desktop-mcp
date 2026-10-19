import Foundation

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

    static func eventStream(_ stream: AsyncStream<String>) -> HTTPResponse {
        HTTPResponse(status: 200, headers: [
            ("content-type", "text/event-stream"),
            ("cache-control", "no-store"),
            ("connection", "keep-alive"),
            ("x-accel-buffering", "no"),
        ], body: .stream(stream))
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

public enum HTTPTransport: String, Sendable {
    case sse
    case streamableHTTP = "streamable-http"
}

/// The HTTP side of the server, independent of the socket layer: CORS, IP allowlist, bearer /
/// OAuth authentication, OAuth endpoints, and the MCP SSE or streamable-HTTP endpoints.
/// Middleware runs in the original's order: OPTIONS, CORS, allowlist, auth, routes.
public final class HTTPApp: Sendable {
    public struct Settings: Sendable {
        public var transport: HTTPTransport
        public var authKey: String?
        public var ipAllowlist: [IPNetwork]?
        public var oauth: OAuthService?
        /// Interval between keep-alive comments on SSE streams; also how disconnects are noticed.
        public var pingInterval: Double = 15

        public init(transport: HTTPTransport, authKey: String? = nil, ipAllowlist: [IPNetwork]? = nil, oauth: OAuthService? = nil) {
            self.transport = transport
            self.authKey = authKey
            self.ipAllowlist = ipAllowlist
            self.oauth = oauth
        }
    }

    static let publicPaths: Set<String> = [
        "/health", "/.well-known/oauth-authorization-server", "/oauth/register", "/oauth/authorize", "/oauth/token",
    ]

    let server: MCPServer
    let settings: Settings
    let sessions = SSESessions()

    public init(server: MCPServer, settings: Settings) {
        self.server = server
        self.settings = settings
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
        switch (settings.transport, request.path) {
        case (.sse, "/sse"):
            return request.method == "GET" ? await openSSE() : methodNotAllowed()
        case (.sse, "/messages/"), (.sse, "/messages"):
            return request.method == "POST" ? await postMessage(request) : methodNotAllowed()
        case (.streamableHTTP, "/mcp"), (.streamableHTTP, "/mcp/"):
            switch request.method {
            case "POST": return await streamablePost(request)
            default:
                // Stateless: no standalone server-to-client stream, no sessions to delete.
                return .json(MCPServer.errorResponse(id: "server-error", code: -32000, message: "Method Not Allowed"),
                             status: 405, headers: [("allow", "POST")])
            }
        default:
            return .text("Not Found", status: 404)
        }
    }

    private func methodNotAllowed() -> HTTPResponse {
        .text("Method Not Allowed", status: 405)
    }

    // MARK: SSE transport (protocol 2024-11-05)

    /// GET /sse: an event stream whose first event names the endpoint to POST messages to.
    private func openSSE() async -> HTTPResponse {
        let id = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let (stream, continuation) = AsyncStream.makeStream(of: String.self, bufferingPolicy: .unbounded)
        let session = MCPSession(server: server) { message in
            continuation.yield("event: message\r\ndata: \(message)\r\n\r\n")
        }
        await sessions.add(id, session)
        continuation.yield("event: endpoint\r\ndata: /messages/?session_id=\(id)\r\n\r\n")

        let interval = settings.pingInterval
        let pinger = Task {
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(interval))
                if Task.isCancelled { break }
                continuation.yield(": ping - \(ISO8601DateFormatter().string(from: Date()))\r\n\r\n")
            }
        }
        let sessions = self.sessions
        continuation.onTermination = { _ in
            pinger.cancel()
            Task {
                await session.cancelAll()
                await sessions.remove(id)
            }
        }
        return .eventStream(stream)
    }

    /// POST /messages/?session_id=…: hands the message to that session; replies go out on its stream.
    private func postMessage(_ request: HTTPRequest) async -> HTTPResponse {
        guard let raw = request.query["session_id"] else { return .text("session_id is required", status: 400) }
        guard raw.count == 32, raw.allSatisfy(\.isHexDigit) else { return .text("Invalid session ID", status: 400) }
        guard let session = await sessions.get(raw.lowercased()) else { return .text("Could not find session", status: 404) }
        guard let incoming = try? JSONCoding.decode(JSONRPCIncoming.self, from: request.body) else {
            return .text("Could not parse message", status: 400)
        }
        await session.receive(incoming)
        return .text("Accepted", status: 202)
    }

    // MARK: Streamable HTTP transport (stateless)

    private func streamablePost(_ request: HTTPRequest) async -> HTTPResponse {
        let contentType = request.header("content-type") ?? ""
        guard contentType.lowercased().contains("json") else {
            return .json(MCPServer.errorResponse(id: "server-error", code: -32600,
                                                 message: "Unsupported Media Type: Content-Type must be application/json"),
                         status: 415)
        }
        guard let incoming = try? JSONCoding.decode(JSONRPCIncoming.self, from: request.body) else {
            return .json(MCPServer.errorResponse(id: .null, code: -32700, message: "Parse error"), status: 400)
        }
        let messages = incoming.messages
        if messages.isEmpty {
            return .json(MCPServer.errorResponse(id: .null, code: -32600, message: "Invalid Request"), status: 400)
        }
        guard messages.contains(where: \.needsResponse) else {
            // Only notifications or responses. Without a session there is nothing to cancel.
            return HTTPResponse(status: 202)
        }
        let client = messages.lazy.compactMap { message -> ClientInfo? in
            if case .initialize(let params) = message.request?.params { return params.clientInfo }
            return nil
        }.first
        let server = self.server

        let accept = (request.header("accept") ?? "").lowercased()
        if accept.contains("text/event-stream") {
            let (stream, continuation) = AsyncStream.makeStream(of: String.self, bufferingPolicy: .unbounded)
            let worker = Task {
                await withTaskGroup(of: Void.self) { group in
                    for item in messages {
                        group.addTask {
                            if let response = await server.respond(to: item, client: client) {
                                continuation.yield("event: message\r\ndata: \(response.serialized())\r\n\r\n")
                            }
                        }
                    }
                }
                continuation.finish()
            }
            // The client hung up: stop the work it was waiting for.
            continuation.onTermination = { reason in
                if case .cancelled = reason { worker.cancel() }
            }
            return .eventStream(stream)
        }

        var responses: [JSONRPCResponse] = []
        for item in messages {
            if let response = await server.respond(to: item, client: client) { responses.append(response) }
        }
        return incoming.isBatch ? .json(responses) : .json(responses[0])
    }
}

/// Live SSE sessions by id.
actor SSESessions {
    private var sessions: [String: MCPSession] = [:]

    func add(_ id: String, _ session: MCPSession) { sessions[id] = session }
    func get(_ id: String) -> MCPSession? { sessions[id] }
    func remove(_ id: String) { sessions[id] = nil }
    var count: Int { sessions.count }
}
