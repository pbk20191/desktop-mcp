import AppKit
import ApplicationServices
import MCP

/// One application window as seen at listing time.
public struct WindowInfo: Sendable, Equatable {
    public var pid: pid_t
    /// Position in the app's `AXWindows` array.
    public var index: Int
    public var app: String
    public var title: String
    /// Screen points, origin top-left of the main display.
    public var frame: CGRect
    public var focused: Bool
    public var minimized: Bool
    public var fullscreen: Bool

    public var id: String { "\(pid):\(index)" }

    public init(pid: pid_t, index: Int, app: String, title: String, frame: CGRect,
                focused: Bool, minimized: Bool, fullscreen: Bool) {
        self.pid = pid
        self.index = index
        self.app = app
        self.title = title
        self.frame = frame
        self.focused = focused
        self.minimized = minimized
        self.fullscreen = fullscreen
    }

    /// The frame as whole points `[x, y, w, h]`.
    var roundedFrame: [Int] {
        [frame.origin.x, frame.origin.y, frame.width, frame.height].map { Int($0.rounded()) }
    }

    /// Whether `other` is still this window: same title and same frame to the point.
    func isSameWindow(as other: WindowInfo) -> Bool {
        title == other.title && roundedFrame == other.roundedFrame
    }
}

/// The windows of running apps and the actions on one of them.
public protocol WindowSource: Sendable {
    /// Windows of regular apps, all of them or those of `app` (localized name, case-insensitive,
    /// or exact bundle id).
    func windows(app: String?) async -> [WindowInfo]
    func windows(pid: pid_t) async -> [WindowInfo]
    /// Whether a regular app matching `app` is running.
    func appExists(_ app: String) async -> Bool
    func focus(_ w: WindowInfo) async -> Bool
    func setMinimized(_ w: WindowInfo, _ on: Bool) async -> Bool
    func move(_ w: WindowInfo, to: CGPoint) async -> Bool
    func resize(_ w: WindowInfo, to: CGSize) async -> Bool
    func close(_ w: WindowInfo) async -> Bool
}

/// Windows through the Accessibility API. Every AX call runs inside `offload`, and actions find
/// the window again by pid and index, in the same call that acts on it.
struct AXWindowSource: WindowSource {
    private static func matches(_ running: NSRunningApplication, _ app: String) -> Bool {
        running.bundleIdentifier == app
            || running.localizedName?.lowercased() == app.trimmingCharacters(in: .whitespaces).lowercased()
    }

    private static func regularPIDs(matching app: String?) -> [pid_t] {
        App.runningApplications()
            .filter { running in
                running.activationPolicy == .regular && (app.map { matches(running, $0) } ?? true)
            }
            .map(\.processIdentifier)
    }

    /// The app's `AXWindows` with their indices, only elements whose role is `AXWindow`.
    private static func axWindows(_ app: App) -> [(index: Int, element: AXUIElement)] {
        app.windows.enumerated().compactMap { index, element in
            AX.string(AX.attribute(element, kAXRoleAttribute)) == kAXWindowRole ? (index, element) : nil
        }
    }

    private static func read(_ app: App, focusedWindow: AXUIElement?) -> [WindowInfo] {
        AX.setMessagingTimeout(app.element, 0.5)
        let name = app.running?.localizedName ?? app.bundleID ?? ""
        return axWindows(app).map { index, element in
            let raw = AX.attributes(element, [kAXTitleAttribute, kAXPositionAttribute, kAXSizeAttribute,
                                              kAXMinimizedAttribute, AXName.fullScreen])
            let origin = AX.point(raw[kAXPositionAttribute]) ?? .zero
            let size = AX.size(raw[kAXSizeAttribute]) ?? .zero
            return WindowInfo(
                pid: app.pid, index: index, app: name, title: AX.string(raw[kAXTitleAttribute]),
                frame: CGRect(origin: origin, size: size),
                focused: focusedWindow.map { CFEqual($0, element) } ?? false,
                minimized: AX.isTrue(raw[kAXMinimizedAttribute]),
                fullscreen: AX.isTrue(raw[AXName.fullScreen]))
        }
    }

    /// The frontmost app's focused window, when that app is `pid`.
    private static func focusedWindow(of pid: pid_t, frontmost: pid_t?) -> AXUIElement? {
        guard pid == frontmost else { return nil }
        return AX.element(AX.attribute(App(pid: pid).element, kAXFocusedWindowAttribute))
    }

    func windows(app: String?) async -> [WindowInfo] {
        let (pids, frontmost) = await offload { (Self.regularPIDs(matching: app), App.frontmost()?.pid) }
        // The first AX call to each process is expensive; they answer concurrently.
        let lists = await concurrentMap(pids) { pid in
            Self.read(App(pid: pid), focusedWindow: Self.focusedWindow(of: pid, frontmost: frontmost))
        }
        return lists.flatMap { $0 }
    }

    func windows(pid: pid_t) async -> [WindowInfo] {
        await offload {
            Self.read(App(pid: pid), focusedWindow: Self.focusedWindow(of: pid, frontmost: App.frontmost()?.pid))
        }
    }

    func appExists(_ app: String) async -> Bool {
        await offload { !Self.regularPIDs(matching: app).isEmpty }
    }

    /// Finds `w` again and runs `act` on it, unless it is gone or no longer the same window.
    private func acting(on w: WindowInfo, _ act: @escaping @Sendable (App, AXUIElement) -> Bool) async -> Bool {
        await offload {
            let app = App(pid: w.pid)
            guard let element = Self.axWindows(app).first(where: { $0.index == w.index })?.element,
                  let current = Self.read(app, focusedWindow: nil).first(where: { $0.index == w.index }),
                  current.isSameWindow(as: w) else { return false }
            return act(app, element)
        }
    }

    func focus(_ w: WindowInfo) async -> Bool {
        await acting(on: w) { app, window in
            app.activate()
            let raised = AX.perform(window, kAXRaiseAction)
            let main = AX.set(window, kAXMainAttribute, kCFBooleanTrue)
            return raised || main
        }
    }

    func setMinimized(_ w: WindowInfo, _ on: Bool) async -> Bool {
        await acting(on: w) { _, window in
            AX.set(window, kAXMinimizedAttribute, on ? kCFBooleanTrue : kCFBooleanFalse)
        }
    }

    func move(_ w: WindowInfo, to point: CGPoint) async -> Bool {
        await acting(on: w) { _, window in AX.setPosition(window, x: point.x, y: point.y) }
    }

    func resize(_ w: WindowInfo, to size: CGSize) async -> Bool {
        await acting(on: w) { _, window in AX.setSize(window, width: size.width, height: size.height) }
    }

    func close(_ w: WindowInfo) async -> Bool {
        await acting(on: w) { _, window in
            guard let button = AX.element(AX.attribute(window, kAXCloseButtonAttribute)) else { return false }
            return AX.perform(button, kAXPressAction)
        }
    }
}

/// The windows seen by the last `capacity` listings, newest first on lookup.
actor WindowCache {
    static let capacity = 64
    private var listings: [[String: WindowInfo]] = []

    func remember(_ windows: [WindowInfo]) {
        guard !windows.isEmpty else { return }
        listings.append(Dictionary(windows.map { ($0.id, $0) }, uniquingKeysWith: { $1 }))
        if listings.count > Self.capacity { listings.removeFirst(listings.count - Self.capacity) }
    }

    func listed(_ id: String) -> WindowInfo? {
        for listing in listings.reversed() {
            if let window = listing[id] { return window }
        }
        return nil
    }
}

enum WindowTool {
    static let modes = ["list", "focus", "move", "resize", "minimize", "restore", "close"]

    /// One `<id>\t<app>\t"<title>"\t[x,y,w,h]\t<flags>` line per window; no flags, no last tab.
    static func format(_ windows: [WindowInfo]) -> String {
        guard !windows.isEmpty else { return "No windows." }
        let flat = { (s: String) in s.components(separatedBy: .newlines).joined(separator: " ")
            .replacingOccurrences(of: "\t", with: " ") }
        return windows.map { w in
            var fields = [w.id, flat(w.app), "\"\(flat(w.title))\"",
                          "[" + w.roundedFrame.map(String.init).joined(separator: ",") + "]"]
            let flags = [(w.focused, "focused"), (w.minimized, "minimized"), (w.fullscreen, "fullscreen")]
                .filter(\.0).map(\.1)
            if !flags.isEmpty { fields.append(flags.joined(separator: ",")) }
            return fields.joined(separator: "\t")
        }.joined(separator: "\n")
    }

    private static func parse(_ id: String) -> (pid: pid_t, index: Int)? {
        let parts = id.split(separator: ":", omittingEmptySubsequences: false)
        guard parts.count == 2, let pid = pid_t(parts[0]), pid > 0,
              let index = Int(parts[1]), index >= 0 else { return nil }
        return (pid, index)
    }

    /// The window `id` names now, provided it still has the title and frame it was listed with.
    static func resolve(_ id: String, source: any WindowSource, cache: WindowCache) async throws -> WindowInfo {
        guard let (pid, index) = parse(id) else {
            throw ToolError("Invalid window id: \(id)", telemetry: "Invalid window id")
        }
        guard let listed = await cache.listed(id) else {
            throw ToolError("Window \(id) was not listed; call Window list first.", telemetry: "Window was not listed")
        }
        guard let current = await source.windows(pid: pid).first(where: { $0.index == index }),
              current.isSameWindow(as: listed) else {
            throw ToolError("Window \(id) changed since it was listed; call Window list again.",
                            telemetry: "Window changed since it was listed")
        }
        return current
    }

    private static func pair(_ args: ToolArguments, _ name: String, mode: String, message: String) throws -> [Int] {
        guard let list = try args.intList(name) else { throw ToolError("\(name) required for \(mode) mode.") }
        guard list.count == 2 else { throw ToolError(message) }
        return list
    }

    private static func failed(_ mode: String, _ id: String) -> ToolError {
        ToolError("Could not \(mode) window \(id).", telemetry: "Could not \(mode) window")
    }

    static func tool(source: any WindowSource = AXWindowSource(), cache: WindowCache = WindowCache()) -> Tool {
        Tool(
            name: "Window",
            description: "Lists and arranges application windows. 'list' (default) returns one line per window: '<id>\\t<app>\\t\"<title>\"\\t[x,y,w,h]\\t<flags>' (flags: focused, minimized, fullscreen), optionally only for 'app' (name or bundle id). The other modes act on the window 'id' from list: 'focus', 'move' (to 'loc' [x, y]), 'resize' (to 'size' [width, height]), 'minimize', 'restore', 'close' (presses its close button; the app may ask to save). A window that moved or changed since it was listed is refused; list again.",
            inputSchema: DesktopMCP.schema([
                "mode": DesktopMCP.string(default: "list", enum: modes),
                "app": DesktopMCP.nullableString(),
                "id": DesktopMCP.nullableString(),
                "loc": DesktopMCP.intPair(nullable: true),
                "size": DesktopMCP.intPair(nullable: true),
            ]),
            annotations: ToolAnnotations(title: "Window", readOnly: false, destructive: true, idempotent: false, openWorld: false),
            handler: { args in
                let mode = try args.choice("mode", in: modes, default: "list")
                if mode == "list" {
                    let app = try args.string("app")
                    let windows = await source.windows(app: app)
                    if windows.isEmpty, let app, !(await source.appExists(app)) {
                        throw ToolError("Application '\(app)' not found.", telemetry: "Application not found")
                    }
                    await cache.remember(windows)
                    return [.text(format(windows))]
                }

                guard let id = try args.string("id") else { throw ToolError("id required for \(mode) mode.") }
                var loc: [Int] = [], size: [Int] = []
                if mode == "move" {
                    loc = try pair(args, "loc", mode: mode, message: "loc must be a list of 2 integers [x, y].")
                }
                if mode == "resize" {
                    size = try pair(args, "size", mode: mode, message: "size must be 2 positive integers [width, height].")
                    guard size.allSatisfy({ $0 > 0 }) else {
                        throw ToolError("size must be 2 positive integers [width, height].")
                    }
                }
                let window = try await resolve(id, source: source, cache: cache)

                switch mode {
                case "focus":
                    if window.minimized {
                        guard await source.setMinimized(window, false) else { throw failed(mode, id) }
                    }
                    guard await source.focus(window) else { throw failed(mode, id) }
                    return [.text("Focused \(id).")]
                case "minimize", "restore":
                    let on = mode == "minimize"
                    if window.minimized != on {
                        guard await source.setMinimized(window, on) else { throw failed(mode, id) }
                    }
                    return [.text(on ? "Minimized \(id)." : "Restored \(id).")]
                case "move", "resize":
                    let done = mode == "move"
                        ? await source.move(window, to: CGPoint(x: loc[0], y: loc[1]))
                        : await source.resize(window, to: CGSize(width: size[0], height: size[1]))
                    guard done else { throw failed(mode, id) }
                    if let moved = await source.windows(pid: window.pid).first(where: { $0.index == window.index }) {
                        await cache.remember([moved])
                    }
                    return [.text(mode == "move" ? "Moved \(id) to (\(loc[0]),\(loc[1]))."
                                                 : "Resized \(id) to \(size[0])×\(size[1]).")]
                default:
                    guard await source.close(window) else { throw failed(mode, id) }
                    return [.text("Close requested for \(id) (\"\(window.title)\").")]
                }
            })
    }
}
