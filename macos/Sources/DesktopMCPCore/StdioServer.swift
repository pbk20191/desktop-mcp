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
    public static func run(_ registry: ToolRegistry,
                           input: FileDescriptor = .standardInput,
                           output: FileDescriptor = .standardOutput) async {
        let transport = StdoutWatchingTransport(input: input, output: output)
        let started: StartedServer
        do {
            started = try await registry.start(on: transport)
        } catch {
            log("Could not start serving stdio: \(error)")
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
private actor StdoutWatchingTransport: Transport {
    nonisolated let logger: Logger
    private let inner: StdioTransport
    private let messages: AsyncThrowingStream<Data, Swift.Error>
    private let continuation: AsyncThrowingStream<Data, Swift.Error>.Continuation
    private var forwarding: Task<Void, Never>?
    private var unanswered: Set<ID> = []
    private var inputEnded = false
    private var outputClosed = false

    init(input: FileDescriptor, output: FileDescriptor) {
        inner = StdioTransport(input: input, output: output)
        logger = inner.logger
        (messages, continuation) = AsyncThrowingStream.makeStream()
    }

    func connect() async throws {
        try await inner.connect()
        let incoming = await inner.receive()
        forwarding = Task {
            do {
                for try await data in incoming { self.received(data) }
            } catch {
                log("Could not read stdin: \(error)")
            }
            self.inputDidEnd()
        }
    }

    func disconnect() async {
        forwarding?.cancel()
        continuation.finish()
        await inner.disconnect()
    }

    func send(_ data: Data) async throws {
        do {
            try await inner.send(data)
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
