import CryptoKit
import Foundation
import MCP

/// A minimal OAuth 2.0 authorization server for MCP clients, as in the original:
/// RFC 8414 metadata, authorization code + PKCE (S256 only), one pre-provisioned confidential
/// client (dynamic registration is refused), loopback redirect URIs only, in-memory state.
public actor OAuthService {
    public static let tokenLifetime: TimeInterval = 3600
    public static let codeLifetime: TimeInterval = 300

    struct AuthorizationCode {
        var clientID: String
        var redirectURI: String
        var challenge: String
        var method: String
        var expiresAt: Date
    }

    let issuer: String
    let clientID: String
    let clientSecret: String
    private var codes: [String: AuthorizationCode] = [:]
    private var tokens: [String: Date] = [:]
    private let now: @Sendable () -> Date
    /// Outstanding authorization codes. `/oauth/authorize` is unauthenticated, so the map is
    /// bounded: beyond this, the oldest codes are dropped.
    static let maxPendingCodes = 1000

    public init(issuer: String, clientID: String, clientSecret: String, now: @escaping @Sendable () -> Date = { Date() }) {
        self.issuer = issuer
        self.clientID = clientID
        self.clientSecret = clientSecret
        self.now = now
    }

    /// Whether `token` is a live access token. Expired tokens are forgotten.
    public func validate(_ token: String) -> Bool {
        guard let expiry = tokens[token] else { return false }
        if now() > expiry {
            tokens[token] = nil
            return false
        }
        return true
    }

    /// The response for an OAuth route, or nil when the request is for something else.
    public func handle(_ request: HTTPRequest) -> HTTPResponse? {
        switch (request.method, request.path) {
        case ("GET", "/.well-known/oauth-authorization-server"): return metadata(for: request)
        case ("POST", "/oauth/register"): return register()
        case ("GET", "/oauth/authorize"): return authorize(request.query)
        case ("POST", "/oauth/token"): return token(request)
        default: return nil
        }
    }

    /// The configured issuer, except that a wildcard bind address (0.0.0.0, ::) is useless to a
    /// client, so then the address the client actually used (its Host header) is advertised.
    func issuer(for request: HTTPRequest) -> String {
        guard let separator = issuer.range(of: "://") else { return issuer }
        let configuredHost = issuer[separator.upperBound...]
        let wildcard = ["0.0.0.0", "[::]", "::"].contains { configuredHost.hasPrefix($0 + ":") || configuredHost == $0 }
        guard wildcard, let host = request.header("host")?.nilIfEmpty else { return issuer }
        return issuer[..<separator.upperBound] + host
    }

    func metadata(for request: HTTPRequest) -> HTTPResponse {
        let issuer = issuer(for: request)
        return .json(AuthorizationServerMetadata(issuer: issuer,
                                                 authorizationEndpoint: "\(issuer)/oauth/authorize",
                                                 tokenEndpoint: "\(issuer)/oauth/token"))
    }

    func register() -> HTTPResponse {
        failure("registration_disabled",
                "Dynamic OAuth client registration is disabled. Configure --oauth-client-id and --oauth-client-secret on the server.",
                status: 403)
    }

    func authorize(_ params: [String: String]) -> HTTPResponse {
        let requestedClient = params["client_id"] ?? ""
        let redirectURI = params["redirect_uri"] ?? ""
        let state = params["state"] ?? ""
        let challenge = params["code_challenge"] ?? ""
        let method = params["code_challenge_method"] ?? "S256"

        guard params["response_type"] == "code" else { return failure("unsupported_response_type") }
        guard !challenge.isEmpty else { return invalidRequest("code_challenge required (PKCE)") }
        guard method == "S256" else { return invalidRequest("code_challenge_method must be S256") }
        guard Self.isLoopbackRedirect(redirectURI) else { return invalidRequest("redirect_uri must be a loopback http(s) URI") }
        guard requestedClient == clientID else { return failure("invalid_client") }

        sweep()
        let code = SecurityUtil.tokenURLSafe(32)
        codes[code] = AuthorizationCode(clientID: requestedClient, redirectURI: redirectURI, challenge: challenge,
                                        method: method, expiresAt: now().addingTimeInterval(Self.codeLifetime))

        var location = redirectURI + (redirectURI.contains("?") ? "&" : "?") + "code=\(Self.quote(code))"
        if !state.isEmpty { location += "&state=\(Self.quote(state))" }
        return HTTPResponse(status: 302, headers: [("location", location)])
    }

    func token(_ request: HTTPRequest) -> HTTPResponse {
        guard let body = Self.formOrJSON(request) else { return failure("invalid_request") }
        guard body["grant_type"] == "authorization_code" else { return failure("unsupported_grant_type") }

        let codeValue = body["code"] ?? ""
        guard let code = codes[codeValue] else { return invalidGrant("unknown code") }
        guard code.clientID == body["client_id"] ?? "" else { return invalidGrant("client_id mismatch") }
        guard code.redirectURI == body["redirect_uri"] ?? "" else { return invalidGrant("redirect_uri mismatch") }
        if now() > code.expiresAt {
            codes[codeValue] = nil
            return invalidGrant("code expired")
        }
        guard Self.verifyPKCE(verifier: body["code_verifier"] ?? "", challenge: code.challenge, method: code.method) else {
            codes[codeValue] = nil  // RFC 7636 §4.6: a failed verification burns the code.
            return invalidGrant("PKCE verification failed")
        }
        codes[codeValue] = nil  // One-time use.
        sweep()

        let secret = body["client_secret"] ?? ""
        guard !secret.isEmpty, SecurityUtil.constantTimeEquals(secret, clientSecret) else {
            return failure("invalid_client", status: 401)
        }
        let accessToken = SecurityUtil.tokenURLSafe(48)
        tokens[accessToken] = now().addingTimeInterval(Self.tokenLifetime)
        return .json(TokenResponse(accessToken: accessToken, expiresIn: Int(Self.tokenLifetime)))
    }

    /// Forgets expired codes and tokens, and caps the number of pending codes.
    private func sweep() {
        let current = now()
        codes = codes.filter { $0.value.expiresAt > current }
        tokens = tokens.filter { $0.value > current }
        if codes.count > Self.maxPendingCodes {
            let oldest = codes.sorted { $0.value.expiresAt < $1.value.expiresAt }.prefix(codes.count - Self.maxPendingCodes)
            oldest.forEach { codes[$0.key] = nil }
        }
    }

    private func invalidRequest(_ description: String) -> HTTPResponse {
        failure("invalid_request", description)
    }

    private func invalidGrant(_ description: String) -> HTTPResponse {
        failure("invalid_grant", description)
    }

    /// An RFC 6749 error response.
    private func failure(_ error: String, _ description: String? = nil, status: Int = 400) -> HTTPResponse {
        .json(OAuthErrorResponse(error: error, errorDescription: description), status: status)
    }

    // MARK: Helpers

    static func verifyPKCE(verifier: String, challenge: String, method: String) -> Bool {
        switch method {
        case "S256":
            let digest = SHA256.hash(data: Data(verifier.utf8))
            return SecurityUtil.constantTimeEquals(SecurityUtil.base64URL(Data(digest)), challenge)
        case "plain":
            return SecurityUtil.constantTimeEquals(verifier, challenge)
        default:
            return false
        }
    }

    static func isLoopbackRedirect(_ uri: String) -> Bool {
        guard let components = URLComponents(string: uri),
              let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = components.host, !host.isEmpty,
              components.fragment == nil else { return false }
        return SecurityUtil.isLoopbackHost(host.trimmingCharacters(in: CharacterSet(charactersIn: "[]")))
    }

    /// Python's `urllib.parse.quote` with its default safe set ("/").
    static func quote(_ s: String) -> String {
        var allowed = CharacterSet.alphanumerics.intersection(CharacterSet(charactersIn: Unicode.Scalar(0)...Unicode.Scalar(127)))
        allowed.insert(charactersIn: "-._~/")
        return s.addingPercentEncoding(withAllowedCharacters: allowed) ?? s
    }

    /// The token request body as form fields (or JSON object), per its content type.
    static func formOrJSON(_ request: HTTPRequest) -> [String: String]? {
        let contentType = (request.header("content-type") ?? "").lowercased()
        if contentType.contains("application/json") {
            guard let object = try? JSONCoding.decode([String: Value].self, from: request.body) else { return nil }
            return object.compactMapValues { value in
                switch value {
                case .string(let s): return s
                case .int(let i): return String(i)
                case .double(let n): return OrderedJSON.formatNumber(n)
                case .bool(let b): return b ? "true" : "false"
                default: return nil
                }
            }
        }
        let text = String(decoding: request.body, as: UTF8.self)
        var fields: [String: String] = [:]
        for pair in text.split(separator: "&") {
            let parts = pair.split(separator: "=", maxSplits: 1).map {
                String($0).replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? String($0)
            }
            if let key = parts.first, fields[key] == nil { fields[key] = parts.count > 1 ? parts[1] : "" }
        }
        return fields
    }
}

// MARK: Wire types

/// RFC 8414 authorization server metadata, the subset the original advertises.
struct AuthorizationServerMetadata: Encodable {
    var issuer: String
    var authorizationEndpoint: String
    var tokenEndpoint: String
    var responseTypesSupported = ["code"]
    var grantTypesSupported = ["authorization_code"]
    var codeChallengeMethodsSupported = ["S256"]
    var tokenEndpointAuthMethodsSupported = ["client_secret_post"]

    enum CodingKeys: String, CodingKey {
        case issuer
        case authorizationEndpoint = "authorization_endpoint"
        case tokenEndpoint = "token_endpoint"
        case responseTypesSupported = "response_types_supported"
        case grantTypesSupported = "grant_types_supported"
        case codeChallengeMethodsSupported = "code_challenge_methods_supported"
        case tokenEndpointAuthMethodsSupported = "token_endpoint_auth_methods_supported"
    }
}

/// RFC 6749 §5.1.
struct TokenResponse: Encodable {
    var accessToken: String
    var tokenType = "Bearer"
    var expiresIn: Int

    enum CodingKeys: String, CodingKey {
        case accessToken = "access_token"
        case tokenType = "token_type"
        case expiresIn = "expires_in"
    }
}

/// RFC 6749 §5.2.
struct OAuthErrorResponse: Encodable {
    var error: String
    var errorDescription: String?

    enum CodingKeys: String, CodingKey {
        case error
        case errorDescription = "error_description"
    }
}
