import Foundation
import MCP

/// The tools this server offers, and the factory for the SDK servers that serve them.
///
/// One registry backs every connection: stdio builds one server from it, the HTTP transports
/// build one per request. Each server reads the tools and the observer when a request arrives,
/// so the registry stays the single source of truth.
public actor ToolRegistry {
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

    /// `tools/list` content, registration order.
    public func descriptors() -> [MCP.Tool] {
        toolOrder.compactMap { tools[$0] }.map { tool in
            MCP.Tool(name: tool.name, description: tool.description, inputSchema: tool.inputSchema,
                     annotations: tool.annotations.sdk)
        }
    }

    private func lookup(_ name: String) -> (Tool?, ToolCallObserver?) {
        (tools[name], observer)
    }

    /// A fresh SDK server (non-strict, capabilities: tools only) with ListTools, CallTool,
    /// ListPrompts, ListResources and ListResourceTemplates handlers, started on `transport`.
    public func start(on transport: any Transport) async throws -> StartedServer {
        let server = Server(name: name, version: version, instructions: instructions,
                            capabilities: .init(tools: .init(listChanged: false)))
        let client = Locked<Client.Info?>(nil)
        let inFlight = Locked(ToolCalls())

        await server
            .withMethodHandler(ListTools.self) { _ in
                .init(tools: await self.descriptors())
            }
            .withMethodHandler(CallTool.self) { params in
                let (tool, observer) = await self.lookup(params.name)
                guard let tool else { throw MCPError.invalidParams("Unknown tool: \(params.name)") }
                return await Self.call(tool, arguments: ToolArguments(params.arguments ?? [:]),
                                       client: client.current, observer: observer, inFlight: inFlight)
            }
            .withMethodHandler(ListPrompts.self) { _ in .init(prompts: []) }
            .withMethodHandler(ListResources.self) { _ in .init(resources: []) }
            .withMethodHandler(ListResourceTemplates.self) { _ in .init(templates: []) }

        try await server.start(transport: transport) { info, _ in
            client.current = info
        }
        return StartedServer(server: server, inFlight: inFlight)
    }

    /// Runs one tool call to a result. Tool failures, cancellation included, are results with
    /// `isError` (what the client shows the model), not protocol errors.
    ///
    /// The handler runs in a task of its own, tracked in `inFlight`, because `Server.stop()`
    /// does not cancel the request tasks it spawned: `StartedServer.shutdown()` cancels these.
    /// A `notifications/cancelled` cancels the request task, which the cancellation handler
    /// forwards.
    static func call(_ tool: Tool, arguments: ToolArguments, client: Client.Info?, observer: ToolCallObserver?,
                     inFlight: Locked<ToolCalls>) async -> CallTool.Result {
        let started = DispatchTime.now()
        func elapsed() -> Int { Int((DispatchTime.now().uptimeNanoseconds - started.uptimeNanoseconds) / 1_000_000) }
        let id = UUID()
        let task = Task { try await tool.handler(arguments) }
        if !inFlight.withLock({ $0.register(task, id: id) }) { task.cancel() }
        defer { inFlight.withLock { $0.tasks[id] = nil } }
        do {
            let content = try await withTaskCancellationHandler {
                try await task.value
            } onCancel: {
                task.cancel()
            }
            await observer?.toolSucceeded(tool.name, durationMs: elapsed(), client: client)
            return .init(content: content, isError: false)
        } catch {
            let cancelled = error is CancellationError
            let message = cancelled ? "Cancelled." : "\(error)"
            // Telemetry gets the ToolError's label or the error's type, never arbitrary text.
            let reported = cancelled ? message : ((error as? ToolError)?.telemetry ?? String(describing: type(of: error)))
            await observer?.toolFailed(tool.name, error: reported, durationMs: elapsed(), client: client)
            let text = cancelled ? message : "Error executing tool \(tool.name): \(message)"
            return .init(content: [.text(text)], isError: true)
        }
    }
}

/// A server started by `ToolRegistry.start(on:)`, with the tool calls it is running.
public struct StartedServer: Sendable {
    public let server: MCP.Server
    let inFlight: Locked<ToolCalls>

    public func waitUntilCompleted() async {
        await server.waitUntilCompleted()
    }

    /// Cancels every tool call in flight, then stops the server (which disconnects the transport).
    public func shutdown() async {
        // Cancelled outside the lock: `cancel()` runs cancellation handlers synchronously.
        let running = inFlight.withLock { $0.close() }
        running.forEach { $0.cancel() }
        await server.stop()
    }
}

/// The tool calls one started server is running. Once closed (shut down), a call that got past
/// the tool lookup but had not registered yet is refused, and its caller cancels it at once.
struct ToolCalls {
    var tasks: [UUID: Task<[MCP.Tool.Content], Error>] = [:]
    private(set) var closed = false

    /// False when closed: the caller must cancel `task` itself.
    mutating func register(_ task: Task<[MCP.Tool.Content], Error>, id: UUID) -> Bool {
        guard !closed else { return false }
        tasks[id] = task
        return true
    }

    /// Closes the set and hands back the tasks still running, for the caller to cancel.
    mutating func close() -> [Task<[MCP.Tool.Content], Error>] {
        closed = true
        return Array(tasks.values)
    }
}
