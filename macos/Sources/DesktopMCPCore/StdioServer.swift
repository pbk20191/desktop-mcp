import Foundation
import Logging
import MCP
import System

public enum StdioServer {
    /// Serves `registry` over `input`/`output` until input hits EOF, a write to output fails
    /// (the client closed it), or the task is cancelled. Cancellation cancels tool calls in flight.
    ///
    /// At EOF the requests already read are still answered before this returns, as a client that
    /// closes stdin after its last request expects. Signal dispositions are the caller's: with
    /// `SIGPIPE` at its default, a write to a closed stdout kills the process instead.
    /// An unterminated trailing line at EOF is dropped, as the SDK transport does.
    /// `label` names the input in log lines (a socket connection's peer, say).
    public static func run(_ registry: ToolRegistry,
                           input: FileDescriptor = .standardInput,
                           output: FileDescriptor = .standardOutput,
                           label: String = "stdin") async {
        let transport = StdoutWatchingTransport(input: input, output: output, label: label)
        let started: StartedServer
        do {
            started = try await registry.start(on: transport)
        } catch {
            log("Could not start serving \(label): \(error)")
            return
        }
        await withTaskCancellationHandler {
            await started.waitUntilCompleted()
        } onCancel: {
            Task { await started.shutdown() }
        }
        await started.shutdown()
    }
}

/// `StdioTransport` with the two ends of a session it does not handle itself.
///
/// The server sends with `try?`, so nothing in the SDK notices a client that closed stdout; a
/// failed write here ends the receive stream, and with it the server loop. And the SDK's stream
/// ends at stdin EOF while responses are still due; here it ends once every request read has
/// been answered (or cancelled by the client, which then expects no answer).
///
/// Sends are written one at a time, in call order. `StdioTransport.send` suspends mid-message
/// when stdout is full, and the server sends each response from its own task, so without the
/// queue a second response could be written into the middle of a first.
private actor StdoutWatchingTransport: Transport {
    nonisolated let logger: Logger
    private let inner: StdioTransport
    /// What the input is called in log lines.
    private let label: String
    private let messages: AsyncThrowingStream<Data, Swift.Error>
    private let continuation: AsyncThrowingStream<Data, Swift.Error>.Continuation
    private var forwarding: Task<Void, Never>?
    /// The last write queued; each write waits for the one before it.
    private var lastWrite: Task<Void, Swift.Error>?
    /// Every write not yet finished, so `disconnect()` can stop them.
    private var writes: [Int: Task<Void, Swift.Error>] = [:]
    private var nextWrite = 0
    private var unanswered: Set<ID> = []
    private var inputEnded = false
    private var outputClosed = false
    /// Set when `disconnect()` starts: a send after that is refused rather than queued, since
    /// it could outlive the wait for the writes already pending.
    private var closing = false

    init(input: FileDescriptor, output: FileDescriptor, label: String) {
        inner = StdioTransport(input: input, output: output)
        self.label = label
        logger = inner.logger
        (messages, continuation) = AsyncThrowingStream.makeStream()
    }

    func connect() async throws {
        try await inner.connect()
        let incoming = await inner.receive()
        forwarding = Task { [label] in
            do {
                for try await data in incoming { self.received(data) }
            } catch {
                log("Could not read \(label): \(error)")
            }
            self.inputDidEnd()
        }
    }

    /// Also stops every write still queued or in progress, and waits for them: `StdioTransport`
    /// keeps retrying a blocked write after its own `disconnect()`, and once the caller closes
    /// the fd (a socket connection) its number can be reused, so a late write would land in
    /// some other file. A send that arrives while this waits (the reply of a tool call that
    /// shutdown just cancelled, say) is refused, as `StdioTransport` refuses one once it is
    /// disconnected, so no write starts that the wait would not see.
    func disconnect() async {
        closing = true
        forwarding?.cancel()
        continuation.finish()
        let pending = Array(writes.values)
        for write in pending { write.cancel() }
        for write in pending { _ = await write.result }
        await inner.disconnect()
    }

    func send(_ data: Data) async throws {
        guard !closing else { throw MCPError.transportError(Errno(rawValue: ENOTCONN)) }
        let previous = lastWrite
        let write = Task { [inner] in
            _ = await previous?.result
            try Task.checkCancellation()
            try await inner.send(data)
        }
        lastWrite = write
        let key = nextWrite
        nextWrite += 1
        writes[key] = write
        defer { writes[key] = nil }
        do {
            try await write.value
        } catch {
            if !Self.isTemporary(error) {
                outputClosed = true
                continuation.finish()
            }
            throw error
        }
        for message in Envelope.all(in: data) where message.method == nil {
            if let id = message.id { unanswered.remove(id) }
        }
        finishIfDrained()
    }

    func receive() -> AsyncThrowingStream<Data, Swift.Error> {
        messages
    }

    private func received(_ data: Data) {
        guard !outputClosed else { return }
        for message in Envelope.all(in: data) {
            if let id = message.id, message.method != nil {
                unanswered.insert(id)
            } else if message.method == CancelledNotification.name, let id = message.cancelledRequest {
                unanswered.remove(id)
            }
        }
        continuation.yield(data)
        finishIfDrained()
    }

    private func inputDidEnd() {
        inputEnded = true
        finishIfDrained()
    }

    private func finishIfDrained() {
        if inputEnded && unanswered.isEmpty { continuation.finish() }
    }

    /// `StdioTransport` retries EAGAIN itself, so this only guards against it surfacing anyway.
    private static func isTemporary(_ error: Swift.Error) -> Bool {
        if case MCPError.transportError(let underlying) = error {
            return MCPError.isResourceTemporarilyUnavailable(underlying)
        }
        return MCPError.isResourceTemporarilyUnavailable(error)
    }

    /// The fields of a JSON-RPC message that say whether it is a request, a response, or a
    /// cancellation. A batch is an array of them.
    private struct Envelope: Decodable {
        var id: ID?
        var method: String?
        var params: Value?

        var cancelledRequest: ID? {
            switch params?.objectValue?["requestId"] {
            case .string(let s)?: return .string(s)
            case .int(let n)?: return .number(n)
            default: return nil
            }
        }

        static func all(in data: Data) -> [Envelope] {
            let decoder = JSONDecoder()
            if let one = try? decoder.decode(Envelope.self, from: data) { return [one] }
            return (try? decoder.decode([Envelope].self, from: data)) ?? []
        }
    }
}
