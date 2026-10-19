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
            (200, Self.sseHeaders, [(0, Data("id: 1\ndata: \n\n: keepalive\n\ndata: {\"id\":1}\n\n".utf8))])
        }
        let collector = LineCollector()
        await stubBridge().forward(Self.ping) { await collector.append($0) }
        let lines = await collector.all()
        XCTAssertEqual(lines, [#"{"id":1}"#])
    }

    func testAStreamEndingWithoutTheReplyIsAnError() async throws {
        // A notification first, then the stream ends: the request was never answered.
        StubHTTP.respond { _ in
            (200, Self.sseHeaders, [(0, Data("id: 1\ndata: \n\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"n\"}\n\n".utf8))])
        }
        let collector = LineCollector()
        await stubBridge().forward(Self.ping) { await collector.append($0) }
        let lines = await collector.all()
        XCTAssertEqual(lines.count, 2, "\(lines)")
        let reply = try JSON.parse(lines.last ?? "")
        XCTAssertEqual(reply["id"]?.intValue, 1)
        XCTAssertEqual(reply["error"]?["code"]?.intValue, -32000)
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

    func testNoInitializeMeansNoReplay() async throws {
        let requests = Locked(0)
        StubHTTP.respond { _ in
            let count = requests.withLock { $0 += 1; return $0 }
            // The first answer hands out a session id; the session is gone by the second.
            if count == 1 {
                return (200, ["Content-Type": "application/json", "Mcp-Session-Id": "a"],
                        [(0, Data(#"{"jsonrpc":"2.0","id":1,"result":{}}"#.utf8))])
            }
            return (404, [:], [(0, Data("Not Found".utf8))])
        }
        let bridge = stubBridge()
        await bridge.forward(Self.ping) { _ in }
        let collector = LineCollector()
        await bridge.forward(#"{"jsonrpc":"2.0","id":2,"method":"ping"}"#) { await collector.append($0) }
        let lines = await collector.all()
        XCTAssertEqual(lines.count, 1)
        XCTAssertEqual(try JSON.parse(lines[0])["error"]?["code"]?.intValue, -32000, lines[0])
        XCTAssertEqual(requests.current, 2)
        let replays = await bridge.state.replays
        XCTAssertEqual(replays, 0)
    }

    func testA404OnTheRetryIsReported() async throws {
        let requests = Locked(0)
        StubHTTP.respond { request in
            // The new session's GET listener, started by the recovery: no stream offered.
            if request.httpMethod == "GET" { return (405, [:], []) }
            let count = requests.withLock { $0 += 1; return $0 }
            // initialize, ping (404), replayed initialize, resent ping (404 again).
            if count % 2 == 1 {
                return (200, ["Content-Type": "application/json", "Mcp-Session-Id": "s\(count)"],
                        [(0, Data(#"{"jsonrpc":"2.0","id":1,"result":{}}"#.utf8))])
            }
            return (404, [:], [(0, Data("Not Found".utf8))])
        }
        let bridge = stubBridge()
        await bridge.forward(#"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"#) { _ in }
        let collector = LineCollector()
        await bridge.forward(#"{"jsonrpc":"2.0","id":2,"method":"ping"}"#) { await collector.append($0) }
        let lines = await collector.all()
        XCTAssertEqual(lines.count, 1)
        let reply = try JSON.parse(lines[0])
        XCTAssertEqual(reply["id"]?.intValue, 2)
        XCTAssertEqual(reply["error"]?["code"]?.intValue, -32000, lines[0])
        XCTAssertTrue(lines[0].contains("404"), lines[0])
        XCTAssertEqual(requests.current, 4)
        let replays = await bridge.state.replays
        XCTAssertEqual(replays, 1)
    }

    /// A long session forwards many lines; on macOS 13 each line's child must be reaped as it
    /// finishes, and `run` must still end at EOF once the last one has.
    func testManyLinesAreForwardedAndRunEndsAtEOFOnTheMacOS13Path() async throws {
        let requests = Locked(0)
        StubHTTP.respond { _ in
            requests.withLock { $0 += 1 }
            return (202, [:], [])
        }
        let bridge = stubBridge()
        let pipe = Pipe()
        let written = Locked<[String]>([])
        let started = Date()
        let running = Task {
            await bridge.run(input: pipe.fileHandleForReading, discardingGroup: false) { line in
                written.withLock { $0.append(line) }
                return true
            }
        }
        let notification = #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#
        let input = Data(String(repeating: notification + "\n", count: 2_000).utf8)
        // More than a pipe buffer holds: write off the cooperative pool while `run` drains it.
        let writer = pipe.fileHandleForWriting
        Thread.detachNewThread {
            writer.write(input)
            try? writer.close()
        }
        await running.value
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
        XCTAssertEqual(requests.current, 2_000)
        XCTAssertEqual(written.current, [])
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
