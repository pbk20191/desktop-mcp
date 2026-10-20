import Foundation
import MCP

/// Raised by a tool handler to report a failure back to the client as an error result.
public struct ToolError: Error, CustomStringConvertible, Sendable {
    public let message: String
    /// What telemetry may see. Defaults to the message; pass a fixed label when the message
    /// quotes an argument (a URL, a hostname), which must stay on this machine.
    public let telemetry: String

    public init(_ message: String, telemetry: String? = nil) {
        self.message = message
        self.telemetry = telemetry ?? message
    }

    public var description: String { message }
}

public struct ToolAnnotations: Encodable, Sendable {
    public var title: String
    public var readOnly: Bool
    public var destructive: Bool
    public var idempotent: Bool
    public var openWorld: Bool

    enum CodingKeys: String, CodingKey {
        case title
        case readOnly = "readOnlyHint"
        case destructive = "destructiveHint"
        case idempotent = "idempotentHint"
        case openWorld = "openWorldHint"
    }
}

extension ToolAnnotations {
    var sdk: MCP.Tool.Annotations {
        MCP.Tool.Annotations(title: title, readOnlyHint: readOnly, destructiveHint: destructive,
                             idempotentHint: idempotent, openWorldHint: openWorld)
    }
}

public struct Tool: Sendable {
    public var name: String
    public var description: String
    public var inputSchema: Value
    public var annotations: ToolAnnotations
    public var handler: @Sendable (ToolArguments) async throws -> [MCP.Tool.Content]
}

extension MCP.Tool.Content {
    /// Plain text. The SDK deprecated its own shorthand for `.text(text:annotations:_meta:)`.
    public static func text(_ text: String) -> MCP.Tool.Content {
        .text(text: text, annotations: nil, _meta: nil)
    }

    /// Base64-encodes `data`, the form the wire needs.
    public static func image(data: Data, mimeType: String) -> MCP.Tool.Content {
        .image(data: data.base64EncodedString(), mimeType: mimeType, annotations: nil, _meta: nil)
    }
}

/// Typed access to a tool call's `arguments` object, with the validation the Python server
/// got from pydantic.
public struct ToolArguments: Sendable {
    public let raw: [String: Value]

    public init(_ raw: [String: Value]) { self.raw = raw }

    private func present(_ name: String) -> Value? {
        guard let value = raw[name], !value.isNull else { return nil }
        return value
    }

    /// The argument as text. `Value` decodes any string that parses as a `data:` URL into
    /// `.data`, so such an argument is turned back into a URL here: a tool asked for a string.
    private func text(_ value: Value) -> String? {
        switch value {
        case .string(let s):
            return s
        case .data(let mimeType, let data):
            return Self.dataURL(mimeType: mimeType, data)
        default:
            return nil
        }
    }

    /// The SDK keeps only the media type and the decoded bytes, so the client's exact text is
    /// gone. Bytes that are UTF-8 come back in the plain `data:<type>,<text>` form, which is the
    /// text the client sent unless it used base64 or percent-escapes; anything else is base64.
    /// Lossy for base64 or percent-escaped data URLs, which no current tool accepts anyway
    /// (Scrape rejects non-http schemes).
    static func dataURL(mimeType: String?, _ data: Data) -> String {
        guard let text = String(data: data, encoding: .utf8) else {
            return data.dataURLEncoded(mimeType: mimeType)
        }
        return "data:\(mimeType ?? "text/plain"),\(text)"
    }

    private func integer(_ value: Value) -> Int? {
        switch value {
        case .int(let n): return n
        case .double(let n): return n == n.rounded() ? Int(exactly: n) : nil
        default: return nil
        }
    }

    private func number(_ value: Value) -> Double? {
        switch value {
        case .int(let n): return Double(n)
        case .double(let n): return n
        default: return nil
        }
    }

    public func string(_ name: String) throws -> String? {
        guard let value = present(name) else { return nil }
        guard let s = text(value) else { throw ToolError("'\(name)' must be a string") }
        return s
    }

    public func requiredString(_ name: String) throws -> String {
        guard let s = try string(name) else { throw ToolError("Missing required argument '\(name)'") }
        return s
    }

    public func int(_ name: String) throws -> Int? {
        guard let value = present(name) else { return nil }
        if let n = integer(value) { return n }
        if let s = value.stringValue, let n = Int(s) { return n }
        throw ToolError("'\(name)' must be an integer")
    }

    public func double(_ name: String) throws -> Double? {
        guard let value = present(name) else { return nil }
        if let n = number(value) { return n }
        if let s = value.stringValue, let n = Double(s) { return n }
        throw ToolError("'\(name)' must be a number")
    }

    public func bool(_ name: String) throws -> Bool? {
        guard let value = present(name) else { return nil }
        if let b = value.boolValue { return b }
        if let s = value.stringValue?.lowercased(), ["true", "false"].contains(s) { return s == "true" }
        throw ToolError("'\(name)' must be a boolean")
    }

    public func choice(_ name: String, in allowed: [String], default fallback: String) throws -> String {
        guard let s = try string(name) else { return fallback }
        guard allowed.contains(s) else {
            throw ToolError("'\(name)' must be one of: \(allowed.joined(separator: ", "))")
        }
        return s
    }

    public func intList(_ name: String) throws -> [Int]? {
        guard let value = present(name) else { return nil }
        guard let items = value.arrayValue else { throw ToolError("'\(name)' must be a list of integers") }
        return try items.map { item in
            guard let n = integer(item) else { throw ToolError("'\(name)' must be a list of integers") }
            return n
        }
    }

    public func stringList(_ name: String) throws -> [String]? {
        guard let value = present(name) else { return nil }
        guard let items = value.arrayValue else { throw ToolError("\(name) must be a list of strings") }
        return try items.map { item in
            guard let s = text(item) else { throw ToolError("\(name) must be a list of strings") }
            return s
        }
    }
}

/// Receives the outcome of every tool call (the PostHog analytics in the original).
public protocol ToolCallObserver: Sendable {
    func toolSucceeded(_ tool: String, durationMs: Int, client: Client.Info?) async
    func toolFailed(_ tool: String, error: String, durationMs: Int, client: Client.Info?) async
}

public enum DesktopMCP {
    public static let version = "0.1.0"
    public static let instructions = """
    macOS MCP server provides tools to interact directly with the macOS desktop, \
    enabling operation of the desktop on the user's behalf.
    """

    /// Tools registered only when named in `include` (`[tools] include`, `--include-tools`).
    public static let optInTools: Set<String> = ["OCR"]

    /// A registry with every on-by-default tool, plus the opt-in tools named in `including`,
    /// minus those named in `excluding` (exclude wins).
    public static func makeRegistry(desktop: Desktop = Desktop(), excluding excluded: [String] = [],
                                    including included: [String] = []) async -> ToolRegistry {
        let registry = ToolRegistry(name: "desktop-mcp", version: version, instructions: instructions)
        let all = tools(desktop)
        for tool in all where !optInTools.contains(tool.name) || included.contains(tool.name) {
            await registry.register(tool)
        }
        for name in included where !optInTools.contains(name) {
            if all.contains(where: { $0.name == name }) {
                log("Tool '\(name)' is on by default; include ignored")
            } else {
                log("Could not include tool '\(name)' — not found")
            }
        }
        // An opt-in tool that was not included is already absent; excluding it is not an error.
        for name in excluded where !(await registry.remove(tool: name)) && !optInTools.contains(name) {
            log("Could not exclude tool '\(name)' — not found")
        }
        return registry
    }

    // MARK: Schema helpers

    static func schema(_ properties: [String: Value], required: [String] = []) -> Value {
        var schema: [String: Value] = ["type": "object", "properties": .object(properties)]
        if !required.isEmpty { schema["required"] = .array(required.map { .string($0) }) }
        return .object(schema)
    }

    static func string(_ description: String? = nil, default value: String? = nil, enum options: [String]? = nil) -> Value {
        var p: [String: Value] = ["type": "string"]
        if let options { p["enum"] = .array(options.map { .string($0) }) }
        if let value { p["default"] = .string(value) }
        if let description { p["description"] = .string(description) }
        return .object(p)
    }

    static func nullableString(enum options: [String]? = nil) -> Value {
        var p: [String: Value] = ["type": ["string", "null"], "default": nil]
        if let options { p["enum"] = .array(options.map { .string($0) } + [.null]) }
        return .object(p)
    }

    static func integer(default value: Int? = nil) -> Value {
        var p: [String: Value] = ["type": "integer"]
        if let value { p["default"] = .int(value) }
        return .object(p)
    }

    static func boolean(default value: Bool) -> Value {
        ["type": "boolean", "default": .bool(value)]
    }

    static func intPair(nullable: Bool) -> Value {
        if nullable {
            return ["anyOf": [["type": "array", "items": ["type": "integer"]], ["type": "null"]], "default": nil]
        }
        return ["type": "array", "items": ["type": "integer"]]
    }

    static func stringArray() -> Value {
        ["type": "array", "items": ["type": "string"]]
    }

    static func point(_ args: ToolArguments, _ name: String, message: String) throws -> (Int, Int)? {
        guard let list = try args.intList(name) else { return nil }
        guard list.count == 2 else { throw ToolError(message) }
        return (list[0], list[1])
    }

    // MARK: Tools

    static func tools(_ desktop: Desktop) -> [Tool] {
        let locMessage = "Location must be a list of exactly 2 integers [x, y]"
        return [
            Tool(
                name: "App",
                description: "Manages macOS applications with four modes: 'launch' (opens the application), 'resize' (adjusts active window size), 'move' (repositions active window), 'switch' (brings specific app into focus).",
                inputSchema: schema([
                    "mode": string(default: "launch", enum: ["launch", "resize", "move", "switch"]),
                    "name": nullableString(),
                    "window_loc": intPair(nullable: true),
                    "window_size": intPair(nullable: true),
                ]),
                annotations: ToolAnnotations(title: "App", readOnly: false, destructive: true, idempotent: false, openWorld: false),
                handler: { args in
                    let mode = try args.choice("mode", in: ["launch", "resize", "move", "switch"], default: "launch")
                    let text = try await desktop.app(mode: mode, name: try args.string("name"),
                                                     windowLoc: try args.intList("window_loc"),
                                                     windowSize: try args.intList("window_size"))
                    return [.text(text)]
                }),

            Tool(
                name: "Shell",
                description: "Execute commands on macOS. Modes: 'shell' (default) for bash/zsh commands, 'osascript' for AppleScript. Use for file system, process management, system operations, and automation scripts. SECURITY WARNING: Commands are executed with the same permissions as the terminal/application running this server. Review and understand all actions before execution, especially those involving file operations, system modifications, or external processes.",
                inputSchema: schema([
                    "command": string(),
                    "mode": string(default: "shell", enum: ["shell", "osascript"]),
                    "timeout": integer(default: 10),
                ], required: ["command"]),
                annotations: ToolAnnotations(title: "Shell", readOnly: false, destructive: true, idempotent: false, openWorld: true),
                handler: { args in
                    let command = try args.requiredString("command")
                    let mode = try args.choice("mode", in: ["shell", "osascript"], default: "shell")
                    let timeout = try args.int("timeout") ?? 10
                    let result = await Shell.execute(command, mode: mode, timeout: timeout)
                    let label = mode == "osascript" ? "AppleScript" : "Shell"
                    return [.text("\(label) Response: \(result.output)\nStatus Code: \(result.status)")]
                }),

            Tool(
                name: "Snapshot",
                description: "Captures complete desktop state including: focused window, open applications, interactive elements (buttons, text fields, links, menus with coordinates), and scrollable areas. Set use_vision=True to include screenshot with numbered annotations on interactive elements. Always call this first to understand the current desktop state before taking actions.",
                inputSchema: schema(["use_vision": boolean(default: false)]),
                annotations: ToolAnnotations(title: "Snapshot", readOnly: true, destructive: false, idempotent: true, openWorld: false),
                handler: { args in
                    await desktop.snapshot(useVision: try args.bool("use_vision") ?? false)
                }),

            Tool(
                name: "Click",
                description: "Performs mouse clicks at specified coordinates [x, y]. Supports button types: 'left' for selection/activation, 'right' for context menus, 'middle'. Supports clicks: 0=hover only, 1=single click, 2=double click.",
                inputSchema: schema([
                    "loc": intPair(nullable: false),
                    "button": string(default: "left", enum: ["left", "right", "middle"]),
                    "clicks": integer(default: 1),
                ], required: ["loc"]),
                annotations: ToolAnnotations(title: "Click", readOnly: false, destructive: true, idempotent: false, openWorld: false),
                handler: { args in
                    guard let (x, y) = try point(args, "loc", message: locMessage) else { throw ToolError("Missing required argument 'loc'") }
                    let button = try args.choice("button", in: ["left", "right", "middle"], default: "left")
                    let clicks = try args.int("clicks") ?? 1
                    try await desktop.click(x: x, y: y, button: Input.Button(rawValue: button)!, clicks: clicks)
                    let label = [0: "Hover", 1: "Single", 2: "Double"][clicks] ?? "None"
                    return [.text("\(label) \(button) clicked at (\(x),\(y)).")]
                }),

            Tool(
                name: "Type",
                description: "Types text at specified coordinates [x, y]. Set clear=True to clear existing text first. Set press_enter=True to submit after typing. Set caret_position to 'start', 'end', or 'idle' (default).",
                inputSchema: schema([
                    "loc": intPair(nullable: false),
                    "text": string(),
                    "clear": boolean(default: false),
                    "caret_position": string(default: "idle", enum: ["start", "idle", "end"]),
                    "press_enter": boolean(default: false),
                ], required: ["loc", "text"]),
                annotations: ToolAnnotations(title: "Type", readOnly: false, destructive: true, idempotent: false, openWorld: false),
                handler: { args in
                    guard let (x, y) = try point(args, "loc", message: locMessage) else { throw ToolError("Missing required argument 'loc'") }
                    let text = try args.requiredString("text")
                    try await desktop.type(
                        x: x, y: y, text: text,
                        caret: try args.choice("caret_position", in: ["start", "idle", "end"], default: "idle"),
                        clear: try args.bool("clear") ?? false,
                        pressEnter: try args.bool("press_enter") ?? false)
                    return [.text("Typed \(text) at (\(x),\(y)).")]
                }),

            Tool(
                name: "Scroll",
                description: "Scrolls at coordinates [x, y] or current mouse position if loc=None. Type: vertical (default) or horizontal. Direction: up/down for vertical, left/right for horizontal. wheel_times controls scroll amount.",
                inputSchema: schema([
                    "loc": intPair(nullable: true),
                    "type": string(default: "vertical", enum: ["horizontal", "vertical"]),
                    "direction": string(default: "down", enum: ["up", "down", "left", "right"]),
                    "wheel_times": integer(default: 1),
                ]),
                annotations: ToolAnnotations(title: "Scroll", readOnly: false, destructive: false, idempotent: true, openWorld: false),
                handler: { args in
                    let loc = try point(args, "loc", message: locMessage)
                    let type = try args.choice("type", in: ["horizontal", "vertical"], default: "vertical")
                    let direction = try args.choice("direction", in: ["up", "down", "left", "right"], default: "down")
                    let times = try args.int("wheel_times") ?? 1
                    if let message = try await desktop.scroll(loc: loc, type: type, direction: direction, wheelTimes: times) {
                        return [.text(message)]
                    }
                    let at = loc.map { " at (\($0.0),\($0.1))." } ?? "."
                    return [.text("Scrolled \(type) \(direction) by \(times) wheel times\(at)")]
                }),

            Tool(
                name: "Move",
                description: "Moves mouse cursor to coordinates [x, y]. Set drag=True to perform a drag-and-drop operation from current position to target coordinates.",
                inputSchema: schema([
                    "loc": intPair(nullable: false),
                    "drag": boolean(default: false),
                ], required: ["loc"]),
                annotations: ToolAnnotations(title: "Move", readOnly: false, destructive: false, idempotent: true, openWorld: false),
                handler: { args in
                    guard let (x, y) = try point(args, "loc", message: "loc must be a list of exactly 2 integers [x, y]") else {
                        throw ToolError("Missing required argument 'loc'")
                    }
                    if try args.bool("drag") ?? false {
                        try await desktop.drag(toX: x, y: y)
                        return [.text("Dragged to (\(x),\(y)).")]
                    }
                    Input.move(to: x, y)
                    return [.text("Moved the mouse pointer to (\(x),\(y)).")]
                }),

            Tool(
                name: "Shortcut",
                description: "Executes keyboard shortcuts using key combinations separated by +. Examples: \"command+c\" (copy), \"command+v\" (paste), \"command+tab\" (switch apps), \"command+space\" (Spotlight). Note: Use \"command\" instead of \"ctrl\" on macOS.",
                inputSchema: schema(["shortcut": string()], required: ["shortcut"]),
                annotations: ToolAnnotations(title: "Shortcut", readOnly: false, destructive: true, idempotent: false, openWorld: false),
                handler: { args in
                    let shortcut = try args.requiredString("shortcut")
                    try await desktop.shortcut(shortcut)
                    return [.text("Pressed \(shortcut).")]
                }),

            Tool(
                name: "Wait",
                description: "Pauses execution for specified duration in seconds. Use when waiting for: applications to launch, UI animations to complete, content to load. Helps ensure UI is ready before next interaction.",
                inputSchema: schema(["duration": integer()], required: ["duration"]),
                annotations: ToolAnnotations(title: "Wait", readOnly: true, destructive: false, idempotent: true, openWorld: false),
                handler: { args in
                    guard let duration = try args.int("duration") else { throw ToolError("Missing required argument 'duration'") }
                    try await Task.sleep(for: .seconds(max(0, duration)))
                    return [.text("Waited for \(duration) seconds.")]
                }),

            Tool(
                name: "Scrape",
                description: "Fetch content from a URL. Performs a lightweight HTTP request and returns the page content as text.",
                inputSchema: schema(["url": string()], required: ["url"]),
                annotations: ToolAnnotations(title: "Scrape", readOnly: true, destructive: false, idempotent: true, openWorld: true),
                handler: { args in
                    [.text(try await Scrape.run(try args.requiredString("url")))]
                }),

            Tool(
                name: "Desktop",
                description: "Manages Mission Control desktop Spaces. mode='create' opens Mission Control and clicks 'Add Desktop' to create a new virtual desktop, verifying the space count increased before reporting success. Uses Accessibility only. Requires Accessibility permission. UI layout can vary by macOS version/locale.",
                inputSchema: schema([
                    "mode": string(default: "create", enum: ["create"]),
                    "close_after": boolean(default: true),
                    "open_delay": ["type": "number", "default": 0.9],
                ]),
                annotations: ToolAnnotations(title: "Desktop", readOnly: false, destructive: false, idempotent: false, openWorld: false),
                handler: { args in
                    let mode = try args.string("mode") ?? "create"
                    guard mode == "create" else { return [.text("Unknown mode: \(mode). Supported modes: create.")] }
                    let text = await Spaces.createDesktop(openDelay: try args.double("open_delay") ?? 0.9,
                                                          closeAfter: try args.bool("close_after") ?? true)
                    return [.text(text)]
                }),

            Tool(
                name: "Notification",
                description: "Sends a macOS notification banner with a message, title, optional subtitle, and optional sound.",
                inputSchema: schema([
                    "message": string(),
                    "title": string(default: "Notification"),
                    "subtitle": nullableString(),
                    "sound": nullableString(enum: ["Glass", "Ping", "Basso", "Tink", "Funk", "Bottle", "Frog",
                                                   "Hero", "Morse", "Pop", "Purr", "Sosumi", "Submarine"]),
                ], required: ["message"]),
                annotations: ToolAnnotations(title: "Notification", readOnly: false, destructive: false, idempotent: false, openWorld: false),
                handler: { args in
                    let text = await desktop.notify(message: try args.requiredString("message"),
                                                    title: try args.string("title") ?? "Notification",
                                                    subtitle: try args.string("subtitle"),
                                                    sound: try args.string("sound"))
                    return [.text(text)]
                }),

            OpenTool.tool(workspace: SystemWorkspace()),
            ClipboardTool.tool(),
            RunShortcutTool.tool(),
            SpotlightTool.tool(),
            WindowTool.tool(),
            MenuTool.tool(),
            OCRTool.tool(),
        ]
    }
}

/// Diagnostics go to stderr: stdout is the stdio transport.
public func log(_ message: String) {
    FileHandle.standardError.write(Data(("[desktop-mcp] " + message + "\n").utf8))
}
