import Foundation
import MCP

enum JSON {
    static func parse(_ text: String) throws -> Value { try JSONDecoder().decode(Value.self, from: Data(text.utf8)) }
    static func parse(_ data: Data) throws -> Value { try JSONDecoder().decode(Value.self, from: data) }
}

extension Value {
    subscript(key: String) -> Value? { objectValue?[key] }
}

actor LineCollector {
    private var lines: [String] = []

    func append(_ line: String) { lines.append(line) }
    func all() -> [String] { lines }
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
