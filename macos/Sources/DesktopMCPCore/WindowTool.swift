import AppKit
import ApplicationServices
import Darwin
import MCP

/// One application window as seen at listing time.
public struct WindowInfo: Sendable, Equatable {
    public var pid: pid_t
    /// Position in the app's `AXWindows` array (z-order, so not stable).
    public var index: Int
    public var app: String
    public var title: String
    /// Screen points, origin top-left of the main display.
    public var frame: CGRect
    public var focused: Bool
    public var minimized: Bool
    public var fullscreen: Bool
    /// The window server's number, stable for the window's lifetime; nil when it can't be read.
    public var number: CGWindowID?

    /// `<pid>:w<number>`, or `<pid>:<index>` when the number is unknown.
    public var id: String { number.map { "\(pid):w\($0)" } ?? "\(pid):\(index)" }

    public init(pid: pid_t, index: Int, app: String, title: String, frame: CGRect,
                focused: Bool, minimized: Bool, fullscreen: Bool, number: CGWindowID? = nil) {
        self.pid = pid
        self.index = index
        self.app = app
        self.title = title
        self.frame = frame
        self.focused = focused
        self.minimized = minimized
        self.fullscreen = fullscreen
        self.number = number
    }

    /// The frame as whole points `[x, y, w, h]`.
    var roundedFrame: [Int] {
        [frame.origin.x, frame.origin.y, frame.width, frame.height].map { Int($0.rounded()) }
    }

    /// Whether `other` is still this window by appearance: same title and same frame to the
    /// point. Only used for index ids, which have nothing better.
    func looksLike(_ other: WindowInfo) -> Bool {
        title == other.title && roundedFrame == other.roundedFrame
    }

    /// Whether `other` is the window this one names: by number when known, else by index.
    func names(_ other: WindowInfo) -> Bool {
        if let number { return other.number == number }
        return other.number == nil && other.index == index
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
    /// Restores the window first when it is minimized.
    func focus(_ w: WindowInfo) async -> Bool
    func setMinimized(_ w: WindowInfo, _ on: Bool) async -> Bool
    func move(_ w: WindowInfo, to: CGPoint) async -> Bool
    func resize(_ w: WindowInfo, to: CGSize) async -> Bool
    func close(_ w: WindowInfo) async -> Bool
}

/// Windows through the Accessibility API. Every AX call runs inside `offload`, and actions find
/// the window again, in the same call that acts on it.
struct AXWindowSource: WindowSource {
    private typealias GetWindow = @convention(c) (AXUIElement, UnsafeMutablePointer<CGWindowID>) -> AXError

    /// Address of the private `_AXUIElementGetWindow`, looked up at run time so that its
    /// absence only loses window numbers. 0 when missing.
    static let getWindowAddress: Int = {
        let rtldDefault = UnsafeMutableRawPointer(bitPattern: -2)
        return dlsym(rtldDefault, "_AXUIElementGetWindow").map { Int(bitPattern: $0) } ?? 0
    }()

    /// The window server's number for a window element, when it can be read.
    static func number(of window: AXUIElement) -> CGWindowID? {
        guard let address = UnsafeRawPointer(bitPattern: getWindowAddress) else { return nil }
        let getWindow = unsafeBitCast(address, to: GetWindow.self)
        var number: CGWindowID = 0
        return getWindow(window, &number) == .success && number != 0 ? number : nil
    }

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

    /// The app (messaging timeout set) for AX calls to `pid`.
    private static func app(_ pid: pid_t) -> App {
        let app = App(pid: pid)
        AX.setMessagingTimeout(app.element, 0.5)
        return app
    }

    /// The app's `AXWindows` with their indices, only elements whose role is `AXWindow`.
    private static func axWindows(_ app: App) -> [(index: Int, element: AXUIElement)] {
        app.windows.enumerated().compactMap { index, element in
            AX.string(AX.attribute(element, kAXRoleAttribute)) == kAXWindowRole ? (index, element) : nil
        }
    }

    private static func info(_ app: App, index: Int, _ element: AXUIElement, focusedWindow: AXUIElement?) -> WindowInfo {
        let raw = AX.attributes(element, [kAXTitleAttribute, kAXPositionAttribute, kAXSizeAttribute,
                                          kAXMinimizedAttribute, AXName.fullScreen])
        let origin = AX.point(raw[kAXPositionAttribute]) ?? .zero
        let size = AX.size(raw[kAXSizeAttribute]) ?? .zero
        return WindowInfo(
            pid: app.pid, index: index, app: app.running?.localizedName ?? app.bundleID ?? "",
            title: AX.string(raw[kAXTitleAttribute]),
            frame: CGRect(origin: origin, size: size),
            focused: focusedWindow.map { CFEqual($0, element) } ?? false,
            minimized: AX.isTrue(raw[kAXMinimizedAttribute]),
            fullscreen: AX.isTrue(raw[AXName.fullScreen]),
            number: number(of: element))
    }

    private static func read(_ app: App, frontmost: pid_t?) -> [WindowInfo] {
        let focused = app.pid == frontmost
            ? AX.element(AX.attribute(app.element, kAXFocusedWindowAttribute)) : nil
        return axWindows(app).map { info(app, index: $0.index, $0.element, focusedWindow: focused) }
    }

    func windows(app: String?) async -> [WindowInfo] {
        let (pids, frontmost) = await offload { (Self.regularPIDs(matching: app), App.frontmost()?.pid) }
        // The first AX call to each process is expensive; they answer concurrently.
        let lists = await concurrentMap(pids) { pid in Self.read(Self.app(pid), frontmost: frontmost) }
        return lists.flatMap { $0 }
    }

    func windows(pid: pid_t) async -> [WindowInfo] {
        await offload { Self.read(Self.app(pid), frontmost: App.frontmost()?.pid) }
    }

    func appExists(_ app: String) async -> Bool {
        await offload { !Self.regularPIDs(matching: app).isEmpty }
    }

    /// Finds `w`'s element again (by number, or by index with the same title and frame) and
    /// runs `act` on that element; false when it is gone.
    private func acting(on w: WindowInfo, _ act: @escaping @Sendable (App, AXUIElement) -> Bool) async -> Bool {
        await offload {
            let app = Self.app(w.pid)
            let element: AXUIElement?
            if let number = w.number {
                element = Self.axWindows(app).first { Self.number(of: $0.element) == number }?.element
            } else if let found = Self.axWindows(app).first(where: { $0.index == w.index }),
                      Self.info(app, index: found.index, found.element, focusedWindow: nil).looksLike(w) {
                element = found.element
            } else {
                element = nil
            }
            guard let element else { return false }
            return act(app, element)
        }
    }

    func focus(_ w: WindowInfo) async -> Bool {
        await acting(on: w) { app, window in
            if AX.isTrue(AX.attribute(window, kAXMinimizedAttribute)) {
                AX.set(window, kAXMinimizedAttribute, kCFBooleanFalse)
            }
            let activated = app.activate()
            let raised = AX.perform(window, kAXRaiseAction)
            let main = AX.set(window, kAXMainAttribute, kCFBooleanTrue)
            return activated || raised || main
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

    enum WindowID: Equatable {
        case number(pid_t, CGWindowID)
        case index(pid_t, Int)
    }

    /// `<pid>:w<number>` or `<pid>:<index>`.
    static func parseID(_ id: String) -> WindowID? {
        let parts = id.split(separator: ":", omittingEmptySubsequences: false)
        guard parts.count == 2, let pid = pid_t(parts[0]), pid > 0 else { return nil }
        if parts[1].hasPrefix("w") {
            guard let number = CGWindowID(parts[1].dropFirst()), number > 0 else { return nil }
            return .number(pid, number)
        }
        guard let index = Int(parts[1]), index >= 0 else { return nil }
        return .index(pid, index)
    }

    /// Tabs and line breaks as spaces, so a name stays on its line and in its field.
    static func flat(_ s: String) -> String {
        s.components(separatedBy: .newlines).joined(separator: " ").replacingOccurrences(of: "\t", with: " ")
    }

    /// One `<id>\t<app>\t"<title>"\t[x,y,w,h]\t<flags>` line per window; no flags, no last tab.
    static func format(_ windows: [WindowInfo]) -> String {
        guard !windows.isEmpty else { return "No windows." }
        return windows.map { w in
            var fields = [w.id, flat(w.app), "\"\(flat(w.title))\"",
                          "[" + w.roundedFrame.map(String.init).joined(separator: ",") + "]"]
            let flags = [(w.focused, "focused"), (w.minimized, "minimized"), (w.fullscreen, "fullscreen")]
                .filter(\.0).map(\.1)
            if !flags.isEmpty { fields.append(flags.joined(separator: ",")) }
            return fields.joined(separator: "\t")
        }.joined(separator: "\n")
    }

    /// The window `id` names now. A number id only needs the window to still exist; an index id
    /// must still have the title and frame it was listed with.
    static func resolve(_ id: String, source: any WindowSource, cache: WindowCache) async throws -> WindowInfo {
        switch parseID(id) {
        case nil:
            throw ToolError("Invalid window id: \(id)", label: "Invalid window id")
        case .number(let pid, let number)?:
            guard let current = await source.windows(pid: pid).first(where: { $0.number == number }) else {
                throw ToolError("Window \(id) no longer exists; call Window list again.",
                                label: "Window no longer exists")
            }
            return current
        case .index(let pid, let index)?:
            guard let listed = await cache.listed(id) else {
                throw ToolError("Window \(id) was not listed; call Window list first.", label: "Window was not listed")
            }
            guard let current = await source.windows(pid: pid).first(where: { $0.number == nil && $0.index == index }),
                  current.looksLike(listed) else {
                throw ToolError("Window \(id) changed since it was listed; call Window list again.",
                                label: "Window changed since it was listed")
            }
            return current
        }
    }

    private static func pair(_ args: ToolArguments, _ name: String, mode: String, message: String) throws -> [Int] {
        guard let list = try args.intList(name) else { throw ToolError("\(name) required for \(mode) mode.") }
        guard list.count == 2 else { throw ToolError(message) }
        return list
    }

    private static func failed(_ mode: String, _ id: String) -> ToolError {
        ToolError("Could not \(mode) window \(id).", label: "Could not \(mode) window")
    }

    static func tool(source: any WindowSource = AXWindowSource(), cache: WindowCache = WindowCache()) -> Tool {
        Tool(
            name: "Window",
            description: "Lists and arranges application windows. 'list' (default) returns one line per window: '<id>\\t<app>\\t\"<title>\"\\t[x,y,w,h]\\t<flags>' (flags: focused, minimized, fullscreen), optionally only for 'app' (name or bundle id). The other modes act on the window 'id' from list: 'focus', 'move' (to 'loc' [x, y]), 'resize' (to 'size' [width, height]), 'minimize', 'restore', 'close' (presses its close button; the app may ask to save). A window that closed since it was listed is refused; list again.",
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
                        throw ToolError("Application '\(app)' not found.", label: "Application not found")
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
                    // Report what the app applied (it may clamp); index ids need the new frame cached.
                    var frame = mode == "move" ? [loc[0], loc[1], 0, 0] : [0, 0, size[0], size[1]]
                    if let applied = await source.windows(pid: window.pid).first(where: window.names) {
                        frame = applied.roundedFrame
                        if applied.number == nil { await cache.remember([applied]) }
                    }
                    return [.text(mode == "move" ? "Moved \(id) to (\(frame[0]),\(frame[1]))."
                                                 : "Resized \(id) to \(frame[2])×\(frame[3]).")]
                default:
                    guard await source.close(window) else { throw failed(mode, id) }
                    return [.text("Close requested for \(id) (\"\(flat(window.title))\").")]
                }
            })
    }
}
