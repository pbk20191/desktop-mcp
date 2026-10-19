import Foundation
import Logging
import MCP
@testable import DesktopMCPCore

enum JSON {
    static func parse(_ text: String) throws -> Value { try JSONDecoder().decode(Value.self, from: Data(text.utf8)) }
    static func parse(_ data: Data) throws -> Value { try JSONDecoder().decode(Value.self, from: data) }
}

extension Value {
    subscript(key: String) -> Value? { objectValue?[key] }
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

    /// Polls until `error` has been reported, for outcomes that land after the caller stopped
    /// waiting (a cancelled call, a shut-down server).
    func waitFor(error: String, timeout: TimeInterval = 5) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !errors.contains(error) {
            if Date() > deadline { throw Timeout() }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }
}

actor LineCollector {
    struct Timeout: Error {}
    private var lines: [String] = []

    func append(_ line: String) { lines.append(line) }
    func all() -> [String] { lines }

    func waitFor(count: Int, timeout: TimeInterval = 5) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while lines.count < count {
            if Date() > deadline { throw Timeout() }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }
}

/// A client connected to a fresh server from `registry` over an in-memory transport pair.
func connect(_ registry: ToolRegistry, clientName: String = "test") async throws -> (Client, StartedServer) {
    let pair = await InMemoryTransport.createConnectedPair()
    let started = try await registry.start(on: pair.server)
    let client = Client(name: clientName, version: "0")
    _ = try await client.connect(transport: pair.client)
    return (client, started)
}

/// Sends one raw JSON-RPC message to a fresh server from `registry` and returns the first
/// message that comes back, as text. For wire shapes `Client` cannot express.
func exchangeRaw(_ registry: ToolRegistry, _ json: String) async throws -> String {
    struct NoReply: Error {}
    let pair = await InMemoryTransport.createConnectedPair()
    let started = try await registry.start(on: pair.server)
    try await pair.client.connect()
    try await pair.client.send(Data(json.utf8))
    var incoming = await pair.client.receive().makeAsyncIterator()
    let reply = try await incoming.next()
    await started.shutdown()
    guard let reply else { throw NoReply() }
    return String(decoding: reply, as: UTF8.self)
}

/// An in-memory server transport that carries HTTP context, as the HTTP transports do: every
/// request reports the same `RequestHandle.header`, so a test can address its tool calls with
/// `StartedServer.cancel(request:)` without running an HTTP server.
actor ContextualTransport: Transport, HTTPContextProviding {
    private let inner: InMemoryTransport
    private let handle: String

    init(wrapping inner: InMemoryTransport, handle: String) {
        self.inner = inner
        self.handle = handle
    }

    nonisolated var logger: Logger { inner.logger }
    func connect() async throws { try await inner.connect() }
    func disconnect() async { await inner.disconnect() }
    func send(_ data: Data) async throws { try await inner.send(data) }

    /// `receive()` is synchronous but the inner transport's is isolated to that actor, so a
    /// task forwards the inner stream.
    func receive() -> AsyncThrowingStream<Data, Swift.Error> {
        let (stream, continuation) = AsyncThrowingStream<Data, Swift.Error>.makeStream()
        let inner = inner
        let pump = Task {
            do {
                for try await data in await inner.receive() { continuation.yield(data) }
                continuation.finish()
            } catch {
                continuation.finish(throwing: error)
            }
        }
        continuation.onTermination = { _ in pump.cancel() }
        return stream
    }

    func httpRequestContext(for id: ID) async -> MCP.HTTPRequest? {
        MCP.HTTPRequest(method: "POST", headers: [RequestHandle.header: handle])
    }
}

/// `connect(_:clientName:)`, but the server's transport tags every request with `handle`.
func connect(_ registry: ToolRegistry, clientName: String = "test", handle: String) async throws -> (Client, StartedServer) {
    let pair = await InMemoryTransport.createConnectedPair()
    let started = try await registry.start(on: ContextualTransport(wrapping: pair.server, handle: handle))
    let client = Client(name: clientName, version: "0")
    _ = try await client.connect(transport: pair.client)
    return (client, started)
}
