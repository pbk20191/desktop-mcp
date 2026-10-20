import XCTest
@testable import DesktopMCPCore

final class AppQuitTests: XCTestCase {
    private func info(_ pid: pid_t, _ name: String, _ bundleID: String? = nil) -> RunningAppInfo {
        RunningAppInfo(pid: pid, name: name, bundleID: bundleID)
    }

    func testQuitTargetsAllMatchesButNeverSelf() throws {
        let running = [info(10, "Preview"), info(11, "Preview"), info(99, "Preview"), info(12, "Safari")]
        let targets = try AppQuit.targets("Preview", running: running, ownPID: 99)
        XCTAssertEqual(targets.map(\.pid), [10, 11])
    }

    func testLoginwindowIsNeverATarget() {
        let running = [info(1, "loginwindow", "com.apple.loginwindow")]
        XCTAssertThrowsError(try AppQuit.targets("loginwindow", running: running, ownPID: 99))
        XCTAssertThrowsError(try AppQuit.targets("com.apple.loginwindow", running: running, ownPID: 99))
    }

    func testNotRunning() {
        XCTAssertThrowsError(try AppQuit.targets("Nope", running: [info(1, "Safari")], ownPID: 99)) { error in
            XCTAssertEqual((error as? ToolError)?.message, "Application 'Nope' is not running.")
        }
    }

    func testMatchesBundleIDExactlyAndNameCaseInsensitively() throws {
        let running = [info(1, "Safari", "com.apple.Safari"), info(2, "Preview", "com.apple.Preview")]
        XCTAssertEqual(try AppQuit.targets("com.apple.Safari", running: running, ownPID: 99).map(\.pid), [1])
        XCTAssertThrowsError(try AppQuit.targets("COM.APPLE.SAFARI", running: running, ownPID: 99))
        XCTAssertEqual(try AppQuit.targets("pReVieW", running: running, ownPID: 99).map(\.pid), [2])
    }

    func testQuitCopy() {
        XCTAssertEqual(Desktop.quitMessage(name: "Preview", force: false), "Quit requested for Preview.")
        XCTAssertEqual(Desktop.quitMessage(name: "Preview", force: true), "Force-quit Preview.")
    }

    func testBlankQueriesSelectNothing() {
        let running = [info(1, ""), info(2, "Preview")]
        for query in ["", " ", "  \t"] {
            XCTAssertThrowsError(try AppQuit.targets(query, running: running, ownPID: 99))
            XCTAssertFalse(App.matches(name: nil, bundleID: nil, query: query))
            XCTAssertFalse(App.matches(name: "", bundleID: nil, query: query))
        }
    }

    func testMatchesNeedsFullNameAndTrimsWhitespace() {
        XCTAssertFalse(App.matches(name: "Preview", bundleID: nil, query: "Pre"))
        XCTAssertTrue(App.matches(name: "Preview", bundleID: nil, query: " Preview "))
        XCTAssertFalse(App.matches(name: nil, bundleID: nil, query: "Preview"))
    }

    func testQuitRequiresName() async {
        for name in [nil, "", "  "] as [String?] {
            do {
                _ = try await Desktop().quit(name: name, force: true)
                XCTFail("expected an error")
            } catch {
                XCTAssertEqual((error as? ToolError)?.message, "App name or bundle ID required for force_quit.")
            }
        }
    }
}
