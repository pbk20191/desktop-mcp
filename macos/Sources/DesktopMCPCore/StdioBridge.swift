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

    /// No inactivity or resource timeout: a tool call can take minutes (Wait, Shell), and the
    /// session's `GET` stream stays open, mostly silent, for as long as the bridge runs; the
    /// server ends a response when the call does.
    public init(target: Target, session: URLSession? = nil) {
        self.target = target
        self.session = session ?? {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.timeoutIntervalForRequest = 0
            configuration.timeoutIntervalForResource = 0
            return URLSession(configuration: configuration)
        }()
    }

    /// Reads stdin until it closes (or the task is cancelled), forwarding each line as it arrives
    /// so a slow call does not hold up the next, and hands every reply line to `output`, which
    /// returns false once a line could not be written. While the bridge holds a session it also
    /// listens on the session's `GET` stream and hands on what the server sends there.
    ///
    /// At EOF the `GET` listener is cancelled and the forwarded requests run to their end. A
    /// failed write cancels everything: nobody is left to read what the rest would write.
    public func run(input: FileHandle, output: @escaping @Sendable (String) async -> Bool) async {
        let (failures, failed) = AsyncStream<Void>.makeStream()
        let write: @Sendable (String) async -> Void = { line in
            if !(await output(line)) { failed.yield() }
        }
        await withTaskGroup(of: Void.self) { outer in
            outer.addTask {
                await withTaskGroup(of: Void.self) { group in
                    func dispatch(_ bytes: [UInt8]) {
                        let line = String(decoding: bytes, as: UTF8.self)
                        guard !line.trimmingCharacters(in: .whitespaces).isEmpty else { return }
                        group.addTask {
                            await forward(line, output: write)
                            await followSession(output: write)
                        }
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
                    // The group now waits for the forwarded requests, but not for the listener.
                    await state.close()
                }
            }
            outer.addTask {
                for await _ in failures { return }
            }
            // Whichever ends first: the input and every request, or stdout.
            await outer.next()
            outer.cancelAll()
        }
        failed.finish()
        await state.close()
    }

    /// Keeps one `GET` listener on the current session: a new one when the session id first
    /// appears or changes, none once the bridge is closing.
    func followSession(output: @escaping @Sendable (String) async -> Void) async {
        await state.follow { sessionID in
            Task { await listen(sessionID: sessionID, output: output) }
        }
    }

    /// The session's standalone `GET` stream, where the server sends what it initiates
    /// (`notifications/tools/list_changed`, say): every non-empty payload goes to `output`. A
    /// stream that ends is re-opened a second later while the session id is unchanged; a 404
    /// (the session is gone) or 405 (the server offers no stream) ends listening for good.
    func listen(sessionID: String, output: @Sendable (String) async -> Void) async {
        while !Task.isCancelled {
            var request = URLRequest(url: target.url)
            request.httpMethod = "GET"
            request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
            request.setValue(sessionID, forHTTPHeaderField: "Mcp-Session-Id")
            authorize(&request)
            do {
                let (bytes, response) = try await session.bytes(for: request)
                let status = (response as? HTTPURLResponse)?.statusCode ?? 0
                if status == 404 || status == 405 { return }
                // Anything else, a 409 while the server still holds the previous stream
                // included, is retried below.
                if (200..<300).contains(status) {
                    try await Self.readEvents(bytes) { await output(Self.singleLine($0)) }
                }
            } catch {}
            guard !Task.isCancelled else { return }
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled, await state.sessionID == sessionID else { return }
        }
    }

    /// One message in, zero or more messages out, each handed to `output` as soon as it is
    /// complete: nothing for notifications and responses (HTTP 202), the reply for a request
    /// (after whatever the server sends first on its event stream), or a JSON-RPC error carrying
    /// the HTTP failure when the server could not be reached, refused the request, or ended the
    /// response without the reply.
    ///
    /// When the server has forgotten the session (404), the first `initialize` this bridge
    /// forwarded is sent again for a new one, its reply dropped (the stdio client has had its
    /// own), and the message is sent once more; a second 404 is reported like any failure.
    public func forward(_ line: String, output: @Sendable (String) async -> Void) async {
        let body = line.trimmingCharacters(in: .whitespaces)
        if HTTPApp.isInitialize(Data(body.utf8)) { await state.rememberInitialize(body) }
        let id = Self.requestID(in: body)
        var answered = false
        func emit(_ payload: String) async {
            let reply = Self.singleLine(payload)
            if let id, !answered, Self.isResponse(reply, to: id) { answered = true }
            await output(reply)
        }
        do {
            var sent = await state.sessionID
            var (bytes, http) = try await post(body, sessionID: sent)
            if http.statusCode == 404, let stale = sent,
               let fresh = await state.recover(from: stale, replay: { await replayInitialize($0) }) {
                sent = fresh
                (bytes, http) = try await post(body, sessionID: fresh)
            }
            if let sessionID = http.value(forHTTPHeaderField: "Mcp-Session-Id") {
                await state.remember(sessionID, replacing: sent)
            }
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
                try await Self.readEvents(bytes) { await emit($0) }
            } else {
                var data: [UInt8] = []
                for try await byte in bytes { data.append(byte) }
                if !data.isEmpty { await emit(String(decoding: data, as: UTF8.self)) }
            }
            // The server ended the response without answering (it restarted, or the session
            // ended mid-call): the stdio client would otherwise wait for the reply forever.
            // Not retried: the request may have had effects.
            if id != nil, !answered { throw Failure("the server ended the response without a reply") }
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

    /// Posts one message: the response head, and the body as it arrives.
    func post(_ body: String, sessionID: String?) async throws -> (URLSession.AsyncBytes, HTTPURLResponse) {
        var request = URLRequest(url: target.url)
        request.httpMethod = "POST"
        request.httpBody = Data(body.utf8)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json, text/event-stream", forHTTPHeaderField: "Accept")
        authorize(&request)
        if let sessionID { request.setValue(sessionID, forHTTPHeaderField: "Mcp-Session-Id") }
        let (bytes, response) = try await session.bytes(for: request)
        guard let http = response as? HTTPURLResponse else { throw Failure("not an HTTP response") }
        return (bytes, http)
    }

    /// Sends the remembered `initialize` without a session id, so the server starts a new
    /// session, and reads its reply to the end without forwarding it. The new session's id, or
    /// nil when the server would not start one.
    func replayInitialize(_ line: String) async -> String? {
        guard let (bytes, http) = try? await post(line, sessionID: nil), (200..<300).contains(http.statusCode),
              let id = http.value(forHTTPHeaderField: "Mcp-Session-Id") else { return nil }
        do {
            for try await _ in bytes {}
        } catch {}
        return id
    }

    func authorize(_ request: inout URLRequest) {
        if let key = target.authKey { request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
    }

    /// Hands each event's `data` to `emit` as soon as the blank line ending it arrives. Lines end
    /// at "\n" only: `AsyncLineSequence` also ends them at CR, NEL, U+2028 and U+2029, which a
    /// JSON string may carry unescaped.
    static func readEvents(_ bytes: URLSession.AsyncBytes, emit: (String) async -> Void) async throws {
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
        private(set) var sessionID: String?
        /// The first `initialize` forwarded: what a recovery sends again.
        private(set) var initializeLine: String?
        /// The `GET` listener and the session it listens on.
        private(set) var listener: Task<Void, Never>?
        private var listenerSessionID: String?
        /// Recoveries attempted (test hook).
        private(set) var replays = 0
        private var recovering: Task<String?, Never>?
        /// Set at EOF: no new listener from then on.
        private var closed = false

        func rememberInitialize(_ line: String) {
            if initializeLine == nil { initializeLine = line }
        }

        /// Takes the id a response carried, unless the session has moved on since the request
        /// was sent (a slow response from a session a recovery has replaced).
        func remember(_ id: String, replacing sent: String?) {
            if sessionID == sent { sessionID = id }
        }

        /// A new session id after the server answered 404 for `stale`, or nil when there is
        /// nothing to replay or the server would not start a session. Requests that hit the
        /// 404 together share one replay; one that comes after it takes the id it produced.
        func recover(from stale: String, replay: @escaping @Sendable (String) async -> String?) async -> String? {
            if let sessionID, sessionID != stale { return sessionID }
            if let recovering { return await recovering.value }
            guard let initializeLine else { return nil }
            sessionID = nil
            stopListening()
            replays += 1
            let task = Task { await replay(initializeLine) }
            recovering = task
            let fresh = await task.value
            recovering = nil
            if let fresh { sessionID = fresh }
            return fresh
        }

        /// Starts a listener with `start` when the session id differs from the one listened on.
        func follow(_ start: @Sendable (String) -> Task<Void, Never>) {
            guard !closed, sessionID != listenerSessionID else { return }
            stopListening()
            listenerSessionID = sessionID
            if let sessionID { listener = start(sessionID) }
        }

        /// Cancels the listener and waits for it to end; no listener starts afterwards.
        func close() async {
            closed = true
            let listener = listener
            stopListening()
            await listener?.value
        }

        private func stopListening() {
            listener?.cancel()
            listener = nil
            listenerSessionID = nil
        }
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
