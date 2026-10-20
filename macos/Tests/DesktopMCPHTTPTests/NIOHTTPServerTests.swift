import Foundation
@testable import DesktopMCPCore
import DesktopMCPHTTP
import MCP
import XCTest

/// End to end over real sockets: NIOTransportServices listener, HTTP/1.1, optional TLS.
final class NIOHTTPServerTests: XCTestCase {
    func start(authKey: String? = nil, allowlist: [String]? = nil, tls: NIOHTTPServer.TLSFiles? = nil,
               registry: ToolRegistry? = nil) async throws -> (Int, Task<Void, Error>, HTTPApp) {
        let settings = HTTPApp.Settings(authKey: authKey, ipAllowlist: try allowlist.map(IPNetwork.parseAllowlist))
        let registry = if let registry { registry } else { await DesktopMCP.makeRegistry() }
        let app = HTTPApp(registry: registry, settings: settings)
        let listening = try await NIOHTTPServer.bind(host: "127.0.0.1", port: 0, tls: tls)
        let task = Task { try await NIOHTTPServer.serve(listening, app: app) }
        return (try XCTUnwrap(listening.port), task, app)
    }

    func request(_ url: String, method: String = "POST", body: String? = nil, headers: [String: String] = [:]) -> URLRequest {
        var request = URLRequest(url: URL(string: url)!, timeoutInterval: 10)
        request.httpMethod = method
        request.httpBody = body.map { Data($0.utf8) }
        for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
        return request
    }

    func testStreamableHTTPOverSockets() async throws {
        let (port, task, _) = try await start(authKey: "k")
        defer { task.cancel() }
        let url = "http://127.0.0.1:\(port)/mcp"
        let body = #"{"jsonrpc":"2.0","id":1,"method":"tools/list"}"#

        let (_, denied) = try await URLSession.shared.data(for: request(url, body: body, headers: ["Content-Type": "application/json"]))
        XCTAssertEqual((denied as? HTTPURLResponse)?.statusCode, 401)

        let headers = ["Content-Type": "application/json", "Authorization": "Bearer k", "Accept": "application/json"]
        let (data, response) = try await URLSession.shared.data(for: request(url, body: body, headers: headers))
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertEqual(try JSON.parse(data)["result"]?["tools"]?.arrayValue?.count, 13)
    }

    func testSDKClientOverHTTP() async throws {
        let (port, task, app) = try await start(authKey: "k")
        defer { task.cancel(); Task { await app.shutdown() } }
        let transport = HTTPClientTransport(
            endpoint: URL(string: "http://127.0.0.1:\(port)/mcp")!, streaming: false,
            requestModifier: { request in
                var request = request
                request.setValue("Bearer k", forHTTPHeaderField: "Authorization")
                return request
            })
        let client = Client(name: "t", version: "0")
        _ = try await client.connect(transport: transport)
        defer { Task { await client.disconnect() } }

        let tools = try await client.listTools().tools
        XCTAssertEqual(tools.count, 13)
        let waited = try await client.callTool(name: "Wait", arguments: ["duration": 0])
        XCTAssertNotEqual(waited.isError, true)
        let clicked = try await client.callTool(name: "Click", arguments: ["loc": [1]])
        XCTAssertEqual(clicked.isError, true)
        guard case .text(let text, _, _) = clicked.content.first else { return XCTFail("\(clicked.content)") }
        XCTAssertEqual(text, "Error executing tool Click: Location must be a list of exactly 2 integers [x, y]")
    }

    // MARK: Sessions

    func testSDKClientGetsASessionAndTheStandaloneGetStream() async throws {
        let (port, task, app) = try await start(authKey: "k")
        defer { task.cancel(); Task { await app.shutdown() } }
        let transport = HTTPClientTransport(
            endpoint: URL(string: "http://127.0.0.1:\(port)/mcp")!, streaming: true,
            requestModifier: { request in
                var request = request
                request.setValue("Bearer k", forHTTPHeaderField: "Authorization")
                return request
            })
        let received = Locked(0)
        let client = Client(name: "t", version: "0")
        await client.onNotification(ToolListChangedNotification.self) { _ in received.withLock { $0 += 1 } }
        _ = try await client.connect(transport: transport)
        defer { Task { await client.disconnect() } }

        let count = await app.sessions.count
        XCTAssertEqual(count, 1)
        let sessionID = await transport.sessionID
        let id = try XCTUnwrap(sessionID)
        let sessionServer = await app.sessionServer(id: id)
        let server = try XCTUnwrap(sessionServer)
        // Server-initiated messages travel on the GET stream, which the client opens on its
        // own shortly after initialising; nothing is replayed, so repeat until it is open.
        let deadline = Date().addingTimeInterval(2)
        while received.current == 0, Date() < deadline {
            try await server.notify(ToolListChangedNotification.message())
            try await Task.sleep(for: .milliseconds(100))
        }
        XCTAssertGreaterThan(received.current, 0)
    }

    func testSDKClientSurvivesEvictionThroughTheReconnectingWrapper() async throws {
        let (port, task, app) = try await start(authKey: "k")
        defer { task.cancel(); Task { await app.shutdown() } }
        let client = ReconnectingClient(endpoint: URL(string: "http://127.0.0.1:\(port)/mcp")!, authKey: "k")
        try await client.connect()
        defer { Task { await client.disconnect() } }

        let first = try await client.callTool(name: "Wait", arguments: ["duration": 0])
        XCTAssertNotEqual(first.isError, true)
        await app.evictAllSessions()
        let second = try await client.callTool(name: "Wait", arguments: ["duration": 0])
        XCTAssertNotEqual(second.isError, true)
        let seen = await client.sessionsSeen
        XCTAssertEqual(seen, 2)
    }

    /// A session started with `initialize` over URLSession; its id.
    func initializeSession(port: Int) async throws -> String {
        let initialize = #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"#
        let (_, response) = try await URLSession.shared.data(for: request(
            "http://127.0.0.1:\(port)/mcp", body: initialize,
            headers: ["Content-Type": "application/json", "Accept": "application/json, text/event-stream"]))
        return try XCTUnwrap((response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Mcp-Session-Id"))
    }

    /// HTTP/1.1: a client that closes its connection (FIN) while a session's tool call streams
    /// is noticed, and the call is cancelled instead of running to its end.
    func testClosingTheConnectionCancelsASessionToolCall() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let (port, task, app) = try await start(registry: registry)
        defer { task.cancel(); Task { await app.shutdown() } }
        let id = try await initializeSession(port: port)

        let connection = try await RawConnection(port: port)
        defer { connection.close() }
        let wait = #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":30}}}"#
        try await connection.send(RawConnection.request("POST", sessionID: id, body: wait))
        let head = try await connection.readHead()
        XCTAssertTrue(head.hasPrefix("HTTP/1.1 200"), head)
        connection.finish()

        try await observer.waitFor(error: "Cancelled.", timeout: 1)
        let events = await observer.events
        XCTAssertEqual(events, ["fail Wait t"])
    }

    /// HTTP/1.1: a `GET` stream whose client closed the socket is let go of, so the session's
    /// next `GET` opens instead of getting 409.
    func testADroppedGetLetsTheNextGetOpen() async throws {
        let (port, task, app) = try await start()
        defer { task.cancel(); Task { await app.shutdown() } }
        let id = try await initializeSession(port: port)

        let first = try await RawConnection(port: port)
        defer { first.close() }
        try await first.send(RawConnection.request("GET", sessionID: id))
        let firstHead = try await first.readHead()
        XCTAssertTrue(firstHead.hasPrefix("HTTP/1.1 200"), firstHead)
        first.finish()

        let deadline = Date().addingTimeInterval(2)
        var last = ""
        while Date() < deadline {
            let second = try await RawConnection(port: port)
            defer { second.close() }
            try await second.send(RawConnection.request("GET", sessionID: id))
            last = try await second.readHead()
            if last.hasPrefix("HTTP/1.1 200") { break }
            try await Task.sleep(for: .milliseconds(100))
        }
        XCTAssertTrue(last.hasPrefix("HTTP/1.1 200"), last)
        XCTAssertTrue(last.lowercased().contains("content-type: text/event-stream"), last)
    }

    func testStatelessClientsStillWorkOverSockets() async throws {
        let (port, task, app) = try await start()
        defer { task.cancel(); Task { await app.shutdown() } }
        let url = "http://127.0.0.1:\(port)/mcp"
        let headers = ["Content-Type": "application/json", "Accept": "application/json"]
        // A client that never accepts SSE gets plain JSON and no session, initialize included.
        let initialize = #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"#
        let (data, response) = try await URLSession.shared.data(for: request(url, body: initialize, headers: headers))
        let http = try XCTUnwrap(response as? HTTPURLResponse)
        XCTAssertEqual(http.statusCode, 200)
        XCTAssertNil(http.value(forHTTPHeaderField: "Mcp-Session-Id"))
        XCTAssertEqual(try JSON.parse(data)["result"]?["serverInfo"]?["name"], "desktop-mcp")
        let call = #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":0}}}"#
        let (waited, _) = try await URLSession.shared.data(for: request(url, body: call, headers: headers))
        XCTAssertEqual(try JSON.parse(waited)["result"]?["isError"], false)
        let count = await app.sessions.count
        XCTAssertEqual(count, 0)
    }

    func testStoppingTheServerClosesTheListenerAndItsEventLoops() async throws {
        let (port, task, _) = try await start()
        let url = "http://127.0.0.1:\(port)/mcp"
        let headers = ["Content-Type": "application/json", "Accept": "application/json"]
        let (_, before) = try await URLSession.shared.data(for: request(url, body: #"{"jsonrpc":"2.0","id":1,"method":"ping"}"#, headers: headers))
        XCTAssertEqual((before as? HTTPURLResponse)?.statusCode, 200)

        // Cancelling `serve` closes the listener and shuts the group down before it returns.
        task.cancel()
        let started = Date()
        do { try await task.value } catch is CancellationError {} catch { XCTFail("\(error)") }
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
        do {
            let (_, after) = try await URLSession.shared.data(for: request(url, body: "{}", headers: headers))
            XCTFail("still answering: \((after as? HTTPURLResponse)?.statusCode ?? 0)")
        } catch {
            XCTAssertEqual((error as? URLError)?.code, .cannotConnectToHost, "\(error)")
        }
    }

    func testStoppingTheServerWithARequestInFlightReturnsPromptly() async throws {
        let (port, task, _) = try await start()
        // A client waiting on a long tool call: its handler is blocked on the call.
        let body = #"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":30}}}"#
        let wait = request("http://127.0.0.1:\(port)/mcp", body: body,
                           headers: ["Content-Type": "application/json", "Accept": "application/json"])
        let call = Task { try await URLSession.shared.data(for: wait) }
        try await Task.sleep(for: .milliseconds(300))

        // Connections are children of `serve`: cancelling it cancels them, and it still returns promptly.
        task.cancel()
        let started = Date()
        do { try await task.value } catch is CancellationError {} catch { XCTFail("\(error)") }
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
        // The client's request ends as well (with an error or an empty response), not after 30 seconds.
        _ = try? await call.value
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
    }

    func testIPAllowlistSeesThePeerAddress() async throws {
        let body = #"{"jsonrpc":"2.0","id":1,"method":"ping"}"#
        let headers = ["Content-Type": "application/json", "Accept": "application/json"]
        for (allowlist, expected) in [(["127.0.0.1"], 200), (["10.0.0.0/8"], 403)] {
            let (port, task, _) = try await start(allowlist: allowlist)
            defer { task.cancel() }
            let (data, response) = try await URLSession.shared.data(for: request("http://127.0.0.1:\(port)/mcp", body: body, headers: headers))
            XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, expected, String(decoding: data, as: UTF8.self))
        }
    }

    /// A throwaway self-signed certificate for 127.0.0.1; the directory is removed with `cleanup`.
    func selfSignedCertificate() throws -> (files: NIOHTTPServer.TLSFiles, cleanup: () -> Void) {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("desktop-mcp-tls-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let cert = directory.appendingPathComponent("cert.pem").path
        let key = directory.appendingPathComponent("key.pem").path
        let openssl = Process()
        openssl.executableURL = URL(fileURLWithPath: "/usr/bin/openssl")
        openssl.arguments = ["req", "-x509", "-newkey", "rsa:2048", "-keyout", key, "-out", cert, "-days", "1", "-nodes",
                             "-subj", "/CN=127.0.0.1"]
        openssl.standardError = FileHandle.nullDevice
        openssl.standardOutput = FileHandle.nullDevice
        try openssl.run()
        openssl.waitUntilExit()
        XCTAssertEqual(openssl.terminationStatus, 0)
        return (.init(certificate: cert, privateKey: key), { try? FileManager.default.removeItem(at: directory) })
    }

    func testTLSFromPEMFiles() async throws {
        let (tls, cleanup) = try selfSignedCertificate()
        defer { cleanup() }
        let (port, task, _) = try await start(tls: tls)
        defer { task.cancel() }
        let session = URLSession(configuration: .ephemeral, delegate: TrustEverything(), delegateQueue: nil)
        let (data, response) = try await session.data(for: request(
            "https://127.0.0.1:\(port)/mcp", body: #"{"jsonrpc":"2.0","id":1,"method":"ping"}"#,
            headers: ["Content-Type": "application/json", "Accept": "application/json"]))
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertEqual(try JSON.parse(data)["id"], 1)
    }

    /// A TLS client that offers only `http/1.1` gets the HTTP/1.1 pipeline.
    func testHTTP1IsNegotiatedOverTLS() async throws {
        let (tls, cleanup) = try selfSignedCertificate()
        defer { cleanup() }
        let (port, task, _) = try await start(tls: tls)
        defer { task.cancel() }
        let connection = try await RawConnection(port: port, alpn: ["http/1.1"])
        defer { connection.close() }
        let ping = #"{"jsonrpc":"2.0","id":1,"method":"ping"}"#
        try await connection.send("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\n"
            + "Accept: application/json\r\nContent-Length: \(ping.utf8.count)\r\n\r\n" + ping)
        let head = try await connection.readHead()
        XCTAssertTrue(head.hasPrefix("HTTP/1.1 200"), head)
    }

    func testHTTP2IsNegotiatedOverTLS() async throws {
        let (tls, cleanup) = try selfSignedCertificate()
        defer { cleanup() }
        let (port, task, _) = try await start(authKey: "k", tls: tls)
        defer { task.cancel() }
        let session = URLSession(configuration: .ephemeral, delegate: TrustEverything(), delegateQueue: nil)
        let url = "https://127.0.0.1:\(port)/mcp"
        let headers = ["Content-Type": "application/json", "Authorization": "Bearer k", "Accept": "application/json"]

        // Plain JSON response, and the connection's protocol as URLSession saw it.
        let metrics = ProtocolRecorder()
        let (data, response) = try await session.data(
            for: request(url, body: #"{"jsonrpc":"2.0","id":7,"method":"ping"}"#, headers: headers), delegate: metrics)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertEqual(try JSON.parse(data)["id"], 7)
        XCTAssertEqual(metrics.protocols.current, ["h2"])

        // Several requests at once share one HTTP/2 connection as separate streams.
        let pings = (1...20).map { request(url, body: #"{"jsonrpc":"2.0","id":\#($0),"method":"ping"}"#, headers: headers) }
        let ids = try await withThrowingTaskGroup(of: Value?.self) { group in
            for ping in pings {
                group.addTask {
                    let (data, _) = try await session.data(for: ping)
                    return try JSON.parse(data)["id"]
                }
            }
            return try await group.reduce(into: [Value?]()) { $0.append($1) }
        }
        XCTAssertEqual(Set(ids.compactMap { $0?.intValue }), Set(1...20))

        // Auth still applies per stream.
        let (_, denied) = try await session.data(for: request(url, body: "{}", headers: ["Content-Type": "application/json"]))
        XCTAssertEqual((denied as? HTTPURLResponse)?.statusCode, 401)
    }
}

/// Records the protocol URLSession negotiated for a task ("h2", "http/1.1").
final class ProtocolRecorder: NSObject, URLSessionTaskDelegate {
    let protocols = Locked<[String]>([])
    func urlSession(_ session: URLSession, task: URLSessionTask, didFinishCollecting metrics: URLSessionTaskMetrics) {
        protocols.current = metrics.transactionMetrics.compactMap(\.networkProtocolName)
    }
}

/// Accepts the self-signed test certificate.
final class TrustEverything: NSObject, URLSessionDelegate {
    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge) async
        -> (URLSession.AuthChallengeDisposition, URLCredential?) {
        guard let trust = challenge.protectionSpace.serverTrust else { return (.performDefaultHandling, nil) }
        return (.useCredential, URLCredential(trust: trust))
    }
}
