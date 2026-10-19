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
