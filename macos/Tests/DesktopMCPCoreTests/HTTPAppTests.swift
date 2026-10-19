import CryptoKit
import XCTest
@testable import DesktopMCPCore

final class HTTPAppTests: XCTestCase {
    func app(authKey: String? = nil, allowlist: [String]? = nil, oauth: OAuthService? = nil,
             registry: ToolRegistry? = nil) async throws -> HTTPApp {
        let settings = HTTPApp.Settings(authKey: authKey, ipAllowlist: try allowlist.map(IPNetwork.parseAllowlist), oauth: oauth)
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

final class Clock: @unchecked Sendable {
    var now = Date()
}
