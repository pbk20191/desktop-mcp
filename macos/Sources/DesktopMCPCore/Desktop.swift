import AppKit
import ApplicationServices
import MCP

/// The operations behind each tool. Mirrors the Python `Desktop` service.
public final class Desktop: Sendable {
    public let tree = Tree()

    public init() {}

    // MARK: Snapshot

    public struct State: Sendable {
        public var activeWindow: WindowView?
        public var windows: [WindowView]
        public var tree: TreeState
        public var screenshot: Data?
    }

    public func state(useVision: Bool) async -> State {
        async let windows = Windows.list()
        let active = await Windows.foreground()
        let treeState = await tree.state(activeWindow: active)
        var screenshot: Data?
        if useVision {
            Screenshot.ensurePermission()
            // Scale the Retina capture to the active display's logical points, so image
            // coordinates and Click/Move coordinates agree.
            let display = display(for: active)
            screenshot = await Screenshot.annotated(nodes: treeState.interactiveNodes,
                                                    scale: min(1.0, 1.0 / (display?.scale ?? 1.0)))
        }
        return State(activeWindow: active, windows: await windows, tree: treeState, screenshot: screenshot)
    }

    func display(for window: WindowView?) -> DisplayInfo? {
        guard let box = window?.boundingBox else { return nil }
        return Displays.containing(x: Double(box.left + box.right) / 2, y: Double(box.top + box.bottom) / 2)
    }

    public func coordinateSpace(for window: WindowView?) -> String {
        guard let d = display(for: window) else {
            return "macOS logical points (display could not be identified)"
        }
        return "macOS logical points; active display origin (\(Int(d.logicalLeft)),\(Int(d.logicalTop))), "
            + "size \(Int(d.logicalWidth))x\(Int(d.logicalHeight)), backing scale \(formatScale(d.scale))x"
    }

    /// Python's `{:g}`: 2.0 -> "2", 1.5 -> "1.5".
    func formatScale(_ value: Double) -> String {
        value == value.rounded() ? String(Int(value)) : String(format: "%g", value)
    }

    /// The Accessibility copy as a leading line when the grant is missing: without it the
    /// snapshot still lists apps, but no elements.
    static func permissionNote(trusted: Bool) -> String {
        trusted ? "" : Permissions.accessibilityMessage + "\n"
    }

    public func snapshot(useVision: Bool) async -> [MCP.Tool.Content] {
        let state = await self.state(useVision: useVision)
        var text = Self.permissionNote(trusted: Permissions.accessibilityTrusted()) + """
        Focused Window:
        \(state.activeWindow?.description ?? "No focused window.")

        Input Coordinate Space:
        \(coordinateSpace(for: state.activeWindow))

        Open Applications:
        \(state.windows.isEmpty ? "No open applications." : state.windows.map(\.description).joined(separator: "\n"))

        List of Interactive Elements:
        \(state.tree.interactiveElementsString())

        List of Scrollable Elements:
        \(state.tree.scrollableElementsString())

        """
        if useVision && state.screenshot == nil {
            // Capture fails transiently (display asleep/locked, hot-plug) or without permission.
            text += """

            Note: Screenshot capture failed, so no annotated image is attached. The elements
            listed above are still valid; call Snapshot again if you need the screenshot.
            If this persists, grant Screen Recording in System Settings > Privacy & Security.

            """
        }
        var content: [MCP.Tool.Content] = [.text(text)]
        if useVision, let png = state.screenshot {
            content.append(.image(data: png, mimeType: "image/png"))
        }
        return content
    }

    // MARK: Apps

    public func app(mode: String, name: String?, windowLoc: [Int]?, windowSize: [Int]?) async throws -> String {
        switch mode {
        case "launch":
            guard let name, !name.isEmpty else { return "App name or bundle ID required for launch." }
            return await launch(name) ? "Launched \(name)." : "Failed to launch \(name)."
        case "switch":
            guard let name, !name.isEmpty else { return "App name or bundle ID required for switch." }
            guard let app = await offload({ App.byName(name) ?? App.byBundleID(name) }) else {
                return "Application '\(name)' not found."
            }
            _ = await offload { app.activate() }
            try await Task.sleep(for: .milliseconds(200))
            return "Switched to \(name)."
        case "resize":
            try Permissions.requireAccessibility()
            guard let window = await offload({ App.frontmost()?.mainWindow }) else { return "No frontmost window to resize." }
            guard let size = windowSize else { return "window_size required for resize mode." }
            guard size.count == 2 else { throw ToolError("window_size must be a list of exactly 2 integers [width, height]") }
            _ = await offload { AX.setSize(window, width: Double(size[0]), height: Double(size[1])) }
            return "Window resized."
        case "move":
            try Permissions.requireAccessibility()
            guard let window = await offload({ App.frontmost()?.mainWindow }) else { return "No frontmost window to move." }
            guard let loc = windowLoc else { return "window_loc required for move mode." }
            guard loc.count == 2 else { throw ToolError("window_loc must be a list of exactly 2 integers [x, y]") }
            _ = await offload { AX.setPosition(window, x: Double(loc[0]), y: Double(loc[1])) }
            return "Window moved."
        case "quit", "force_quit":
            return try await quit(name: name, force: mode == "force_quit")
        default:
            return "Unknown mode: \(mode)"
        }
    }

    /// `open -a` takes a name or a path; `open -b` a bundle id.
    func launch(_ name: String) async -> Bool {
        for flag in ["-a", "-b"] {
            if let run = try? await Shell.run(["/usr/bin/open", flag, name], timeout: 15), run.status == 0 {
                return true
            }
        }
        return false
    }

    // MARK: Input

    public func click(x: Int, y: Int, button: Input.Button, clicks: Int) async throws {
        if clicks == 0 {
            Input.move(to: x, y)
            return
        }
        try await Input.click(x: x, y: y, button: button, count: clicks)
    }

    /// Whether the element at the point, or its parent (a click often lands on the field's
    /// inner element), is a password field. Only role and subrole are read; false on any error.
    public func isSecureField(atX x: Int, y: Int) async -> Bool {
        await offload {
            var element: AXUIElement?
            guard AXUIElementCopyElementAtPosition(AX.systemWide, Float(x), Float(y), &element) == .success,
                  let element else { return false }
            if SecureField.isSecure(element) { return true }
            guard let parent = AX.element(AX.attribute(element, kAXParentAttribute)) else { return false }
            return SecureField.isSecure(parent)
        }
    }

    public func type(x: Int, y: Int, text: String, caret: String, clear: Bool, pressEnter: Bool) async throws {
        try await Input.click(x: x, y: y)
        try await Input.pause(0.1)
        if clear {
            try await Input.hotKey(["command", "a"])
            try await Input.pause(0.05)
            try await Input.hotKey(["delete"])
            try await Input.pause(0.05)
        }
        if caret == "start" {
            try await Input.hotKey(["command", "left"])
            try await Input.pause(0.02)
        } else if caret == "end" {
            try await Input.hotKey(["command", "right"])
            try await Input.pause(0.02)
        }
        try await Input.type(text)
        if pressEnter {
            try await Input.keyPress(KeyMap.returnKey)
        }
    }

    /// Returns a message when the direction does not fit the scroll type.
    public func scroll(loc: (Int, Int)?, type: String, direction: String, wheelTimes: Int) async throws -> String? {
        if type == "vertical" && !["up", "down"].contains(direction) {
            return "Use direction 'up' or 'down' for vertical scroll."
        }
        if type == "horizontal" && !["left", "right"].contains(direction) {
            return "Use direction 'left' or 'right' for horizontal scroll."
        }
        if let loc {
            Input.move(to: loc.0, loc.1)
            try await Input.pause(0.05)
        }
        for _ in 0..<max(0, wheelTimes) {
            switch direction {
            case "up": Input.wheel(vertical: 3)
            case "down": Input.wheel(vertical: -3)
            case "left": Input.wheel(horizontal: 3)
            default: Input.wheel(horizontal: -3)
            }
            try await Input.pause(0.05)
        }
        return nil
    }

    public func drag(toX x: Int, y: Int) async throws {
        let start = Input.cursorPosition()
        try await Input.drag(fromX: Int(start.x), fromY: Int(start.y), toX: x, toY: y)
    }

    public func shortcut(_ shortcut: String) async throws {
        let keys = shortcut.split(separator: "+", omittingEmptySubsequences: false)
            .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
        guard !keys.isEmpty, keys.allSatisfy({ !$0.isEmpty }) else {
            throw ToolError("shortcut must contain non-empty key names separated by '+'")
        }
        try await Input.hotKey(keys)
    }

    // MARK: Notification

    public func notify(message: String, title: String, subtitle: String?, sound: String?) async -> String {
        // A JSON string literal is also a valid AppleScript string literal.
        func quoted(_ s: String) -> String { JSONCoding.encodeString(s) }
        var script = "display notification \(quoted(message)) with title \(quoted(title))"
        if let subtitle, !subtitle.isEmpty { script += " subtitle \(quoted(subtitle))" }
        if let sound, !sound.isEmpty { script += " sound name \(quoted(sound))" }
        do {
            let run = try await Shell.run(["/usr/bin/osascript", "-e", script], timeout: 10)
            if run.status == 0 { return "Notification sent: [\(title)] \(message)" }
            return "Failed to send notification: \(run.stderr.trimmingCharacters(in: .whitespacesAndNewlines))"
        } catch {
            return "Failed to send notification: \(error)"
        }
    }
}
