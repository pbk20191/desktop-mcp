import CoreGraphics
import Foundation
import MCP
import XCTest
@testable import DesktopMCPCore

/// Answers `find` from a fixed list, filtered as the AX finder would (first match).
final class FakeElementFinder: ElementFinder {
    let elements: [FoundElement]

    init(_ elements: [FoundElement]) { self.elements = elements }

    func find(app: String?, title: String?, role: String?, deadline: ContinuousClock.Instant) async -> FoundElement? {
        elements.first { e in
            (role.map { $0 == e.role } ?? true)
                && (title.map { e.title.range(of: $0, options: .caseInsensitive) != nil } ?? true)
        }
    }
}

/// Walks a fake tree as the AX finder walks the app's windows, honouring the deadline.
struct TreeElementFinder: ElementFinder {
    let roots: [FakeNode]

    func find(app: String?, title: String?, role: String?, deadline: ContinuousClock.Instant) async -> FoundElement? {
        var budget = AXElementFinder.maxElements
        let result = AXElementFinder.walk(roots, title: title, role: role, budget: &budget,
                                          deadline: deadline, cancelled: Locked(false))
        return result.exact ?? result.partial
    }
}

/// A fake accessibility tree for the element walk. Every attribute read is counted, and
/// `onVisit` runs on each one (to cancel or stall mid-walk).
struct FakeNode: ElementNode, Sendable {
    var attributesValue: ElementAttributes
    var valueText = ""
    var kids: [FakeNode] = []
    var hangs = false
    let visits: Locked<Int>
    var onVisit: @Sendable (Int) -> Void = { _ in }

    init(role: String = "AXGroup", subrole: String? = nil, title: String = "", description: String = "",
         value: String = "", frame: CGRect? = CGRect(x: 0, y: 0, width: 10, height: 10),
         kids: [FakeNode] = [], hangs: Bool = false, visits: Locked<Int>, onVisit: @escaping @Sendable (Int) -> Void = { _ in }) {
        attributesValue = ElementAttributes(role: role, subrole: subrole, title: title, description: description, frame: frame)
        valueText = value
        self.kids = kids
        self.hangs = hangs
        self.visits = visits
        self.onVisit = onVisit
    }

    func attributes() throws -> ElementAttributes {
        let n = visits.withLock { $0 += 1; return $0 }
        onVisit(n)
        if hangs { throw ElementNotAnswering() }
        return attributesValue
    }

    func value() -> String {
        if SecureField.isSecure(role: attributesValue.role, subrole: attributesValue.subrole) || attributesValue.role.isEmpty {
            XCTFail("read the value of a secure or unreadable element")
        }
        return valueText
    }

    func children() throws -> [FakeNode] { kids }
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
        XCTAssertLessThan(Date().timeIntervalSince(start), 2)
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

    func testWindowGoneWaitsWhileTheAppDoesNotAnswer() async throws {
        // A busy app reads as no windows; that is not the window closing.
        let busy = FakeWindowSource([window()])
        await busy.setAnswering(false)
        let start = Date()
        let timedOut = await error(windows: busy, ["until": "window_gone", "app": "Preview", "timeout": 1])
        XCTAssertEqual(timedOut, "Timed out after 1s waiting for window in Preview to close.")
        XCTAssertGreaterThanOrEqual(Date().timeIntervalSince(start), 1)

        // It answers again, the window gone.
        let source = FakeWindowSource([window()])
        await source.setAnswering(false)
        Task {
            try await Task.sleep(for: .milliseconds(150))
            await source.set([])
            await source.setAnswering(true)
        }
        let text = try await run(windows: source, ["until": "window_gone", "app": "Preview"])
        XCTAssertEqual(text, "No window matches any more.")

        // 'window' finds nothing in an app that does not answer.
        let window = await error(windows: busy, ["until": "window", "app": "Preview", "timeout": 0])
        XCTAssertEqual(window, "Timed out after 0s waiting for window in Preview.")
    }

    func testElementFoundReturnsCentre() async throws {
        let finder = FakeElementFinder([
            FoundElement(role: "AXButton", title: "Save", center: CGPoint(x: 119.6, y: 340.2)),
            FoundElement(role: "AXButton", title: "Save As…", center: CGPoint(x: 10, y: 10)),
        ])
        let text = try await run(elements: finder, ["until": "element", "title": "save", "role": "AXButton"])
        XCTAssertEqual(text, "AXButton \"Save\" at [120,340]")
    }

    func testElementWithTimeoutZeroStillLooksOnce() async throws {
        let visits = Locked(0)
        let save = FakeNode(role: "AXButton", title: "Save", frame: CGRect(x: 100, y: 300, width: 40, height: 80), visits: visits)
        let finder = TreeElementFinder(roots: [FakeNode(role: "AXWindow", kids: [FakeNode(visits: visits), save], visits: visits)])
        let text = try await run(elements: finder, ["until": "element", "title": "Save", "timeout": 0])
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
        XCTAssertEqual(described?.name, "Password")

        // A plain field matches on its value; the reply names its title, or the query.
        XCTAssertEqual(AXElementFinder.label(role: "AXStaticText", subrole: nil, title: "", description: "",
                                             value: { "Save changes?" }, matching: "SAVE", role: nil)?.name, "SAVE")
        XCTAssertEqual(AXElementFinder.label(role: "AXButton", subrole: nil, title: "Save", description: "",
                                             value: { "" }, matching: "sav", role: "AXButton")?.name, "Save")
        XCTAssertNil(AXElementFinder.label(role: "AXButton", subrole: nil, title: "Save", description: "",
                                           value: { "" }, matching: "save", role: "AXCheckBox"))
        // Role alone: the title or description, never the value.
        XCTAssertEqual(AXElementFinder.label(role: "AXTextField", subrole: nil, title: "", description: "",
                                             value: { XCTFail("read a value for a role-only match"); return "x" },
                                             matching: nil, role: "AXTextField")?.name, "")
    }

    private func walk(_ roots: [FakeNode], title: String? = nil, role: String? = nil, budget: Int = 5000,
                      deadline: ContinuousClock.Instant = .now + .seconds(60),
                      cancelled: Locked<Bool> = Locked(false)) -> AXElementFinder.WalkResult {
        var budget = budget
        return AXElementFinder.walk(roots, title: title, role: role, budget: &budget, deadline: deadline, cancelled: cancelled)
    }

    /// A window holding `count` plain groups, then `last`.
    private func window(_ visits: Locked<Int>, groups count: Int, then last: [FakeNode] = [],
                        onVisit: @escaping @Sendable (Int) -> Void = { _ in }) -> FakeNode {
        FakeNode(role: "AXWindow", kids: (0..<count).map { _ in FakeNode(visits: visits, onVisit: onVisit) } + last,
                 visits: visits, onVisit: onVisit)
    }

    func testWalkStopsAtTheFirstExactMatch() {
        let visits = Locked(0)
        let save = FakeNode(role: "AXButton", title: "Save", frame: CGRect(x: 100, y: 300, width: 40, height: 80), visits: visits)
        let root = window(visits, groups: 3, then: [save] + (0..<50).map { _ in FakeNode(visits: visits) })
        let result = walk([root], title: "save", role: "AXButton")
        XCTAssertEqual(result.exact, FoundElement(role: "AXButton", title: "Save", center: CGPoint(x: 120, y: 340)))
        XCTAssertEqual(visits.current, 5)  // the window, three groups, the button
    }

    func testWalkPrefersAnExactTitleOverAnEarlierSubstring() {
        let visits = Locked(0)
        let saveAs = FakeNode(role: "AXButton", title: "Save As…", visits: visits)
        let save = FakeNode(role: "AXButton", title: "SAVE", visits: visits)
        let result = walk([window(visits, groups: 0, then: [saveAs, save])], title: "save")
        XCTAssertEqual(result.exact?.title, "SAVE")

        let onlyPartial = walk([window(Locked(0), groups: 0, then: [FakeNode(role: "AXButton", title: "Save As…", visits: Locked(0))])],
                               title: "save")
        XCTAssertNil(onlyPartial.exact)
        XCTAssertEqual(onlyPartial.partial?.title, "Save As…")
    }

    func testWalkHonoursADeadlineMidWalk() {
        let visits = Locked(0)
        // Each read takes 20 ms; the deadline passes after a few of them.
        let root = window(visits, groups: 200) { _ in usleep(20_000) }
        let start = Date()
        let result = walk([root], title: "missing", deadline: .now + .milliseconds(100))
        XCTAssertTrue(result.stopped)
        XCTAssertNil(result.exact)
        XCTAssertLessThan(visits.current, 20)
        XCTAssertLessThan(Date().timeIntervalSince(start), 1)

        let past = Locked(0)
        _ = walk([window(past, groups: 5)], title: "x", deadline: .now - .seconds(1))
        XCTAssertEqual(past.current, 0)
    }

    func testWalkHonoursCancellationMidWalk() {
        let visits = Locked(0)
        let cancelled = Locked(false)
        let root = window(visits, groups: 100) { n in if n == 7 { cancelled.current = true } }
        let result = walk([root], title: "missing", cancelled: cancelled)
        XCTAssertTrue(result.stopped)
        XCTAssertEqual(visits.current, 7)
    }

    func testWalkEndsWhenTheAppStopsAnswering() {
        let visits = Locked(0)
        let hung = FakeNode(hangs: true, visits: visits)
        let root = window(visits, groups: 2, then: [hung] + (0..<10).map { _ in FakeNode(visits: visits) })
        let result = walk([root], title: "missing")
        XCTAssertNil(result.exact)
        XCTAssertEqual(visits.current, 4)
    }

    func testWalkBudgetBoundsVisitsAndQueue() {
        let visits = Locked(0)
        let root = window(visits, groups: 100)
        var budget = 10
        let result = AXElementFinder.walk([root], title: "missing", role: nil, budget: &budget,
                                          deadline: .now + .seconds(60), cancelled: Locked(false))
        XCTAssertEqual(visits.current, 10)
        XCTAssertEqual(budget, 0)
        XCTAssertTrue(result.stopped)
    }

    func testWalkNeverReadsSecureOrUnreadableValues() {
        let visits = Locked(0)
        // FakeNode.value fails the test if called for these.
        let secure = FakeNode(role: "AXTextField", subrole: "AXSecureTextField", value: "Save", visits: visits)
        let secureRole = FakeNode(role: "AXSecureTextField", value: "Save", visits: visits)
        let unreadable = FakeNode(role: "", value: "Save", visits: visits)
        let result = walk([window(visits, groups: 0, then: [secure, secureRole, unreadable])], title: "Save")
        XCTAssertNil(result.exact)
        XCTAssertNil(result.partial)
        XCTAssertEqual(visits.current, 4)

        let plain = walk([window(Locked(0), groups: 0, then: [FakeNode(role: "AXStaticText", value: "save", visits: Locked(0))])],
                         title: "Save")
        XCTAssertEqual(plain.exact?.title, "Save")
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
