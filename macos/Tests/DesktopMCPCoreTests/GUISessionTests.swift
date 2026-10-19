import XCTest
@testable import DesktopMCPCore

final class GUISessionTests: XCTestCase {
    func testTestHostRunsInsideAWindowServerSession() {
        // The test runner is started from a logged-in session; the detection must say so, or the
        // server would never use AppKit.
        XCTAssertTrue(GUISession.isAvailable)
    }
}
