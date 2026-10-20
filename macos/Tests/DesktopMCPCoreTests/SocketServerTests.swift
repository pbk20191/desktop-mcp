import Foundation
import MCP
import System
import XCTest
@testable import DesktopMCPCore

final class SocketServerTests: XCTestCase {
    override func setUp() {
        super.setUp()
        // A write to a closed socket must fail with EPIPE rather than kill the test process; the
        // client side's fds do not carry SO_NOSIGPIPE.
        signal(SIGPIPE, SIG_IGN)
    }

    struct Timeout: Error {}

    /// A fresh socket path under the temporary directory, short enough for `sun_path`.
    func socketPath() -> String {
        let path = FileManager.default.temporaryDirectory
            .appendingPathComponent("dm-\(UInt32.random(in: 0...9999)).sock").path
        XCTAssertLessThan(path.utf8.count, 104)
        try? FileManager.default.removeItem(atPath: path)
        return path
    }

    /// A connected, blocking client socket to `path`.
    func connectRaw(_ path: String) throws -> Int32 {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw Errno(rawValue: errno) }
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: path.utf8.prefix(raw.count - 1))
        }
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard result == 0 else {
            let error = Errno(rawValue: errno)
            close(fd)
            throw error
        }
        return fd
    }

    /// An SDK client over a connection to `path`, initialised.
    func client(_ path: String) async throws -> (Client, FileDescriptor) {
        let fd = FileDescriptor(rawValue: try connectRaw(path))
        let client = Client(name: "t", version: "0")
        _ = try await client.connect(transport: StdioTransport(input: fd, output: fd))
        return (client, fd)
    }

    /// A server listening at `path`, with its accept loop running.
    func serving(_ registry: ToolRegistry, path: String) async throws -> (SocketServer, Task<Void, Never>) {
        let server = try await SocketServer.listen(path: path, launchdName: nil, registry: registry)
        return (server, Task { await server.run() })
    }

    func send(_ fd: Int32, _ text: String) {
        let bytes = Array(text.utf8)
        var sent = 0
        while sent < bytes.count {
            let n = bytes[sent...].withUnsafeBytes { write(fd, $0.baseAddress, $0.count) }
            guard n > 0 else { return }
            sent += n
        }
    }

    /// Everything the server writes to `fd` until it closes its side, as JSON lines.
    func readToEOF(_ fd: Int32) async throws -> [Value] {
        let data = await Task.detached {
            var data = Data()
            var buffer = [UInt8](repeating: 0, count: 65536)
            while true {
                let n = read(fd, &buffer, buffer.count)
                if n <= 0 { break }
                data.append(contentsOf: buffer[..<n])
            }
            return data
        }.value
        return try data.split(separator: 0x0A).map { try JSON.parse(Data($0)) }
    }

    /// Polls `condition` until it holds; throws once `seconds` pass first.
    func waitUntil(within seconds: Double, _ condition: () async -> Bool) async throws {
        let deadline = Date().addingTimeInterval(seconds)
        while !(await condition()) {
            if Date() > deadline { throw Timeout() }
            try await Task.sleep(for: .milliseconds(20))
        }
    }

    static let initializeLine = #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"# + "\n"
    static func wait(id: Int, seconds: Int) -> String {
        #"{"jsonrpc":"2.0","id":\#(id),"method":"tools/call","params":{"name":"Wait","arguments":{"duration":\#(seconds)}}}"# + "\n"
    }

    func testEachConnectionGetsItsOwnServer() async throws {
        let path = socketPath()
        let (server, run) = try await serving(await DesktopMCP.makeRegistry(), path: path)
        let (a, aFD) = try await client(path)
        let (b, bFD) = try await client(path)
        _ = try await a.listTools()
        _ = try await b.listTools()
        let result = try await a.callTool(name: "Wait", arguments: ["duration": 0])
        XCTAssertEqual(result.isError, false)
        let count = await server.connections
        XCTAssertEqual(count, 2)
        var info = stat()
        XCTAssertEqual(stat(path, &info), 0)
        XCTAssertEqual(info.st_mode & 0o777, 0o600)
        await a.disconnect()
        await b.disconnect()
        await server.shutdown()
        await run.value
        try aFD.close()
        try bFD.close()
        XCTAssertFalse(FileManager.default.fileExists(atPath: path), "the owner unlinks the socket file")
    }

    func testHalfCloseDrainsThenTheAgentCloses() async throws {
        let path = socketPath()
        let (server, run) = try await serving(await DesktopMCP.makeRegistry(), path: path)
        let fd = try connectRaw(path)
        send(fd, Self.initializeLine + Self.wait(id: 2, seconds: 1))
        Darwin.shutdown(fd, SHUT_WR)
        let responses = try await readToEOF(fd)  // returns once the agent closes its side
        XCTAssertEqual(responses.count, 2)
        let reply = responses.first { $0["id"]?.intValue == 2 }
        XCTAssertEqual(reply?["result"]?["content"]?.arrayValue?.first?["text"]?.stringValue,
                       "Waited for 1 seconds.")
        try await waitUntil(within: 2) { await server.connections == 0 }
        close(fd)
        await server.shutdown()
        await run.value
    }

    func testFullCloseMidCallIsNoticedWhenTheReplyFails() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let path = socketPath()
        let (server, run) = try await serving(registry, path: path)
        let fd = try connectRaw(path)
        send(fd, Self.initializeLine + Self.wait(id: 2, seconds: 1))
        try await waitUntil(within: 2) { await server.connections == 1 }
        close(fd)
        try await waitUntil(within: 3) { await observer.events.contains { $0.hasPrefix("ok Wait") } }
        try await waitUntil(within: 3) { await server.connections == 0 }
        await server.shutdown()
        await run.value
    }

    func testASilentPeerClosingFreesItsServer() async throws {
        let path = socketPath()
        let (server, run) = try await serving(await DesktopMCP.makeRegistry(), path: path)
        let fd = try connectRaw(path)
        try await waitUntil(within: 2) { await server.connections == 1 }
        close(fd)
        try await waitUntil(within: 2) { await server.connections == 0 }
        await server.shutdown()
        await run.value
    }

    /// A registry with one tool, "Big", that returns `size` bytes of text.
    func bigRegistry(size: Int) async -> ToolRegistry {
        let registry = ToolRegistry(name: "t", version: "0", instructions: "")
        await registry.register(Tool(
            name: "Big", description: "Returns a lot of text.",
            inputSchema: .object(["type": .string("object")]),
            annotations: ToolAnnotations(title: "Big", readOnly: true, destructive: false,
                                         idempotent: true, openWorld: false),
            handler: { _ in [.text(String(repeating: "x", count: size))] }))
        return registry
    }

    /// Adds "Hold" to `registry`: it counts each call that starts in `started`, then waits until
    /// it is cancelled.
    func registerHold(on registry: ToolRegistry, started: Locked<Int>) async {
        await registry.register(Tool(
            name: "Hold", description: "Waits until cancelled.",
            inputSchema: .object(["type": .string("object")]),
            annotations: ToolAnnotations(title: "Hold", readOnly: true, destructive: false,
                                         idempotent: true, openWorld: false),
            handler: { _ in
                started.withLock { $0 += 1 }
                try await Task.sleep(for: .seconds(60))
                return []
            }))
    }

    func testShutdownStopsAWriteBlockedOnAPeerThatDoesNotRead() async throws {
        // A reply bigger than the send buffer, to a client that never reads: the SDK transport
        // retries the write every 10 ms. Once shutdown returns and the fd is closed, its number
        // is free for the next socket; the retrying write must not land there, nor may the
        // replies of the calls in flight, which shutdown cancels just before it disconnects.
        let path = socketPath()
        let registry = await bigRegistry(size: 4 * 1024 * 1024)
        let held = Locked(0)
        await registerHold(on: registry, started: held)
        let (server, run) = try await serving(registry, path: path)
        let fd = try connectRaw(path)
        // Several, so that their replies may come while the transport disconnects.
        let holds = 16
        for id in 2..<(2 + holds) {
            send(fd, #"{"jsonrpc":"2.0","id":\#(id),"method":"tools/call","params":{"name":"Hold","arguments":{}}}"# + "\n")
        }
        try await waitUntil(within: 5) { held.current == holds }
        send(fd, #"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Big","arguments":{}}}"# + "\n")
        try await Task.sleep(for: .milliseconds(500))  // the write is now blocked
        let clock = ContinuousClock()
        let started = clock.now
        await server.shutdown()
        XCTAssertLessThan(clock.now - started, .seconds(2))
        let count = await server.connections
        XCTAssertEqual(count, 0)
        await run.value

        // Both ends are writable, so whichever end takes the freed number would carry a stray
        // write to the other.
        var pair: [Int32] = [0, 0]
        XCTAssertEqual(socketpair(AF_UNIX, SOCK_STREAM, 0, &pair), 0)
        defer { close(pair[0]); close(pair[1]) }
        for end in pair { _ = fcntl(end, F_SETFL, fcntl(end, F_GETFL) | O_NONBLOCK) }
        try await Task.sleep(for: .milliseconds(200))
        var buffer = [UInt8](repeating: 0, count: 4096)
        XCTAssertLessThan(read(pair[0], &buffer, buffer.count), 1, "nothing may arrive on a socket reusing the number")
        XCTAssertLessThan(read(pair[1], &buffer, buffer.count), 1, "nothing may arrive on a socket reusing the number")
        close(fd)
    }

    func testALargeReplyArrivesWhole() async throws {
        let size = 2 * 1024 * 1024
        let registry = await bigRegistry(size: size)
        let path = socketPath()
        let (server, run) = try await serving(registry, path: path)

        // Timed with a plain blocking reader: the SDK client's own read loop (4 KiB reads, each
        // rescanning the whole pending buffer for a newline) takes seconds over 2 MiB in a debug
        // build whatever the server does, so it would time the client, not the socket.
        let raw = try connectRaw(path)
        let clock = ContinuousClock()
        let started = clock.now
        send(raw, #"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Big","arguments":{}}}"# + "\n")
        Darwin.shutdown(raw, SHUT_WR)
        let responses = try await readToEOF(raw)
        let elapsed = clock.now - started
        close(raw)
        XCTAssertEqual(responses.first?["result"]?["content"]?.arrayValue?.first?["text"]?.stringValue?.count, size)
        XCTAssertLessThan(elapsed, .seconds(1))

        // And the SDK client gets it whole.
        let (client, fd) = try await client(path)
        let result = try await client.callTool(name: "Big")
        guard case .text(let text, _, _)? = result.content.first else {
            return XCTFail("expected text, got \(result.content)")
        }
        XCTAssertEqual(text.count, size)
        await client.disconnect()
        await server.shutdown()
        await run.value
        try fd.close()
    }

    func testBusyWhenAnotherServerListens() async throws {
        let path = socketPath()
        let (server, run) = try await serving(await DesktopMCP.makeRegistry(), path: path)
        do {
            _ = try await SocketServer.listen(path: path, launchdName: nil, registry: await DesktopMCP.makeRegistry())
            XCTFail("a second listen on a live socket must throw")
        } catch is SocketServer.Busy {
        }
        await server.shutdown()
        await run.value
    }

    func testStaleSocketFileIsReplaced() async throws {
        let path = socketPath()
        // A listener that went away without unlinking its file.
        let stale = socket(AF_UNIX, SOCK_STREAM, 0)
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: path.utf8.prefix(raw.count - 1))
        }
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(stale, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        XCTAssertEqual(bound, 0)
        XCTAssertEqual(Darwin.listen(stale, 1), 0)
        close(stale)
        XCTAssertTrue(FileManager.default.fileExists(atPath: path))

        let (server, run) = try await serving(await DesktopMCP.makeRegistry(), path: path)
        let (client, fd) = try await client(path)
        _ = try await client.listTools()
        await client.disconnect()
        await server.shutdown()
        await run.value
        try fd.close()
    }

    func testRegularFileAtPathIsRefusedAndKept() async throws {
        let path = socketPath()
        try "keep".write(toFile: path, atomically: false, encoding: .utf8)
        defer { try? FileManager.default.removeItem(atPath: path) }
        do {
            _ = try await SocketServer.listen(path: path, launchdName: nil, registry: await DesktopMCP.makeRegistry())
            XCTFail("a regular file at the path must be refused")
        } catch is SocketServer.CannotListen {
        }
        XCTAssertEqual(try String(contentsOfFile: path, encoding: .utf8), "keep")
    }

    func testMissingDirectoryIsCannotListen() async {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent("nope-\(UInt32.random(in: 0...9999))/x.sock").path
        do {
            _ = try await SocketServer.listen(path: path, launchdName: nil, registry: await DesktopMCP.makeRegistry())
            XCTFail("a missing directory must be refused")
        } catch is SocketServer.CannotListen {
        } catch {
            XCTFail("expected CannotListen, got \(error)")
        }
    }

    func testAnUnknownLaunchdNameFallsBackToBinding() async throws {
        // Not run by launchd (or no such key): launch_activate_socket fails with ENOENT/ESRCH and
        // the path is bound here instead.
        let path = socketPath()
        let server = try await SocketServer.listen(path: path, launchdName: "NoSuchSocket",
                                                   registry: await DesktopMCP.makeRegistry())
        let run = Task { await server.run() }
        let activated = await server.activatedByLaunchd
        XCTAssertFalse(activated)
        XCTAssertTrue(FileManager.default.fileExists(atPath: path))
        let (client, fd) = try await client(path)
        try await client.ping()
        await client.disconnect()
        await server.shutdown()
        await run.value
        try fd.close()
        XCTAssertFalse(FileManager.default.fileExists(atPath: path), "the owner unlinks the socket file")
    }

    func testShutdownCancelsCallsInFlight() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let path = socketPath()
        let (server, run) = try await serving(registry, path: path)
        let (client, fd) = try await client(path)
        let call = Task { try await client.callTool(name: "Wait", arguments: ["duration": 30]) }
        try await Task.sleep(for: .milliseconds(200))
        await server.shutdown()
        try await observer.waitFor(error: "Cancelled.", timeout: 2)
        await run.value
        call.cancel()
        await client.disconnect()
        try fd.close()
    }

    // MARK: - Accept failures

    /// A listening socket bound at `path`, for `SocketServer.listen(fileDescriptors:)`.
    func listener(at path: String) throws -> Int32 {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw Errno(rawValue: errno) }
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: path.utf8.prefix(raw.count - 1))
        }
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard bound == 0, Darwin.listen(fd, 16) == 0 else {
            let error = Errno(rawValue: errno)
            close(fd)
            throw error
        }
        return fd
    }

    /// An `accept` that fails with `code` for its first `failures` calls (every call when nil),
    /// then accepts; `calls` counts every call.
    func failingAccept(_ code: Int32, failures: Int?, calls: Locked<Int>) -> Acceptor.Accept {
        { listener in
            let call = calls.withLock { $0 += 1; return $0 }
            if failures.map({ call <= $0 }) ?? true {
                errno = code
                return -1
            }
            return Darwin.accept(listener, nil, nil)
        }
    }

    func testRunningOutOfDescriptorsPausesAcceptingInsteadOfEndingIt() async throws {
        // Under launchd, ending the accept loop would leave launchd queueing connections that
        // nobody accepts; running out of fds must clear by itself instead.
        let path = socketPath()
        defer { unlink(path) }
        let calls = Locked(0)
        let server = try await SocketServer.listen(
            fileDescriptors: [FileDescriptor(rawValue: try listener(at: path))],
            registry: await DesktopMCP.makeRegistry(), accept: failingAccept(EMFILE, failures: 3, calls: calls))
        let run = Task { await server.run() }
        let (client, fd) = try await client(path)
        try await client.ping()
        XCTAssertGreaterThanOrEqual(calls.current, 4, "three failures, then the accept that served")
        let count = await server.connections
        XCTAssertEqual(count, 1)
        await client.disconnect()
        try fd.close()
        await server.shutdown()
        await run.value
    }

    func testShutdownDuringABackOffClosesTheListener() async throws {
        let path = socketPath()
        defer { unlink(path) }
        let calls = Locked(0)
        let listening = try listener(at: path)
        let server = try await SocketServer.listen(
            fileDescriptors: [FileDescriptor(rawValue: listening)],
            registry: await DesktopMCP.makeRegistry(), accept: failingAccept(ENOBUFS, failures: nil, calls: calls))
        let run = Task { await server.run() }
        let fd = try connectRaw(path)
        defer { close(fd) }
        try await waitUntil(within: 5) { calls.current >= 1 }
        await server.shutdown()
        await run.value
        // The source's cancel handler closes the listener, and a suspended source runs it only
        // once resumed.
        try await waitUntil(within: 2) { fcntl(listening, F_GETFD) == -1 }
    }

    func testAnAcceptErrorThatCannotClearEndsRun() async throws {
        let path = socketPath()
        defer { unlink(path) }
        let calls = Locked(0)
        let server = try await SocketServer.listen(
            fileDescriptors: [FileDescriptor(rawValue: try listener(at: path))],
            registry: await DesktopMCP.makeRegistry(), accept: failingAccept(EBADF, failures: nil, calls: calls))
        let run = Task { await server.run() }
        let fd = try connectRaw(path)
        defer { close(fd) }
        let ended = Locked(false)
        Task { await run.value; ended.current = true }
        try await waitUntil(within: 5) { ended.current }
        XCTAssertEqual(calls.current, 1)
    }
}
