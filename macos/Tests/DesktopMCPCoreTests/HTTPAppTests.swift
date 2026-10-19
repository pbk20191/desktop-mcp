import CryptoKit
import XCTest
@testable import DesktopMCPCore

final class HTTPAppTests: XCTestCase {
    func app(_ transport: HTTPTransport, authKey: String? = nil, allowlist: [String]? = nil,
             oauth: OAuthService? = nil) async throws -> HTTPApp {
        let server = await DesktopMCP.makeServer()
        var settings = HTTPApp.Settings(transport: transport, authKey: authKey,
                                        ipAllowlist: try allowlist.map(IPNetwork.parseAllowlist), oauth: oauth)
        settings.pingInterval = 0.2
        return HTTPApp(server: server, settings: settings)
    }

    func post(_ path: String, _ body: String, headers: [(String, String)] = [], remote: String? = "127.0.0.1") -> HTTPRequest {
        HTTPRequest(method: "POST", target: path, headers: [("Content-Type", "application/json")] + headers,
                    body: Data(body.utf8), remoteAddress: remote)
    }

    static let ping = #"{"jsonrpc":"2.0","id":1,"method":"ping"}"#

    /// Reads SSE events until `count` `message` events arrive.
    func messages(from response: HTTPResponse, count: Int) async throws -> [JSONValue] {
        guard case .stream(let stream) = response.body else { XCTFail("not a stream"); return [] }
        var found: [JSONValue] = []
        for await chunk in stream {
            for event in chunk.components(separatedBy: "\r\n\r\n") where event.hasPrefix("event: message") {
                let data = event.components(separatedBy: "\r\n").first { $0.hasPrefix("data: ") }!.dropFirst(6)
                found.append(try JSONValue.parse(String(data)))
            }
            if found.count >= count { break }
        }
        return found
    }

    // MARK: Streamable HTTP

    func testStreamableJSONAndEventStreamResponses() async throws {
        let app = try await app(.streamableHTTP)
        let json = await app.handle(post("/mcp", Self.ping, headers: [("Accept", "application/json")]))
        XCTAssertEqual(json.status, 200)
        XCTAssertEqual(try JSONValue.parse(json.bodyText!)["result"], .object([:]))

        let sse = await app.handle(post("/mcp/", Self.ping, headers: [("Accept", "application/json, text/event-stream")]))
        XCTAssertEqual(sse.header("content-type"), "text/event-stream")
        let events = try await messages(from: sse, count: 1)
        XCTAssertEqual(events.first?["id"], 1)
    }

    func testStreamableEdgeCases() async throws {
        let app = try await app(.streamableHTTP)
        let notification = await app.handle(post("/mcp", #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#))
        XCTAssertEqual(notification.status, 202)
        let bad = await app.handle(post("/mcp", "{nope"))
        XCTAssertEqual(bad.status, 400)
        let wrongType = await app.handle(HTTPRequest(method: "POST", target: "/mcp", headers: [("Content-Type", "text/plain")], body: Data(Self.ping.utf8)))
        XCTAssertEqual(wrongType.status, 415)
        let get = await app.handle(HTTPRequest(method: "GET", target: "/mcp", headers: []))
        XCTAssertEqual(get.status, 405)
        let missing = await app.handle(HTTPRequest(method: "GET", target: "/sse", headers: []))
        XCTAssertEqual(missing.status, 404)
        let batch = await app.handle(post("/mcp", "[\(Self.ping),{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}]"))
        XCTAssertEqual(try JSONValue.parse(batch.bodyText!).arrayValue?.count, 2)
    }

    func testStreamableDisconnectCancelsTheToolCall() async throws {
        let app = try await app(.streamableHTTP)
        let body = #"{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"Shell","arguments":{"command":"sleep 2; echo late"}}}"#
        let response = await app.handle(post("/mcp", body, headers: [("Accept", "text/event-stream")]))
        guard case .stream(let stream) = response.body else { return XCTFail("expected a stream") }
        // Consume in a task and cancel it, as the socket layer does when the peer closes.
        let consumer = Task { for await _ in stream {} }
        try await Task.sleep(nanoseconds: 200_000_000)
        let started = Date()
        consumer.cancel()
        await consumer.value
        XCTAssertLessThan(Date().timeIntervalSince(started), 1)
    }

    // MARK: SSE

    func testSSESessionRoundTrip() async throws {
        let app = try await app(.sse)
        let open = await app.handle(HTTPRequest(method: "GET", target: "/sse", headers: []))
        guard case .stream(let stream) = open.body else { return XCTFail("expected a stream") }
        var iterator = stream.makeAsyncIterator()
        let first = await iterator.next()!
        XCTAssertTrue(first.hasPrefix("event: endpoint\r\ndata: /messages/?session_id="), first)
        let endpoint = first.components(separatedBy: "data: ")[1].trimmingCharacters(in: .whitespacesAndNewlines)

        let accepted = await app.handle(post(endpoint, Self.ping))
        XCTAssertEqual(accepted.status, 202)
        XCTAssertEqual(accepted.bodyText, "Accepted")

        var message: String?
        while let chunk = await iterator.next() {
            if chunk.hasPrefix("event: message") { message = chunk; break }
        }
        XCTAssertTrue(message?.contains(#""id":1"#) == true, message ?? "")

        let v1 = await app.handle(post("/messages/", Self.ping)).status
        XCTAssertEqual(v1, 400)
        let v2 = await app.handle(post("/messages/?session_id=zzz", Self.ping)).status
        XCTAssertEqual(v2, 400)
        let v3 = await app.handle(post("/messages/?session_id=\(String(repeating: "a", count: 32))", Self.ping)).status
        XCTAssertEqual(v3, 404)
        let v4 = await app.handle(post(endpoint, "{bad")).status
        XCTAssertEqual(v4, 400)
    }

    func testSSEKeepAlivePings() async throws {
        let app = try await app(.sse)
        let open = await app.handle(HTTPRequest(method: "GET", target: "/sse", headers: []))
        guard case .stream(let stream) = open.body else { return XCTFail("expected a stream") }
        var iterator = stream.makeAsyncIterator()
        _ = await iterator.next()
        let ping = await iterator.next()
        XCTAssertTrue(ping?.hasPrefix(": ping - ") == true, ping ?? "")
    }

    // MARK: Middleware

    func testBearerAuth() async throws {
        let app = try await app(.streamableHTTP, authKey: "s3cret")
        let none = await app.handle(post("/mcp", Self.ping))
        XCTAssertEqual(none.status, 401)
        XCTAssertEqual(none.header("www-authenticate"), "Bearer")
        let wrong = await app.handle(post("/mcp", Self.ping, headers: [("Authorization", "Bearer nope")]))
        XCTAssertEqual(wrong.status, 401)
        XCTAssertEqual(try JSONValue.parse(wrong.bodyText!)["error"], "Invalid authentication token")
        let right = await app.handle(post("/mcp", Self.ping, headers: [("Authorization", "Bearer s3cret"), ("Accept", "application/json")]))
        XCTAssertEqual(right.status, 200)
    }

    func testOptionsAndCORS() async throws {
        let app = try await app(.streamableHTTP)
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

    func testEmptyBatchAndInvalidItemsGetErrors() async throws {
        let app = try await app(.streamableHTTP)
        let empty = await app.handle(post("/mcp", "[]", headers: [("Accept", "application/json")]))
        XCTAssertEqual(empty.status, 400)
        XCTAssertEqual(try JSONValue.parse(empty.bodyText!)["error"]?["code"], -32600)

        // A batch of non-objects is not "only notifications": each item earns an Invalid Request.
        let junk = await app.handle(post("/mcp", "[1, \"x\"]", headers: [("Accept", "application/json")]))
        XCTAssertEqual(junk.status, 200)
        let errors = try JSONValue.parse(junk.bodyText!).arrayValue ?? []
        XCTAssertEqual(errors.count, 2)
        XCTAssertEqual(errors.first?["error"]?["code"], -32600)
        XCTAssertEqual(errors.first?["id"], .null)
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
        XCTAssertEqual(try JSONValue.parse(seen!.bodyText!)["token_endpoint"], "https://mac.local:8000/oauth/token")

        let fixed = OAuthService(issuer: "https://127.0.0.1:8000", clientID: "c", clientSecret: "s")
        let kept = await fixed.handle(HTTPRequest(method: "GET", target: "/.well-known/oauth-authorization-server",
                                                  headers: [("Host", "mac.local:8000")]))
        XCTAssertEqual(try JSONValue.parse(kept!.bodyText!)["issuer"], "https://127.0.0.1:8000")
    }

    func testIPAllowlist() async throws {
        let app = try await app(.streamableHTTP, allowlist: ["10.0.0.0/8"])
        let outside = await app.handle(post("/mcp", Self.ping, remote: "192.168.0.2"))
        XCTAssertEqual(outside.status, 403)
        XCTAssertEqual(try JSONValue.parse(outside.bodyText!)["error"], "Forbidden: 192.168.0.2 is not in the IP allowlist")
        let v5 = await app.handle(post("/mcp", Self.ping, remote: nil)).status
        XCTAssertEqual(v5, 403)
        let v6 = await app.handle(post("/mcp", Self.ping, headers: [("Accept", "application/json")], remote: "10.9.9.9")).status
        XCTAssertEqual(v6, 200)
    }

    // MARK: OAuth

    func testOAuthAuthorizationCodeFlowWithPKCE() async throws {
        let oauth = OAuthService(issuer: "http://127.0.0.1:8000", clientID: "cid", clientSecret: "csecret")
        let app = try await app(.streamableHTTP, authKey: "static", oauth: oauth)

        let metadata = await app.handle(HTTPRequest(method: "GET", target: "/.well-known/oauth-authorization-server", headers: []))
        XCTAssertEqual(try JSONValue.parse(metadata.bodyText!)["token_endpoint"], "http://127.0.0.1:8000/oauth/token")
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
        XCTAssertEqual(try JSONValue.parse(badVerifier.bodyText!)["error_description"], "PKCE verification failed")

        let second = await authorize()
        let code2 = try XCTUnwrap(URLComponents(string: second.header("location")!)?.queryItems?.first { $0.name == "code" }?.value)
        let token = await exchange(code2, verifier: verifier)
        XCTAssertEqual(token.status, 200)
        let accessToken = try XCTUnwrap(try JSONValue.parse(token.bodyText!)["access_token"]?.stringValue)
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
        let accessToken = try JSONValue.parse(token!.bodyText!)["access_token"]!.stringValue!
        let v10 = await oauth.validate(accessToken)
        XCTAssertTrue(v10)
        clock.now = clock.now.addingTimeInterval(OAuthService.tokenLifetime + 1)
        let v11 = await oauth.validate(accessToken)
        XCTAssertFalse(v11)
    }
}

final class Clock: @unchecked Sendable {
    var now = Date()
}
