import Foundation
import MCP
import Security

/// Anonymous usage telemetry sent to the original project's PostHog, as the Python server does.
/// On unless `ANONYMIZED_TELEMETRY=false`. Events: `tool_executed` (name, duration, client) and
/// `exception` (tool, error text). No arguments or tool output are ever sent.
///
/// Delivery is structured: `run()` hosts a task group in which every queued event is sent as a
/// child task, so it belongs to whatever task group the server runs in. `close()` ends the queue
/// and waits (bounded) for the sends in flight.
public actor PostHogAnalytics: ToolCallObserver {
    public static let apiKey = "phc_uxdCItyVTjXNU0sMPr97dq3tcz39scQNt3qjTYw5vLV"
    public static let host = "https://us.i.posthog.com"
    public static let optOutEnv = "ANONYMIZED_TELEMETRY"

    /// Whether telemetry is on for this environment.
    public static func isEnabled(_ environment: [String: String] = ProcessInfo.processInfo.environment) -> Bool {
        environment[optOutEnv]?.lowercased() != "false"
    }

    let endpoint: URL
    let sessionID: String
    let verbose: Bool
    private let send: @Sendable (URLRequest) async -> Void
    private var cachedUserID: String?
    /// Captured events waiting for `run()` to send them.
    private let queue: AsyncStream<URLRequest>
    private let enqueue: AsyncStream<URLRequest>.Continuation
    /// Finished by `run()` once the queue is closed and every send has ended.
    private let drained: AsyncStream<Void>
    private let markDrained: AsyncStream<Void>.Continuation

    public init(host: String = PostHogAnalytics.host, verbose: Bool = false,
                send: (@Sendable (URLRequest) async -> Void)? = nil) {
        self.endpoint = URL(string: host)!.appendingPathComponent("batch/")
        self.sessionID = "mcp_\(Int(Date().timeIntervalSince1970 * 1000))_\(getpid())"
        self.verbose = verbose
        self.send = send ?? { request in _ = try? await URLSession.shared.data(for: request) }
        (queue, enqueue) = AsyncStream.makeStream(of: URLRequest.self, bufferingPolicy: .unbounded)
        (drained, markDrained) = AsyncStream.makeStream(of: Void.self)
    }

    /// At most this many sends at once; further events wait in the queue.
    public static let maxInFlight = 8

    /// Sends events as they are captured, each in a child task, until `close()` is called and
    /// the queue is empty (or this task is cancelled, which abandons the sends in flight).
    /// Run it alongside the server, in the same task group.
    ///
    /// A discarding task group in all but name (that needs macOS 14): results are `Void` and
    /// are consumed as sends finish, so nothing accumulates however long the server runs.
    public nonisolated func run() async {
        await withTaskGroup(of: Void.self) { group in
            var inFlight = 0
            for await request in queue {
                if inFlight >= Self.maxInFlight {
                    await group.next()
                    inFlight -= 1
                }
                group.addTask { await self.send(request) }
                inFlight += 1
            }
        }
        markDrained.finish()
    }

    /// Stops accepting events and waits for queued sends to land, giving up after `timeout`
    /// seconds (a stuck send must not hold up process exit).
    public nonisolated func close(timeout: Double = 3) async {
        enqueue.finish()
        await withTaskGroup(of: Void.self) { group in
            group.addTask { for await _ in self.drained {} }
            group.addTask { try? await Task.sleep(for: .seconds(timeout)) }
            await group.next()
            group.cancelAll()
        }
    }

    /// Persistent anonymous id, shared with the Python server (same file in $TMPDIR).
    var userID: String {
        if let cachedUserID { return cachedUserID }
        let file = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(".desktop-mcp-user-id")
        if let saved = try? String(contentsOf: file, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines),
           !saved.isEmpty {
            cachedUserID = saved
        } else {
            let id = Self.uuid7()
            do {
                try id.write(to: file, atomically: true, encoding: .utf8)
            } catch {
                log("Could not persist user ID: \(error)")
            }
            cachedUserID = id
        }
        return cachedUserID!
    }

    public func toolSucceeded(_ tool: String, durationMs: Int, client: Client.Info?) {
        var properties = EventProperties(toolName: tool, durationMs: durationMs, client: client)
        properties.success = true
        capture("tool_executed", properties)
        if verbose { log("[Analytics] \(tool): SUCCESS (\(durationMs)ms)") }
    }

    public func toolFailed(_ tool: String, error: String, durationMs: Int, client: Client.Info?) {
        var properties = EventProperties(toolName: tool, durationMs: durationMs, client: client)
        properties.exception = error
        properties.traceback = error
        capture("exception", properties)
        log("ERROR in \(tool): \(error)")
    }

    func capture(_ event: String, _ properties: EventProperties) {
        var properties = properties
        properties.sessionId = sessionID
        let payload = Batch(apiKey: Self.apiKey, batch: [
            Event(event: event, distinctId: userID, properties: properties, timestamp: Date(), uuid: UUID().uuidString.lowercased()),
        ])
        var request = URLRequest(url: endpoint, timeoutInterval: 10)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data(JSONCoding.encodeString(payload).utf8)
        enqueue.yield(request)
    }

    /// A version 7 UUID (Unix-millisecond timestamp first), as `uuid7str()` produces.
    static func uuid7() -> String {
        var bytes = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, 16, &bytes)
        let millis = UInt64(Date().timeIntervalSince1970 * 1000)
        for i in 0..<6 { bytes[i] = UInt8(truncatingIfNeeded: millis >> (40 - 8 * UInt64(i))) }
        bytes[6] = 0x70 | (bytes[6] & 0x0f)
        bytes[8] = 0x80 | (bytes[8] & 0x3f)
        let hex = bytes.map { String(format: "%02x", $0) }.joined()
        let parts = [hex.prefix(8), hex.dropFirst(8).prefix(4), hex.dropFirst(12).prefix(4),
                     hex.dropFirst(16).prefix(4), hex.dropFirst(20)]
        return parts.joined(separator: "-")
    }
}

// MARK: Wire types (PostHog batch API)

struct Batch: Encodable {
    var apiKey: String
    var batch: [Event]

    enum CodingKeys: String, CodingKey {
        case apiKey = "api_key"
        case batch
    }
}

struct Event: Encodable {
    var event: String
    var distinctId: String
    var properties: EventProperties
    var timestamp: Date
    var uuid: String

    enum CodingKeys: String, CodingKey {
        case event
        case distinctId = "distinct_id"
        case properties, timestamp, uuid
    }
}

/// The properties the original sends, and nothing else: no arguments, no output.
struct EventProperties: Encodable {
    var toolName: String
    var durationMs: Int
    var success: Bool?
    var exception: String?
    var traceback: String?
    var clientName: String?
    var clientVersion: String?
    var sessionId = ""
    var processPersonProfile = true
    var lib = "desktop-mcp-swift"
    var libVersion = DesktopMCP.version

    init(toolName: String, durationMs: Int, client: Client.Info?) {
        self.toolName = toolName
        self.durationMs = durationMs
        clientName = client?.name
        clientVersion = client?.version
    }

    enum CodingKeys: String, CodingKey {
        case toolName = "tool_name"
        case durationMs = "duration_ms"
        case success, exception, traceback
        case clientName = "client_name"
        case clientVersion = "client_version"
        case sessionId = "session_id"
        case processPersonProfile = "process_person_profile"
        case lib = "$lib"
        case libVersion = "$lib_version"
    }
}
