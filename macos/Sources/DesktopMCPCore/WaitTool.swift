import AppKit
import ApplicationServices
import MCP

/// A UI element found by `Wait until element`.
public struct FoundElement: Sendable, Equatable {
    public var role: String
    /// The title or description that matched, or the query when only the value did.
    public var title: String
    /// Centre in `Click` points (top-left origin of the main display).
    public var center: CGPoint

    public init(role: String, title: String, center: CGPoint) {
        self.role = role
        self.title = title
        self.center = center
    }
}

/// Finds UI elements by title and role, for `Wait until element`.
public protocol ElementFinder: Sendable {
    /// Elements of `app` (name or bundle id; the menu-bar owner when nil) whose role is `role`
    /// exactly and whose title, description or value contains `title`, ignoring case.
    func find(app: String?, title: String?, role: String?) async -> [FoundElement]
}

/// Walks the app's windows breadth-first through the Accessibility API, depth and count limited.
/// A secure field is matched by role, title and description only; its value is never read.
struct AXElementFinder: ElementFinder {
    static let maxDepth = 25
    static let maxElements = 5000

    func find(app: String?, title: String?, role: String?) async -> [FoundElement] {
        let pids: [pid_t]
        if let app {
            pids = await offload {
                App.runningApplications()
                    .filter { App.matches(name: $0.localizedName, bundleID: $0.bundleIdentifier, query: app) }
                    .map(\.processIdentifier)
            }
        } else {
            // As Menu: the active app owns the menu bar even when the frontmost window is another's.
            let owner: pid_t? = await MainActor.run {
                (NSWorkspace.shared.menuBarOwningApplication ?? NSWorkspace.shared.frontmostApplication)?
                    .processIdentifier
            }
            pids = owner.map { [$0] } ?? []
        }
        return await offload { Self.search(pids, title: title, role: role) }
    }

    private static func search(_ pids: [pid_t], title: String?, role: String?) -> [FoundElement] {
        var found: [FoundElement] = []
        var budget = maxElements
        for pid in pids {
            let app = App(pid: pid)
            AX.setMessagingTimeout(app.element, 0.5)
            var queue = app.windows.map { (element: $0, depth: 0) }
            var next = 0
            while next < queue.count, budget > 0 {
                let (element, depth) = queue[next]
                next += 1
                budget -= 1
                if let match = match(element, title: title, role: role) { found.append(match) }
                if depth < maxDepth {
                    queue += AX.children(element).map { ($0, depth + 1) }
                }
            }
        }
        return found
    }

    private static func match(_ element: AXUIElement, title: String?, role wanted: String?) -> FoundElement? {
        // Role and subrole come first: whether the value may be read depends on them.
        let raw = AX.attributes(element, [kAXRoleAttribute, kAXSubroleAttribute, kAXTitleAttribute,
                                          kAXDescriptionAttribute, kAXPositionAttribute, kAXSizeAttribute])
        let role = AX.string(raw[kAXRoleAttribute])
        guard let label = label(role: role, subrole: raw[kAXSubroleAttribute] as? String,
                                title: AX.string(raw[kAXTitleAttribute]),
                                description: AX.string(raw[kAXDescriptionAttribute]),
                                value: { AX.string(AX.attribute(element, kAXValueAttribute)) },
                                matching: title, role: wanted),
              let origin = AX.point(raw[kAXPositionAttribute]), let size = AX.size(raw[kAXSizeAttribute])
        else { return nil }
        return FoundElement(role: role, title: label,
                            center: CGPoint(x: origin.x + size.width / 2, y: origin.y + size.height / 2))
    }

    /// What to call a matching element, or nil when it does not match: `role` exactly when
    /// given, `query` as a case-insensitive substring of the title, the description, or the value
    /// (`value` is called only for a non-secure element, and only when the others do not match).
    /// The name is the matched title or description, else the element's title or description for
    /// a role-only match, else the query; never the value.
    static func label(role: String, subrole: String?, title: String, description: String,
                      value: () -> String, matching query: String?, role wanted: String?) -> String? {
        if let wanted, role != wanted { return nil }
        guard let query else { return title.isEmpty ? description : title }
        func contains(_ s: String) -> Bool { s.range(of: query, options: .caseInsensitive) != nil }
        if contains(title) { return title }
        if contains(description) { return description }
        if SecureField.isSecure(role: role, subrole: subrole) { return nil }
        return contains(value()) ? query : nil
    }
}

/// `Wait`: a fixed duration, or until an app runs or quits, a window appears or closes, or an
/// element appears.
enum WaitTool {
    static let conditions = ["app_running", "app_quit", "window", "window_gone", "element"]

    /// Calls `check` at once, then after 0.1 s, doubling to at most 1 s between checks, until it
    /// returns a value or `timeout` seconds have passed (the last sleep ends at the deadline, and
    /// one last check follows). Nil on timeout; throws `CancellationError` when cancelled.
    static func poll<T: Sendable>(timeout: Double, check: @Sendable () async -> T?) async throws -> T? {
        let clock = ContinuousClock()
        let deadline = clock.now + .milliseconds(Int((max(0, timeout) * 1000).rounded()))
        var delay = Duration.milliseconds(100)
        while true {
            try Task.checkCancellation()
            if let value = await check() { return value }
            let now = clock.now
            guard now < deadline else { return nil }
            try await Task.sleep(for: min(delay, deadline - now))
            delay = min(delay * 2, .seconds(1))
        }
    }

    /// What the timeout copy says is awaited.
    static func describe(_ until: String, app: String?, title: String?, role: String?) -> String {
        switch until {
        case "app_running": return "app \(app ?? "") to run"
        case "app_quit": return "app \(app ?? "") to quit"
        case "window", "window_gone":
            var parts = ["window"]
            if let title { parts.append("\"\(title)\"") }
            if let app { parts.append("in \(app)") }
            if until == "window_gone" { parts.append("to close") }
            return parts.joined(separator: " ")
        default:
            var parts = ["element"]
            if let role { parts.append(role) }
            if let title { parts.append("\"\(title)\"") }
            return parts.joined(separator: " ")
        }
    }

    /// The argument, or nil when absent or blank.
    private static func given(_ args: ToolArguments, _ name: String) throws -> String? {
        guard let s = try args.string(name), !s.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        return s
    }

    static func tool(windows: any WindowSource = AXWindowSource(),
                     elements: any ElementFinder = AXElementFinder(),
                     running: @escaping @Sendable () async -> [RunningAppInfo] = { await AppQuit.runningApps() }) -> Tool {
        Tool(
            name: "Wait",
            description: "Waits a fixed 'duration' in seconds, or with 'until' waits for a condition, checking with backoff until it holds or 'timeout' seconds (default 10) pass, then fails. 'duration' is ignored with 'until'. Conditions: 'app_running' / 'app_quit' ('app': name or bundle id); 'window' / 'window_gone' ('app' and/or 'title', a case-insensitive substring; 'window' returns the matching Window list lines); 'element' ('title' and/or 'role', an exact AX role such as AXButton, in 'app' or else the app owning the menu bar; title matches title, description or value; returns '<role> \"<title>\" at [x,y]', the centre in Click coordinates). Window and element conditions need Accessibility.",
            inputSchema: DesktopMCP.schema([
                "duration": DesktopMCP.integer(),
                "until": DesktopMCP.nullableString(enum: conditions),
                "app": DesktopMCP.nullableString(),
                "title": DesktopMCP.nullableString(),
                "role": DesktopMCP.nullableString(),
                "timeout": DesktopMCP.integer(default: 10),
            ]),
            annotations: ToolAnnotations(title: "Wait", readOnly: true, destructive: false, idempotent: true, openWorld: false),
            handler: { args in
                guard let until = try args.string("until") else {
                    guard let duration = try args.int("duration") else { throw ToolError("Missing required argument 'duration'") }
                    try await Task.sleep(for: .seconds(max(0, duration)))
                    return [.text("Waited for \(duration) seconds.")]
                }
                guard conditions.contains(until) else {
                    throw ToolError("'until' must be one of: \(conditions.joined(separator: ", "))")
                }
                let app = try given(args, "app"), title = try given(args, "title"), role = try given(args, "role")
                let timeout = max(0, Shell.clampTimeout(try args.int("timeout") ?? 10))

                let check: @Sendable () async -> String?
                switch until {
                case "app_running", "app_quit":
                    guard let app else { throw ToolError("app required for \(until).") }
                    let wantRunning = until == "app_running"
                    check = {
                        let isRunning = await running().contains {
                            App.matches(name: $0.name, bundleID: $0.bundleID, query: app)
                        }
                        guard isRunning == wantRunning else { return nil }
                        return wantRunning ? "App \(app) is running." : "App \(app) quit."
                    }
                case "window", "window_gone":
                    guard app != nil || title != nil else { throw ToolError("app or title required for \(until).") }
                    try Permissions.requireAccessibility()
                    let wantGone = until == "window_gone"
                    check = {
                        let matching = await windows.windows(app: app).filter { w in
                            title.map { w.title.range(of: $0, options: .caseInsensitive) != nil } ?? true
                        }
                        if wantGone { return matching.isEmpty ? "No window matches any more." : nil }
                        return matching.isEmpty ? nil : WindowTool.format(matching)
                    }
                default:
                    guard title != nil || role != nil else { throw ToolError("title or role required for element.") }
                    try Permissions.requireAccessibility()
                    check = {
                        guard let e = await elements.find(app: app, title: title, role: role).first else { return nil }
                        return "\(e.role) \"\(WindowTool.flat(e.title))\" at [\(Int(e.center.x.rounded())),\(Int(e.center.y.rounded()))]"
                    }
                }

                guard let text = try await poll(timeout: Double(timeout), check: check) else {
                    let condition = describe(until, app: app, title: title, role: role)
                    throw ToolError("Timed out after \(timeout)s waiting for \(condition).", telemetry: "Wait timed out")
                }
                return [.text(text)]
            })
    }
}
