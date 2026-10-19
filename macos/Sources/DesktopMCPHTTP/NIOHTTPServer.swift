import Foundation
import DesktopMCPCore
import NIOCore
import NIOHTTP1
import NIOHTTP2
import NIOSSL
import NIOTransportServices

/// Serves an `HTTPApp` on Network.framework (NIOTransportServices). Plain connections speak
/// HTTP/1.1; TLS connections negotiate HTTP/2 or HTTP/1.1 through ALPN. An HTTP/2 stream is
/// presented to the request code exactly like an HTTP/1.1 connection, so one handler serves
/// both. Each connection (and each HTTP/2 stream) is handled in its own task; when a client
/// disconnects or resets a stream, the work it was waiting for (such as a tool call) is
/// cancelled.
///
/// `bind` creates an event loop group of its own for the listener; `serve` shuts it down when
/// serving ends, whichever way it ends, so nothing outlives the server.
public enum NIOHTTPServer {
    static let maxBodyBytes = 8 * 1024 * 1024
    /// Streams one HTTP/2 connection may have open at once (advertised to the peer in SETTINGS).
    static let maxConcurrentStreams = 100

    public struct TLSFiles: Sendable {
        public var certificate: String
        public var privateKey: String

        public init(certificate: String, privateKey: String) {
            self.certificate = certificate
            self.privateKey = privateKey
        }
    }

    /// An HTTP/1.1 connection, or one HTTP/2 stream wearing the HTTP/1.1 part types.
    typealias RequestChannel = NIOAsyncChannel<HTTPServerRequestPart, HTTPServerResponsePart>
    typealias Negotiated = NIONegotiatedHTTPVersion<RequestChannel, (Void, NIOHTTP2Handler.AsyncStreamMultiplexer<RequestChannel>)>

    public struct Listening: Sendable {
        public let channel: NIOAsyncChannel<Connection, Never>
        /// The event loops behind this listener and its connections; owned, shut down by `serve`.
        let group: NIOTSEventLoopGroup
        /// The bound port (useful when binding to port 0).
        public var port: Int? { channel.channel.localAddress?.port }
    }

    /// An accepted connection whose protocol is settled once the TLS handshake (if any) completes.
    public struct Connection: Sendable {
        let channel: Channel
        let negotiated: EventLoopFuture<Negotiated>
    }

    /// Loads PEM certificate chain and key into a server TLS context offering `h2` and `http/1.1`.
    public static func makeTLSContext(_ files: TLSFiles) throws -> NIOSSLContext {
        let chain = try NIOSSLCertificate.fromPEMFile(files.certificate)
        let key = try NIOSSLPrivateKey(file: files.privateKey, format: .pem)
        var configuration = TLSConfiguration.makeServerConfiguration(
            certificateChain: chain.map { .certificate($0) }, privateKey: .privateKey(key))
        configuration.applicationProtocols = NIOHTTP2SupportedALPNProtocols
        return try NIOSSLContext(configuration: configuration)
    }

    /// Binds and returns once the socket is listening; call `serve` to accept connections.
    public static func bind(host: String, port: Int, tls: TLSFiles? = nil) async throws -> Listening {
        let sslContext = try tls.map(makeTLSContext)
        // uvicorn resolves "localhost" to IPv4 loopback; do the same so 127.0.0.1 clients connect.
        let bindHost = host.lowercased() == "localhost" ? "127.0.0.1" : host
        let group = NIOTSEventLoopGroup()
        do {
            let channel = try await NIOTSListenerBootstrap(group: group)
                .serverChannelOption(ChannelOptions.socketOption(.so_reuseaddr), value: 1)
                .bind(host: bindHost, port: port) { channel in
                    if let sslContext {
                        return channel.eventLoop.makeCompletedFuture {
                            try channel.pipeline.syncOperations.addHandler(NIOSSLServerHandler(context: sslContext))
                        }.flatMap {
                            channel.configureAsyncHTTPServerPipeline(
                                http2Configuration: http2Configuration,
                                http1ConnectionInitializer: http1Connection,
                                http2ConnectionInitializer: { $0.eventLoop.makeSucceededVoidFuture() },
                                http2StreamInitializer: http2Stream)
                        }.map { Connection(channel: channel, negotiated: $0) }
                    }
                    return channel.eventLoop.makeCompletedFuture {
                        try channel.pipeline.syncOperations.configureHTTPServerPipeline(withErrorHandling: true)
                        let wrapped = try RequestChannel(wrappingChannelSynchronously: channel)
                        return Connection(channel: channel,
                                          negotiated: channel.eventLoop.makeSucceededFuture(.http1_1(wrapped)))
                    }
                }
            return Listening(channel: channel, group: group)
        } catch {
            try? await group.shutdownGracefully()
            throw error
        }
    }

    static var http2Configuration: NIOHTTP2Handler.Configuration {
        var configuration = NIOHTTP2Handler.Configuration()
        configuration.connection.initialSettings = nioDefaultSettings.filter { $0.parameter != .maxConcurrentStreams }
            + [HTTP2Setting(parameter: .maxConcurrentStreams, value: maxConcurrentStreams)]
        return configuration
    }

    /// After ALPN chose HTTP/1.1: the codec is already in place, add the protocol error handler.
    @Sendable static func http1Connection(_ channel: Channel) -> EventLoopFuture<RequestChannel> {
        channel.eventLoop.makeCompletedFuture {
            try channel.pipeline.syncOperations.addHandler(HTTPServerProtocolErrorHandler())
            return try RequestChannel(wrappingChannelSynchronously: channel)
        }
    }

    /// Each HTTP/2 stream is translated to HTTP/1.1 parts; the codec also drops connection-specific
    /// headers (such as `connection: close`) that HTTP/2 forbids.
    @Sendable static func http2Stream(_ channel: Channel) -> EventLoopFuture<RequestChannel> {
        channel.eventLoop.makeCompletedFuture {
            try channel.pipeline.syncOperations.addHandler(HTTP2FramePayloadToHTTP1ServerCodec())
            return try RequestChannel(wrappingChannelSynchronously: channel)
        }
    }

    /// Accepts connections until the listener closes or the task is cancelled. Every connection
    /// is a child task, so cancelling `serve` cancels them and `serve` returns only when they have
    /// finished; then the event loop group is shut down, which closes anything still open.
    public static func serve(_ listening: Listening, app: HTTPApp) async throws {
        let outcome: Result<Void, any Error>
        do {
            try await listening.channel.executeThenClose { inbound in
                try await forEachConcurrently(inbound) { connection in await handle(connection, app: app) }
            }
            outcome = .success(())
        } catch {
            outcome = .failure(error)
        }
        try? await listening.group.shutdownGracefully()
        try outcome.get()
    }

    /// Runs `body` for every element of `source` as a child task of the caller, without waiting
    /// for one element's work before taking the next, and without keeping finished children
    /// around. Ends when `source` ends and all work is done; cancellation cancels the work.
    ///
    /// macOS 14 has `DiscardingTaskGroup` for this. A plain task group keeps a finished child
    /// until `next()` is called, and the loop that reads `source` cannot also sit in `next()`,
    /// so on macOS 13 reading `source` is done by children as well: one child owns the (non
    /// Sendable) iterator and relays elements, one child at a time takes the next element, and
    /// the body only ever awaits `next()`, which reaps whatever finished.
    static func forEachConcurrently<Source: AsyncSequence & Sendable>(
        _ source: Source, discardingGroup: Bool = true, _ body: @escaping @Sendable (Source.Element) async -> Void
    ) async throws where Source.Element: Sendable {
        if discardingGroup, #available(macOS 14.0, *) {
            try await withThrowingDiscardingTaskGroup { group in
                for try await element in source {
                    group.addTask { await body(element) }
                }
            }
            return
        }
        try await withThrowingTaskGroup(of: ServeEvent<Source.Element>.self) { group in
            let (elements, relay) = AsyncStream.makeStream(of: Source.Element.self)
            group.addTask {
                defer { relay.finish() }
                for try await element in source { relay.yield(element) }
                return .handled
            }
            group.addTask { var next = elements.makeAsyncIterator(); return .took(await next.next()) }
            while let event = try await group.next() {
                switch event {
                case .took(let element?):
                    group.addTask { await body(element); return .handled }
                    group.addTask { var next = elements.makeAsyncIterator(); return .took(await next.next()) }
                case .took(nil), .handled:
                    break
                }
            }
        }
    }

    static func handle(_ connection: Connection, app: HTTPApp) async {
        do {
            // The handshake may never finish (a client that connects and goes quiet); closing the
            // channel on cancellation fails the negotiation so the task can end.
            let negotiated = try await withTaskCancellationHandler {
                try await connection.negotiated.get()
            } onCancel: {
                connection.channel.close(promise: nil)
            }
            switch negotiated {
            case .http1_1(let channel):
                await handle(channel, app: app)
            case .http2((_, let multiplexer)):
                // Each stream is a child of the connection's task.
                try await forEachConcurrently(multiplexer.inbound) { stream in await handle(stream, app: app) }
            }
        } catch {
            // A failed TLS handshake or an unexpected ALPN result: the channel is already closed.
        }
    }

    static func handle(_ channel: RequestChannel, app: HTTPApp) async {
        // One close callback per connection, not per request: NIO keeps every callback until the
        // future fires, and a per-request closure would pin each response for the connection's
        // lifetime. The slot holds whatever the current request is waiting on.
        let onClose = Locked<(@Sendable () -> Void)?>(nil)
        channel.channel.closeFuture.whenComplete { _ in onClose.current?() }
        do {
            try await channel.executeThenClose { inbound, outbound in
                var head: HTTPRequestHead?
                var body = ByteBuffer()
                var tooLarge = false
                for try await part in inbound {
                    switch part {
                    case .head(let requestHead):
                        head = requestHead
                        body.clear()
                        tooLarge = false
                    case .body(var chunk):
                        if body.readableBytes + chunk.readableBytes > maxBodyBytes {
                            tooLarge = true
                        } else {
                            body.writeBuffer(&chunk)
                        }
                    case .end:
                        guard let requestHead = head else { continue }
                        head = nil
                        let response: HTTPResponse
                        if tooLarge {
                            response = .text("Request Entity Too Large", status: 413)
                        } else {
                            let request = HTTPRequest(
                                method: requestHead.method.rawValue, target: requestHead.uri,
                                headers: requestHead.headers.map { ($0.name, $0.value) },
                                body: Data(buffer: body),
                                // Read per request: a Network.framework child channel has no peer
                                // address yet while its initializer runs.
                                remoteAddress: channel.channel.remoteAddress?.ipAddress)
                            // Cancelled if the client disconnects while it is being prepared, or
                            // if the server is stopping.
                            let work = Task { await app.handle(request) }
                            onClose.current = { work.cancel() }
                            response = await withTaskCancellationHandler {
                                await work.value
                            } onCancel: {
                                work.cancel()
                            }
                            onClose.current = nil
                        }
                        let keepAlive = try await write(response, for: requestHead, to: outbound, onClose: onClose)
                        if !keepAlive || tooLarge { return }
                    }
                }
            }
        } catch {
            // Peer resets and TLS failures end the connection; nothing to report to anyone.
        }
    }

    /// Writes the response; returns whether the connection may be reused.
    static func write(_ response: HTTPResponse, for request: HTTPRequestHead,
                      to outbound: NIOAsyncChannelOutboundWriter<HTTPServerResponsePart>,
                      onClose: Locked<(@Sendable () -> Void)?>) async throws -> Bool {
        var headers = HTTPHeaders(response.headers)
        let keepAlive = request.isKeepAlive
        let status = HTTPResponseStatus(statusCode: response.status)

        switch response.body {
        case .data(let data):
            headers.replaceOrAdd(name: "content-length", value: String(data.count))
            if !keepAlive { headers.replaceOrAdd(name: "connection", value: "close") }
            try await outbound.write(.head(HTTPResponseHead(version: request.version, status: status, headers: headers)))
            if !data.isEmpty { try await outbound.write(.body(.byteBuffer(ByteBuffer(bytes: data)))) }
            try await outbound.write(.end(nil))
            return keepAlive

        case .stream(let stream):
            // No content-length: HTTP/1.1 gets chunked encoding from the encoder, HTTP/2 sends
            // DATA frames until END_STREAM.
            try await outbound.write(.head(HTTPResponseHead(version: request.version, status: status, headers: headers)))
            let consumer = Task {
                for await chunk in stream {
                    try await outbound.write(.body(.byteBuffer(ByteBuffer(string: chunk))))
                }
            }
            // Cancelling the consumer terminates the stream, which stops the work behind it;
            // that happens when the client goes away and when the server is stopping.
            onClose.current = { consumer.cancel() }
            defer { onClose.current = nil }
            try await withTaskCancellationHandler {
                try await consumer.value
            } onCancel: {
                consumer.cancel()
            }
            try await outbound.write(.end(nil))
            return keepAlive
        }
    }
}

/// What a child of `NIOHTTPServer.forEachConcurrently` reports back on macOS 13.
enum ServeEvent<Element: Sendable>: Sendable {
    /// The next element, or nil when the source has ended.
    case took(Element?)
    /// Work for one element finished (or the relay ended).
    case handled
}
