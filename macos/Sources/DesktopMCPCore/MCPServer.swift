import Foundation

/// One item of a tool result.
public enum ToolContent: Equatable, Sendable {
    case text(String)
    case image(data: Data, mimeType: String)
}

extension ToolContent: Codable {
    enum CodingKeys: String, CodingKey {
        case type, text, data, mimeType
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        switch try container.decode(String.self, forKey: .type) {
        case "text":
            self = .text(try container.decode(String.self, forKey: .text))
        case "image":
            self = .image(data: try container.decode(Data.self, forKey: .data),
                          mimeType: try container.decode(String.self, forKey: .mimeType))
        case let other:
            throw DecodingError.dataCorruptedError(forKey: .type, in: container, debugDescription: "Unknown content type '\(other)'")
        }
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        switch self {
        case .text(let text):
            try container.encode("text", forKey: .type)
            try container.encode(text, forKey: .text)
        case .image(let data, let mimeType):
            try container.encode("image", forKey: .type)
            try container.encode(data, forKey: .data)  // base64, the encoder's default for Data
            try container.encode(mimeType, forKey: .mimeType)
        }
    }
}

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

public struct Tool: Sendable {
    public var name: String
    public var description: String
    public var inputSchema: JSONValue
    public var annotations: ToolAnnotations
    public var handler: @Sendable (ToolArguments) async throws -> [ToolContent]

    var descriptor: ToolDescriptor {
        ToolDescriptor(name: name, description: description, inputSchema: inputSchema, annotations: annotations)
    }
}

/// Typed access to a tool call's `arguments` object, with the validation the Python server
/// got from pydantic.
public struct ToolArguments: Sendable {
    public let raw: [String: JSONValue]

    public init(_ raw: [String: JSONValue]) { self.raw = raw }

    private func present(_ name: String) -> JSONValue? {
        guard let value = raw[name], !value.isNull else { return nil }
        return value
    }

    public func string(_ name: String) throws -> String? {
        guard let value = present(name) else { return nil }
        guard let s = value.stringValue else { throw ToolError("'\(name)' must be a string") }
        return s
    }

    public func requiredString(_ name: String) throws -> String {
        guard let s = try string(name) else { throw ToolError("Missing required argument '\(name)'") }
        return s
    }

    public func int(_ name: String) throws -> Int? {
        guard let value = present(name) else { return nil }
        if let n = value.intValue { return n }
        if let s = value.stringValue, let n = Int(s) { return n }
        throw ToolError("'\(name)' must be an integer")
    }

    public func double(_ name: String) throws -> Double? {
        guard let value = present(name) else { return nil }
        if let n = value.doubleValue { return n }
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
            guard let n = item.intValue else { throw ToolError("'\(name)' must be a list of integers") }
            return n
        }
    }
}

/// Receives the outcome of every tool call (the PostHog analytics in the original).
public protocol ToolCallObserver: Sendable {
    func toolSucceeded(_ tool: String, durationMs: Int, client: ClientInfo?) async
    func toolFailed(_ tool: String, error: String, durationMs: Int, client: ClientInfo?) async
}

/// Tool registry and JSON-RPC dispatch, shared by every transport and session.
public actor MCPServer {
    public static let supportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"]

    let name: String
    let version: String
    let instructions: String
    private var tools: [String: Tool] = [:]
    private var toolOrder: [String] = []
    private var observer: ToolCallObserver?

    public init(name: String, version: String, instructions: String) {
        self.name = name
        self.version = version
        self.instructions = instructions
    }

    public func register(_ tool: Tool) {
        if tools[tool.name] == nil { toolOrder.append(tool.name) }
        tools[tool.name] = tool
    }

    @discardableResult
    public func remove(tool name: String) -> Bool {
        guard tools.removeValue(forKey: name) != nil else { return false }
        toolOrder.removeAll { $0 == name }
        return true
    }

    public var toolNames: [String] { toolOrder }

    public func setObserver(_ observer: ToolCallObserver?) {
        self.observer = observer
    }

    /// Serves stdin/stdout until stdin closes, stdout is closed by the client, or the task is
    /// cancelled.
    public func runStdio(input: FileHandle = .standardInput) async {
        let (stdoutClosed, markStdoutClosed) = AsyncStream.makeStream(of: Void.self)
        let session = MCPSession(server: self) { text in
            if await !StdoutWriter.shared.write(text + "\n") { markStdoutClosed.finish() }
        }
        await withTaskGroup(of: Void.self) { group in
            group.addTask { await session.run(input: input) }
            group.addTask { for await _ in stdoutClosed {} }
            await group.next()
            group.cancelAll()  // whichever ended first, the other has nothing left to do
        }
    }

    /// The response for one message, or nil when none is due (notifications, responses).
    public func respond(to message: JSONRPCMessage, client: ClientInfo? = nil) async -> JSONRPCResponse? {
        switch message {
        case .notification, .response:
            return nil
        case .invalid(let id):
            return Self.errorResponse(id: id, code: -32600, message: "Invalid Request")
        case .request(let request):
            let id = request.id ?? .null
            do {
                return JSONRPCResponse(id: id, result: try await dispatch(request, client: client))
            } catch let error as RPCError {
                return Self.errorResponse(id: id, code: error.code, message: error.message)
            } catch {
                return Self.errorResponse(id: id, code: -32603, message: "\(error)")
            }
        }
    }

    struct RPCError: Error {
        let code: Int
        let message: String
    }

    private func dispatch(_ request: JSONRPCRequest, client: ClientInfo?) async throws -> any Encodable & Sendable {
        if case .invalid(let reason) = request.params {
            throw RPCError(code: -32602, message: "Invalid params: \(reason)")
        }
        switch request.method {
        case "initialize":
            var requested = ""
            if case .initialize(let params) = request.params { requested = params.protocolVersion ?? "" }
            let version = Self.supportedProtocolVersions.contains(requested) ? requested : Self.supportedProtocolVersions[0]
            return InitializeResult(protocolVersion: version,
                                    serverInfo: .init(name: name, version: self.version),
                                    instructions: instructions)
        case "ping":
            return EmptyResult()
        case "tools/list":
            return ToolsListResult(tools: toolOrder.compactMap { tools[$0]?.descriptor })
        case "tools/call":
            guard case .toolCall(let params) = request.params, let toolName = params.name else {
                throw RPCError(code: -32602, message: "Missing tool name")
            }
            guard let tool = tools[toolName] else {
                throw RPCError(code: -32602, message: "Unknown tool: \(toolName)")
            }
            return await call(tool, arguments: ToolArguments(params.arguments ?? [:]), client: client)
        case "resources/list":
            return ["resources": []] as JSONValue
        case "resources/templates/list":
            return ["resourceTemplates": []] as JSONValue
        case "prompts/list":
            return ["prompts": []] as JSONValue
        default:
            throw RPCError(code: -32601, message: "Method not found: \(request.method)")
        }
    }

    private func call(_ tool: Tool, arguments: ToolArguments, client: ClientInfo?) async -> ToolCallResult {
        let started = DispatchTime.now()
        func elapsed() -> Int { Int((DispatchTime.now().uptimeNanoseconds - started.uptimeNanoseconds) / 1_000_000) }
        let observer = self.observer
        do {
            let content = try await tool.handler(arguments)
            await observer?.toolSucceeded(tool.name, durationMs: elapsed(), client: client)
            return ToolCallResult(content: content, isError: false)
        } catch {
            let cancelled = error is CancellationError
            let message = cancelled ? "Cancelled." : "\(error)"
            // Telemetry gets the ToolError's label or the error's type, never arbitrary text.
            let reported = cancelled ? message : ((error as? ToolError)?.telemetry ?? String(describing: type(of: error)))
            await observer?.toolFailed(tool.name, error: reported, durationMs: elapsed(), client: client)
            let text = cancelled ? message : "Error executing tool \(tool.name): \(message)"
            return ToolCallResult(content: [.text(text)], isError: true)
        }
    }

    static func errorResponse(id: JSONValue, code: Int, message: String) -> JSONRPCResponse {
        JSONRPCResponse(id: id, code: code, message: message)
    }
}

/// One conversation with one client over a message stream (stdio, or one SSE connection).
///
/// Every request runs in its own task, so a long `Wait` or `Shell` call never holds up a `ping`,
/// and `notifications/cancelled` cancels the matching task. Request ids are only unique within
/// a session, which is why in-flight tracking lives here and not on the server.
public actor MCPSession {
    let server: MCPServer
    private let output: @Sendable (String) async -> Void
    private var inFlight: [String: Task<Void, Never>] = [:]
    private var client: ClientInfo?

    /// `output` receives each outgoing message as one line of JSON (without the newline).
    public init(server: MCPServer, output: @escaping @Sendable (String) async -> Void) {
        self.server = server
        self.output = output
    }

    /// Reads newline-delimited messages until the input closes or the task is cancelled, then
    /// lets in-flight requests finish (cancellation cancels them first).
    ///
    /// Splits on `\n` only. `AsyncLineSequence` would also break on U+2028, U+2029, NEL, FF and
    /// VT, which JSON allows unescaped inside strings and JavaScript clients do emit.
    public func run(input: FileHandle) async {
        var buffer: [UInt8] = []
        for await chunk in Self.chunks(of: input) {
            for byte in chunk {
                if byte == 0x0A {
                    receive(line: String(decoding: buffer, as: UTF8.self))
                    buffer.removeAll(keepingCapacity: true)
                } else {
                    buffer.append(byte)
                }
            }
        }
        if Task.isCancelled {
            cancelAll()
        } else if !buffer.isEmpty {
            receive(line: String(decoding: buffer, as: UTF8.self))
        }
        await drain()
    }

    /// The handle's data as it arrives. Unlike `FileHandle.bytes`, which only notices cancellation
    /// when the next byte arrives, this ends as soon as the consuming task is cancelled.
    static func chunks(of input: FileHandle) -> AsyncStream<Data> {
        AsyncStream(bufferingPolicy: .unbounded) { continuation in
            input.readabilityHandler = { handle in
                let data = handle.availableData
                if data.isEmpty {
                    handle.readabilityHandler = nil
                    continuation.finish()
                } else {
                    continuation.yield(data)
                }
            }
            continuation.onTermination = { _ in input.readabilityHandler = nil }
        }
    }

    /// Waits for every in-flight request to finish.
    public func drain() async {
        // Removes what it waited for itself: relying on the tracking task to do it would spin
        // here without ever giving that task the actor.
        while let (key, task) = inFlight.first {
            await task.value
            inFlight[key] = nil
        }
    }

    /// Cancels everything in flight (the client went away).
    public func cancelAll() {
        inFlight.values.forEach { $0.cancel() }
    }

    /// Accepts one incoming line; any response is written to the output when ready.
    public func receive(line: String) {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        guard let incoming = try? JSONCoding.decode(JSONRPCIncoming.self, from: trimmed) else {
            send(MCPServer.errorResponse(id: .null, code: -32700, message: "Parse error"))
            return
        }
        receive(incoming)
    }

    public func receive(_ incoming: JSONRPCIncoming) {
        if case .batch(let batch) = incoming {
            guard !batch.isEmpty else {
                send(MCPServer.errorResponse(id: .null, code: -32600, message: "Invalid Request"))
                return
            }
            batch.forEach(noteInitialize)
            let (server, client, output) = (self.server, self.client, self.output)
            let task = Task {
                var responses: [JSONRPCResponse] = []
                for item in batch {
                    if let response = await server.respond(to: item, client: client) { responses.append(response) }
                }
                if !responses.isEmpty { await output(JSONCoding.encodeString(responses)) }
            }
            track(task, key: "batch-\(UUID().uuidString)")
            return
        }

        guard case .single(let message) = incoming else { return }
        if case .notification(let request) = message, case .cancelled(let params) = request.params {
            inFlight[params.requestId.serialized()]?.cancel()
            return
        }
        noteInitialize(message)

        let key = message.request?.id.map { $0.serialized() } ?? "notification-\(UUID().uuidString)"
        let (server, client, output) = (self.server, self.client, self.output)
        let task = Task {
            if let response = await server.respond(to: message, client: client) {
                await output(response.serialized())
            }
        }
        track(task, key: key)
    }

    private func noteInitialize(_ message: JSONRPCMessage) {
        if case .initialize(let params) = message.request?.params, let info = params.clientInfo {
            client = info
        }
    }

    private func track(_ task: Task<Void, Never>, key: String) {
        inFlight[key] = task
        Task {
            await task.value
            self.finished(key)
        }
    }

    private func finished(_ key: String) {
        inFlight[key] = nil
    }

    private func send(_ response: JSONRPCResponse) {
        let output = self.output
        Task { await output(response.serialized()) }
    }
}

/// Serialises writes so concurrent responses never interleave on stdout.
public actor StdoutWriter {
    public static let shared = StdoutWriter()
    private var closed = false

    /// Writes the whole text; false once stdout is gone (the client closed its end), after which
    /// nothing more is attempted.
    @discardableResult
    public func write(_ text: String) -> Bool {
        if closed { return false }
        let bytes = Array(text.utf8)
        var offset = 0
        while offset < bytes.count {
            let written = bytes.withUnsafeBytes { buffer in
                Darwin.write(STDOUT_FILENO, buffer.baseAddress! + offset, buffer.count - offset)
            }
            if written < 0 {
                if errno == EINTR { continue }
                closed = true
                return false
            }
            offset += written
        }
        return true
    }
}

public func log(_ message: String) {
    FileHandle.standardError.write(Data(("[desktop-mcp] " + message + "\n").utf8))
}
