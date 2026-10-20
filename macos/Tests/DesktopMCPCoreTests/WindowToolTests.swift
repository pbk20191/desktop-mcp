import CoreGraphics
import Foundation
import MCP
import XCTest
@testable import DesktopMCPCore

/// Stands in for the Accessibility windows: a mutable list, and a record of every action.
actor FakeWindowSource: WindowSource {
    var list: [WindowInfo]
    var actions: [String] = []
    let apps: Set<String>

    init(_ list: [WindowInfo], apps: Set<String> = []) {
        self.list = list
        self.apps = apps
    }

    func set(_ list: [WindowInfo]) { self.list = list }

    func windows(app: String?) async -> [WindowInfo] {
        guard let app else { return list }
        return list.filter { $0.app.lowercased() == app.lowercased() }
    }

    func windows(pid: pid_t) async -> [WindowInfo] { list.filter { $0.pid == pid } }

    func appExists(_ app: String) async -> Bool {
        apps.contains(app) || list.contains { $0.app.lowercased() == app.lowercased() }
    }

    private func update(_ w: WindowInfo, _ change: (inout WindowInfo) -> Void) {
        guard let i = list.firstIndex(where: { $0.id == w.id }) else { return }
        change(&list[i])
    }

    func focus(_ w: WindowInfo) async -> Bool {
        actions.append("focus \(w.id)")
        update(w) { $0.focused = true }
        return true
    }

    func setMinimized(_ w: WindowInfo, _ on: Bool) async -> Bool {
        actions.append("\(on ? "minimize" : "restore") \(w.id)")
        update(w) { $0.minimized = on }
        return true
    }

    func move(_ w: WindowInfo, to point: CGPoint) async -> Bool {
        actions.append("move \(w.id) \(Int(point.x)),\(Int(point.y))")
        update(w) { $0.frame.origin = point }
        return true
    }

    func resize(_ w: WindowInfo, to size: CGSize) async -> Bool {
        actions.append("resize \(w.id) \(Int(size.width))x\(Int(size.height))")
        update(w) { $0.frame.size = size }
        return true
    }

    func close(_ w: WindowInfo) async -> Bool {
        actions.append("close \(w.id)")
        return true
    }
}

final class WindowToolTests: XCTestCase {
    private func window(pid: pid_t = 1, index: Int = 0, app: String = "Safari", title: String = "Docs",
                        frame: CGRect = CGRect(x: 10, y: 20, width: 800, height: 600),
                        focused: Bool = false, minimized: Bool = false, fullscreen: Bool = false) -> WindowInfo {
        WindowInfo(pid: pid, index: index, app: app, title: title, frame: frame,
                   focused: focused, minimized: minimized, fullscreen: fullscreen)
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
            window(pid: 123, focused: true),
            window(pid: 123, index: 1, title: "Other", frame: CGRect(x: 0.4, y: -0.6, width: 300.5, height: 200),
                   minimized: true, fullscreen: true),
            window(pid: 9, app: "Notes", title: ""),
        ]), """
        123:0\tSafari\t"Docs"\t[10,20,800,600]\tfocused
        123:1\tSafari\t"Other"\t[0,-1,301,200]\tminimized,fullscreen
        9:0\tNotes\t""\t[10,20,800,600]
        """)
        XCTAssertEqual(WindowTool.format([]), "No windows.")

        let source = FakeWindowSource([window(pid: 123)])
        let listed = try await run(source, WindowCache(), [:])
        XCTAssertEqual(listed, "123:0\tSafari\t\"Docs\"\t[10,20,800,600]")
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
        for id in ["abc", "1", "1:", ":0", "1:-1", "0:0", "1:0:0"] {
            let message = await error(source, WindowCache(), ["mode": "focus", "id": .string(id)])
            XCTAssertEqual(message, "Invalid window id: \(id)")
        }
    }

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
        let source = FakeWindowSource([window(), window(index: 1, title: "Second")])
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
        XCTAssertEqual(actions, ["move 1:0 5,5", "resize 1:0 640x480", "focus 1:0"])
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
        XCTAssertEqual(actions, ["minimize 1:0", "restore 1:0", "focus 1:0"])
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
        }
        let tool = WindowTool.tool(source: Refusing(window()), cache: WindowCache())
        _ = try await tool.handler(ToolArguments([:]))
        let calls: [(String, [String: Value])] = [
            ("focus", [:]), ("minimize", [:]), ("close", [:]), ("move", ["loc": [1, 2]]), ("resize", ["size": [3, 4]]),
        ]
        for (mode, extra) in calls {
            var arguments = extra
            arguments["mode"] = .string(mode)
            arguments["id"] = "1:0"
            do {
                _ = try await tool.handler(ToolArguments(arguments))
                XCTFail("expected an error for \(mode)")
            } catch {
                XCTAssertEqual("\(error)", "Could not \(mode) window 1:0.")
            }
        }
    }

    func testCloseReportsRequest() async throws {
        let source = FakeWindowSource([window()])
        let cache = WindowCache()
        _ = try await run(source, cache, [:])
        let closed = try await run(source, cache, ["mode": "close", "id": "1:0"])
        XCTAssertEqual(closed, "Close requested for 1:0 (\"Docs\").")
        let actions = await source.actions
        XCTAssertEqual(actions, ["close 1:0"])
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
}
