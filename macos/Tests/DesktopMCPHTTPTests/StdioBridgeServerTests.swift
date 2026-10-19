import Foundation
@testable import DesktopMCPCore
import DesktopMCPHTTP
import MCP
import XCTest

/// The `connect` bridge against a real streamable-http server over sockets.
final class StdioBridgeServerTests: XCTestCase {
    func start(authKey: String?) async throws -> (Int, Task<Void, Error>) {
        let (port, task, _) = try await startApp(settings: HTTPApp.Settings(authKey: authKey))
        return (port, task)
    }

    func startApp(settings: HTTPApp.Settings = HTTPApp.Settings()) async throws -> (Int, Task<Void, Error>, HTTPApp) {
        let app = HTTPApp(registry: await DesktopMCP.makeRegistry(), settings: settings)
        let listening = try await NIOHTTPServer.bind(host: "127.0.0.1", port: 0)
        let task = Task { try await NIOHTTPServer.serve(listening, app: app) }
        return (try XCTUnwrap(listening.port), task, app)
    }

    func bridge(port: Int, authKey: String?) -> StdioBridge {
        StdioBridge(target: .init(url: URL(string: "http://127.0.0.1:\(port)/mcp")!, authKey: authKey))
    }

    /// Every line `bridge` hands back for `line`.
    func forward(_ line: String, through bridge: StdioBridge) async -> [String] {
        let collector = LineCollector()
        await bridge.forward(line) { await collector.append($0) }
        return await collector.all()
    }

    func testRequestsComeBackAsOneLineEachAndNotificationsAreSilent() async throws {
        let (port, task) = try await start(authKey: "k")
        defer { task.cancel() }
        let bridge = bridge(port: port, authKey: "k")

        let initialize = await forward(#"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"#, through: bridge)
        XCTAssertEqual(initialize.count, 1)
        let reply = try JSON.parse(initialize[0])
        XCTAssertEqual(reply["id"]?.intValue, 1)
        XCTAssertEqual(reply["result"]?["serverInfo"]?["name"]?.stringValue, "desktop-mcp")
        XCTAssertFalse(initialize[0].contains("\n"))

        let notified = await forward(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#, through: bridge)
        XCTAssertEqual(notified, [])

        let tools = await forward(#"{"jsonrpc":"2.0","id":"two","method":"tools/list"}"#, through: bridge)
        XCTAssertEqual(tools.count, 1)
        XCTAssertEqual(try JSON.parse(tools[0])["result"]?["tools"]?.arrayValue?.count, 12)
    }

    func testHTTPFailuresBecomeJSONRPCErrorsForTheSameId() async throws {
        let (port, task) = try await start(authKey: "k")
        defer { task.cancel() }
        let wrongKey = bridge(port: port, authKey: "nope")
        let lines = await forward(#"{"jsonrpc":"2.0","id":7,"method":"ping"}"#, through: wrongKey)
        XCTAssertEqual(lines.count, 1)
        let reply = try JSON.parse(lines[0])
        XCTAssertEqual(reply["id"]?.intValue, 7)
        XCTAssertEqual(reply["error"]?["code"]?.intValue, -32000)
        XCTAssertTrue(reply["error"]?["message"]?.stringValue?.contains("401") == true, lines[0])

        // A notification that fails has nobody to answer; nothing is written.
        let silent = await forward(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#, through: wrongKey)
        XCTAssertEqual(silent, [])

        task.cancel()
        try? await task.value
        let gone = await forward(#"{"jsonrpc":"2.0","id":8,"method":"ping"}"#, through: wrongKey)
        XCTAssertEqual(try JSON.parse(gone[0])["error"]?["code"]?.intValue, -32000)
    }

    func testRunForwardsEveryLineUntilInputCloses() async throws {
        let (port, task) = try await start(authKey: nil)
        defer { task.cancel() }
        let pipe = Pipe()
        let collected = Collected()
        let bridge = bridge(port: port, authKey: nil)
        let running = Task { await bridge.run(input: pipe.fileHandleForReading) { await collected.add($0) } }
        pipe.fileHandleForWriting.write(Data(#"{"jsonrpc":"2.0","id":1,"method":"ping"}"# .utf8 + [0x0A]))
        pipe.fileHandleForWriting.write(Data(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"# .utf8 + [0x0A]))
        pipe.fileHandleForWriting.write(Data(#"{"jsonrpc":"2.0","id":2,"method":"ping"}"# .utf8))  // no trailing newline
        try pipe.fileHandleForWriting.close()
        await running.value
        let lines = await collected.lines
        XCTAssertEqual(lines.count, 2)
        XCTAssertEqual(Set(lines.compactMap { try? JSON.parse($0)["id"]?.intValue }), [1, 2])
    }

    static let initializeLine = #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"#

    static func wait(id: Int, seconds: Int) -> String {
        #"{"jsonrpc":"2.0","id":\#(id),"method":"tools/call","params":{"name":"Wait","arguments":{"duration":\#(seconds)}}}"#
    }

    static func ping(id: Int) -> String { #"{"jsonrpc":"2.0","id":\#(id),"method":"ping"}"# }

    func testBridgeGetsASessionAndRecoversAfterEviction() async throws {
        let (port, task, app) = try await startApp()
        defer { task.cancel() }
        let bridge = bridge(port: port, authKey: nil)
        let initialized = await forward(Self.initializeLine, through: bridge)
        XCTAssertEqual(initialized.count, 1)
        let first = await bridge.state.sessionID
        XCTAssertNotNil(first)

        await app.evictAllSessions()
        let lines = await forward(Self.wait(id: 2, seconds: 0), through: bridge)
        XCTAssertEqual(lines.count, 1, "\(lines)")
        let reply = try JSON.parse(lines[0])
        XCTAssertEqual(reply["id"]?.intValue, 2)
        XCTAssertNil(reply["error"], lines[0])
        let second = await bridge.state.sessionID
        XCTAssertNotNil(second)
        XCTAssertNotEqual(first, second)
        let count = await app.sessions.count
        XCTAssertEqual(count, 1)
    }

    func testSecond404IsReportedAsAnError() async throws {
        // One session at most: once another client's session is busy, the replayed initialize
        // is refused and the original request cannot be resent.
        var settings = HTTPApp.Settings()
        settings.maxSessions = 1
        let (port, task, app) = try await startApp(settings: settings)
        defer { task.cancel() }
        let bridge = bridge(port: port, authKey: nil)
        _ = await forward(Self.initializeLine, through: bridge)
        await app.evictAllSessions()

        let other = self.bridge(port: port, authKey: nil)
        _ = await forward(Self.initializeLine, through: other)
        let call = Self.wait(id: 9, seconds: 2)
        let busy = Task { await other.forward(call) { _ in } }
        try await Task.sleep(for: .milliseconds(500))

        let lines = await forward(Self.ping(id: 3), through: bridge)
        XCTAssertEqual(lines.count, 1, "\(lines)")
        let reply = try JSON.parse(lines[0])
        XCTAssertEqual(reply["id"]?.intValue, 3)
        XCTAssertEqual(reply["error"]?["code"]?.intValue, -32000, lines[0])
        let replays = await bridge.state.replays
        XCTAssertEqual(replays, 1)
        await busy.value
    }

    /// The session ends mid-call: the server finishes the response stream without the reply,
    /// and the stdio client gets an error for its request instead of waiting forever.
    func testAStreamThatEndsWithoutTheReplyIsAnError() async throws {
        let (port, task, app) = try await startApp()
        defer { task.cancel(); Task { await app.shutdown() } }
        let bridge = bridge(port: port, authKey: nil)
        _ = await forward(Self.initializeLine, through: bridge)

        let collector = LineCollector()
        let call = Self.wait(id: 5, seconds: 5)
        let forwarding = Task { await bridge.forward(call) { await collector.append($0) } }
        try await Task.sleep(for: .milliseconds(300))
        await app.evictAllSessions()

        try await collector.waitFor(count: 1, timeout: 2)
        await forwarding.value
        let lines = await collector.all()
        XCTAssertEqual(lines.count, 1, "\(lines)")
        let reply = try JSON.parse(lines[0])
        XCTAssertEqual(reply["id"]?.intValue, 5)
        XCTAssertEqual(reply["error"]?["code"]?.intValue, -32000, lines[0])
    }

    func testNotificationGetting404IsReplayedSilently() async throws {
        let (port, task, app) = try await startApp()
        defer { task.cancel() }
        let bridge = bridge(port: port, authKey: nil)
        _ = await forward(Self.initializeLine, through: bridge)
        let first = await bridge.state.sessionID
        await app.evictAllSessions()

        let notified = await forward(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#, through: bridge)
        XCTAssertEqual(notified, [])
        let second = await bridge.state.sessionID
        XCTAssertNotNil(second)
        XCTAssertNotEqual(first, second)
        var replays = await bridge.state.replays
        XCTAssertEqual(replays, 1)

        let pinged = await forward(Self.ping(id: 4), through: bridge)
        XCTAssertEqual(pinged.count, 1)
        XCTAssertNotNil(try JSON.parse(pinged[0])["result"], pinged[0])
        replays = await bridge.state.replays
        XCTAssertEqual(replays, 1)
    }

    func testBridgeForwardsServerNotificationsFromTheGetStream() async throws {
        let (port, task, app) = try await startApp()
        defer { task.cancel() }
        let pipe = Pipe()
        let collected = Collected()
        let bridge = bridge(port: port, authKey: nil)
        let running = Task { await bridge.run(input: pipe.fileHandleForReading) { await collected.add($0) } }

        pipe.fileHandleForWriting.write(Data(Self.initializeLine.utf8 + [0x0A]))
        let first = try await session(of: bridge, other: nil)
        let firstServer = await app.sessionServer(id: first)
        try await notifyUntilForwarded(server: XCTUnwrap(firstServer), collected: collected, count: 1)

        // Notifications still on their way from the first session land before the baseline,
        // so only one from the second session can satisfy the count below.
        try await Task.sleep(for: .milliseconds(300))
        let baseline = await forwardedNotifications(collected)
        await app.evictAllSessions()
        pipe.fileHandleForWriting.write(Data(Self.ping(id: 2).utf8 + [0x0A]))
        let second = try await session(of: bridge, other: first)
        let secondServer = await app.sessionServer(id: second)
        try await notifyUntilForwarded(server: XCTUnwrap(secondServer), collected: collected, count: baseline + 1)

        try pipe.fileHandleForWriting.close()
        await running.value
    }

    func testEOFCancelsTheListenerAndWaitsForRequests() async throws {
        let (port, task, _) = try await startApp()
        defer { task.cancel() }
        let pipe = Pipe()
        let collected = Collected()
        let bridge = bridge(port: port, authKey: nil)
        let started = Date()
        let running = Task { await bridge.run(input: pipe.fileHandleForReading) { await collected.add($0) } }
        pipe.fileHandleForWriting.write(Data(Self.initializeLine.utf8 + [0x0A]))
        pipe.fileHandleForWriting.write(Data(Self.wait(id: 2, seconds: 2).utf8 + [0x0A]))
        try await Task.sleep(for: .milliseconds(200))
        var listening = await bridge.state.listener != nil
        let deadline = Date().addingTimeInterval(1)
        while !listening, Date() < deadline {
            try await Task.sleep(for: .milliseconds(50))
            listening = await bridge.state.listener != nil
        }
        XCTAssertTrue(listening)
        try pipe.fileHandleForWriting.close()

        await running.value
        let elapsed = Date().timeIntervalSince(started)
        let lines = await collected.lines
        XCTAssertTrue(lines.contains { (try? JSON.parse($0)["id"]?.intValue) == 2 }, "\(lines)")
        XCTAssertGreaterThanOrEqual(elapsed, 1.9)
        XCTAssertLessThan(elapsed, 4)
        let listener = await bridge.state.listener
        XCTAssertNil(listener)
    }

    func testFailedWriteCancelsEverything() async throws {
        let (port, task, _) = try await startApp()
        defer { task.cancel() }
        let pipe = Pipe()
        defer { try? pipe.fileHandleForWriting.close() }
        let bridge = bridge(port: port, authKey: nil)
        let started = Date()
        // stdout is gone: the first line cannot be written, so the 30 s call is not waited for
        // and stdin, still open, is no longer read.
        let running = Task { await bridge.run(input: pipe.fileHandleForReading) { _ in false } }
        pipe.fileHandleForWriting.write(Data(Self.wait(id: 2, seconds: 30).utf8 + [0x0A]))
        pipe.fileHandleForWriting.write(Data(Self.ping(id: 3).utf8 + [0x0A]))
        await running.value
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
    }

    struct NoSession: Error {}

    /// The bridge's session id once it is set and differs from `other`.
    func session(of bridge: StdioBridge, other: String?) async throws -> String {
        let deadline = Date().addingTimeInterval(2)
        while Date() < deadline {
            if let id = await bridge.state.sessionID, id != other { return id }
            try await Task.sleep(for: .milliseconds(20))
        }
        throw NoSession()
    }

    /// Server-initiated messages travel on the GET stream, which the bridge opens on its own
    /// once it has a session id; nothing is replayed, so repeat until one comes through.
    func notifyUntilForwarded(server: MCP.Server, collected: Collected, count: Int) async throws {
        let deadline = Date().addingTimeInterval(2)
        while await forwardedNotifications(collected) < count, Date() < deadline {
            try await server.notify(ToolListChangedNotification.message())
            try await Task.sleep(for: .milliseconds(100))
        }
        let seen = await forwardedNotifications(collected)
        XCTAssertGreaterThanOrEqual(seen, count)
    }

    /// The `list_changed` notifications the bridge has written so far.
    func forwardedNotifications(_ collected: Collected) async -> Int {
        await collected.lines.filter { (try? JSON.parse($0)["method"]?.stringValue) == "notifications/tools/list_changed" }.count
    }

    actor Collected {
        var lines: [String] = []
        func add(_ line: String) -> Bool {
            lines.append(line)
            return true
        }
    }
}
