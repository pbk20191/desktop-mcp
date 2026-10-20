import AppKit
import ApplicationServices

public enum BundleIDs {
    public static let finder = "com.apple.finder"

    public static let browsers: Set<String> = [
        "com.apple.Safari", "com.google.Chrome", "org.mozilla.firefox", "com.microsoft.edgemac",
        "com.brave.Browser", "com.operasoftware.Opera", "com.vivaldi.Vivaldi",
        "company.thebrowser.Browser",  // Arc
    ]

    /// Left out of the window list (Finder is always running and usually just the desktop).
    public static let excludedFromWindows: Set<String> = [finder]

    /// System UI scanned on every snapshot, in a fixed order.
    public static let systemUI: [String] = [
        "com.apple.dock",
        "com.apple.controlcenter",
        "com.apple.systemuiserver",
        "com.apple.Spotlight",
        "com.apple.notificationcenterui",
    ]
}

/// Window server stacking levels (kCGWindowLayer).
enum WindowLevel {
    static let normal = 0
    static let dock = 20
}

/// One entry of the window server's on-screen list, front to back.
struct OnScreenWindow: Sendable {
    var windowID: Int
    var pid: pid_t
    var ownerName: String
    var title: String
    var layer: Int
    var zIndex: Int
    var bounds: Rect

    /// An application's window, as opposed to the desktop or the Dock/menu bar above.
    var isApplication: Bool { layer >= WindowLevel.normal && layer < WindowLevel.dock }

    static func all() -> [OnScreenWindow] {
        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else {
            return []
        }
        var result: [OnScreenWindow] = []
        for info in list {
            if let alpha = info[kCGWindowAlpha as String] as? Double, alpha == 0 { continue }
            let bounds = info[kCGWindowBounds as String] as? [String: Double] ?? [:]
            let x = bounds["X"] ?? 0, y = bounds["Y"] ?? 0
            result.append(OnScreenWindow(
                windowID: info[kCGWindowNumber as String] as? Int ?? 0,
                pid: pid_t(info[kCGWindowOwnerPID as String] as? Int ?? 0),
                ownerName: info[kCGWindowOwnerName as String] as? String ?? "",
                title: info[kCGWindowName as String] as? String ?? "",
                layer: info[kCGWindowLayer as String] as? Int ?? 0,
                zIndex: result.count,
                bounds: Rect(left: x, top: y, right: x + (bounds["Width"] ?? 0), bottom: y + (bounds["Height"] ?? 0))
            ))
        }
        return result
    }

    /// The active application: owner of the front window at the normal level.
    static func frontmostPID(_ screen: [OnScreenWindow]) -> pid_t? {
        screen.first { $0.layer == WindowLevel.normal && $0.pid != 0 }?.pid
    }
}

public enum AppStatus: String, Sendable {
    case active = "Active"
    case fullscreen = "Fullscreen"
    case visible = "Visible"
    case hidden = "Hidden"
    case minimized = "Minimized"
    case windowless = "Windowless"
}

/// A running application seen through both NSRunningApplication and its AX element.
public struct App: Sendable {
    public let pid: pid_t
    public let element: AXUIElement
    public let running: NSRunningApplication?

    public init(pid: pid_t) {
        self.pid = pid
        self.element = AX.application(pid)
        self.running = NSRunningApplication(processIdentifier: pid)
    }

    init(_ running: NSRunningApplication) {
        self.pid = running.processIdentifier
        self.element = AX.application(running.processIdentifier)
        self.running = running
    }

    public var bundleID: String? { running?.bundleIdentifier }

    /// AX title first (what the menu bar shows), then the localized name.
    public var name: String {
        let title = AX.string(AX.attribute(element, kAXTitleAttribute))
        return title.isEmpty ? (running?.localizedName ?? "") : title
    }

    public var mainWindow: AXUIElement? { AX.element(AX.attribute(element, kAXMainWindowAttribute)) }
    public var windows: [AXUIElement] { AX.elements(AX.attribute(element, kAXWindowsAttribute)) }
    public var menuBar: AXUIElement? { AX.element(AX.attribute(element, kAXMenuBarAttribute)) }
    public var extrasMenuBar: AXUIElement? { AX.element(AX.attribute(element, AXName.extrasMenuBar)) }

    public var status: AppStatus {
        if running?.isHidden == true { return .hidden }
        let windows = self.windows
        if windows.isEmpty { return .windowless }
        if windows.allSatisfy({ AX.isTrue(AX.attribute($0, kAXMinimizedAttribute)) }) { return .minimized }
        if running?.isActive == true {
            if windows.contains(where: { AX.isTrue(AX.attribute($0, AXName.fullScreen)) }) { return .fullscreen }
            return .active
        }
        return .visible
    }

    // MARK: Lookup

    static func runningApplications() -> [NSRunningApplication] {
        NSWorkspace.shared.runningApplications.filter { !$0.isTerminated }
    }

    public static func regular() -> [App] {
        runningApplications().filter { $0.activationPolicy == .regular }.map(App.init)
    }

    /// Several processes can share a bundle id (a headless Chrome next to the browser), so the
    /// one with on-screen windows wins, then the more user-facing activation policy.
    public static func byBundleID(_ bundleID: String) -> App? {
        var matches = runningApplications().filter { $0.bundleIdentifier == bundleID }
        guard !matches.isEmpty else { return nil }
        if matches.count > 1 {
            let onScreen = Set(OnScreenWindow.all().filter(\.isApplication).map(\.pid))
            matches.sort {
                let a = (onScreen.contains($0.processIdentifier) ? 0 : 1, $0.activationPolicy.rawValue)
                let b = (onScreen.contains($1.processIdentifier) ? 0 : 1, $1.activationPolicy.rawValue)
                return a < b
            }
        }
        return App(matches[0])
    }

    /// Case-insensitive match on the localized name, then on the AX title.
    public static func byName(_ name: String) -> App? {
        let wanted = name.trimmingCharacters(in: .whitespaces).lowercased()
        let running = runningApplications()
        if let match = running.first(where: { $0.localizedName?.lowercased() == wanted }) {
            return App(match)
        }
        for app in running.map(App.init) where app.name.lowercased() == wanted {
            return app
        }
        return nil
    }

    /// The app whose window is frontmost, per the window server (reliable without AppKit events).
    public static func frontmost() -> App? {
        OnScreenWindow.frontmostPID(OnScreenWindow.all()).map(App.init(pid:))
    }

    @discardableResult
    public func activate() -> Bool {
        var ok = false
        if let running {
            ok = running.activate(options: [.activateAllWindows])
        }
        // Cooperative activation can refuse a background caller; AXFrontmost does not.
        if AX.set(element, AXName.frontmost, kCFBooleanTrue) { ok = true }
        return ok
    }

    // MARK: Dialogs

    /// The sheet or modal dialog blocking this app, found by walking its on-screen windows front
    /// to back and matching each to its AX window by frame.
    func dialog(_ screen: [OnScreenWindow]) -> DialogInfo? {
        let own = screen.filter { $0.pid == pid && $0.isApplication }
        guard !own.isEmpty else { return nil }
        let frontmost = OnScreenWindow.frontmostPID(screen) == pid
        let axWindows = windows.map { ($0, AX.rect($0)) }
        for entry in own {
            guard let window = axWindows.first(where: { $0.1.map { sameFrame($0, entry.bounds) } ?? false })?.0 else {
                continue
            }
            let floating = entry.layer > WindowLevel.normal
            if isDialogWindow(window) {
                return DialogInfo(app: self, window: window, floating: floating, frontmost: frontmost)
            }
            if let sheet = AX.children(window).first(where: {
                AX.string(AX.attribute($0, kAXRoleAttribute)) == kAXSheetRole
            }) {
                return DialogInfo(app: self, window: sheet, floating: floating, frontmost: frontmost)
            }
        }
        return nil
    }

    public var dialog: DialogInfo? { dialog(OnScreenWindow.all()) }

    /// AXModal covers an app's own alerts; system prompts identify themselves by subrole instead.
    /// Subrole AXDialog alone is not trusted: Chrome reports it on ordinary browser windows.
    func isDialogWindow(_ window: AXUIElement) -> Bool {
        let raw = AX.attributes(window, [AXName.modal, kAXSubroleAttribute])
        return AX.isTrue(raw[AXName.modal]) || AX.string(raw[kAXSubroleAttribute]) == kAXSystemDialogSubrole
    }
}

func sameFrame(_ rect: Rect, _ bounds: Rect) -> Bool {
    abs(rect.left - bounds.left) <= 1 && abs(rect.top - bounds.top) <= 1
        && abs(rect.width - bounds.width) <= 1 && abs(rect.height - bounds.height) <= 1
}

public struct DialogInfo: Sendable {
    public let app: App
    /// The AXSheet or the modal AXWindow.
    public let window: AXUIElement
    /// Stacked above every normal window, whichever app is active.
    public let floating: Bool
    /// The owning app is the active one.
    public let frontmost: Bool

    /// Whether the user can see and click it right now.
    public var reachable: Bool { floating || frontmost }

    /// The title, or the first line of text for alert panels, which have none.
    public var title: String {
        let title = AX.string(AX.attribute(window, kAXTitleAttribute))
        if !title.isEmpty { return title }
        for child in AX.children(window) {
            // The value only after the role: a password field's is never fetched.
            if AX.string(AX.attribute(child, kAXRoleAttribute)) == kAXStaticTextRole {
                let text = AX.describe(AX.attribute(child, kAXValueAttribute)).trimmingCharacters(in: .whitespacesAndNewlines)
                if !text.isEmpty { return text }
            }
        }
        return "Dialog"
    }

    /// Every dialog on screen across all apps, front to back.
    public static func all() -> [DialogInfo] {
        let screen = OnScreenWindow.all()
        var seen = Set<pid_t>()
        var result: [DialogInfo] = []
        for entry in screen where entry.isApplication && !seen.contains(entry.pid) {
            seen.insert(entry.pid)
            let app = App(pid: entry.pid)
            // An unresponsive process must not stall the whole scan.
            AX.setMessagingTimeout(app.element, 0.5)
            if let dialog = app.dialog(screen) { result.append(dialog) }
        }
        return result
    }
}

/// An application as listed in a snapshot.
public struct WindowView: Sendable {
    public struct Dialog: Sendable {
        public var title: String
        public var reachable: Bool

        public var description: String {
            var line = "dialog: \"\(title)\""
            if !reachable { line += " (behind other windows; switch to the app to reach it)" }
            return line
        }
    }

    public var name: String
    public var isBrowser: Bool
    public var status: AppStatus
    public var boundingBox: BoundingBox
    public var pid: pid_t
    public var bundleID: String
    public var dialog: Dialog?

    public var description: String {
        var line = "\(name) (\(bundleID)) - \(status.rawValue)"
        if let dialog { line += " - \(dialog.description)" }
        return line
    }

    var isWindowless: Bool { boundingBox.width == 0 && boundingBox.height == 0 }
}

public enum Windows {
    /// The window the user is working in. Falls back to Finder (the desktop) when nothing else is.
    public static func foreground() async -> WindowView? {
        let frontmost = OnScreenWindow.frontmostPID(OnScreenWindow.all())
        let pid = if let frontmost { frontmost } else {
            await MainActor.run { NSWorkspace.shared.menuBarOwningApplication?.processIdentifier }
        }
        return await offload { foregroundWindow(pid: pid) }
    }

    static func foregroundWindow(pid: pid_t?) -> WindowView? {
        guard let app = pid.map(App.init(pid:)) ?? App.byBundleID(BundleIDs.finder) else { return nil }
        let bundleID = app.bundleID ?? BundleIDs.finder

        guard let window = app.mainWindow else {
            // Windowless (minimised or no windows). Returned as-is so the tree still scans its
            // menu bar; for Finder this is the desktop-only state.
            let status = app.status
            return WindowView(
                name: bundleID == BundleIDs.finder ? "Finder" : (app.name.isEmpty ? bundleID : app.name),
                isBrowser: BundleIDs.browsers.contains(bundleID),
                status: status,
                boundingBox: .empty, pid: app.pid, bundleID: bundleID)
        }
        let box = AX.rect(window).map(BoundingBox.init) ?? .empty
        let dialog = app.dialog.map { WindowView.Dialog(title: $0.title, reachable: $0.reachable) }
        return WindowView(
            name: AX.string(AX.attribute(window, kAXTitleAttribute)),
            isBrowser: BundleIDs.browsers.contains(bundleID),
            status: app.status, boundingBox: box, pid: app.pid, bundleID: bundleID, dialog: dialog)
    }

    /// User-facing apps (Dock-visible ones, plus any background app showing a dialog).
    public static func list() async -> [WindowView] {
        let apps = App.regular()
        var dialogs: [pid_t: DialogInfo] = [:]
        for dialog in await offload({ DialogInfo.all() }) where dialogs[dialog.app.pid] == nil {
            dialogs[dialog.app.pid] = dialog
        }
        let regularPIDs = Set(apps.map(\.pid))
        let tasks = apps + dialogs.values.map(\.app).filter { !regularPIDs.contains($0.pid) }

        // The first AX call to each process is expensive; they answer concurrently.
        let results = await concurrentMap(tasks) { [dialogs] app -> WindowView? in
            let bundleID = app.bundleID ?? ""
            let dialog = dialogs[app.pid]
            if BundleIDs.excludedFromWindows.contains(bundleID) && dialog == nil { return nil }
            AX.setMessagingTimeout(app.element, 0.5)
            let status = app.status
            var box = BoundingBox.empty
            if ![.hidden, .minimized, .windowless].contains(status),
               let rect = app.mainWindow.flatMap(AX.rect) {
                box = BoundingBox(rect)
            }
            return WindowView(
                name: app.name, isBrowser: BundleIDs.browsers.contains(bundleID), status: status,
                boundingBox: box, pid: app.pid, bundleID: bundleID,
                dialog: dialog.map { WindowView.Dialog(title: $0.title, reachable: $0.reachable) })
        }
        return results.compactMap { $0 }
    }
}

/// Accessibility and window-server calls are synchronous IPC that can take hundreds of
/// milliseconds against a busy app. They run here, off Swift's cooperative pool, so a slow app
/// never starves the tasks serving other requests.
let blockingQueue = DispatchQueue(label: "desktop-mcp.blocking", qos: .userInitiated, attributes: .concurrent)

/// Runs synchronous, blocking work on `blockingQueue` and resumes with its result.
func offload<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
    try await withCheckedThrowingContinuation { continuation in
        blockingQueue.async {
            continuation.resume(with: Result { try autoreleasepool { try work() } })
        }
    }
}

func offload<T: Sendable>(_ work: @escaping @Sendable () -> T) async -> T {
    await withCheckedContinuation { continuation in
        blockingQueue.async {
            continuation.resume(returning: autoreleasepool { work() })
        }
    }
}

/// Order-preserving parallel map; each element's (blocking) transform is offloaded.
func concurrentMap<T: Sendable, U: Sendable>(_ items: [T], _ transform: @escaping @Sendable (T) -> U) async -> [U] {
    await withTaskGroup(of: (Int, U).self) { group in
        for (i, item) in items.enumerated() {
            group.addTask { (i, await offload { transform(item) }) }
        }
        var results = [U?](repeating: nil, count: items.count)
        for await (i, value) in group { results[i] = value }
        return results.map { $0! }
    }
}

extension App {
    /// The shared app filter: a bundle id exactly, or a localized name ignoring case and
    /// surrounding spaces. A blank query or a missing name never matches.
    static func matches(name: String?, bundleID: String?, query: String) -> Bool {
        let trimmed = query.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return false }
        if let bundleID, bundleID == query { return true }
        guard let name else { return false }
        return name.lowercased() == trimmed.lowercased()
    }
}
