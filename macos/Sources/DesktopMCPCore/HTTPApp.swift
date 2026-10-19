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
/// OAuth authentication, OAuth endpoints, and the stateless streamable-HTTP MCP endpoint.
/// Middleware runs in the original's order: OPTIONS, CORS, allowlist, auth, routes.
public final class HTTPApp: Sendable {
    public struct Settings: Sendable {
        public var authKey: String?
        public var ipAllowlist: [IPNetwork]?
        public var oauth: OAuthService?

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

    public init(registry: ToolRegistry, settings: Settings) {
        self.registry = registry
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
        switch request.path {
        case "/mcp", "/mcp/":
            guard request.method == "POST" else {
                // Stateless: no standalone server-to-client stream, no sessions to delete.
                return Self.convert(.error(statusCode: 405, .invalidRequest("Method Not Allowed"),
                                           extraHeaders: ["Allow": "POST"]))
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
            ProtocolVersionValidator(),
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
