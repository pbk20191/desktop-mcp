import XCTest
@testable import DesktopMCPCore

final class ServerTests: XCTestCase {
    func makeServer(excluding: [String] = []) async -> MCPServer {
        await DesktopMCP.makeServer(excluding: excluding)
    }

    func call(_ server: MCPServer, _ method: String, id: Int = 1, params: JSONValue = [:]) async -> JSONValue? {
        await server.respond(to: ["jsonrpc": "2.0", "id": .integer(Int64(id)), "method": .string(method), "params": params])
    }

    func toolText(_ response: JSONValue?) -> String? {
        response?["result"]?["content"]?.arrayValue?.first?["text"]?.stringValue
    }

    func testInitializeNegotiatesProtocolVersion() async {
        let server = await makeServer()
        let known = await call(server, "initialize", params: ["protocolVersion": "2025-03-26"])
        XCTAssertEqual(known?["result"]?["protocolVersion"], "2025-03-26")
        XCTAssertEqual(known?["result"]?["serverInfo"]?["name"], "desktop-mcp")
        XCTAssertNotNil(known?["result"]?["capabilities"]?["tools"])

        let unknown = await call(server, "initialize", params: ["protocolVersion": "1999-01-01"])
        XCTAssertEqual(unknown?["result"]?["protocolVersion"]?.stringValue, MCPServer.supportedProtocolVersions[0])
    }

    func testToolsListMatchesOriginalToolSet() async {
        let server = await makeServer()
        let response = await call(server, "tools/list")
        let tools = response?["result"]?["tools"]?.arrayValue ?? []
        XCTAssertEqual(tools.compactMap { $0["name"]?.stringValue },
                       ["App", "Shell", "Snapshot", "Click", "Type", "Scroll", "Move",
                        "Shortcut", "Wait", "Scrape", "Desktop", "Notification"])
        let click = tools.first { $0["name"] == "Click" }
        XCTAssertEqual(click?["inputSchema"]?["required"], ["loc"])
        XCTAssertEqual(click?["annotations"]?["destructiveHint"], true)
    }

    func testExcludedToolsAreRemoved() async {
        let server = await makeServer(excluding: ["Shell", "Scrape", "NoSuchTool"])
        let names = await server.toolNames
        XCTAssertFalse(names.contains("Shell"))
        XCTAssertFalse(names.contains("Scrape"))
        XCTAssertEqual(names.count, 10)
    }

    func testUnknownMethodNotificationsAndPing() async {
        let server = await makeServer()
        let response = await call(server, "nope/nope")
        XCTAssertEqual(response?["error"]?["code"], -32601)
        let notification = await server.respond(to: ["jsonrpc": "2.0", "method": "notifications/initialized"])
        XCTAssertNil(notification)
        let ping = await call(server, "ping")
        XCTAssertEqual(ping?["result"], .object([:]))
    }

    func testToolCallSuccessAndErrors() async {
        let server = await makeServer()
        let wait = await call(server, "tools/call", params: ["name": "Wait", "arguments": ["duration": 0]])
        XCTAssertEqual(toolText(wait), "Waited for 0 seconds.")
        XCTAssertEqual(wait?["result"]?["isError"], false)

        let shell = await call(server, "tools/call", params: ["name": "Shell", "arguments": ["command": "echo hi"]])
        XCTAssertEqual(toolText(shell), "Shell Response: hi\nStatus Code: 0")

        let badLoc = await call(server, "tools/call", params: ["name": "Click", "arguments": ["loc": [1, 2, 3]]])
        XCTAssertEqual(badLoc?["result"]?["isError"], true)
        XCTAssertTrue(toolText(badLoc)?.contains("exactly 2 integers") == true)

        let badEnum = await call(server, "tools/call", params: ["name": "Shell", "arguments": ["command": "x", "mode": "zsh"]])
        XCTAssertEqual(badEnum?["result"]?["isError"], true)

        let unknown = await call(server, "tools/call", params: ["name": "Nope"])
        XCTAssertEqual(unknown?["error"]?["code"], -32602)
    }

    func testRequestsRunConcurrentlyAndCanBeCancelled() async throws {
        let lines = LineCollector()
        let server = await DesktopMCP.makeServer()
        let session = MCPSession(server: server) { await lines.append($0) }

        // A long Wait must not hold up a ping sent after it.
        await session.receive(line: #"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":30}}}"#)
        await session.receive(line: #"{"jsonrpc":"2.0","id":2,"method":"ping"}"#)
        try await lines.waitFor(count: 1)
        let first = try JSONValue.parse(await lines.all()[0])
        XCTAssertEqual(first["id"], 2)

        // Cancelling the Wait answers it promptly rather than after 30 seconds.
        let started = Date()
        await session.receive(line: #"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}"#)
        try await lines.waitFor(count: 2)
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
        let second = try JSONValue.parse(await lines.all()[1])
        XCTAssertEqual(second["id"], 1)
        XCTAssertEqual(second["result"]?["isError"], true)
    }

    func testToolOutcomesReachTheObserverWithClientInfo() async throws {
        let observer = RecordingObserver()
        let server = await DesktopMCP.makeServer()
        await server.setObserver(observer)
        let session = MCPSession(server: server) { _ in }
        await session.receive(line: #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"claude-code","version":"2.0"}}}"#)
        await session.receive(line: #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":0}}}"#)
        await session.receive(line: #"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"Click","arguments":{"loc":[1]}}}"#)
        await session.drain()
        let events = await observer.events.sorted()
        XCTAssertEqual(events, ["fail Click claude-code", "ok Wait claude-code"])
    }

    func testTelemetryNeverSeesArgumentText() async throws {
        let observer = RecordingObserver()
        let server = await DesktopMCP.makeServer()
        await server.setObserver(observer)
        let session = MCPSession(server: server) { _ in }
        await session.receive(line: #"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Scrape","arguments":{"url":"http://intranet.corp.example/secret?token=abc"}}}"#)
        await session.receive(line: #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Shortcut","arguments":{"shortcut":"command+zzz"}}}"#)
        await session.drain()
        let errors = await observer.errors.sorted()
        XCTAssertEqual(errors, ["Could not resolve hostname", "Unknown key"])
    }

    func testStdioSplitsOnNewlineOnly() async throws {
        // JSON strings may contain U+2028, U+2029 and NEL unescaped (JavaScript clients emit them
        // that way); none of them ends a message.
        let lines = LineCollector()
        let server = await DesktopMCP.makeServer()
        let session = MCPSession(server: server) { await lines.append($0) }
        let pipe = Pipe()
        let reader = Task { await session.run(input: pipe.fileHandleForReading) }
        let request = "{\"jsonrpc\":\"2.0\",\"id\":\"a\u{2028}b\u{2029}c\u{0085}d\",\"method\":\"ping\"}\n"
        pipe.fileHandleForWriting.write(Data(request.utf8))
        pipe.fileHandleForWriting.write(Data("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}".utf8))  // no trailing newline
        try pipe.fileHandleForWriting.close()
        await reader.value
        let responses = try await lines.all().map(JSONValue.parse)
        XCTAssertEqual(responses.count, 2)
        XCTAssertEqual(Set(responses.compactMap { $0["id"]?.stringValue }), ["a\u{2028}b\u{2029}c\u{0085}d"])
        XCTAssertTrue(responses.allSatisfy { $0["result"] != nil })
    }

    func testStdioRunStopsWhenCancelledAndCancelsItsRequests() async throws {
        let lines = LineCollector()
        let server = await DesktopMCP.makeServer()
        let session = MCPSession(server: server) { await lines.append($0) }
        let pipe = Pipe()
        let reader = Task { await session.run(input: pipe.fileHandleForReading) }
        pipe.fileHandleForWriting.write(Data(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":30}}}"#.utf8 + [0x0A]))
        try await Task.sleep(for: .milliseconds(200))
        let started = Date()
        reader.cancel()  // the pipe stays open: only cancellation can end the read
        await reader.value
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
        let response = try JSONValue.parse(await lines.all()[0])
        XCTAssertEqual(response["id"], 1)
        XCTAssertEqual(response["result"]?["isError"], true)
    }

    func testEmptyBatchIsInvalidRequest() async throws {
        let lines = LineCollector()
        let server = await DesktopMCP.makeServer()
        let session = MCPSession(server: server) { await lines.append($0) }
        await session.receive(line: "[]")
        try await lines.waitFor(count: 1)
        let error = try JSONValue.parse(await lines.all()[0])
        XCTAssertEqual(error["error"]?["code"], -32600)
        XCTAssertEqual(error["id"], .null)
        let junk = await server.respond(to: 42)
        XCTAssertEqual(junk?["error"]?["code"], -32600)
    }

    func testMessageClassification() throws {
        func classify(_ text: String) throws -> JSONRPCMessage {
            guard case .single(let message) = try JSONCoding.decode(JSONRPCIncoming.self, from: text) else { throw Fail() }
            return message
        }
        struct Fail: Error {}
        guard case .request(let request) = try classify(#"{"jsonrpc":"2.0","id":"x","method":"tools/call","params":{"name":"Wait","arguments":{"duration":0}}}"#),
              case .toolCall(let params) = request.params else { return XCTFail("request") }
        XCTAssertEqual(request.id, "x")
        XCTAssertEqual(params.name, "Wait")
        XCTAssertEqual(params.arguments?["duration"], 0)

        guard case .notification(let note) = try classify(#"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}"#),
              case .cancelled(let cancel) = note.params else { return XCTFail("notification") }
        XCTAssertEqual(cancel.requestId, 7)
        XCTAssertFalse(try classify(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#).needsResponse)
        // `id: null` is a (discouraged) request, not a notification.
        guard case .request = try classify(#"{"jsonrpc":"2.0","id":null,"method":"ping"}"#) else { return XCTFail("null id") }

        guard case .response = try classify(#"{"jsonrpc":"2.0","id":1,"result":{}}"#) else { return XCTFail("response") }
        guard case .invalid(let id) = try classify(#"{"jsonrpc":"2.0","id":3}"#) else { return XCTFail("invalid") }
        XCTAssertEqual(id, 3)
        guard case .invalid = try classify(#"{"id":4,"method":5}"#) else { return XCTFail("non-string method") }
        guard case .invalid = try classify("\"just a string\"") else { return XCTFail("fragment") }

        // Params of the wrong shape are reported as Invalid Params, naming the field.
        guard case .request(let bad) = try classify(#"{"id":1,"method":"tools/call","params":{"name":["Wait"]}}"#),
              case .invalid(let reason) = bad.params else { return XCTFail("invalid params") }
        XCTAssertTrue(reason.hasPrefix("params.name:"), reason)
        guard case .batch(let batch) = try JSONCoding.decode(JSONRPCIncoming.self, from: "[1, {\"id\":1,\"method\":\"ping\"}]") else { return XCTFail("batch") }
        XCTAssertEqual(batch.count, 2)
    }

    func testInvalidParamsAndLargeIds() async throws {
        let server = await makeServer()
        let bad = await server.respond(to: ["jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": ["name": "Wait", "arguments": "nope"]])
        XCTAssertEqual(bad?["error"]?["code"], -32602)
        XCTAssertTrue(bad?["error"]?["message"]?.stringValue?.contains("params.arguments") == true, "\(bad as Any)")

        // Ids beyond 2^53 come back exactly as sent (JavaScript clients cannot send them, but
        // others can), and a cancel for such an id matches.
        let big = "9007199254740993"
        let echoed = await server.respond(to: ["jsonrpc": "2.0", "id": .integer(9007199254740993), "method": "ping"])
        XCTAssertTrue(echoed.map { $0.serialized() }?.contains("\"id\":\(big)") == true, echoed?.serialized() ?? "")
        let lines = LineCollector()
        let session = MCPSession(server: server) { await lines.append($0) }
        await session.receive(line: #"{"jsonrpc":"2.0","id":9007199254740993,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":30}}}"#)
        await session.receive(line: #"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9007199254740993}}"#)
        try await lines.waitFor(count: 1)
        let line = await lines.all()[0]
        XCTAssertTrue(line.contains("\"id\":\(big)"), line)
        XCTAssertEqual(try JSONValue.parse(line)["result"]?["isError"], true)
    }

    func testToolContentAndResultsEncodeAsTheProtocolExpects() throws {
        let image = ToolContent.image(data: Data([0xff, 0xd8, 0xff]), mimeType: "image/jpeg")
        let result = ToolCallResult(content: [.text("hi"), image], isError: false)
        let json = try JSONValue.parse(JSONCoding.encodeString(result))
        XCTAssertEqual(json, ["content": [["type": "text", "text": "hi"], ["type": "image", "data": "/9j/", "mimeType": "image/jpeg"]], "isError": false])
        XCTAssertEqual(try JSONCoding.decode([ToolContent].self, from: JSONCoding.encodeString([image])), [image])

        let error = JSONRPCResponse(id: .null, code: -32700, message: "Parse error").serialized()
        XCTAssertEqual(error, #"{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"#)
        XCTAssertEqual(JSONRPCResponse(id: 1, result: EmptyResult()).serialized(), #"{"id":1,"jsonrpc":"2.0","result":{}}"#)
    }

    func testParseErrorAndBatch() async throws {
        let lines = LineCollector()
        let server = await DesktopMCP.makeServer()
        let session = MCPSession(server: server) { await lines.append($0) }
        await session.receive(line: "{not json")
        try await lines.waitFor(count: 1)
        let parseError = try JSONValue.parse(await lines.all()[0])
        XCTAssertEqual(parseError["error"]?["code"], -32700)

        await session.receive(line: #"[{"jsonrpc":"2.0","id":7,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"}]"#)
        try await lines.waitFor(count: 2)
        let batch = try JSONValue.parse(await lines.all()[1])
        XCTAssertEqual(batch.arrayValue?.count, 1)
        XCTAssertEqual(batch.arrayValue?.first?["id"], 7)
    }
}

extension MCPServer {
    /// Round-trips through the wire format, so tests see exactly what a client would.
    func respond(to value: JSONValue, client: ClientInfo? = nil) async -> JSONValue? {
        guard case .single(let message) = try! JSONCoding.decode(JSONRPCIncoming.self, from: value.serialized()) else {
            fatalError("single messages only")
        }
        guard let response = await respond(to: message, client: client) else { return nil }
        return try! JSONValue.parse(response.serialized())
    }
}

actor RecordingObserver: ToolCallObserver {
    var events: [String] = []

    func toolSucceeded(_ tool: String, durationMs: Int, client: ClientInfo?) {
        events.append("ok \(tool) \(client?.name ?? "-")")
    }

    func toolFailed(_ tool: String, error: String, durationMs: Int, client: ClientInfo?) {
        events.append("fail \(tool) \(client?.name ?? "-")")
        errors.append(error)
    }

    var errors: [String] = []
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
