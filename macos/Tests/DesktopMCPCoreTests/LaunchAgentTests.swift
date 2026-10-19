import XCTest
@testable import DesktopMCPCore

final class LaunchAgentTests: XCTestCase {
    func testPlistPathIsUnderTheUsersLaunchAgents() {
        let home = URL(fileURLWithPath: "/Users/someone")
        XCTAssertEqual(LaunchAgent.plistURL(home: home).path,
                       "/Users/someone/Library/LaunchAgents/com.desktop-mcp.server.plist")
        XCTAssertEqual(LaunchAgent.label, "com.desktop-mcp.server")
    }

    func testProgramArgumentsRunServeWithExplicitTransportHostAndPort() {
        XCTAssertEqual(
            LaunchAgent.programArguments(executable: "/opt/desktop-mcp", transport: "streamable-http",
                                         host: "127.0.0.1", port: 8000),
            ["/opt/desktop-mcp", "serve", "--transport", "streamable-http", "--host", "127.0.0.1", "--port", "8000"])
    }

    func testPlistRoundTripsSpecialCharactersAndMatchesTheOriginalKeys() throws {
        let logs = URL(fileURLWithPath: "/tmp/config & logs <local>")
        let arguments = ["/tmp/tool & helper", "serve", "--label=<local>", "a>b"]
        let data = try LaunchAgent.plist(programArguments: arguments, logDirectory: logs)
        let parsed = try XCTUnwrap(PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any])
        XCTAssertEqual(parsed["Label"] as? String, "com.desktop-mcp.server")
        XCTAssertEqual(parsed["ProgramArguments"] as? [String], arguments)
        XCTAssertEqual(parsed["RunAtLoad"] as? Bool, true)
        XCTAssertEqual(parsed["KeepAlive"] as? Bool, true)
        XCTAssertEqual(parsed["StandardOutPath"] as? String, "/tmp/config & logs <local>/server.log")
        XCTAssertEqual(parsed["StandardErrorPath"] as? String, "/tmp/config & logs <local>/server.error.log")
        XCTAssertEqual(parsed.count, 6)
        XCTAssertTrue(String(decoding: data, as: UTF8.self).hasPrefix("<?xml"))
    }

    func testStdioCannotBeInstalledAsAService() {
        XCTAssertThrowsError(try LaunchAgent.validateTransport("stdio"))
        XCTAssertThrowsError(try LaunchAgent.validateTransport("websocket"))
        XCTAssertNoThrow(try LaunchAgent.validateTransport("streamable-http"))
        XCTAssertThrowsError(try LaunchAgent.validateTransport("sse")) { error in
            XCTAssertTrue("\(error)".contains("streamable-http"), "\(error)")
        }
    }

    func testFreePortSkipsAPortSomethingElseIsListeningOn() throws {
        // Occupy an ephemeral port for the duration of the test.
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        XCTAssertGreaterThanOrEqual(fd, 0)
        defer { close(fd) }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = 0
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        XCTAssertEqual(bound, 0)
        XCTAssertEqual(listen(fd, 1), 0)
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        _ = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) }
        }
        let occupied = Int(UInt16(bigEndian: address.sin_port))

        let chosen = try LaunchAgent.findFreePort(host: "127.0.0.1", preferred: occupied)
        XCTAssertNotEqual(chosen, occupied)
        XCTAssertGreaterThan(chosen, occupied)
        XCTAssertLessThan(chosen, occupied + 100)
    }

    func testFreePortReturnsThePreferredPortWhenItIsAvailable() throws {
        // Port 0 asks the kernel for any free port; the finder must try the preferred one first.
        let probe = try LaunchAgent.findFreePort(host: "127.0.0.1", preferred: 0)
        XCTAssertEqual(probe, 0)
    }

    func testFreePortGivesUpAfterMaxTries() {
        XCTAssertThrowsError(try LaunchAgent.findFreePort(host: "256.1.1.1", preferred: 8000, maxTries: 3))
    }
}
