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

/// Finds a UI element by title and role, for `Wait until element`.
public protocol ElementFinder: Sendable {
    /// An element of `app` (name or bundle id; the menu-bar owner when nil) whose role is `role`
    /// exactly and whose title, description or value contains `title`, ignoring case; an exact
    /// match is preferred. Nil when there is none, or when `deadline` passes or the task is
    /// cancelled before one is found.
    func find(app: String?, title: String?, role: String?, deadline: ContinuousClock.Instant) async -> FoundElement?
}

/// The attributes `Wait until element` reads in one round trip. Never the value.
struct ElementAttributes: Sendable, Equatable {
    var role: String
    var subrole: String?
    var title: String
    var description: String
    var frame: CGRect?
}

/// The app stopped answering (`kAXErrorCannotComplete`, which a messaging timeout also gives).
struct ElementNotAnswering: Error {}

/// One node of the tree the element walk visits; the AX tree in production, a fake in tests.
protocol ElementNode {
    /// Role, subrole, title, description and frame. Throws `ElementNotAnswering` when the app
    /// does not answer.
    func attributes() throws -> ElementAttributes
    /// The value as text; only called for a non-secure element.
    func value() -> String
    func children() throws -> [Self]
}

/// An element of another app, with a 0.5 s messaging timeout set before its first read.
struct AXElementNode: ElementNode {
    let element: AXUIElement

    init(_ element: AXUIElement) {
        AX.setMessagingTimeout(element, 0.5)
        self.element = element
    }

    static let names = [kAXRoleAttribute, kAXSubroleAttribute, kAXTitleAttribute, kAXDescriptionAttribute,
                        kAXPositionAttribute, kAXSizeAttribute]

    private static func notAnswering(_ value: AnyObject) -> Bool {
        guard CFGetTypeID(value) == AXValueGetTypeID(), AXValueGetType(value as! AXValue) == .axError else { return false }
        var error = AXError.success
        return AXValueGetValue(value as! AXValue, .axError, &error) && error == .cannotComplete
    }

    func attributes() throws -> ElementAttributes {
        var values: CFArray?
        let err = AXUIElementCopyMultipleAttributeValues(
            element, Self.names as CFArray, AXCopyMultipleAttributeOptions(rawValue: 0), &values)
        if err == .cannotComplete { throw ElementNotAnswering() }
        guard err == .success, let array = values as? [AnyObject] else {
            return ElementAttributes(role: "", subrole: nil, title: "", description: "", frame: nil)
        }
        var raw: [String: AnyObject] = [:]
        for (name, value) in zip(Self.names, array) {
            if Self.notAnswering(value) { throw ElementNotAnswering() }
            if !AX.isError(value) && !(value is NSNull) { raw[name] = value }
        }
        let frame = AX.point(raw[kAXPositionAttribute]).flatMap { origin in
            AX.size(raw[kAXSizeAttribute]).map { CGRect(origin: origin, size: $0) }
        }
        return ElementAttributes(role: AX.string(raw[kAXRoleAttribute]), subrole: raw[kAXSubroleAttribute] as? String,
                                 title: AX.string(raw[kAXTitleAttribute]),
                                 description: AX.string(raw[kAXDescriptionAttribute]), frame: frame)
    }

    func value() -> String { AX.string(AX.attribute(element, kAXValueAttribute)) }

    private static func list(_ element: AXUIElement, _ name: String) throws -> [AXElementNode] {
        var value: CFTypeRef?
        let err = AXUIElementCopyAttributeValue(element, name as CFString, &value)
        if err == .cannotComplete { throw ElementNotAnswering() }
        guard err == .success else { return [] }
        return AX.elements(value).map(AXElementNode.init)
    }

    func children() throws -> [AXElementNode] { try Self.list(element, kAXChildrenAttribute) }

    /// The app's windows, the roots of the walk.
    static func windows(_ pid: pid_t) throws -> [AXElementNode] {
        let app = AX.application(pid)
        AX.setMessagingTimeout(app, 0.5)
        return try list(app, kAXWindowsAttribute)
    }
}

/// Walks the app's windows breadth-first through the Accessibility API, depth and count limited.
/// A secure field is matched by role, title and description only; its value is never read.
struct AXElementFinder: ElementFinder {
    static let maxDepth = 25
    static let maxElements = 5000

    func find(app: String?, title: String?, role: String?, deadline: ContinuousClock.Instant) async -> FoundElement? {
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
        let cancelled = Locked(false)
        return await withTaskCancellationHandler {
            await offload {
                var budget = Self.maxElements
                var fallback: FoundElement?
                for pid in pids {
                    // An app that does not answer is skipped.
                    guard let roots = try? AXElementNode.windows(pid) else { continue }
                    let result = Self.walk(roots, title: title, role: role, budget: &budget,
                                           deadline: deadline, cancelled: cancelled)
                    if let exact = result.exact { return exact }
                    fallback = fallback ?? result.partial
                    if result.stopped { break }
                }
                return fallback
            }
        } onCancel: {
            cancelled.current = true
        }
    }

    /// What one walk found: an exact match (the walk ended there), else the first partial match.
    /// `stopped` when the deadline passed, the task was cancelled or the budget ran out.
    struct WalkResult {
        var exact: FoundElement?
        var partial: FoundElement?
        var stopped = false
    }

    /// Breadth-first from `roots` to depth `maxDepth`, visiting at most `budget` nodes (the rest is
    /// left in `budget`). Ends at the first exact match, when the deadline passes, when `cancelled`
    /// is set, or when the app stops answering.
    static func walk<Node: ElementNode>(_ roots: [Node], title: String?, role: String?, budget: inout Int,
                                        deadline: ContinuousClock.Instant, cancelled: Locked<Bool>) -> WalkResult {
        var result = WalkResult()
        var queue = roots.prefix(max(0, budget)).map { (node: $0, depth: 0) }
        var next = 0
        while next < queue.count {
            if cancelled.current || ContinuousClock.now >= deadline {
                result.stopped = true
                return result
            }
            let (node, depth) = queue[next]
            next += 1
            budget -= 1
            do {
                let attributes = try node.attributes()
                if let match = label(attributes, value: node.value, matching: title, role: role),
                   let frame = attributes.frame {
                    let found = FoundElement(role: attributes.role, title: match.name,
                                             center: CGPoint(x: frame.midX, y: frame.midY))
                    if match.exact {
                        result.exact = found
                        return result
                    }
                    result.partial = result.partial ?? found
                }
                if depth < maxDepth {
                    // Enqueued nodes count against the budget, so the queue stays bounded.
                    let room = budget - (queue.count - next)
                    if room > 0 { queue += try node.children().prefix(room).map { ($0, depth + 1) } }
                }
            } catch {
                return result
            }
        }
        if budget <= 0 { result.stopped = true }
        return result
    }

    /// `label` for the attributes the walk read. An element whose role could not be read is
    /// treated as secure: its value is not read.
    static func label(_ a: ElementAttributes, value: () -> String, matching query: String?,
                      role wanted: String?) -> (name: String, exact: Bool)? {
        label(role: a.role, subrole: a.role.isEmpty ? SecureField.role : a.subrole, title: a.title,
              description: a.description, value: value, matching: query, role: wanted)
    }

    /// What to call a matching element, or nil when it does not match: `role` exactly when
    /// given, `query` as a case-insensitive substring of the title, the description, or the value
    /// (`value` is called only for a non-secure element, and only when the others do not match).
    /// The name is the matched title or description, else the element's title or description for
    /// a role-only match, else the query; never the value. `exact` when the matched text equals
    /// the query ignoring case (always for a role-only match).
    static func label(role: String, subrole: String?, title: String, description: String,
                      value: () -> String, matching query: String?, role wanted: String?) -> (name: String, exact: Bool)? {
        if let wanted, role != wanted { return nil }
        guard let query else { return (title.isEmpty ? description : title, true) }
        func contains(_ s: String) -> Bool { s.range(of: query, options: .caseInsensitive) != nil }
        func equals(_ s: String) -> Bool { s.caseInsensitiveCompare(query) == .orderedSame }
        if contains(title) { return (title, equals(title)) }
        if contains(description) { return (description, equals(description)) }
        if SecureField.isSecure(role: role, subrole: subrole) { return nil }
        let value = value()
        return contains(value) ? (query, equals(value)) : nil
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
        try await poll(until: deadline(timeout), check: check)
    }

    static func deadline(_ timeout: Double) -> ContinuousClock.Instant {
        ContinuousClock.now + .milliseconds(Int((max(0, timeout) * 1000).rounded()))
    }

    static func poll<T: Sendable>(until deadline: ContinuousClock.Instant, check: @Sendable () async -> T?) async throws -> T? {
        var delay = Duration.milliseconds(100)
        while true {
            try Task.checkCancellation()
            if let value = await check() { return value }
            try Task.checkCancellation()
            let now = ContinuousClock.now
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

                let check: @Sendable (ContinuousClock.Instant) async -> String?
                switch until {
                case "app_running", "app_quit":
                    guard let app else { throw ToolError("app required for \(until).") }
                    let wantRunning = until == "app_running"
                    check = { _ in
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
                    check = { _ in
                        let matching = await windows.windows(app: app).filter { w in
                            title.map { w.title.range(of: $0, options: .caseInsensitive) != nil } ?? true
                        }
                        if wantGone { return matching.isEmpty ? "No window matches any more." : nil }
                        return matching.isEmpty ? nil : WindowTool.format(matching)
                    }
                default:
                    guard title != nil || role != nil else { throw ToolError("title or role required for element.") }
                    try Permissions.requireAccessibility()
                    check = { deadline in
                        guard let e = await elements.find(app: app, title: title, role: role, deadline: deadline) else { return nil }
                        return "\(e.role) \"\(WindowTool.flat(e.title))\" at [\(Int(e.center.x.rounded())),\(Int(e.center.y.rounded()))]"
                    }
                }

                let deadline = deadline(Double(timeout))
                guard let text = try await poll(until: deadline, check: { await check(deadline) }) else {
                    let condition = describe(until, app: app, title: title, role: role)
                    throw ToolError("Timed out after \(timeout)s waiting for \(condition).", telemetry: "Wait timed out")
                }
                return [.text(text)]
            })
    }
}
