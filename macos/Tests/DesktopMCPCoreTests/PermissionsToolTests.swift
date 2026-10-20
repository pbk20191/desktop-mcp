import MCP
import XCTest
@testable import DesktopMCPCore

final class PermissionsToolTests: XCTestCase {
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
        let state = PermissionState(accessibility: false, screenRecording: false, automation: .notDetermined)
        withPermissions(state)
        let output = try await Permissions.tool().handler(ToolArguments([:]))
        XCTAssertEqual(text(output), Permissions.format(state))
    }

    func testRequireAccessibilityThrowsTheExactCopy() {
        XCTAssertEqual(Permissions.accessibilityMessage,
                       "Accessibility is not granted to desktop-mcp. Grant it in System Settings > Privacy & Security > Accessibility, then restart desktop-mcp.")
        // The guard reads only the Accessibility check, never the full probe.
        let probed = Locked(0)
        let original = Permissions.probe.current
        Permissions.probe.current = {
            probed.withLock { $0 += 1 }
            return PermissionState(accessibility: true, screenRecording: true, automation: .granted)
        }
        addTeardownBlock { Permissions.probe.current = original }

        withAccessibility(false)
        XCTAssertThrowsError(try Permissions.requireAccessibility()) { error in
            let toolError = error as? ToolError
            XCTAssertEqual(toolError?.message, Permissions.accessibilityMessage)
            XCTAssertEqual(toolError?.telemetry, "Accessibility not granted")
        }
        withAccessibility(true)
        XCTAssertNoThrow(try Permissions.requireAccessibility())
        XCTAssertEqual(probed.current, 0)
    }

    func testGuardedToolsRefuseWithoutAccessibility() async throws {
        withAccessibility(false)
        let posted = capturingInput()
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let (client, started) = try await connect(registry)
        defer { Task { await started.shutdown() } }

        let calls: [(String, [String: Value])] = [
            ("Window", ["mode": "list"]),
            ("Menu", ["mode": "list"]),
            ("Click", ["loc": [10, 10]]),
            ("Type", ["loc": [10, 10], "text": "x"]),
            ("Scroll", ["loc": [10, 10]]),
            ("Move", ["loc": [10, 10], "drag": true]),
            ("Shortcut", ["shortcut": "command+c"]),
            ("Desktop", [:]),
            ("App", ["mode": "resize", "window_size": [400, 300]]),
            ("App", ["mode": "move", "window_loc": [0, 0]]),
        ]
        for (name, arguments) in calls {
            let result = try await client.callTool(name: name, arguments: arguments)
            XCTAssertEqual(result.isError, true, "\(name) \(arguments)")
            XCTAssertEqual(text(result.content), "Error executing tool \(name): \(Permissions.accessibilityMessage)")
        }
        let errors = await observer.errors
        XCTAssertEqual(errors, Array(repeating: "Accessibility not granted", count: calls.count))
        XCTAssertEqual(posted.current, 0, "the guard must throw before any input is posted")

        // A plain Wait needs no permission.
        let wait = try await client.callTool(name: "Wait", arguments: ["duration": 0])
        XCTAssertNotEqual(wait.isError, true)
        XCTAssertEqual(text(wait.content), "Waited for 0 seconds.")

        // Control: with the grant, the same Move reaches the (captured) poster.
        withAccessibility(true)
        let moved = try await client.callTool(name: "Move", arguments: ["loc": [10, 10]])
        XCTAssertNotEqual(moved.isError, true)
        XCTAssertEqual(posted.current, 1)
    }

    func testSnapshotNotesMissingAccessibility() {
        XCTAssertEqual(Desktop.permissionNote(trusted: false), Permissions.accessibilityMessage + "\n")
        XCTAssertEqual(Desktop.permissionNote(trusted: true), "")
    }
}
