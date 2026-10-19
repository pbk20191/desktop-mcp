import DesktopMCPCore
import Foundation
import MCP
import Network

enum JSON {
    static func parse(_ text: String) throws -> Value { try JSONDecoder().decode(Value.self, from: Data(text.utf8)) }
    static func parse(_ data: Data) throws -> Value { try JSONDecoder().decode(Value.self, from: data) }
}

extension Value {
    subscript(key: String) -> Value? { objectValue?[key] }
}

actor LineCollector {
    struct Timeout: Error {}
    private var lines: [String] = []

    func append(_ line: String) { lines.append(line) }
    func all() -> [String] { lines }

    func waitFor(count: Int, timeout: TimeInterval) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while lines.count < count {
            if Date() > deadline { throw Timeout() }
            try await Task.sleep(for: .milliseconds(20))
        }
    }
}

actor RecordingObserver: ToolCallObserver {
    struct Timeout: Error {}
    var events: [String] = []
    var errors: [String] = []

    func toolSucceeded(_ tool: String, durationMs: Int, client: Client.Info?) {
        events.append("ok \(tool) \(client?.name ?? "-")")
    }

    func toolFailed(_ tool: String, error: String, durationMs: Int, client: Client.Info?) {
        events.append("fail \(tool) \(client?.name ?? "-")")
        errors.append(error)
    }

    func waitFor(error: String, timeout: TimeInterval) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !errors.contains(error) {
            if Date() > deadline { throw Timeout() }
            try await Task.sleep(for: .milliseconds(20))
        }
    }
}

/// A plain TCP connection (Network.framework) for tests that must close the socket at a chosen
/// moment, which URLSession does not allow. `close` is a graceful close: the peer reads a FIN.
final class RawConnection: Sendable {
    struct Closed: Error {}
    let connection: NWConnection

    /// `alpn`: connect over TLS (trusting any certificate) offering these protocols.
    init(port: Int, alpn: [String]? = nil) async throws {
        let parameters: NWParameters
        if let alpn {
            let tls = NWProtocolTLS.Options()
            for name in alpn { sec_protocol_options_add_tls_application_protocol(tls.securityProtocolOptions, name) }
            sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, _, complete in complete(true) }, .global())
            parameters = NWParameters(tls: tls)
        } else {
            parameters = .tcp
        }
        connection = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: UInt16(port))!, using: parameters)
        let resumed = Locked(false)
        let connection = connection
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            @Sendable func resume(_ result: Result<Void, Error>) {
                let first = resumed.withLock { done in
                    defer { done = true }
                    return !done
                }
                if first { continuation.resume(with: result) }
            }
            connection.stateUpdateHandler = { state in
                switch state {
                case .ready: resume(.success(()))
                case .failed(let error), .waiting(let error): resume(.failure(error))
                case .cancelled: resume(.failure(Closed()))
                default: break
                }
            }
            connection.start(queue: .global())
        }
    }

    func send(_ text: String) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.send(content: Data(text.utf8), completion: .contentProcessed { error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            })
        }
    }

    /// Reads until the response head is complete (a blank line) and returns everything read so far.
    /// The connection is cancelled when `timeout` passes first, which fails the read.
    func readHead(timeout: TimeInterval = 5) async throws -> String {
        let connection = connection
        let watchdog = Task {
            try await Task.sleep(for: .seconds(timeout))
            connection.cancel()
        }
        defer { watchdog.cancel() }
        var received = Data()
        while received.range(of: Data("\r\n\r\n".utf8)) == nil {
            let chunk: Data = try await withCheckedThrowingContinuation { continuation in
                connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, _, error in
                    if let error { return continuation.resume(throwing: error) }
                    if let data, !data.isEmpty { return continuation.resume(returning: data) }
                    continuation.resume(throwing: Closed())
                }
            }
            received.append(chunk)
        }
        return String(decoding: received, as: UTF8.self)
    }

    func close() { connection.cancel() }

    /// Sends a FIN (shuts down the sending side) and keeps the connection otherwise open: the
    /// client is done, whatever the peer still writes.
    func finish() {
        connection.send(content: nil, contentContext: .finalMessage, isComplete: true, completion: .idempotent)
    }

    /// An HTTP/1.1 request to `/mcp` in a session.
    static func request(_ method: String, sessionID: String, body: String = "") -> String {
        var text = "\(method) /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nAccept: application/json, text/event-stream\r\n"
            + "Mcp-Session-Id: \(sessionID)\r\n"
        if method == "POST" { text += "Content-Type: application/json\r\nContent-Length: \(body.utf8.count)\r\n" }
        return text + "\r\n" + body
    }
}

/// An SDK `Client` that re-initialises when the server answers 404 (what the TypeScript SDK
/// clients do by spec; the Swift SDK 0.12.1 client does not: it forgets the session id and
/// throws "Session expired"). Delete it once the SDK client recovers on its own.
actor ReconnectingClient {
    private let endpoint: URL
    private let authKey: String?
    private var client: Client?
    /// Sessions this client has initialised, the first included.
    private(set) var sessionsSeen = 0

    init(endpoint: URL, authKey: String?) {
        self.endpoint = endpoint
        self.authKey = authKey
    }

    /// Initialises a new session, dropping the previous client (and its `GET` retry loop).
    func connect() async throws {
        if let client { await client.disconnect() }
        client = nil
        let authKey = authKey
        let transport = HTTPClientTransport(endpoint: endpoint, streaming: true, requestModifier: { request in
            var request = request
            if let authKey { request.setValue("Bearer \(authKey)", forHTTPHeaderField: "Authorization") }
            return request
        })
        let client = Client(name: "t", version: "0")
        _ = try await client.connect(transport: transport)
        self.client = client
        sessionsSeen += 1
    }

    func disconnect() async {
        await client?.disconnect()
        client = nil
    }

    /// Re-initialises and retries once when the session has expired.
    func callTool(name: String, arguments: [String: Value]) async throws -> (content: [MCP.Tool.Content], isError: Bool?) {
        do {
            return try await connected().callTool(name: name, arguments: arguments)
        } catch let error as MCPError where Self.isSessionExpired(error) {
            try await connect()
            return try await connected().callTool(name: name, arguments: arguments)
        }
    }

    private func connected() async throws -> Client {
        if let client { return client }
        try await connect()
        return client!
    }

    static func isSessionExpired(_ error: MCPError) -> Bool {
        if case .internalError(let message) = error { return message == "Session expired" }
        return false
    }
}
