import Dispatch
import Foundation

/// `connect` over the Launch Agent's Unix socket: stdin's bytes go to the socket and the
/// socket's bytes go to stdout as they arrive, and nothing is parsed, not even into lines. The
/// agent serves the stdio binding on each connection, so the client talks to it as to a stdio
/// server it spawned, and the bridge ends the way that server's session would.
public struct SocketBridge: Sendable {
    /// Nothing accepts at the path (`ENOENT`, `ECONNREFUSED`, ...), as the system's text.
    public struct Refused: Error, CustomStringConvertible {
        public let reason: String
        public var description: String { reason }
    }

    public enum Outcome: Equatable, Sendable {
        /// stdin closed, and the agent answered what it had read and then closed its side.
        case finished
        /// `output` refused a chunk: nobody is left to read stdout.
        case outputClosed
        /// The agent closed the connection (it restarted, shut down, or dropped it) while stdin
        /// was still open: what a stdio server exiting looks like to its client.
        case serverClosed
    }

    public let path: String

    public init(path: String) {
        self.path = path
    }

    /// Connects; `Refused` when nothing accepts at `path`, at once. Nothing is read from stdin
    /// here, so a caller that is refused can still hand stdin to another bridge.
    public func connect() async throws -> Connection {
        let path = path
        let fd: Int32 = try await withCheckedThrowingContinuation { continuation in
            // A blocking `connect(2)`, which is quick for a Unix socket but stays off the
            // cooperative pool all the same.
            DispatchQueue.global().async {
                continuation.resume(with: Result { try Self.open(path) })
            }
        }
        return Connection(fd: fd)
    }

    public struct Connection: Sendable {
        let fd: Int32

        /// Copies bytes both ways until one side ends, then closes the socket. Run once.
        ///
        /// At stdin EOF the socket's write side is shut down and the agent's replies are still
        /// copied until it closes its side (`.finished`). The agent closing first stops the
        /// reading of stdin (`.serverClosed`); `output` returning false closes the socket
        /// fully (`.outputClosed`), which the agent notices on its next write. Closing stdout
        /// is the caller's.
        public func run(input: FileHandle, output: @escaping @Sendable (Data) async -> Bool) async -> Outcome {
            let fd = fd
            let (released, release) = AsyncStream<Void>.makeStream()
            let (received, source) = SocketBridge.reading(fd, onCancel: { release.finish() })
            let inputEnded = Locked(false)
            let outcome = await withTaskGroup(of: Outcome?.self) { group in
                group.addTask {
                    for await chunk in SocketBridge.chunks(of: input) {
                        guard await SocketBridge.write(chunk, to: fd) else { return nil }
                    }
                    guard !Task.isCancelled else { return nil }
                    inputEnded.current = true
                    shutdown(fd, SHUT_WR)
                    return nil
                }
                group.addTask {
                    for await chunk in received {
                        guard await output(chunk) else { return .outputClosed }
                    }
                    return inputEnded.current ? .finished : .serverClosed
                }
                var outcome = Outcome.serverClosed
                for await result in group {
                    guard let result else { continue }
                    outcome = result
                    group.cancelAll()
                    break
                }
                return outcome
            }
            // Only once nothing reads or writes the fd any more, so its number cannot be reused
            // under a write still in flight.
            source.cancel()
            for await _ in released {}
            close(fd)
            return outcome
        }
    }

    // MARK: - Connecting

    /// A connected, non-blocking socket to `path` that carries `SO_NOSIGPIPE`.
    private static func open(_ path: String) throws -> Int32 {
        var address = sockaddr_un()
        let capacity = MemoryLayout.size(ofValue: address.sun_path)
        guard path.utf8.count < capacity else {
            throw Refused(reason: "\(path): longer than the \(capacity - 1) bytes a socket path can have")
        }
        address.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &address.sun_path) { $0.copyBytes(from: path.utf8) }
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw Refused(reason: lastError()) }
        func failure() -> Refused {
            let reason = lastError()
            close(fd)
            return Refused(reason: reason)
        }
        // A write after the agent went away then fails with EPIPE instead of raising SIGPIPE.
        var on: Int32 = 1
        guard setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size)) == 0 else {
            throw failure()
        }
        let connected = withUnsafePointer(to: address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard connected == 0 else { throw failure() }
        let flags = fcntl(fd, F_GETFL)
        guard flags >= 0, fcntl(fd, F_SETFL, flags | O_NONBLOCK) >= 0 else { throw failure() }
        return fd
    }

    // MARK: - Copying

    private static let socketQueue = DispatchQueue(label: "desktop-mcp.socket-bridge")

    /// What arrives on the socket, chunk by chunk; the stream ends at EOF or a failed read. The
    /// source's cancel handler calls `onCancel`, after which it no longer touches `fd`. Built
    /// here, outside any isolation, for the reason `SocketServer.makeSource` gives.
    private static func reading(_ fd: Int32, onCancel: @escaping @Sendable () -> Void)
        -> (AsyncStream<Data>, any DispatchSourceRead) {
        let (stream, continuation) = AsyncStream.makeStream(of: Data.self)
        let source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: socketQueue)
        source.setEventHandler(handler: readHandler(fd, source: source, yieldingTo: continuation))
        source.setCancelHandler(handler: onCancel)
        source.activate()
        return (stream, source)
    }

    /// Reads until the socket has nothing pending. At EOF or on an error the stream ends and the
    /// source is cancelled, so that it stops firing on a socket that stays readable.
    private static func readHandler(_ fd: Int32, source: any DispatchSourceRead,
                                    yieldingTo continuation: AsyncStream<Data>.Continuation) -> @Sendable () -> Void {
        {
            var buffer = [UInt8](repeating: 0, count: 65536)
            while true {
                let count = read(fd, &buffer, buffer.count)
                if count > 0 {
                    continuation.yield(Data(buffer[..<count]))
                    continue
                }
                if count < 0, errno == EINTR { continue }
                if count < 0, errno == EAGAIN { return }
                continuation.finish()
                source.cancel()
                return
            }
        }
    }

    /// stdin, chunk by chunk as it is read, until EOF. Ending the iteration (a cancelled task)
    /// stops the reading.
    private static func chunks(of input: FileHandle) -> AsyncStream<Data> {
        AsyncStream(bufferingPolicy: .unbounded) { continuation in
            input.readabilityHandler = { handle in
                let data = handle.availableData
                if data.isEmpty {
                    handle.readabilityHandler = nil
                    continuation.finish()
                } else {
                    continuation.yield(data)
                }
            }
            continuation.onTermination = { _ in input.readabilityHandler = nil }
        }
    }

    /// Writes all of `data`; false once the socket fails or the task is cancelled. When the
    /// socket is full it waits 10 ms and tries again, as the SDK's stdio transport does.
    private static func write(_ data: Data, to fd: Int32) async -> Bool {
        var offset = 0
        while offset < data.count {
            let written = data.withUnsafeBytes { Darwin.write(fd, $0.baseAddress! + offset, $0.count - offset) }
            if written >= 0 {
                offset += written
                continue
            }
            switch errno {
            case EINTR:
                continue
            case EAGAIN:
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return false }
            default:
                return false
            }
        }
        return true
    }

    private static func lastError() -> String {
        String(cString: strerror(errno))
    }
}
