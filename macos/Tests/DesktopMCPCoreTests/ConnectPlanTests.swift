import XCTest
@testable import DesktopMCPCore

final class ConnectPlanTests: XCTestCase {
    func testSocketWhenTheAgentOffersOneWithTheHTTPTargetAsFallback() throws {
        let args = LaunchAgent.programArguments(executable: "/x", transport: "streamable-http", host: "127.0.0.1", port: 8123, socketPath: "/s.sock")
        let plist = try LaunchAgent.plist(programArguments: args, logDirectory: URL(fileURLWithPath: "/tmp"), socketPath: "/s.sock")
        let plan = try ConnectPlan.installed(plist: plist, config: DesktopMCPConfig())
        XCTAssertEqual(plan, .socket(path: "/s.sock", fallback: StdioBridge.Target(url: URL(string: "http://127.0.0.1:8123/mcp")!, authKey: nil)))
    }

    func testHTTPWhenTheAgentPredatesTheSocket() throws {
        let args = LaunchAgent.programArguments(executable: "/x", transport: "streamable-http", host: "127.0.0.1", port: 8000, socketPath: nil)
        let plist = try LaunchAgent.plist(programArguments: args, logDirectory: URL(fileURLWithPath: "/tmp"), socketPath: nil)
        XCTAssertEqual(try ConnectPlan.installed(plist: plist, config: DesktopMCPConfig()),
                       .http(StdioBridge.Target(url: URL(string: "http://127.0.0.1:8000/mcp")!, authKey: nil)))
    }

    func testSocketPathIsReadFromThePlist() throws {
        let withSocket = try LaunchAgent.plist(
            programArguments: LaunchAgent.programArguments(executable: "/x", transport: "streamable-http", host: "127.0.0.1",
                                                           port: 8000, socketPath: "/s.sock"),
            logDirectory: URL(fileURLWithPath: "/tmp"), socketPath: "/s.sock")
        XCTAssertEqual(ConnectPlan.socketPath(plist: withSocket), "/s.sock")
        let without = try LaunchAgent.plist(
            programArguments: LaunchAgent.programArguments(executable: "/x", transport: "streamable-http", host: "127.0.0.1",
                                                           port: 8000, socketPath: nil),
            logDirectory: URL(fileURLWithPath: "/tmp"), socketPath: nil)
        XCTAssertNil(ConnectPlan.socketPath(plist: without))
        XCTAssertNil(ConnectPlan.socketPath(plist: Data("nope".utf8)))
    }

    func testUnreadablePlistThrows() {
        XCTAssertThrowsError(try ConnectPlan.installed(plist: Data("nope".utf8), config: DesktopMCPConfig()))
    }
}
