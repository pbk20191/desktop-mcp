import XCTest
@testable import DesktopMCPCore

final class StdioBridgeTests: XCTestCase {
    func testSSEDataIsJoinedPerEventAndCommentsAreIgnored() {
        let stream = "event: message\r\ndata: {\"a\":\r\ndata: 1}\r\n\r\n: keep-alive\n\ndata: x\n\nid: 3\ndata:y\n"
        XCTAssertEqual(SSE.dataPayloads(stream), ["{\"a\":\n1}", "x", "y"])
        XCTAssertEqual(SSE.dataPayloads(""), [])
        XCTAssertEqual(SSE.dataPayloads("event: ping\n\n"), [])
        // The stateful transport's priming event carries an empty `data` line before each reply.
        XCTAssertEqual(SSE.dataPayloads("id: 1\ndata: \n\nevent: message\ndata: {}\n\n"), ["{}"])
    }

    func stubBridge() -> StdioBridge {
        StdioBridge(target: .init(url: URL(string: "http://stub.invalid/mcp")!, authKey: nil), session: StubHTTP.session())
    }

    static let sseHeaders = ["Content-Type": "text/event-stream"]
    static let ping = #"{"jsonrpc":"2.0","id":1,"method":"ping"}"#

    func testSSEEventsAreEmittedAsTheyArrive() async throws {
        StubHTTP.respond { _ in
            (200, Self.sseHeaders, [(0, Data("data: {\"id\":1}\n\n".utf8)), (0.3, Data("data: {\"id\":2}\n\n".utf8))])
        }
        let collector = LineCollector()
        await stubBridge().forward(Self.ping) { await collector.append($0) }
        let lines = await collector.all()
        XCTAssertEqual(try lines.map { try JSON.parse($0)["id"]?.intValue }, [1, 2])
        let arrivals = await collector.arrivals
        XCTAssertGreaterThanOrEqual(arrivals[1].timeIntervalSince(arrivals[0]), 0.25)
    }

    func testEmptyAndCommentEventsAreDropped() async throws {
        StubHTTP.respond { _ in
            (200, Self.sseHeaders, [(0, Data("id: 1\ndata: \n\n: keepalive\n\ndata: {\"id\":3}\n\n".utf8))])
        }
        let collector = LineCollector()
        await stubBridge().forward(Self.ping) { await collector.append($0) }
        let lines = await collector.all()
        XCTAssertEqual(lines, [#"{"id":3}"#])
    }

    func testJSONBodyIsEmittedOnceWhenComplete() async throws {
        let body = #"{"jsonrpc":"2.0","id":1,"result":{}}"#
        StubHTTP.respond { _ in
            (200, ["Content-Type": "application/json"],
             [(0, Data(body.prefix(16).utf8)), (0.1, Data(body.dropFirst(16).utf8))])
        }
        let collector = LineCollector()
        await stubBridge().forward(Self.ping) { await collector.append($0) }
        let lines = await collector.all()
        XCTAssertEqual(lines, [body])
    }

    func testPayloadsSplitOnLFOnly() async throws {
        // U+2028, U+2029 and NEL may sit unescaped in a JSON string; only "\n" ends a line.
        let payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"s\":\"a\u{2028}b\u{2029}c\u{85}d\"}}"
        StubHTTP.respond { _ in (200, Self.sseHeaders, [(0, Data("data: \(payload)\n\n".utf8))]) }
        let collector = LineCollector()
        await stubBridge().forward(Self.ping) { await collector.append($0) }
        let lines = await collector.all()
        XCTAssertEqual(lines.count, 1)
        XCTAssertEqual(try JSON.parse(lines[0])["result"]?["s"]?.stringValue, "a\u{2028}b\u{2029}c\u{85}d")
    }

    func testParserHandlesCRLFAndSplitBoundaries() {
        let stream = "event: message\r\ndata: {\"a\":\r\ndata: \"\u{E9}\"}\r\n\r\n: keep-alive\r\n\r\nid: 2\r\ndata: \r\n\r\ndata: x\r\n\r\ndata:y"
        var parser = SSEParser()
        var payloads: [String] = []
        for byte in stream.utf8 { payloads += parser.feed(CollectionOfOne(byte)) }
        if let last = parser.finish() { payloads.append(last) }
        XCTAssertEqual(payloads, ["{\"a\":\n\"\u{E9}\"}", "x", "y"])
        XCTAssertEqual(payloads, SSE.dataPayloads(stream))
    }

    func testInstalledTargetComesFromTheAgentPlistAndConfig() throws {
        let plist = try LaunchAgent.plist(
            programArguments: LaunchAgent.programArguments(executable: "/x", transport: "streamable-http",
                                                           host: "127.0.0.1", port: 8123),
            logDirectory: URL(fileURLWithPath: "/tmp"))
        var config = DesktopMCPConfig()
        config.server.authKey = "abc"
        let target = try StdioBridge.Target.installed(plist: plist, config: config)
        XCTAssertEqual(target.url.absoluteString, "http://127.0.0.1:8123/mcp")
        XCTAssertEqual(target.authKey, "abc")

        config.server.sslCertfile = "/c.pem"
        config.server.sslKeyfile = "/k.pem"
        XCTAssertEqual(try StdioBridge.Target.installed(plist: plist, config: config).url.scheme, "https")

        let sse = try LaunchAgent.plist(
            programArguments: LaunchAgent.programArguments(executable: "/x", transport: "sse", host: "::1", port: 1),
            logDirectory: URL(fileURLWithPath: "/tmp"))
        // An agent installed by an older version still says sse; it serves /mcp anyway.
        XCTAssertEqual(try StdioBridge.Target.installed(plist: sse, config: config).url.absoluteString, "https://[::1]:1/mcp")
        XCTAssertThrowsError(try StdioBridge.Target.installed(plist: Data("nope".utf8), config: config))
    }

    func testIPv6HostIsBracketedInTheURL() throws {
        let plist = try LaunchAgent.plist(
            programArguments: LaunchAgent.programArguments(executable: "/x", transport: "streamable-http", host: "::1", port: 8000),
            logDirectory: URL(fileURLWithPath: "/tmp"))
        XCTAssertEqual(try StdioBridge.Target.installed(plist: plist, config: DesktopMCPConfig()).url.absoluteString,
                       "http://[::1]:8000/mcp")
    }
}
