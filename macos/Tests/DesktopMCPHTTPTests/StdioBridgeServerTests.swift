import Foundation
import DesktopMCPCore
import DesktopMCPHTTP
import XCTest

/// The `connect` bridge against a real streamable-http server over sockets.
final class StdioBridgeServerTests: XCTestCase {
    func start(authKey: String?) async throws -> (Int, Task<Void, Error>) {
        let server = await DesktopMCP.makeServer()
        let app = HTTPApp(server: server, settings: HTTPApp.Settings(transport: .streamableHTTP, authKey: authKey))
        let listening = try await NIOHTTPServer.bind(host: "127.0.0.1", port: 0)
        let task = Task { try await NIOHTTPServer.serve(listening, app: app) }
        return (try XCTUnwrap(listening.port), task)
    }

    func bridge(port: Int, authKey: String?) -> StdioBridge {
        StdioBridge(target: .init(url: URL(string: "http://127.0.0.1:\(port)/mcp")!, authKey: authKey))
    }

    func testRequestsComeBackAsOneLineEachAndNotificationsAreSilent() async throws {
        let (port, task) = try await start(authKey: "k")
        defer { task.cancel() }
        let bridge = bridge(port: port, authKey: "k")

        let initialize = await bridge.forward(#"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"#)
        XCTAssertEqual(initialize.count, 1)
        let reply = try JSONValue.parse(initialize[0])
        XCTAssertEqual(reply["id"]?.intValue, 1)
        XCTAssertEqual(reply["result"]?["serverInfo"]?["name"]?.stringValue, "desktop-mcp")
        XCTAssertFalse(initialize[0].contains("\n"))

        let notified = await bridge.forward(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#)
        XCTAssertEqual(notified, [])

        let tools = await bridge.forward(#"{"jsonrpc":"2.0","id":"two","method":"tools/list"}"#)
        XCTAssertEqual(tools.count, 1)
        XCTAssertEqual(try JSONValue.parse(tools[0])["result"]?["tools"]?.arrayValue?.count, 12)
    }

    func testHTTPFailuresBecomeJSONRPCErrorsForTheSameId() async throws {
        let (port, task) = try await start(authKey: "k")
        defer { task.cancel() }
        let wrongKey = bridge(port: port, authKey: "nope")
        let lines = await wrongKey.forward(#"{"jsonrpc":"2.0","id":7,"method":"ping"}"#)
        XCTAssertEqual(lines.count, 1)
        let reply = try JSONValue.parse(lines[0])
        XCTAssertEqual(reply["id"]?.intValue, 7)
        XCTAssertEqual(reply["error"]?["code"]?.intValue, -32000)
        XCTAssertTrue(reply["error"]?["message"]?.stringValue?.contains("401") == true, lines[0])

        // A notification that fails has nobody to answer; nothing is written.
        let silent = await wrongKey.forward(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#)
        XCTAssertEqual(silent, [])

        task.cancel()
        try? await task.value
        let gone = await wrongKey.forward(#"{"jsonrpc":"2.0","id":8,"method":"ping"}"#)
        XCTAssertEqual(try JSONValue.parse(gone[0])["error"]?["code"]?.intValue, -32000)
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
        XCTAssertEqual(Set(lines.compactMap { try? JSONValue.parse($0)["id"]?.intValue }), [1, 2])
    }

    actor Collected {
        var lines: [String] = []
        func add(_ line: String) { lines.append(line) }
    }
}
