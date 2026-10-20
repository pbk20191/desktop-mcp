import CoreGraphics
import Foundation
import MCP
import XCTest
@testable import DesktopMCPCore

/// Answers `find` from a fixed list, filtered as the AX finder would, and counts the calls.
final class FakeElementFinder: ElementFinder {
    let elements: [FoundElement]
    let calls = Locked(0)

    init(_ elements: [FoundElement]) { self.elements = elements }

    func find(app: String?, title: String?, role: String?) async -> [FoundElement] {
        calls.withLock { $0 += 1 }
        return elements.filter { e in
            (role.map { $0 == e.role } ?? true)
                && (title.map { e.title.range(of: $0, options: .caseInsensitive) != nil } ?? true)
        }
    }
}

final class WaitToolTests: XCTestCase {
    /// Window and element conditions check Accessibility first; these tests drive fakes.
    override func setUp() {
        super.setUp()
        withAccessibility(true)
    }

    private func window(pid: pid_t = 1, app: String = "Preview", title: String = "Report") -> WindowInfo {
        WindowInfo(pid: pid, index: 0, app: app, title: title, frame: CGRect(x: 10, y: 20, width: 800, height: 600),
                   focused: false, minimized: false, fullscreen: false, number: 7)
    }

    private func run(windows: any WindowSource = FakeWindowSource([]),
                     elements: any ElementFinder = FakeElementFinder([]),
                     running: @escaping @Sendable () async -> [RunningAppInfo] = { [] },
                     _ arguments: [String: Value]) async throws -> String {
        let tool = WaitTool.tool(windows: windows, elements: elements, running: running)
        let content = try await tool.handler(ToolArguments(arguments))
        guard case .text(let text, _, _) = content.first else { return "" }
        return text
    }

    private func error(windows: any WindowSource = FakeWindowSource([]),
                       elements: any ElementFinder = FakeElementFinder([]),
                       running: @escaping @Sendable () async -> [RunningAppInfo] = { [] },
                       _ arguments: [String: Value]) async -> String? {
        do {
            _ = try await run(windows: windows, elements: elements, running: running, arguments)
            return nil
        } catch {
            return "\(error)"
        }
    }

    func testPlainDurationStillWorks() async throws {
        let text = try await run(["duration": 0])
        XCTAssertEqual(text, "Waited for 0 seconds.")
        let missing = await error([:])
        XCTAssertEqual(missing, "Missing required argument 'duration'")
    }

    func testPollReturnsAsSoonAsTheCheckSucceeds() async throws {
        let calls = Locked(0)
        let start = Date()
        let value = try await WaitTool.poll(timeout: 10) { () -> String? in
            calls.withLock { $0 += 1; return $0 == 3 ? "done" : nil }
        }
        XCTAssertEqual(value, "done")
        XCTAssertEqual(calls.current, 3)
        XCTAssertLessThan(Date().timeIntervalSince(start), 1)
    }

    func testPollTimesOut() async throws {
        let start = Date()
        let value = try await WaitTool.poll(timeout: 0.3) { () -> String? in nil }
        let elapsed = Date().timeIntervalSince(start)
        XCTAssertNil(value)
        XCTAssertGreaterThanOrEqual(elapsed, 0.3)
        XCTAssertLessThan(elapsed, 1.5)
    }

    func testPollStopsWhenCancelled() async throws {
        let task = Task { try await WaitTool.poll(timeout: 30) { () -> String? in nil } }
        try await Task.sleep(for: .milliseconds(150))
        task.cancel()
        let start = Date()
        do {
            _ = try await task.value
            XCTFail("expected cancellation")
        } catch is CancellationError {}
        XCTAssertLessThan(Date().timeIntervalSince(start), 1)
    }

    func testAppRunningAndQuit() async throws {
        let preview = RunningAppInfo(pid: 42, name: "Preview", bundleID: "com.apple.Preview")
        // Appears on the third look.
        let looks = Locked(0)
        let appearing: @Sendable () async -> [RunningAppInfo] = {
            looks.withLock { $0 += 1; return $0 >= 3 ? [preview] : [] }
        }
        let running = try await run(running: appearing, ["until": "app_running", "app": "Preview"])
        XCTAssertEqual(running, "App Preview is running.")

        // Gone on the third look; matched by bundle id.
        let quitLooks = Locked(0)
        let quitting: @Sendable () async -> [RunningAppInfo] = {
            quitLooks.withLock { $0 += 1; return $0 >= 3 ? [] : [preview] }
        }
        let quit = try await run(running: quitting, ["until": "app_quit", "app": "com.apple.Preview"])
        XCTAssertEqual(quit, "App com.apple.Preview quit.")
        XCTAssertEqual(quitLooks.current, 3)
    }

    func testAppConditionsNeedNoAccessibility() async throws {
        withAccessibility(false)
        let preview = RunningAppInfo(pid: 42, name: "Preview", bundleID: nil)
        let text = try await run(running: { [preview] }, ["until": "app_running", "app": "preview"])
        XCTAssertEqual(text, "App preview is running.")
        let refused = await error(windows: FakeWindowSource([window()]), ["until": "window", "app": "Preview"])
        XCTAssertEqual(refused, Permissions.accessibilityMessage)
    }

    func testWindowAppearsReturnsTheListLine() async throws {
        let source = FakeWindowSource([window(title: "Untitled")])
        let later = [window(title: "Untitled"), window(pid: 1, title: "Quarterly report")]
        Task {
            try await Task.sleep(for: .milliseconds(150))
            await source.set(later)
        }
        let text = try await run(windows: source, ["until": "window", "app": "Preview", "title": "REPORT"])
        XCTAssertEqual(text, "1:w7\tPreview\t\"Quarterly report\"\t[10,20,800,600]")

        // Title alone looks at every app's windows.
        let any = try await run(windows: FakeWindowSource([window(app: "Notes", title: "Report")]),
                                ["until": "window", "title": "report"])
        XCTAssertEqual(any, "1:w7\tNotes\t\"Report\"\t[10,20,800,600]")
    }

    func testWindowGone() async throws {
        let source = FakeWindowSource([window(), window(app: "Notes", title: "Other")])
        let later = [window(app: "Notes", title: "Other")]
        Task {
            try await Task.sleep(for: .milliseconds(150))
            await source.set(later)
        }
        let text = try await run(windows: source, ["until": "window_gone", "app": "Preview"])
        XCTAssertEqual(text, "No window matches any more.")
    }

    func testElementFoundReturnsCentre() async throws {
        let finder = FakeElementFinder([
            FoundElement(role: "AXButton", title: "Save", center: CGPoint(x: 119.6, y: 340.2)),
            FoundElement(role: "AXButton", title: "Save As…", center: CGPoint(x: 10, y: 10)),
        ])
        let text = try await run(elements: finder, ["until": "element", "title": "save", "role": "AXButton"])
        XCTAssertEqual(text, "AXButton \"Save\" at [120,340]")
    }

    func testElementWaitIgnoresSecureValues() async throws {
        // A secure field whose value is "Save": never matched, and its value never read.
        let secure = AXElementFinder.label(role: "AXTextField", subrole: "AXSecureTextField", title: "",
                                           description: "", value: { XCTFail("read a secure value"); return "Save" },
                                           matching: "Save", role: nil)
        XCTAssertNil(secure)
        let secureByRole = AXElementFinder.label(role: "AXSecureTextField", subrole: nil, title: "", description: "",
                                                 value: { XCTFail("read a secure value"); return "Save" },
                                                 matching: "save", role: "AXSecureTextField")
        XCTAssertNil(secureByRole)
        // Matched by its description instead, still without reading the value.
        let described = AXElementFinder.label(role: "AXSecureTextField", subrole: nil, title: "",
                                              description: "Password", value: { XCTFail("read a secure value"); return "x" },
                                              matching: "pass", role: nil)
        XCTAssertEqual(described, "Password")

        // A plain field matches on its value; the reply names its title, or the query.
        XCTAssertEqual(AXElementFinder.label(role: "AXStaticText", subrole: nil, title: "", description: "",
                                             value: { "Save changes?" }, matching: "SAVE", role: nil), "SAVE")
        XCTAssertEqual(AXElementFinder.label(role: "AXButton", subrole: nil, title: "Save", description: "",
                                             value: { "" }, matching: "sav", role: "AXButton"), "Save")
        XCTAssertNil(AXElementFinder.label(role: "AXButton", subrole: nil, title: "Save", description: "",
                                           value: { "" }, matching: "save", role: "AXCheckBox"))
        // Role alone: the title or description, never the value.
        XCTAssertEqual(AXElementFinder.label(role: "AXTextField", subrole: nil, title: "", description: "",
                                             value: { XCTFail("read a value for a role-only match"); return "x" },
                                             matching: nil, role: "AXTextField"), "")
    }

    func testTimeoutCopy() async {
        let window = await error(windows: FakeWindowSource([]),
                                 ["until": "window", "app": "Preview", "title": "Report", "timeout": 1])
        XCTAssertEqual(window, "Timed out after 1s waiting for window \"Report\" in Preview.")
        let gone = await error(windows: FakeWindowSource([self.window()]),
                               ["until": "window_gone", "title": "Report", "timeout": 0])
        XCTAssertEqual(gone, "Timed out after 0s waiting for window \"Report\" to close.")
        let appOnly = await error(["until": "window", "app": "Preview", "timeout": 0])
        XCTAssertEqual(appOnly, "Timed out after 0s waiting for window in Preview.")
        let running = await error(["until": "app_running", "app": "Preview", "timeout": 0])
        XCTAssertEqual(running, "Timed out after 0s waiting for app Preview to run.")
        let quit = await error(running: { [RunningAppInfo(pid: 1, name: "Preview", bundleID: nil)] },
                               ["until": "app_quit", "app": "Preview", "timeout": 0])
        XCTAssertEqual(quit, "Timed out after 0s waiting for app Preview to quit.")
        let element = await error(["until": "element", "role": "AXButton", "title": "Save", "timeout": 0])
        XCTAssertEqual(element, "Timed out after 0s waiting for element AXButton \"Save\".")
        let titleOnly = await error(["until": "element", "title": "Save", "timeout": 0])
        XCTAssertEqual(titleOnly, "Timed out after 0s waiting for element \"Save\".")

        do {
            _ = try await run(["until": "app_running", "app": "Preview", "timeout": 0])
            XCTFail("expected a timeout")
        } catch let error as ToolError {
            XCTAssertEqual(error.telemetry, "Wait timed out")
        } catch {
            XCTFail("\(error)")
        }
    }

    func testArgumentRules() async {
        let element = await error(["until": "element", "app": "Preview"])
        XCTAssertEqual(element, "title or role required for element.")
        let running = await error(["until": "app_running"])
        XCTAssertEqual(running, "app required for app_running.")
        let quit = await error(["until": "app_quit", "app": "  "])
        XCTAssertEqual(quit, "app required for app_quit.")
        let window = await error(["until": "window"])
        XCTAssertEqual(window, "app or title required for window.")
        let gone = await error(["until": "window_gone", "title": ""])
        XCTAssertEqual(gone, "app or title required for window_gone.")
        let unknown = await error(["until": "sunrise"])
        XCTAssertEqual(unknown, "'until' must be one of: app_running, app_quit, window, window_gone, element")
        // 'duration' is not needed, and ignored, with 'until'.
        let preview = RunningAppInfo(pid: 1, name: "Preview", bundleID: nil)
        let text = try? await run(running: { [preview] }, ["until": "app_running", "app": "Preview", "duration": 60])
        XCTAssertEqual(text, "App Preview is running.")
    }
}
