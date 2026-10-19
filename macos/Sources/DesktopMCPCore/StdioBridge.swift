import Foundation
import MCP

/// `connect`: a stdio MCP endpoint that forwards every message to the streamable-http server
/// and writes what comes back, one JSON-RPC message per line. For clients that can only spawn
/// stdio servers (Claude Desktop's config file), so they too can use the Launch Agent, which is
/// the process that holds the Accessibility and Screen Recording permissions.
public struct StdioBridge: Sendable {
    public struct Target: Equatable, Sendable {
        public var url: URL
        public var authKey: String?

        public init(url: URL, authKey: String?) {
            self.url = url
            self.authKey = authKey
        }

        /// Where the installed Launch Agent listens (from its plist) and how to authenticate
        /// (from config.toml, which the agent reads as well).
        public static func installed(plist: Data, config: DesktopMCPConfig) throws -> Target {
            guard let dictionary = try? PropertyListSerialization.propertyList(from: plist, format: nil) as? [String: Any],
                  let arguments = dictionary["ProgramArguments"] as? [String] else {
                throw ConfigErrorText("Could not read the Launch Agent plist.")
            }
            func value(after flag: String) -> String? {
                guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
                return arguments[index + 1]
            }
            let transport = value(after: "--transport") ?? "streamable-http"
            // An agent installed with `--transport sse` maps it to streamable-http itself.
            guard transport == "streamable-http" || transport == "sse" else {
                throw ConfigErrorText("connect supports streamable-http only; the installed agent runs \(transport).")
            }
            let host = value(after: "--host") ?? "127.0.0.1"
            let port = value(after: "--port").flatMap(Int.init) ?? 8000
            let scheme = config.server.sslCertfile != nil ? "https" : "http"
            let authority = host.contains(":") ? "[\(host)]" : host
            guard let url = URL(string: "\(scheme)://\(authority):\(port)/mcp") else {
                throw ConfigErrorText("Invalid server address: \(host):\(port)")
            }
            return Target(url: url, authKey: config.server.authKey)
        }
    }

    public let target: Target
    let session: URLSession
    let state = State()

    /// No inactivity timeout: a tool call can take minutes (Wait, Shell) and a session stream
    /// can stay silent for hours; the server ends the response when the call does.
    public init(target: Target, session: URLSession? = nil) {
        self.target = target
        self.session = session ?? {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.timeoutIntervalForRequest = 0
            configuration.timeoutIntervalForResource = 24 * 3600
            return URLSession(configuration: configuration)
        }()
    }

    /// Reads stdin until it closes (or the task is cancelled), forwarding each line as it arrives
    /// so a slow call does not hold up the next, and hands every reply line to `output`.
    public func run(input: FileHandle, output: @escaping @Sendable (String) async -> Void) async {
        await withTaskGroup(of: Void.self) { group in
            func dispatch(_ bytes: [UInt8]) {
                let line = String(decoding: bytes, as: UTF8.self)
                guard !line.trimmingCharacters(in: .whitespaces).isEmpty else { return }
                group.addTask { await forward(line, output: output) }
            }
            var buffer: [UInt8] = []
            for await chunk in Self.chunks(of: input) {
                for byte in chunk {
                    if byte == 0x0A {
                        dispatch(buffer)
                        buffer.removeAll(keepingCapacity: true)
                    } else {
                        buffer.append(byte)
                    }
                }
            }
            if !buffer.isEmpty, !Task.isCancelled { dispatch(buffer) }
        }
    }

    /// One message in, zero or more messages out, each handed to `output` as soon as it is
    /// complete: nothing for notifications and responses (HTTP 202), the reply for a request
    /// (after whatever the server sends first on its event stream), or a JSON-RPC error carrying
    /// the HTTP failure when the server could not be reached or refused the request.
    public func forward(_ line: String, output: @Sendable (String) async -> Void) async {
        let body = line.trimmingCharacters(in: .whitespaces)
        var request = URLRequest(url: target.url)
        request.httpMethod = "POST"
        request.httpBody = Data(body.utf8)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json, text/event-stream", forHTTPHeaderField: "Accept")
        if let key = target.authKey { request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
        if let sessionID = await state.sessionID { request.setValue(sessionID, forHTTPHeaderField: "Mcp-Session-Id") }
        let id = Self.requestID(in: body)
        var answered = false
        func emit(_ payload: String) async {
            let reply = Self.singleLine(payload)
            if let id, !answered, Self.isResponse(reply, to: id) { answered = true }
            await output(reply)
        }
        do {
            let (bytes, response) = try await session.bytes(for: request)
            guard let http = response as? HTTPURLResponse else { throw Failure("not an HTTP response") }
            if let sessionID = http.value(forHTTPHeaderField: "Mcp-Session-Id") { await state.remember(sessionID) }
            guard (200..<300).contains(http.statusCode) else {
                var head: [UInt8] = []
                for try await byte in bytes {
                    head.append(byte)
                    if head.count == 500 { break }
                }
                let text = String(decoding: head, as: UTF8.self)
                throw Failure("HTTP \(http.statusCode)\(text.isEmpty ? "" : ": \(text)")")
            }
            if http.statusCode == 202 { return }
            let isEventStream = (http.value(forHTTPHeaderField: "Content-Type") ?? "").lowercased().hasPrefix("text/event-stream")
            if isEventStream {
                // Lines end at "\n" only: `AsyncLineSequence` also ends them at CR, NEL, U+2028 and
                // U+2029, which a JSON string may carry unescaped.
                var parser = SSEParser()
                var pendingLine: [UInt8] = []
                for try await byte in bytes {
                    pendingLine.append(byte)
                    guard byte == 0x0A else { continue }
                    for payload in parser.feed(pendingLine) { await emit(payload) }
                    pendingLine.removeAll(keepingCapacity: true)
                }
                _ = parser.feed(pendingLine)
                if let payload = parser.finish() { await emit(payload) }
            } else {
                var data: [UInt8] = []
                for try await byte in bytes { data.append(byte) }
                if !data.isEmpty { await emit(String(decoding: data, as: UTF8.self)) }
            }
        } catch {
            let message = "desktop-mcp connect: \(target.url.absoluteString): \((error as? Failure)?.description ?? error.localizedDescription)"
            // Nobody waits for an answer to a notification or a response, and a request whose
            // reply already went out must not get a second one when the stream breaks after it.
            guard let id, !answered else {
                log(message)
                return
            }
            let reply: Value = ["jsonrpc": "2.0", "id": id, "error": ["code": -32000, "message": .string(message)]]
            await output(JSONCoding.encodeString(reply))
        }
    }

    /// Whether `text` is the response (result or error) to the request with `id`.
    static func isResponse(_ text: String, to id: Value) -> Bool {
        guard let message = try? JSONCoding.decode(Value.self, from: text), let object = message.objectValue else { return false }
        return object["method"] == nil && object["id"] == id
    }

    /// The id when `text` is a single request (has a method and a non-null id).
    static func requestID(in text: String) -> Value? {
        guard let message = try? JSONCoding.decode(Value.self, from: text), let object = message.objectValue, object["method"] != nil,
              let id = object["id"], !id.isNull else { return nil }
        return id
    }

    /// stdio framing is one message per line, so a payload must not contain raw newlines.
    static func singleLine(_ payload: String) -> String {
        guard payload.contains("\n") || payload.contains("\r") else { return payload }
        if let value = try? JSONCoding.decode(Value.self, from: payload) { return JSONCoding.encodeString(value) }
        return payload.replacingOccurrences(of: "\r", with: "").replacingOccurrences(of: "\n", with: " ")
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

    struct Failure: Error, CustomStringConvertible {
        let description: String
        init(_ description: String) { self.description = description }
    }

    actor State {
        var sessionID: String?
        func remember(_ id: String) { sessionID = id }
    }
}

/// The subset of Server-Sent Events the MCP transports use.
public enum SSE {
    /// The `id` field of each `id:` line, in order.
    public static func eventIDs(_ text: String) -> [String] {
        text.split(whereSeparator: { $0 == "\n" || $0 == "\r\n" }).compactMap { line in
            guard line.hasPrefix("id:") else { return nil }
            var value = line.dropFirst(3)
            if value.hasPrefix(" ") { value = value.dropFirst() }
            return String(value)
        }
    }

    /// The `data` of each event, lines joined with "\n"; comments, ids and event names are
    /// dropped, and a trailing event without a blank line still counts.
    public static func dataPayloads(_ text: String) -> [String] {
        var parser = SSEParser()
        var payloads = parser.feed(text.utf8)
        if let last = parser.finish() { payloads.append(last) }
        return payloads
    }
}

/// An incremental reader for the subset of Server-Sent Events the MCP transports use: a line
/// ends at "\n" (a "\r" before it is dropped), only `data` fields are kept, and a blank line
/// ends the event. Bytes may be split anywhere, inside a line or a UTF-8 sequence included.
public struct SSEParser {
    private var line: [UInt8] = []
    private var data: [String]? = nil

    public init() {}

    /// Feeds bytes; returns the non-empty `data` payloads of events completed by them.
    public mutating func feed(_ bytes: some Sequence<UInt8>) -> [String] {
        var payloads: [String] = []
        for byte in bytes {
            guard byte == 0x0A else {
                line.append(byte)
                continue
            }
            if let payload = endLine() { payloads.append(payload) }
        }
        return payloads
    }

    /// A trailing event without its blank line, if any.
    public mutating func finish() -> String? {
        if !line.isEmpty { _ = endLine() }
        return endEvent()
    }

    /// Takes in the buffered line; the event's payload when it was the blank line ending one.
    private mutating func endLine() -> String? {
        if line.last == 0x0D { line.removeLast() }
        defer { line.removeAll(keepingCapacity: true) }
        if line.isEmpty { return endEvent() }
        let text = String(decoding: line, as: UTF8.self)
        guard text.hasPrefix("data:") else { return nil }  // comments, ids, event names
        var value = text.dropFirst(5)
        if value.hasPrefix(" ") { value = value.dropFirst() }
        data = (data ?? []) + [String(value)]
        return nil
    }

    /// An empty payload carries nothing to forward: the stateful transport's priming event
    /// (`data: ` before each reply) would otherwise become an empty stdout line.
    private mutating func endEvent() -> String? {
        defer { data = nil }
        guard let payload = data?.joined(separator: "\n"), !payload.isEmpty else { return nil }
        return payload
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
