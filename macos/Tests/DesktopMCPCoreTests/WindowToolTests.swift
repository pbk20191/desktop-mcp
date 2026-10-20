import CoreGraphics
import Foundation
import MCP
import XCTest
@testable import DesktopMCPCore

/// Stands in for the Accessibility windows: a mutable list, and a record of every action.
/// Like `AXWindows`, each app's windows are indexed front to back, and focusing (or
/// restoring through focus) brings a window to the front.
actor FakeWindowSource: WindowSource {
    private(set) var list: [WindowInfo] = []
    var actions: [String] = []
    let apps: Set<String>
    /// Widths above this are clamped, as an app with a size limit would.
    var maxWidth: CGFloat?
    let displayList: [DisplayFrame]

    init(_ list: [WindowInfo], apps: Set<String> = [], maxWidth: CGFloat? = nil, displays: [DisplayFrame] = []) {
        self.apps = apps
        self.maxWidth = maxWidth
        self.displayList = displays
        self.list = Self.reindexed(list)
    }

    private static func reindexed(_ list: [WindowInfo]) -> [WindowInfo] {
        var next: [pid_t: Int] = [:]
        return list.map { w in
            var w = w
            w.index = next[w.pid, default: 0]
            next[w.pid] = w.index + 1
            return w
        }
    }

    func set(_ list: [WindowInfo]) { self.list = Self.reindexed(list) }

    func windows(app: String?) async -> [WindowInfo] {
        guard let app else { return list }
        return list.filter { $0.app.lowercased() == app.lowercased() }
    }

    func windows(pid: pid_t) async -> [WindowInfo] { list.filter { $0.pid == pid } }

    func appExists(_ app: String) async -> Bool {
        apps.contains(app) || list.contains { $0.app.lowercased() == app.lowercased() }
    }

    /// Records `name` and applies `change` to the window `w` names, as the AX source finds it.
    private func act(_ name: String, _ w: WindowInfo, _ change: (inout WindowInfo) -> Void = { _ in }) -> Bool {
        guard let i = list.firstIndex(where: { $0.pid == w.pid && w.names($0) }) else { return false }
        actions.append("\(name) \(list[i].id)")
        change(&list[i])
        return true
    }

    func focus(_ w: WindowInfo) async -> Bool {
        guard act("focus", w, { $0.minimized = false; $0.focused = true }) else { return false }
        let i = list.firstIndex { $0.pid == w.pid && w.names($0) }!
        let front = list.remove(at: i)
        for j in list.indices { list[j].focused = false }
        list.insert(front, at: 0)
        list = Self.reindexed(list)
        return true
    }

    func setMinimized(_ w: WindowInfo, _ on: Bool) async -> Bool {
        act(on ? "minimize" : "restore", w) { $0.minimized = on }
    }

    func move(_ w: WindowInfo, to point: CGPoint) async -> Bool {
        act("move", w) { $0.frame.origin = point }
    }

    func resize(_ w: WindowInfo, to size: CGSize) async -> Bool {
        let width = maxWidth.map { min($0, size.width) } ?? size.width
        return act("resize", w) { $0.frame.size = CGSize(width: width, height: size.height) }
    }

    func close(_ w: WindowInfo) async -> Bool {
        act("close", w)
    }

    func setFrame(_ w: WindowInfo, _ frame: CGRect) async -> Bool {
        act("frame", w) { $0.frame = frame }
    }

    func setFullScreen(_ w: WindowInfo, _ on: Bool) async -> Bool {
        act(on ? "fullscreen" : "exit_fullscreen", w) { $0.fullscreen = on }
    }

    func displays() async -> [DisplayFrame] { displayList }
}

final class WindowToolTests: XCTestCase {
    /// The handler checks Accessibility first; these tests drive a fake source, so grant it.
    override func setUp() {
        super.setUp()
        withAccessibility(true)
    }

    private func window(pid: pid_t = 1, index: Int = 0, app: String = "Safari", title: String = "Docs",
                        frame: CGRect = CGRect(x: 10, y: 20, width: 800, height: 600),
                        focused: Bool = false, minimized: Bool = false, fullscreen: Bool = false,
                        number: CGWindowID? = nil) -> WindowInfo {
        WindowInfo(pid: pid, index: index, app: app, title: title, frame: frame,
                   focused: focused, minimized: minimized, fullscreen: fullscreen, number: number)
    }

    private func run(_ source: FakeWindowSource, _ cache: WindowCache, _ arguments: [String: Value]) async throws -> String {
        let content = try await WindowTool.tool(source: source, cache: cache).handler(ToolArguments(arguments))
        guard case .text(let text, _, _) = content.first else { return "" }
        return text
    }

    private func error(_ source: FakeWindowSource, _ cache: WindowCache, _ arguments: [String: Value]) async -> String? {
        do { _ = try await run(source, cache, arguments); return nil } catch { return "\(error)" }
    }

    func testListFormat() async throws {
        XCTAssertEqual(WindowTool.format([
            window(pid: 123, focused: true, number: 456),
            window(pid: 123, index: 1, title: "Other", frame: CGRect(x: 0.4, y: -0.6, width: 300.5, height: 200),
                   minimized: true, fullscreen: true),
            window(pid: 9, app: "Notes", title: "a\tb\nc"),
        ]), """
        123:w456\tSafari\t"Docs"\t[10,20,800,600]\tfocused
        123:1\tSafari\t"Other"\t[0,-1,301,200]\tminimized,fullscreen
        9:0\tNotes\t"a b c"\t[10,20,800,600]
        """)
        XCTAssertEqual(WindowTool.format([]), "No windows.")

        let source = FakeWindowSource([window(pid: 123)])
        let listed = try await run(source, WindowCache(), [:])
        XCTAssertEqual(listed, "123:0\tSafari\t\"Docs\"\t[10,20,800,600]")
    }

    func testParseID() {
        XCTAssertEqual(WindowTool.parseID("123:w456"), .number(123, 456))
        XCTAssertEqual(WindowTool.parseID("123:4"), .index(123, 4))
        for id in ["123:wx", "123:", "x:1", "123:w", "123:w0", "123:w-1", "abc", "1", ":0", "1:-1", "0:0", "1:0:0"] {
            XCTAssertNil(WindowTool.parseID(id), id)
        }
    }

    func testListByApp() async throws {
        let source = FakeWindowSource([window(), window(pid: 2, app: "Notes", title: "N")], apps: ["Mail"])
        let cache = WindowCache()
        let notes = try await run(source, cache, ["app": "notes"])
        XCTAssertEqual(notes, "2:0\tNotes\t\"N\"\t[10,20,800,600]")
        let mail = try await run(source, cache, ["app": "Mail"])
        XCTAssertEqual(mail, "No windows.")
        let missing = await error(source, cache, ["app": "Nope"])
        XCTAssertEqual(missing, "Application 'Nope' not found.")
    }

    func testInvalidIdErrors() async {
        let source = FakeWindowSource([window()])
        for id in ["abc", "1", "1:", ":0", "1:-1", "0:0", "1:0:0", "1:wx"] {
            let message = await error(source, WindowCache(), ["mode": "focus", "id": .string(id)])
            XCTAssertEqual(message, "Invalid window id: \(id)")
        }
    }

    // MARK: Number ids

    func testNumberIdFollowsTheWindowThroughAReorder() async throws {
        // Two windows nobody can tell apart by title and frame.
        let source = FakeWindowSource([window(title: "Untitled", number: 10), window(title: "Untitled", number: 11)])
        let cache = WindowCache()
        let listed = try await run(source, cache, [:])
        XCTAssertEqual(listed, """
        1:w10\tSafari\t"Untitled"\t[10,20,800,600]
        1:w11\tSafari\t"Untitled"\t[10,20,800,600]
        """)
        let focused = try await run(source, cache, ["mode": "focus", "id": "1:w11"])
        XCTAssertEqual(focused, "Focused 1:w11.")
        // Focus brought w11 to the front: it is now at index 0.
        let reordered = await source.list
        XCTAssertEqual(reordered.map(\.number), [11, 10])
        XCTAssertEqual(reordered.map(\.index), [0, 1])

        let minimized = try await run(source, cache, ["mode": "minimize", "id": "1:w11"])
        XCTAssertEqual(minimized, "Minimized 1:w11.")
        let after = await source.list
        XCTAssertEqual(after.first { $0.number == 11 }?.minimized, true)
        XCTAssertEqual(after.first { $0.number == 10 }?.minimized, false)
        let actions = await source.actions
        XCTAssertEqual(actions, ["focus 1:w11", "minimize 1:w11"])
    }

    func testNumberIdIgnoresTitleAndFrameChanges() async throws {
        let source = FakeWindowSource([window(number: 10)])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        await source.set([window(title: "Renamed", frame: CGRect(x: 300, y: 300, width: 100, height: 100), number: 10)])
        let closed = try await run(source, cache, ["mode": "close", "id": "1:w10"])
        XCTAssertEqual(closed, "Close requested for 1:w10 (\"Renamed\").")
    }

    func testFocusRestoresAMinimizedWindowEvenIfItsFrameChanges() async throws {
        let source = FakeWindowSource([window(minimized: true, number: 10)])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        await source.set([window(frame: CGRect(x: 0, y: 0, width: 10, height: 10), minimized: true, number: 10)])
        let focused = try await run(source, cache, ["mode": "focus", "id": "1:w10"])
        XCTAssertEqual(focused, "Focused 1:w10.")
        let restored = await source.list.first?.minimized
        XCTAssertEqual(restored, false)
    }

    func testGoneNumberIdErrors() async throws {
        let source = FakeWindowSource([window(number: 10), window(number: 11)])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        await source.set([window(number: 11)])
        let message = await error(source, cache, ["mode": "close", "id": "1:w10"])
        XCTAssertEqual(message, "Window 1:w10 no longer exists; call Window list again.")
        let actions = await source.actions
        XCTAssertEqual(actions, [])
    }

    func testMoveAndResizeReportTheAppliedFrame() async throws {
        let source = FakeWindowSource([window(number: 10)], maxWidth: 1440)
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        let moved = try await run(source, cache, ["mode": "move", "id": "1:w10", "loc": [5, 5]])
        XCTAssertEqual(moved, "Moved 1:w10 to (5,5).")
        let resized = try await run(source, cache, ["mode": "resize", "id": "1:w10", "size": [5000, 480]])
        XCTAssertEqual(resized, "Resized 1:w10 to 1440×480.")
    }

    // MARK: Index ids (window number unreadable)

    func testUnlistedIdErrors() async {
        let source = FakeWindowSource([window()])
        let message = await error(source, WindowCache(), ["mode": "focus", "id": "1:0"])
        XCTAssertEqual(message, "Window 1:0 was not listed; call Window list first.")
        let actions = await source.actions
        XCTAssertEqual(actions, [])
    }

    func testMovedWindowIsRefused() async throws {
        let source = FakeWindowSource([window()])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        await source.set([window(frame: CGRect(x: 11, y: 20, width: 800, height: 600))])
        let message = await error(source, cache, ["mode": "close", "id": "1:0"])
        XCTAssertEqual(message, "Window 1:0 changed since it was listed; call Window list again.")

        // Another window now at the same index, same frame but another title.
        await source.set([window(title: "Other")])
        let retitled = await error(source, cache, ["mode": "close", "id": "1:0"])
        XCTAssertEqual(retitled, "Window 1:0 changed since it was listed; call Window list again.")
        let actions = await source.actions
        XCTAssertEqual(actions, [])
    }

    func testSubPointMovesAreTheSameWindow() async throws {
        let source = FakeWindowSource([window()])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        await source.set([window(frame: CGRect(x: 10.2, y: 19.8, width: 800.3, height: 600))])
        let focused = try await run(source, cache, ["mode": "focus", "id": "1:0"])
        XCTAssertEqual(focused, "Focused 1:0.")
    }

    func testClosedWindowIsRefused() async throws {
        let source = FakeWindowSource([window(), window(title: "Second")])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        await source.set([window()])
        let message = await error(source, cache, ["mode": "minimize", "id": "1:1"])
        XCTAssertEqual(message, "Window 1:1 changed since it was listed; call Window list again.")
        let actions = await source.actions
        XCTAssertEqual(actions, [])
    }

    func testMoveUpdatesTheCache() async throws {
        let source = FakeWindowSource([window()])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        let moved = try await run(source, cache, ["mode": "move", "id": "1:0", "loc": [5, 5]])
        XCTAssertEqual(moved, "Moved 1:0 to (5,5).")
        let resized = try await run(source, cache, ["mode": "resize", "id": "1:0", "size": [640, 480]])
        XCTAssertEqual(resized, "Resized 1:0 to 640×480.")
        let focused = try await run(source, cache, ["mode": "focus", "id": "1:0"])
        XCTAssertEqual(focused, "Focused 1:0.")
        let actions = await source.actions
        XCTAssertEqual(actions, ["move 1:0", "resize 1:0", "focus 1:0"])
        let frame = await source.list.first?.frame
        XCTAssertEqual(frame, CGRect(x: 5, y: 5, width: 640, height: 480))
    }

    func testModesNeedTheirArguments() async throws {
        let source = FakeWindowSource([window()])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        let noLoc = await error(source, cache, ["mode": "move", "id": "1:0"])
        XCTAssertEqual(noLoc, "loc required for move mode.")
        let noSize = await error(source, cache, ["mode": "resize", "id": "1:0"])
        XCTAssertEqual(noSize, "size required for resize mode.")
        let badLoc = await error(source, cache, ["mode": "move", "id": "1:0", "loc": [1]])
        XCTAssertEqual(badLoc, "loc must be a list of 2 integers [x, y].")
        let badSize = await error(source, cache, ["mode": "resize", "id": "1:0", "size": [0, 10]])
        XCTAssertEqual(badSize, "size must be 2 positive integers [width, height].")
        for mode in ["focus", "move", "resize", "minimize", "restore", "close"] {
            let message = await error(source, cache, ["mode": .string(mode)])
            XCTAssertEqual(message, "id required for \(mode) mode.")
        }
        let actions = await source.actions
        XCTAssertEqual(actions, [])
    }

    func testMinimizeRestoreAndFocus() async throws {
        let source = FakeWindowSource([window()])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        let restoredVisible = try await run(source, cache, ["mode": "restore", "id": "1:0"])
        XCTAssertEqual(restoredVisible, "Restored 1:0.")
        let minimized = try await run(source, cache, ["mode": "minimize", "id": "1:0"])
        XCTAssertEqual(minimized, "Minimized 1:0.")
        let again = try await run(source, cache, ["mode": "minimize", "id": "1:0"])
        XCTAssertEqual(again, "Minimized 1:0.")
        let focused = try await run(source, cache, ["mode": "focus", "id": "1:0"])
        XCTAssertEqual(focused, "Focused 1:0.")
        let actions = await source.actions
        XCTAssertEqual(actions, ["minimize 1:0", "focus 1:0"])
        let restored = await source.list.first?.minimized
        XCTAssertEqual(restored, false)
    }

    func testFailedActionErrors() async throws {
        actor Refusing: WindowSource {
            let w: WindowInfo
            init(_ w: WindowInfo) { self.w = w }
            func windows(app: String?) async -> [WindowInfo] { [w] }
            func windows(pid: pid_t) async -> [WindowInfo] { [w] }
            func appExists(_ app: String) async -> Bool { true }
            func focus(_ w: WindowInfo) async -> Bool { false }
            func setMinimized(_ w: WindowInfo, _ on: Bool) async -> Bool { false }
            func move(_ w: WindowInfo, to: CGPoint) async -> Bool { false }
            func resize(_ w: WindowInfo, to: CGSize) async -> Bool { false }
            func close(_ w: WindowInfo) async -> Bool { false }
            func setFrame(_ w: WindowInfo, _ frame: CGRect) async -> Bool { false }
            func setFullScreen(_ w: WindowInfo, _ on: Bool) async -> Bool { false }
            func displays() async -> [DisplayFrame] { WindowToolTests.displays }
        }
        let tool = WindowTool.tool(source: Refusing(window(number: 7)), cache: WindowCache())
        _ = try await tool.handler(ToolArguments([:]))
        let calls: [(String, [String: Value])] = [
            ("focus", [:]), ("minimize", [:]), ("close", [:]), ("move", ["loc": [1, 2]]), ("resize", ["size": [3, 4]]),
            ("maximize", [:]), ("fullscreen", [:]),
        ]
        for (mode, extra) in calls {
            var arguments = extra
            arguments["mode"] = .string(mode)
            arguments["id"] = "1:w7"
            do {
                _ = try await tool.handler(ToolArguments(arguments))
                XCTFail("expected an error for \(mode)")
            } catch {
                XCTAssertEqual("\(error)", "Could not \(mode) window 1:w7.")
            }
        }
        do {
            _ = try await tool.handler(ToolArguments(["mode": "display", "id": "1:w7", "display": 1]))
            XCTFail("expected an error for display")
        } catch {
            XCTAssertEqual("\(error)", "Could not move window 1:w7 to display 1.")
        }
        // Leaving full screen only calls the source for a window in full screen.
        let fullscreen = WindowTool.tool(source: Refusing(window(fullscreen: true, number: 7)), cache: WindowCache())
        do {
            _ = try await fullscreen.handler(ToolArguments(["mode": "exit_fullscreen", "id": "1:w7"]))
            XCTFail("expected an error for exit_fullscreen")
        } catch {
            XCTAssertEqual("\(error)", "Could not exit fullscreen window 1:w7.")
        }
    }

    func testCloseReportsRequest() async throws {
        let source = FakeWindowSource([window(), window(title: "Two\tlines\nhere")])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        let closed = try await run(source, cache, ["mode": "close", "id": "1:0"])
        XCTAssertEqual(closed, "Close requested for 1:0 (\"Docs\").")
        let flattened = try await run(source, cache, ["mode": "close", "id": "1:1"])
        XCTAssertEqual(flattened, "Close requested for 1:1 (\"Two lines here\").")
        let actions = await source.actions
        XCTAssertEqual(actions, ["close 1:0", "close 1:1"])
    }

    func testCacheDropsOldest() async {
        let cache = WindowCache()
        for pid in 1...65 {
            await cache.remember([window(pid: pid_t(pid))])
        }
        let first = await cache.listed("1:0")
        XCTAssertNil(first)
        let second = await cache.listed("2:0")
        XCTAssertEqual(second, window(pid: 2))
        let last = await cache.listed("65:0")
        XCTAssertEqual(last, window(pid: 65))
    }

    func testNewerListingWins() async {
        let cache = WindowCache()
        await cache.remember([window()])
        await cache.remember([window(title: "Renamed")])
        let listed = await cache.listed("1:0")
        XCTAssertEqual(listed?.title, "Renamed")
    }

    // MARK: Maximize, full screen and displays

    /// The main display (index 0) and one to its left (index 1), top-left origin, y down.
    static let displays = [
        DisplayFrame(index: 0, name: "Built-in", frame: CGRect(x: 0, y: 0, width: 1440, height: 900),
                     visible: CGRect(x: 0, y: 25, width: 1440, height: 800), scale: 2, main: true),
        DisplayFrame(index: 1, name: "Left", frame: CGRect(x: -1920, y: 0, width: 1920, height: 1080),
                     visible: CGRect(x: -1920, y: 25, width: 1920, height: 1055), scale: 1, main: false),
    ]

    func testMaximizeUsesTheWindowsDisplay() async throws {
        // Centre at x -500: on the left display, not the main one.
        let frame = CGRect(x: -900, y: 100, width: 800, height: 600)
        XCTAssertEqual(WindowTool.maximizedFrame(for: frame, displays: Self.displays),
                       CGRect(x: -1920, y: 25, width: 1920, height: 1055))
        XCTAssertEqual(WindowTool.maximizedFrame(for: CGRect(x: 100, y: 100, width: 400, height: 300), displays: Self.displays),
                       CGRect(x: 0, y: 25, width: 1440, height: 800))

        let source = FakeWindowSource([window(frame: frame, number: 10)], displays: Self.displays)
        let maximized = try await run(source, WindowCache(), ["mode": "maximize", "id": "1:w10"])
        XCTAssertEqual(maximized, "Maximized 1:w10.")
        let applied = await source.list.first?.frame
        XCTAssertEqual(applied, CGRect(x: -1920, y: 25, width: 1920, height: 1055))
        let actions = await source.actions
        XCTAssertEqual(actions, ["frame 1:w10"])
    }

    func testMaximizeOffscreenWindowUsesNearestDisplay() {
        XCTAssertEqual(WindowTool.maximizedFrame(for: CGRect(x: 5000, y: 100, width: 400, height: 300), displays: Self.displays),
                       CGRect(x: 0, y: 25, width: 1440, height: 800))
        XCTAssertEqual(WindowTool.maximizedFrame(for: CGRect(x: -4000, y: 2000, width: 400, height: 300), displays: Self.displays),
                       CGRect(x: -1920, y: 25, width: 1920, height: 1055))
        XCTAssertNil(WindowTool.maximizedFrame(for: .zero, displays: []))
    }

    func testMoveToDisplayKeepsOffsetAndClamps() async throws {
        let main = Self.displays[0], left = Self.displays[1]
        XCTAssertEqual(WindowTool.frame(CGRect(x: 100, y: 125, width: 800, height: 600), movedFrom: main, to: left),
                       CGRect(x: -1820, y: 125, width: 800, height: 600))
        XCTAssertEqual(WindowTool.frame(CGRect(x: 100, y: 125, width: 2500, height: 600), movedFrom: main, to: left),
                       CGRect(x: -1920, y: 125, width: 1920, height: 600))
        // Back to the smaller main display: the offset would push it past the bottom right.
        XCTAssertEqual(WindowTool.frame(CGRect(x: -900, y: 500, width: 800, height: 600), movedFrom: left, to: main),
                       CGRect(x: 640, y: 225, width: 800, height: 600))

        let source = FakeWindowSource([window(frame: CGRect(x: 100, y: 125, width: 800, height: 600), number: 10)],
                                      displays: Self.displays)
        let moved = try await run(source, WindowCache(), ["mode": "display", "id": "1:w10", "display": 1])
        XCTAssertEqual(moved, "Moved 1:w10 to display 1.")
        let applied = await source.list.first?.frame
        XCTAssertEqual(applied, CGRect(x: -1820, y: 125, width: 800, height: 600))
    }

    func testFormatDisplays() async throws {
        XCTAssertEqual(WindowTool.formatDisplays(Self.displays), """
        0\tBuilt-in\t[0,0,1440,900]\tvisible [0,25,1440,800]\tscale 2\tmain
        1\tLeft\t[-1920,0,1920,1080]\tvisible [-1920,25,1920,1055]\tscale 1
        """)
        XCTAssertEqual(WindowTool.formatDisplays([
            DisplayFrame(index: 0, name: "A\tB", frame: .zero, visible: .zero, scale: 1.5, main: false),
        ]), "0\tA B\t[0,0,0,0]\tvisible [0,0,0,0]\tscale 1.5")
        XCTAssertEqual(WindowTool.formatDisplays([]), "No displays.")

        let source = FakeWindowSource([], displays: Self.displays)
        let listed = try await run(source, WindowCache(), ["mode": "displays"])
        XCTAssertEqual(listed, WindowTool.formatDisplays(Self.displays))
    }

    func testCocoaFramesFlipToTopLeft() {
        // A 1920×1080 display left of a 1440×900 main one, bottom edges aligned; Dock on the main one.
        let main = DisplayFrame.fromCocoa(index: 0, name: "Built-in", frame: CGRect(x: 0, y: 0, width: 1440, height: 900),
                                          visible: CGRect(x: 0, y: 75, width: 1440, height: 800),
                                          mainHeight: 900, scale: 2, main: true)
        XCTAssertEqual(main.frame, CGRect(x: 0, y: 0, width: 1440, height: 900))
        XCTAssertEqual(main.visible, CGRect(x: 0, y: 25, width: 1440, height: 800))
        let left = DisplayFrame.fromCocoa(index: 1, name: "Left", frame: CGRect(x: -1920, y: 0, width: 1920, height: 1080),
                                          visible: CGRect(x: -1920, y: 0, width: 1920, height: 1055),
                                          mainHeight: 900, scale: 1, main: false)
        XCTAssertEqual(left.frame, CGRect(x: -1920, y: -180, width: 1920, height: 1080))
        XCTAssertEqual(left.visible, CGRect(x: -1920, y: -155, width: 1920, height: 1055))
    }

    func testFullscreenModesCallTheSource() async throws {
        let source = FakeWindowSource([window(number: 10)])
        let cache = WindowCache()
        let entered = try await run(source, cache, ["mode": "fullscreen", "id": "1:w10"])
        XCTAssertEqual(entered, "Full screen 1:w10.")
        let again = try await run(source, cache, ["mode": "fullscreen", "id": "1:w10"])
        XCTAssertEqual(again, "Full screen 1:w10.")
        let left = try await run(source, cache, ["mode": "exit_fullscreen", "id": "1:w10"])
        XCTAssertEqual(left, "Left full screen 1:w10.")
        let leftAgain = try await run(source, cache, ["mode": "exit_fullscreen", "id": "1:w10"])
        XCTAssertEqual(leftAgain, "Left full screen 1:w10.")
        let actions = await source.actions
        XCTAssertEqual(actions, ["fullscreen 1:w10", "exit_fullscreen 1:w10"])
    }

    func testDisplayModeNeedsAValidIndex() async {
        let source = FakeWindowSource([window(number: 10)], displays: Self.displays)
        let cache = WindowCache()
        let missing = await error(source, cache, ["mode": "display", "id": "1:w10"])
        XCTAssertEqual(missing, "display required for display mode.")
        let unknown = await error(source, cache, ["mode": "display", "id": "1:w10", "display": 9])
        XCTAssertEqual(unknown, "No display 9; see Window displays.")
        for mode in ["maximize", "fullscreen", "exit_fullscreen", "display"] {
            let message = await error(source, cache, ["mode": .string(mode)])
            XCTAssertEqual(message, "id required for \(mode) mode.")
        }
        let actions = await source.actions
        XCTAssertEqual(actions, [])
    }

    func testNewModesRefuseWithoutAccessibility() async {
        withAccessibility(false)
        let source = FakeWindowSource([window(number: 10)], displays: Self.displays)
        let calls: [[String: Value]] = [
            ["mode": "maximize", "id": "1:w10"], ["mode": "fullscreen", "id": "1:w10"],
            ["mode": "exit_fullscreen", "id": "1:w10"], ["mode": "display", "id": "1:w10", "display": 1],
            ["mode": "displays"],
        ]
        for arguments in calls {
            let message = await error(source, WindowCache(), arguments)
            XCTAssertEqual(message, Permissions.accessibilityMessage, "\(arguments)")
        }
        let actions = await source.actions
        XCTAssertEqual(actions, [])
    }

    /// Whether the private symbol behind window numbers exists here; without it every id falls
    /// back to an index id (covered above).
    func testWindowNumberSymbolLookup() throws {
        guard AXWindowSource.getWindowAddress != 0 else { throw XCTSkip("_AXUIElementGetWindow not found") }
        // An application element is not a window: the call fails cleanly instead of crashing.
        XCTAssertNil(AXWindowSource.number(of: AX.application(getpid())))
    }
}
