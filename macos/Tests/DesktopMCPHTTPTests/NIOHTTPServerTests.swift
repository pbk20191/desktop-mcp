import Foundation
import DesktopMCPCore
import DesktopMCPHTTP
import XCTest

/// End to end over real sockets: NIOTransportServices listener, HTTP/1.1, optional TLS.
final class NIOHTTPServerTests: XCTestCase {
    func start(_ transport: HTTPTransport, authKey: String? = nil, allowlist: [String]? = nil,
               tls: NIOHTTPServer.TLSFiles? = nil) async throws -> (Int, Task<Void, Error>) {
        let server = await DesktopMCP.makeServer()
        let settings = HTTPApp.Settings(transport: transport, authKey: authKey,
                                        ipAllowlist: try allowlist.map(IPNetwork.parseAllowlist))
        let app = HTTPApp(server: server, settings: settings)
        let listening = try await NIOHTTPServer.bind(host: "127.0.0.1", port: 0, tls: tls)
        let task = Task { try await NIOHTTPServer.serve(listening, app: app) }
        return (try XCTUnwrap(listening.port), task)
    }

    func request(_ url: String, method: String = "POST", body: String? = nil, headers: [String: String] = [:]) -> URLRequest {
        var request = URLRequest(url: URL(string: url)!, timeoutInterval: 10)
        request.httpMethod = method
        request.httpBody = body.map { Data($0.utf8) }
        for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
        return request
    }

    func testStreamableHTTPOverSockets() async throws {
        let (port, task) = try await start(.streamableHTTP, authKey: "k")
        defer { task.cancel() }
        let url = "http://127.0.0.1:\(port)/mcp"
        let body = #"{"jsonrpc":"2.0","id":1,"method":"tools/list"}"#

        let (_, denied) = try await URLSession.shared.data(for: request(url, body: body, headers: ["Content-Type": "application/json"]))
        XCTAssertEqual((denied as? HTTPURLResponse)?.statusCode, 401)

        let headers = ["Content-Type": "application/json", "Authorization": "Bearer k", "Accept": "application/json"]
        let (data, response) = try await URLSession.shared.data(for: request(url, body: body, headers: headers))
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertEqual(try JSONValue.parse(data)["result"]?["tools"]?.arrayValue?.count, 12)

        var streaming = headers
        streaming["Accept"] = "application/json, text/event-stream"
        let (sse, sseResponse) = try await URLSession.shared.data(for: request(url, body: body, headers: streaming))
        XCTAssertEqual((sseResponse as? HTTPURLResponse)?.value(forHTTPHeaderField: "Content-Type"), "text/event-stream")
        XCTAssertTrue(String(decoding: sse, as: UTF8.self).hasPrefix("event: message\r\ndata: {"))
    }

    func testStoppingTheServerClosesTheListenerAndItsEventLoops() async throws {
        let (port, task) = try await start(.streamableHTTP)
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

    func testStoppingTheServerWithAnOpenStreamReturnsPromptly() async throws {
        let (port, task) = try await start(.sse)
        // A client holding an event stream open: its handler is blocked on the stream.
        let (bytes, response) = try await URLSession.shared.bytes(for: request("http://127.0.0.1:\(port)/sse", method: "GET"))
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        var lines = bytes.lines.makeAsyncIterator()
        let first = try await lines.next()
        XCTAssertEqual(first, "event: endpoint")

        // Connections are children of `serve`: cancelling it cancels them, and it still returns promptly.
        task.cancel()
        let started = Date()
        do { try await task.value } catch is CancellationError {} catch { XCTFail("\(error)") }
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
        // The client's stream ends as well (with EOF or a connection error).
        do { while let _ = try await lines.next() {} } catch {}
    }

    func testIPAllowlistSeesThePeerAddress() async throws {
        let body = #"{"jsonrpc":"2.0","id":1,"method":"ping"}"#
        let headers = ["Content-Type": "application/json", "Accept": "application/json"]
        for (allowlist, expected) in [(["127.0.0.1"], 200), (["10.0.0.0/8"], 403)] {
            let (port, task) = try await start(.streamableHTTP, allowlist: allowlist)
            defer { task.cancel() }
            let (data, response) = try await URLSession.shared.data(for: request("http://127.0.0.1:\(port)/mcp", body: body, headers: headers))
            XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, expected, String(decoding: data, as: UTF8.self))
        }
    }

    func testSSEOverSockets() async throws {
        let (port, task) = try await start(.sse)
        defer { task.cancel() }
        let (bytes, _) = try await URLSession.shared.bytes(for: request("http://127.0.0.1:\(port)/sse", method: "GET"))
        var lines = bytes.lines.makeAsyncIterator()
        let first = try await lines.next()
        XCTAssertEqual(first, "event: endpoint")
        let second = try await lines.next()
        let endpoint = try XCTUnwrap(second).replacingOccurrences(of: "data: ", with: "")

        let post = request("http://127.0.0.1:\(port)\(endpoint)", body: #"{"jsonrpc":"2.0","id":9,"method":"ping"}"#,
                           headers: ["Content-Type": "application/json"])
        let (_, accepted) = try await URLSession.shared.data(for: post)
        XCTAssertEqual((accepted as? HTTPURLResponse)?.statusCode, 202)

        while let line = try await lines.next() {
            if line.hasPrefix("data: ") {
                XCTAssertEqual(try JSONValue.parse(String(line.dropFirst(6)))["id"], 9)
                break
            }
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
        let (port, task) = try await start(.streamableHTTP, tls: tls)
        defer { task.cancel() }
        let session = URLSession(configuration: .ephemeral, delegate: TrustEverything(), delegateQueue: nil)
        let (data, response) = try await session.data(for: request(
            "https://127.0.0.1:\(port)/mcp", body: #"{"jsonrpc":"2.0","id":1,"method":"ping"}"#,
            headers: ["Content-Type": "application/json", "Accept": "application/json"]))
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertEqual(try JSONValue.parse(data)["id"], 1)
    }

    func testHTTP2IsNegotiatedOverTLS() async throws {
        let (tls, cleanup) = try selfSignedCertificate()
        defer { cleanup() }
        let (port, task) = try await start(.streamableHTTP, authKey: "k", tls: tls)
        defer { task.cancel() }
        let session = URLSession(configuration: .ephemeral, delegate: TrustEverything(), delegateQueue: nil)
        let url = "https://127.0.0.1:\(port)/mcp"
        let headers = ["Content-Type": "application/json", "Authorization": "Bearer k"]

        // Plain JSON response, and the connection's protocol as URLSession saw it.
        let metrics = ProtocolRecorder()
        let (data, response) = try await session.data(
            for: request(url, body: #"{"jsonrpc":"2.0","id":7,"method":"ping"}"#, headers: headers), delegate: metrics)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertEqual(try JSONValue.parse(data)["id"], 7)
        XCTAssertEqual(metrics.protocols.current, ["h2"])

        // A streamed (SSE) response has no content-length; over HTTP/2 it must still arrive whole.
        let streaming = headers.merging(["Accept": "text/event-stream"]) { $1 }
        let (sse, sseResponse) = try await session.data(
            for: request(url, body: #"{"jsonrpc":"2.0","id":8,"method":"tools/list"}"#, headers: streaming), delegate: metrics)
        XCTAssertEqual((sseResponse as? HTTPURLResponse)?.value(forHTTPHeaderField: "content-type"), "text/event-stream")
        let text = String(decoding: sse, as: UTF8.self)
        XCTAssertTrue(text.hasPrefix("event: message\r\ndata: "), text)
        XCTAssertTrue(text.contains(#""id":8"#), text)
        XCTAssertEqual(metrics.protocols.current, ["h2"])

        // Several requests at once share one HTTP/2 connection as separate streams.
        let pings = (1...20).map { request(url, body: #"{"jsonrpc":"2.0","id":\#($0),"method":"ping"}"#, headers: headers) }
        let ids = try await withThrowingTaskGroup(of: JSONValue?.self) { group in
            for ping in pings {
                group.addTask {
                    let (data, _) = try await session.data(for: ping)
                    return try JSONValue.parse(data)["id"]
                }
            }
            return try await group.reduce(into: [JSONValue?]()) { $0.append($1) }
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
