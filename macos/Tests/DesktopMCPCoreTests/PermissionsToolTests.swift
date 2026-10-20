import MCP
import XCTest
@testable import DesktopMCPCore

/// Swaps `Permissions.probe`; XCTest runs a class's tests one at a time, so the swap is safe here.
final class PermissionsToolTests: XCTestCase {
    private var originalProbe: (@Sendable () -> PermissionState)!

    override func setUp() {
        super.setUp()
        originalProbe = Permissions.probe.current
    }

    override func tearDown() {
        Permissions.probe.current = originalProbe
        super.tearDown()
    }

    private func stub(_ state: PermissionState) {
        Permissions.probe.current = { state }
    }

    private func text(_ content: [MCP.Tool.Content]) -> String? {
        guard case .text(let text, _, _)? = content.first else { return nil }
        return text
    }

    func testFormat() async throws {
        let needs = """
        Needs Accessibility: App (resize, move), Click, Type, Scroll, Move, Shortcut, Snapshot, Desktop, Window, Menu, Wait (until)
        Needs Screen Recording: Snapshot (use_vision), OCR
        """
        XCTAssertEqual(Permissions.format(PermissionState(accessibility: true, screenRecording: true, automation: .granted)), """
        Accessibility: granted
        Screen Recording: granted
        Automation (System Events): granted
        """ + "\n" + needs)
        XCTAssertEqual(Permissions.format(PermissionState(accessibility: true, screenRecording: false, automation: .notDetermined)), """
        Accessibility: granted
        Screen Recording: not granted
        Automation (System Events): not determined
        """ + "\n" + needs)
        XCTAssertEqual(Permissions.format(PermissionState(accessibility: false, screenRecording: true, automation: .denied)), """
        Accessibility: not granted
        Screen Recording: granted
        Automation (System Events): not granted
        """ + "\n" + needs)

        // The tool reports what the probe sees.
        stub(PermissionState(accessibility: false, screenRecording: false, automation: .notDetermined))
        let output = try await Permissions.tool().handler(ToolArguments([:]))
        XCTAssertEqual(text(output), Permissions.format(Permissions.current()))
        XCTAssertTrue(text(output)?.hasPrefix("Accessibility: not granted\n") == true)
    }

    func testRequireAccessibilityThrowsTheExactCopy() {
        XCTAssertEqual(Permissions.accessibilityMessage,
                       "Accessibility is not granted to desktop-mcp. Grant it in System Settings > Privacy & Security > Accessibility, then restart desktop-mcp.")
        stub(PermissionState(accessibility: false, screenRecording: true, automation: .granted))
        XCTAssertThrowsError(try Permissions.requireAccessibility()) { error in
            let toolError = error as? ToolError
            XCTAssertEqual(toolError?.message, Permissions.accessibilityMessage)
            XCTAssertEqual(toolError?.telemetry, "Accessibility not granted")
        }
        stub(PermissionState(accessibility: true, screenRecording: false, automation: .denied))
        XCTAssertNoThrow(try Permissions.requireAccessibility())
    }

    func testGuardedToolsRefuseWithoutAccessibility() async throws {
        stub(PermissionState(accessibility: false, screenRecording: false, automation: .notDetermined))
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let (client, started) = try await connect(registry)
        defer { Task { await started.shutdown() } }

        for name in ["Window", "Menu"] {
            let result = try await client.callTool(name: name, arguments: ["mode": "list"])
            XCTAssertEqual(result.isError, true, name)
            XCTAssertEqual(text(result.content), "Error executing tool \(name): \(Permissions.accessibilityMessage)")
        }
        let errors = await observer.errors
        XCTAssertEqual(errors, ["Accessibility not granted", "Accessibility not granted"])

        // A plain Wait needs no permission.
        let wait = try await client.callTool(name: "Wait", arguments: ["duration": 0])
        XCTAssertNotEqual(wait.isError, true)
        XCTAssertEqual(text(wait.content), "Waited for 0 seconds.")
    }

    func testSnapshotNotesMissingAccessibility() {
        XCTAssertEqual(Desktop.permissionNote(trusted: false), Permissions.accessibilityMessage + "\n")
        XCTAssertEqual(Desktop.permissionNote(trusted: true), "")
    }
}
