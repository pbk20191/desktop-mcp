import Dispatch
import Foundation
import launch
import System

/// The stdio binding served over a Unix domain socket: every accepted connection gets its own
/// `StdioServer.run`, with the stdio rules as they are (EOF answers what was read, then ends;
/// a failed write ends the server), and the connection is closed when that server returns.
///
/// Only connections from this process's effective uid are served; the socket file is `0600`
/// as well, but `bind` creates it before `chmod` can narrow it.
public actor SocketServer {
    /// Another server answered on the socket path: it is live, and is left alone.
    public struct Busy: Error, CustomStringConvertible {
        public var description: String { "another server is listening on the socket" }
    }

    /// The socket could not be taken: a file that is not a socket is in the way, or a system
    /// call failed.
    public struct CannotListen: Error, CustomStringConvertible {
        public let reason: String
        public var description: String { reason }
    }

    private let registry: ToolRegistry
    /// What the socket is called in log lines.
    private let name: String
    /// The path this process bound, and so unlinks at shutdown.
    private let ownedPath: String?
    private let activated: Bool
    /// One log line per connection accepted and per connection ended.
    private let debug: Bool
    private let sources: [any DispatchSourceRead]
    private let accepted: AsyncStream<Int32>
    private let continuation: AsyncStream<Int32>.Continuation
    /// Each connection's server, by its fd. A connection removes itself before closing its fd,
    /// so a later accept that reuses the number cannot collide with it.
    private var live: [Int32: Task<Void, Never>] = [:]
    private var isRunning = false
    private var isShutDown = false

    /// Connections whose server is still running.
    public var connections: Int { live.count }

    /// Whether the listeners came from launchd rather than being bound here.
    public var activatedByLaunchd: Bool { activated }

    /// Listens at `path`. With a `launchdName`, launchd is asked first for the listeners of that
    /// `Sockets` key, and when it has them they are used and nothing is bound or unlinked here.
    /// Without one, or when the process was not started by launchd or has no such key, the path
    /// is bound here: a stale socket file left by a server that is gone is replaced, a live one
    /// is `Busy`, and anything that is not a socket is `CannotListen` and is never removed. The
    /// file is made `0600` at once.
    public static func listen(path: String, launchdName: String?, registry: ToolRegistry,
                              debug: Bool = false) async throws -> SocketServer {
        if let launchdName, let listeners = try activate(launchdName) {
            try adopt(listeners)
            return SocketServer(listeners: listeners, name: path, ownedPath: nil, activated: true,
                                registry: registry, debug: debug)
        }
        let listener = try bind(path)
        do {
            try setNonBlocking(listener)
        } catch {
            close(listener)
            unlink(path)
            throw error
        }
        return SocketServer(listeners: [listener], name: path, ownedPath: path, activated: false,
                            registry: registry, debug: debug)
    }

    /// Serves on listeners that are already bound and listening (from launchd, or a test). They
    /// become this server's: it makes them non-blocking and closes them at shutdown, or at once
    /// if that fails. Nothing is unlinked.
    public static func listen(fileDescriptors: [FileDescriptor], registry: ToolRegistry,
                              debug: Bool = false) async throws -> SocketServer {
        let listeners = fileDescriptors.map(\.rawValue)
        try adopt(listeners)
        return SocketServer(listeners: listeners, name: boundPath(listeners[0]) ?? "socket", ownedPath: nil,
                            activated: false, registry: registry, debug: debug)
    }

    private init(listeners: [Int32], name: String, ownedPath: String?, activated: Bool,
                 registry: ToolRegistry, debug: Bool) {
        let (accepted, continuation) = AsyncStream.makeStream(of: Int32.self)
        self.accepted = accepted
        self.continuation = continuation
        self.registry = registry
        self.name = name
        self.ownedPath = ownedPath
        self.activated = activated
        self.debug = debug
        sources = listeners.map { Self.makeSource(listener: $0, yieldingTo: continuation) }
    }

    /// Serves every connection accepted until `shutdown()`, then returns once their servers
    /// have. Called once; connections accepted before it are served when it starts.
    public func run() async {
        guard !isRunning else { return }
        isRunning = true
        for await fd in accepted {
            if isShutDown {
                close(fd)
            } else {
                serve(fd)
            }
        }
        await shutdown()
    }

    /// Stops accepting, closes the listeners, shuts every connection's server down (cancelling
    /// its tool calls), waits for them, and unlinks the socket file if this process bound it.
    public func shutdown() async {
        guard !isShutDown else {
            await waitForConnections()
            return
        }
        isShutDown = true
        for source in sources { source.cancel() }
        continuation.finish()
        for task in live.values { task.cancel() }
        await waitForConnections()
        if let ownedPath { unlink(ownedPath) }
    }

    private func waitForConnections() async {
        for task in Array(live.values) { await task.value }
    }

    private func serve(_ fd: Int32) {
        var uid: uid_t = 0
        var gid: gid_t = 0
        guard getpeereid(fd, &uid, &gid) == 0 else {
            log("Could not identify a peer on \(name): \(Self.lastError())")
            close(fd)
            return
        }
        guard uid == geteuid() else {
            log("Refused a connection on \(name) from uid \(uid)")
            close(fd)
            return
        }
        // Without it a write to a peer that went away raises SIGPIPE, which kills the process
        // unless it is ignored; with it the write fails with EPIPE and that server ends.
        var on: Int32 = 1
        guard setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size)) == 0 else {
            log("Could not set up a connection on \(name): \(Self.lastError())")
            close(fd)
            return
        }
        // Best effort: with the default 8 KiB and the SDK transport's 10 ms sleep on EAGAIN, a
        // large reply (a Snapshot image) would crawl.
        var bufferSize: Int32 = 1 << 20
        _ = setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &bufferSize, socklen_t(MemoryLayout<Int32>.size))
        do {
            try Self.setNonBlocking(fd)
        } catch {
            log("Could not set up a connection on \(name): \(error)")
            close(fd)
            return
        }
        let label = "\(name) peer \(Self.peerPID(fd).map(String.init) ?? "?")"
        if debug { log("Accepted a connection: \(label)") }
        let registry = registry
        live[fd] = Task {
            let descriptor = FileDescriptor(rawValue: fd)
            await StdioServer.run(registry, input: descriptor, output: descriptor, label: label)
            self.ended(fd, label: label)
            close(fd)
        }
    }

    private func ended(_ fd: Int32, label: String) {
        live[fd] = nil
        if debug { log("Connection ended: \(label)") }
    }

    // MARK: - Accepting

    private static let acceptQueue = DispatchQueue(label: "desktop-mcp.socket-accept")

    /// A read source on `listener` that accepts into `continuation`. Built here, outside the
    /// actor's isolation: Dispatch runs the handlers on its own queue, and a closure formed in an
    /// actor method would be inferred actor-isolated and trap there under Swift 6's checks.
    private static func makeSource(listener: Int32,
                                   yieldingTo continuation: AsyncStream<Int32>.Continuation) -> any DispatchSourceRead {
        let source = DispatchSource.makeReadSource(fileDescriptor: listener, queue: acceptQueue)
        source.setEventHandler(handler: acceptHandler(listener: listener, yieldingTo: continuation))
        source.setCancelHandler(handler: closeHandler(listener))
        source.activate()
        return source
    }

    /// Accepts until the listener has nothing pending. A connection accepted after shutdown has
    /// finished the stream is closed. Any failure other than "nothing pending" ends the stream,
    /// and with it `run()`.
    private static func acceptHandler(listener: Int32,
                                      yieldingTo continuation: AsyncStream<Int32>.Continuation) -> @Sendable () -> Void {
        {
            while true {
                let fd = accept(listener, nil, nil)
                if fd >= 0 {
                    if case .terminated = continuation.yield(fd) { close(fd) }
                    continue
                }
                switch errno {
                case EINTR, ECONNABORTED:
                    continue
                case EAGAIN:
                    return
                default:
                    log("Could not accept on the socket: \(lastError())")
                    continuation.finish()
                    return
                }
            }
        }
    }

    private static func closeHandler(_ listener: Int32) -> @Sendable () -> Void {
        { close(listener) }
    }

    // MARK: - launchd

    /// The listeners launchd holds for `name`, or nil when this process was not started by
    /// launchd or its job has no such socket (`ENOENT`, `ESRCH`). The descriptors are blocking.
    private static func activate(_ name: String) throws -> [Int32]? {
        var fds: UnsafeMutablePointer<Int32>?
        var count = 0
        let code = withUnsafeMutablePointer(to: &fds) {
            // `int * _Nonnull * _Nullable`: an optional pointer has the same layout, and stays nil
            // when launchd hands nothing back.
            $0.withMemoryRebound(to: UnsafeMutablePointer<Int32>.self, capacity: 1) {
                launch_activate_socket(name, $0, &count)
            }
        }
        defer { free(fds) }
        switch code {
        case 0:
            guard let fds, count > 0 else { throw CannotListen(reason: "launchd has no sockets for \(name)") }
            return Array(UnsafeBufferPointer(start: fds, count: count))
        case ENOENT, ESRCH:
            return nil
        default:
            throw CannotListen(reason: "launch_activate_socket: \(String(cString: strerror(code)))")
        }
    }

    /// Makes listeners handed over (by launchd or a test) non-blocking. On failure every one of
    /// them is closed, so that launchd stops queueing connections nobody would accept.
    private static func adopt(_ listeners: [Int32]) throws {
        guard !listeners.isEmpty else { throw CannotListen(reason: "no listening sockets") }
        do {
            for fd in listeners { try setNonBlocking(fd) }
        } catch {
            for fd in listeners { close(fd) }
            throw error
        }
    }

    // MARK: - Binding

    /// Binds and listens at `path` (blocking; the caller makes it non-blocking).
    private static func bind(_ path: String) throws -> Int32 {
        let address = try address(path)
        var info = stat()
        if stat(path, &info) == 0 {
            guard info.st_mode & S_IFMT == S_IFSOCK else {
                throw CannotListen(reason: "\(path) exists and is not a socket")
            }
            try replaceStale(path, at: address)
        } else if errno != ENOENT {
            throw CannotListen(reason: "\(path): \(lastError())")
        }
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw CannotListen(reason: "socket: \(lastError())") }
        guard withSockaddr(address, { Darwin.bind(fd, $0, $1) }) == 0 else {
            let reason = "\(path): \(lastError())"
            close(fd)
            throw CannotListen(reason: reason)
        }
        // At once and without `umask`, which is process-wide; the peer uid check covers the gap.
        guard chmod(path, 0o600) == 0, Darwin.listen(fd, 16) == 0 else {
            let reason = "\(path): \(lastError())"
            close(fd)
            unlink(path)
            throw CannotListen(reason: reason)
        }
        return fd
    }

    /// Unlinks the socket file at `path` when nothing answers on it; `Busy` when something does.
    private static func replaceStale(_ path: String, at address: sockaddr_un) throws {
        let probe = socket(AF_UNIX, SOCK_STREAM, 0)
        guard probe >= 0 else { throw CannotListen(reason: "socket: \(lastError())") }
        defer { close(probe) }
        if withSockaddr(address, { Darwin.connect(probe, $0, $1) }) == 0 { throw Busy() }
        guard errno == ECONNREFUSED else { throw CannotListen(reason: "\(path): \(lastError())") }
        guard unlink(path) == 0 || errno == ENOENT else { throw CannotListen(reason: "\(path): \(lastError())") }
    }

    private static func address(_ path: String) throws -> sockaddr_un {
        var address = sockaddr_un()
        let capacity = MemoryLayout.size(ofValue: address.sun_path)
        guard path.utf8.count < capacity else {
            throw CannotListen(reason: "\(path): longer than the \(capacity - 1) bytes a socket path can have")
        }
        address.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &address.sun_path) { $0.copyBytes(from: path.utf8) }
        return address
    }

    private static func withSockaddr(_ address: sockaddr_un,
                                     _ body: (UnsafePointer<sockaddr>, socklen_t) -> Int32) -> Int32 {
        withUnsafePointer(to: address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                body($0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
    }

    // MARK: - Descriptors

    private static func setNonBlocking(_ fd: Int32) throws {
        let flags = fcntl(fd, F_GETFL)
        guard flags >= 0, fcntl(fd, F_SETFL, flags | O_NONBLOCK) >= 0 else {
            throw CannotListen(reason: "fcntl: \(lastError())")
        }
    }

    /// The path a listener is bound to, for log lines.
    private static func boundPath(_ fd: Int32) -> String? {
        var address = sockaddr_un()
        var length = socklen_t(MemoryLayout<sockaddr_un>.size)
        let result = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) }
        }
        guard result == 0, address.sun_family == sa_family_t(AF_UNIX) else { return nil }
        let path = withUnsafeBytes(of: address.sun_path) { String(decoding: $0.prefix { $0 != 0 }, as: UTF8.self) }
        return path.isEmpty ? nil : path
    }

    private static func peerPID(_ fd: Int32) -> pid_t? {
        var pid: pid_t = 0
        var length = socklen_t(MemoryLayout<pid_t>.size)
        return getsockopt(fd, SOL_LOCAL, LOCAL_PEERPID, &pid, &length) == 0 ? pid : nil
    }

    private static func lastError() -> String {
        String(cString: strerror(errno))
    }
}
