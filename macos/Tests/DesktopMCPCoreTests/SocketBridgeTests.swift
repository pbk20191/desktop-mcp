import Foundation
import MCP
import System
import XCTest
@testable import DesktopMCPCore

final class SocketBridgeTests: XCTestCase {
    override func setUp() {
        super.setUp()
        // The bridge's socket carries SO_NOSIGPIPE, but the test's own pipes do not.
        signal(SIGPIPE, SIG_IGN)
    }

    struct Timeout: Error {}

    /// A fresh socket path under the temporary directory, short enough for `sun_path`.
    func socketPath() -> String {
        let path = FileManager.default.temporaryDirectory
            .appendingPathComponent("db-\(UInt32.random(in: 0...9999)).sock").path
        XCTAssertLessThan(path.utf8.count, 104)
        try? FileManager.default.removeItem(atPath: path)
        return path
    }

    /// A server listening at `path`, with its accept loop running.
    func serving(_ registry: ToolRegistry, path: String) async throws -> (SocketServer, Task<Void, Never>) {
        let server = try await SocketServer.listen(path: path, launchdName: nil, registry: registry)
        return (server, Task { await server.run() })
    }

    /// The bridge's stdin (a pipe the test writes) and its stdout (what `output` was handed).
    final class Ends: Sendable {
        let input = Pipe()
        let written = Locked(Data())
        /// When set, `output` refuses every chunk, as a closed stdout would.
        let refuse = Locked(false)
        let outputCalls = Locked(0)

        func send(_ text: String) {
            input.fileHandleForWriting.write(Data(text.utf8))
        }

        func closeInput() throws {
            try input.fileHandleForWriting.close()
        }

        var output: @Sendable (Data) async -> Bool {
            { data in
                self.outputCalls.withLock { $0 += 1 }
                if self.refuse.current { return false }
                self.written.withLock { $0.append(data) }
                return true
            }
        }

        /// The complete lines written so far, as JSON.
        func lines() throws -> [Value] {
            let data = written.current
            guard let last = data.lastIndex(of: 0x0A) else { return [] }
            return try data[...last].split(separator: 0x0A).map { try JSON.parse(Data($0)) }
        }
    }

    func run(_ connection: SocketBridge.Connection, _ ends: Ends) -> Task<SocketBridge.Outcome, Never> {
        let input = ends.input.fileHandleForReading
        let output = ends.output
        return Task { await connection.run(input: input, output: output) }
    }

    /// The task's outcome, or nil when `seconds` pass first.
    func outcome(of task: Task<SocketBridge.Outcome, Never>, within seconds: Double) async -> SocketBridge.Outcome? {
        await withTaskGroup(of: SocketBridge.Outcome?.self) { group in
            group.addTask { await task.value }
            group.addTask { try? await Task.sleep(for: .seconds(seconds)); return nil }
            let first = await group.next() ?? nil
            group.cancelAll()
            return first
        }
    }

    /// Polls `condition` until it holds; throws once `seconds` pass first.
    func waitUntil(within seconds: Double, _ condition: () async throws -> Bool) async throws {
        let deadline = Date().addingTimeInterval(seconds)
        while !(try await condition()) {
            if Date() > deadline { throw Timeout() }
            try await Task.sleep(for: .milliseconds(20))
        }
    }

    static let initializeLine = #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"# + "\n"
    static func wait(id: Int, seconds: Int) -> String {
        #"{"jsonrpc":"2.0","id":\#(id),"method":"tools/call","params":{"name":"Wait","arguments":{"duration":\#(seconds)}}}"# + "\n"
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

    func testPipesRequestsAndRepliesUntilStdinCloses() async throws {
        let path = socketPath()
        let (server, serverRun) = try await serving(await DesktopMCP.makeRegistry(), path: path)
        let connection = try await SocketBridge(path: path).connect()
        let ends = Ends()
        let bridge = run(connection, ends)
        // One at a time: the server answers each request from its own task, so two sent together
        // may be answered in either order.
        ends.send(Self.initializeLine)
        try await waitUntil(within: 5) { try ends.lines().count == 1 }
        ends.send(#"{"jsonrpc":"2.0","id":2,"method":"ping"}"# + "\n")
        try ends.closeInput()
        let result = await outcome(of: bridge, within: 5)
        XCTAssertEqual(result, .finished)
        let lines = try ends.lines()
        XCTAssertEqual(lines.compactMap { $0["id"]?.intValue }, [1, 2])
        XCTAssertTrue(lines.allSatisfy { $0["result"] != nil })
        XCTAssertEqual(ends.written.current.last, 0x0A, "nothing after the last line")
        await server.shutdown()
        await serverRun.value
    }

    func testBytesSplitAcrossReadsArriveByteExact() async throws {
        let size = 2 * 1024 * 1024
        let path = socketPath()
        let (server, serverRun) = try await serving(await bigRegistry(size: size), path: path)
        let connection = try await SocketBridge(path: path).connect()
        let ends = Ends()
        let bridge = run(connection, ends)
        ends.send(Self.initializeLine)
        try await waitUntil(within: 5) { try ends.lines().count == 1 }
        ends.send(#"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Big","arguments":{}}}"# + "\n")
        try ends.closeInput()
        let result = await outcome(of: bridge, within: 10)
        XCTAssertEqual(result, .finished)
        XCTAssertGreaterThan(ends.outputCalls.current, 2, "the reply came in more than one read")
        let lines = try ends.lines()
        XCTAssertEqual(lines.count, 2)
        let text = lines.last?["result"]?["content"]?.arrayValue?.first?["text"]?.stringValue
        XCTAssertEqual(text?.utf8.count, size)
        XCTAssertTrue(text?.utf8.allSatisfy { $0 == UInt8(ascii: "x") } ?? false)
        await server.shutdown()
        await serverRun.value
    }

    func testOutputClosedEndsTheBridgeAtOnce() async throws {
        let path = socketPath()
        let (server, serverRun) = try await serving(await DesktopMCP.makeRegistry(), path: path)
        let connection = try await SocketBridge(path: path).connect()
        let ends = Ends()
        ends.refuse.current = true
        let bridge = run(connection, ends)
        ends.send(Self.initializeLine + Self.wait(id: 2, seconds: 5))
        let result = await outcome(of: bridge, within: 2)
        XCTAssertEqual(result, .outputClosed)
        // The Wait is still running; its reply's write fails, and that ends the server.
        try await waitUntil(within: 7) { await server.connections == 0 }
        try ends.closeInput()
        await server.shutdown()
        await serverRun.value
    }

    func testServerClosingEndsTheBridgeNonZero() async throws {
        let path = socketPath()
        let (server, serverRun) = try await serving(await DesktopMCP.makeRegistry(), path: path)
        let connection = try await SocketBridge(path: path).connect()
        let ends = Ends()
        let bridge = run(connection, ends)
        ends.send(Self.initializeLine + Self.wait(id: 2, seconds: 30))
        try await waitUntil(within: 5) { try ends.lines().contains { $0["id"]?.intValue == 1 } }
        await server.shutdown()
        await serverRun.value
        let result = await outcome(of: bridge, within: 2)
        XCTAssertEqual(result, .serverClosed)
        let ids = try ends.lines().compactMap { $0["id"]?.intValue }
        XCTAssertTrue(ids.contains(1))
        try ends.closeInput()
    }

    func testNothingListeningIsRefused() async {
        do {
            _ = try await SocketBridge(path: socketPath()).connect()
            XCTFail("connect must be refused")
        } catch let refused as SocketBridge.Refused {
            XCTAssertTrue(refused.reason.contains("No such file"), refused.reason)
        } catch {
            XCTFail("unexpected \(error)")
        }
    }

    func testConnectsToAListenerThatAcceptsLate() async throws {
        let path = socketPath()
        let listener = socket(AF_UNIX, SOCK_STREAM, 0)
        XCTAssertGreaterThanOrEqual(listener, 0)
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: path.utf8.prefix(raw.count - 1))
        }
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(listener, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        XCTAssertEqual(bound, 0)
        XCTAssertEqual(Darwin.listen(listener, 16), 0)
        defer { unlink(path) }

        let connection = try await SocketBridge(path: path).connect()
        let ends = Ends()
        let bridge = run(connection, ends)
        ends.send(Self.initializeLine)
        try await Task.sleep(for: .milliseconds(500))
        XCTAssertTrue(ends.written.current.isEmpty, "nobody has accepted yet")

        let server = try await SocketServer.listen(fileDescriptors: [FileDescriptor(rawValue: listener)],
                                                   registry: await DesktopMCP.makeRegistry())
        let serverRun = Task { await server.run() }
        try await waitUntil(within: 5) { try ends.lines().count == 1 }
        XCTAssertEqual(try ends.lines().first?["id"]?.intValue, 1)
        try ends.closeInput()
        let result = await outcome(of: bridge, within: 5)
        XCTAssertEqual(result, .finished)
        await server.shutdown()
        await serverRun.value
    }
}
