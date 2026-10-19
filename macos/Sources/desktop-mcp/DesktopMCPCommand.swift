import ArgumentParser
@preconcurrency import CoreFoundation
import Foundation
import DesktopMCPCore
import DesktopMCPHTTP

@main
struct DesktopMCPCommand: AsyncParsableCommand {
    static let configuration = CommandConfiguration(
        commandName: "desktop-mcp",
        abstract: "MCP server for macOS desktop automation.",
        discussion: """
        Permissions are granted to the app that launches this binary (Claude, your terminal, ...) \
        under System Settings > Privacy & Security > Accessibility and > Screen Recording. \
        Installed as a Launch Agent (`install`), desktop-mcp holds them itself.
        """,
        version: DesktopMCP.version,
        subcommands: [Serve.self, Auth.self, Permissions.self, Install.self, Uninstall.self, Connect.self],
        defaultSubcommand: Serve.self
    )
}

/// A configuration problem: reported as "Error: …" without the usage text.
struct CLIError: Error, CustomStringConvertible {
    let description: String
    init(_ error: Any) { description = "\(error)" }
}

enum TransportChoice: String, ExpressibleByArgument, CaseIterable {
    case stdio, sse
    case streamableHTTP = "streamable-http"
}

/// Async: the Swift runtime drives the main thread's CFRunLoop while the root task runs here, so
/// NSWorkspace updates and `MainActor` work are delivered without a hand-rolled run loop.
struct Serve: AsyncParsableCommand {
    static let configuration = CommandConfiguration(
        abstract: "Run the MCP server. The default command.",
        discussion: "Values not given here come from the config file (~/.desktop-mcp/config.toml), then defaults.")

    @Option(help: "The transport layer used by the MCP server. (default: stdio)")
    var transport: TransportChoice?

    @Option(help: "Host to bind the SSE/Streamable HTTP server. (default: localhost)")
    var host: String?

    @Option(help: "Port to bind the SSE/Streamable HTTP server. (default: 8000)")
    var port: Int?

    @Flag(help: "Enable verbose logging for troubleshooting.")
    var debug = false

    @Option(help: "Path to desktop-mcp config file (TOML).")
    var config: String?

    @Option(name: .customLong("auth-key"),
            help: ArgumentHelp("Bearer token required on all HTTP requests.", discussion: "Or $DESKTOP_MCP_AUTH_KEY."))
    var authKey: String?

    @Flag(name: .customLong("allow-insecure-remote"),
          help: "Allow binding to non-loopback addresses without authentication (not recommended).")
    var allowInsecureRemote = false

    @Option(name: .customLong("ip-allowlist"),
            help: ArgumentHelp("Comma-separated client IPs or CIDR ranges (IPv4/IPv6).", discussion: "Or $DESKTOP_MCP_IP_ALLOWLIST."))
    var ipAllowlist: String?

    @Option(name: .customLong("ssl-certfile"),
            help: ArgumentHelp("TLS certificate (.pem) for HTTPS. Requires --ssl-keyfile.", discussion: "Or $DESKTOP_MCP_SSL_CERTFILE."))
    var sslCertfile: String?

    @Option(name: .customLong("ssl-keyfile"),
            help: ArgumentHelp("TLS private key (.pem) for HTTPS. Requires --ssl-certfile.", discussion: "Or $DESKTOP_MCP_SSL_KEYFILE."))
    var sslKeyfile: String?

    @Option(name: .customLong("oauth-client-id"),
            help: ArgumentHelp("OAuth client ID (pre-provisioned confidential client).", discussion: "Or $DESKTOP_MCP_OAUTH_CLIENT_ID."))
    var oauthClientID: String?

    @Option(name: .customLong("oauth-client-secret"),
            help: ArgumentHelp("OAuth client secret.", discussion: "Or $DESKTOP_MCP_OAUTH_CLIENT_SECRET."))
    var oauthClientSecret: String?

    @Option(name: .customLong("exclude-tools"),
            help: ArgumentHelp("Comma-separated tool names to disable, e.g. \"Shell,Scrape\".",
                               discussion: "Added to [tools] exclude. Or $DESKTOP_MCP_EXCLUDE_TOOLS."))
    var excludeTools: String?

    @Option(name: .customLong("max-timeout"),
            help: ArgumentHelp("Upper bound in seconds for Shell timeouts (0 = no cap).",
                               discussion: "Or $\(Shell.maxTimeoutEnv); default 240."))
    var maxTimeout: Int?

    func validate() throws {
        if let maxTimeout, maxTimeout < 0 { throw ValidationError("--max-timeout must be 0 or greater.") }
        if let port, !(0...65535).contains(port) { throw ValidationError("--port must be between 0 and 65535.") }
    }

    func plan() throws -> ServePlan {
        let fileConfig: DesktopMCPConfig
        do {
            fileConfig = try DesktopMCPConfig.load(DesktopMCPConfig.discover(explicit: config))
        } catch {
            throw CLIError(error)
        }
        var values = ServePlan.CommandLineValues()
        values.transport = transport?.rawValue
        values.host = host
        values.port = port
        values.authKey = authKey
        values.allowInsecureRemote = allowInsecureRemote
        values.ipAllowlist = ipAllowlist
        values.sslCertfile = sslCertfile
        values.sslKeyfile = sslKeyfile
        values.oauthClientID = oauthClientID
        values.oauthClientSecret = oauthClientSecret
        values.excludeTools = excludeTools
        do {
            return try ServePlan.resolve(values, environment: ProcessInfo.processInfo.environment, config: fileConfig)
        } catch {
            throw CLIError(error)
        }
    }

    func run() async throws {
        let plan = try plan()
        plan.warnings.forEach { log($0) }
        if let maxTimeout { setenv(Shell.maxTimeoutEnv, String(maxTimeout), 1) }

        // Without Accessibility only Shell/Scrape/Wait/Notification work; keep serving so the
        // client gets useful errors instead of a dead server, and let the system prompt once.
        _ = Permissions.checkAccessibility(prompt: true)
        // A write to a closed stdio pipe or socket must surface as EPIPE, not kill the process
        // before telemetry is flushed.
        signal(SIGPIPE, SIG_IGN)
        // Bound every AX call so one hung application cannot stall a request indefinitely.
        AX.setMessagingTimeout(AX.systemWide, 2.0)

        let analytics = PostHogAnalytics.isEnabled() ? PostHogAnalytics(verbose: debug) : nil
        let debug = self.debug
        let stdio = plan.transport == "stdio"
        let registry = await DesktopMCP.makeRegistry(excluding: plan.excludedTools)
        await registry.setObserver(analytics)

        // This task is the root; everything else is a child of it: telemetry delivery, the server
        // (stdio or HTTP) and the signal watcher that stops it. The process exits by returning.
        let code = await withTaskGroup(of: Int32?.self) { group in
            if let analytics { group.addTask { await analytics.run(); return nil } }
            group.addTask {
                // The server, until its input or listener closes, it fails, or a signal arrives.
                await withTaskGroup(of: Int32.self) { race in
                    race.addTask {
                        if stdio {
                            await StdioServer.run(registry)
                            return 0
                        }
                        do {
                            try await serveHTTP(registry, plan: plan, debug: debug)
                            return 0
                        } catch is CancellationError {
                            return 0
                        } catch {
                            log("HTTP server failed: \(error)")
                            return 1
                        }
                    }
                    race.addTask { await Signals.first() }
                    let code = await race.next() ?? 0
                    race.cancelAll()  // the first to finish ends the other
                    return code
                }
            }
            var code: Int32 = 0
            while let result = await group.next() {
                if let result { code = result; break }
            }
            // The server has stopped and its event loops are shut down: let queued telemetry
            // land (bounded), then let go of whatever is still stuck.
            await analytics?.close()
            group.cancelAll()
            return code
        }
        if code != 0 { throw ExitCode(code) }
    }

    func serveHTTP(_ registry: ToolRegistry, plan: ServePlan, debug: Bool) async throws {
        var settings = HTTPApp.Settings(authKey: plan.authKey,
                                        ipAllowlist: plan.ipAllowlist.isEmpty ? nil : plan.ipAllowlist)
        if let oauth = plan.oauth {
            settings.oauth = OAuthService(issuer: "\(plan.scheme)://\(plan.host):\(plan.port)",
                                          clientID: oauth.clientID, clientSecret: oauth.clientSecret)
        }
        let app = HTTPApp(registry: registry, settings: settings)
        let tls = plan.tls.map { NIOHTTPServer.TLSFiles(certificate: $0.certificate, privateKey: $0.privateKey) }
        let listening = try await NIOHTTPServer.bind(host: plan.host, port: plan.port, tls: tls)
        log("Serving streamable-http on \(plan.scheme)://\(plan.host):\(listening.port ?? plan.port)/mcp"
            + (plan.tls != nil ? " (TLS; HTTP/2 or HTTP/1.1 via ALPN)" : "")
            + (plan.authKey != nil ? " (auth key required)" : "")
            + (plan.oauth != nil ? " (OAuth enabled)" : ""))
        if plan.transport == "sse" {
            log("Warning: the HTTP+SSE transport is deprecated since MCP 2025-03-26 and may be removed; "
                + "switch clients to streamable-http (POST /mcp).")
        }
        if debug { log("IP allowlist: \(plan.ipAllowlist.isEmpty ? "none" : plan.ipAllowlist.map(\.description).joined(separator: ", "))") }
        try await NIOHTTPServer.serve(listening, app: app)
    }
}

/// SIGINT and SIGTERM, as the exit codes they stand for (130 and 0, as the original's uvicorn).
enum Signals {
    /// Waits for the first signal. Both are handled here while waiting; afterwards they get
    /// their default disposition back, so a second one ends the process the ordinary way.
    static func first() async -> Int32 {
        let signals = AsyncStream<Int32> { continuation in
            let sources = [(SIGINT, Int32(130)), (SIGTERM, Int32(0))].map { signo, code in
                signal(signo, SIG_IGN)
                let source = DispatchSource.makeSignalSource(signal: signo, queue: .global())
                source.setEventHandler { continuation.yield(code) }
                source.resume()
                return source
            }
            continuation.onTermination = { _ in
                sources.forEach { $0.cancel() }
                signal(SIGINT, SIG_DFL)
                signal(SIGTERM, SIG_DFL)
            }
        }
        for await code in signals { return code }
        return 0  // cancelled: the server stopped on its own
    }
}

struct Auth: ParsableCommand {
    static let configuration = CommandConfiguration(
        abstract: "Generate an auth key (and optionally TLS certs) and save to ~/.desktop-mcp/config.toml.")

    @Option(help: "Transport mode to configure. Saved to config.toml. (sse is deprecated since MCP 2025-03-26)")
    var transport: TransportChoice = .streamableHTTP

    @Option(help: "Host to bind the server to.")
    var host = "0.0.0.0"

    @Option(help: "Port to bind the server to.")
    var port = 8000

    @Flag(name: .customLong("with-tls"), help: "Generate a TLS certificate and key (mkcert if installed, else openssl).")
    var withTLS = false

    @Flag(help: "Overwrite existing credentials without prompting.")
    var force = false

    func run() throws {
        let path = DesktopMCPConfig.defaultFile
        var config: DesktopMCPConfig
        do {
            config = FileManager.default.fileExists(atPath: path.path) ? try DesktopMCPConfig.load(path) : DesktopMCPConfig()
        } catch {
            throw CLIError(error)
        }
        if config.server.authKey != nil && !force {
            print("Auth key already set in \(path.path). Use --force to regenerate.")
            return
        }
        if withTLS && transport == .stdio {
            throw ValidationError("TLS has no effect on stdio transport.")
        }

        let key = SecurityUtil.tokenURLSafe(32)
        config.server.authKey = key
        config.server.transport = transport.rawValue
        config.server.host = host
        config.server.port = port
        print("Generated auth key: \(key)")

        if withTLS {
            let cert = DesktopMCPConfig.directory.appendingPathComponent("cert.pem").path
            let privateKey = DesktopMCPConfig.directory.appendingPathComponent("key.pem").path
            try FileManager.default.createDirectory(at: DesktopMCPConfig.directory, withIntermediateDirectories: true)
            try TLSGenerator.generate(host: host, certificate: cert, privateKey: privateKey)
            config.server.sslCertfile = cert
            config.server.sslKeyfile = privateKey
        }
        try config.write(to: path)
        print("\nSaved to \(path.path)")

        let executable = Bundle.main.executablePath ?? "desktop-mcp"
        if transport == .stdio {
            print("\n─── Claude Desktop config (stdio) ───")
            print(clientConfig(ClientConfig.Server(command: executable, args: ["serve"])))
            return
        }
        let scheme = withTLS ? "https" : "http"
        print("\n─── Start the server ───")
        print("  \(executable) serve")
        let headers = ["Authorization": "Bearer \(key)"]
        if transport == .sse {
            print("\n─── Client config (SSE) ───")
            print(clientConfig(ClientConfig.Server(type: "sse", url: "\(scheme)://\(host):\(port)/sse", headers: headers)))
        } else {
            print("\n─── Client config (Streamable HTTP) ───")
            print(clientConfig(ClientConfig.Server(type: "http", url: "\(scheme)://\(host):\(port)/mcp/", headers: headers)))
            print(Install.claudeDesktopNote(executable: executable, url: "\(scheme)://\(host):\(port)/mcp/", authKey: key))
        }
    }

    func clientConfig(_ server: ClientConfig.Server) -> String {
        JSONCoding.encodeString(ClientConfig(mcpServers: ["desktop-mcp": server]), pretty: true)
    }
}

/// The `mcpServers` snippet MCP clients (Claude Desktop, Claude Code, Cursor) read.
struct ClientConfig: Encodable {
    struct Server: Encodable {
        var type: String?
        var command: String?
        var args: [String]?
        var url: String?
        var headers: [String: String]?
        var env: [String: String]?
    }

    var mcpServers: [String: Server]
}

/// Self-signed (openssl, part of macOS) or locally trusted (mkcert, if installed) certificates.
enum TLSGenerator {
    static func generate(host: String, certificate: String, privateKey: String) throws {
        // The key file must never be readable by others, not even briefly.
        let previousMask = umask(0o077)
        defer { umask(previousMask) }
        if let mkcert = which("mkcert") {
            print("mkcert detected — generating a locally-trusted certificate...")
            try run(mkcert, ["-install"], failure: "mkcert -install failed")
            let names = ["0.0.0.0", ""].contains(host) ? ["localhost", "127.0.0.1", "::1"] : [host]
            try run(mkcert, ["-cert-file", certificate, "-key-file", privateKey] + names, failure: "mkcert failed")
            print("  Certificate is automatically trusted by macOS.")
        } else {
            print("mkcert not found — falling back to openssl (self-signed)...")
            print("  Tip: brew install mkcert  for auto-trusted certs next time.")
            try run("/usr/bin/openssl", ["req", "-x509", "-newkey", "rsa:4096", "-keyout", privateKey, "-out", certificate,
                                         "-days", "365", "-nodes", "-subj", "/CN=\(host.isEmpty ? "desktop-mcp" : host)"],
                    failure: "openssl failed")
            print("  To make macOS trust this cert, run:")
            print("    sudo security add-trusted-cert -d -r trustRoot \\")
            print("      -k /Library/Keychains/System.keychain \(certificate)")
        }
        chmod(privateKey, 0o600)
        print("  cert → \(certificate)")
        print("  key  → \(privateKey)")
    }

    static func which(_ tool: String) -> String? {
        let paths = (ProcessInfo.processInfo.environment["PATH"] ?? "") + ":/opt/homebrew/bin:/usr/local/bin"
        return paths.split(separator: ":").map { "\($0)/\(tool)" }.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static func run(_ tool: String, _ arguments: [String], failure: String) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        let errors = Pipe()
        process.standardError = errors
        process.standardOutput = FileHandle.nullDevice
        try process.run()
        // Drain stderr before waiting, or a chatty tool blocks on a full pipe and never exits.
        let message = String(decoding: errors.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        process.waitUntilExit()
        if process.terminationStatus != 0 {
            throw CLIError("\(failure):\n\(message.trimmingCharacters(in: .whitespacesAndNewlines))")
        }
    }
}

struct Permissions: ParsableCommand {
    static let configuration = CommandConfiguration(
        abstract: "Check, and request, Accessibility and Screen Recording access.")

    static func checkAccessibility(prompt: Bool) -> Bool {
        let granted = AX.isTrusted(prompt: prompt)
        if !granted {
            log("Accessibility permission is not granted. Grant it to the app that launched desktop-mcp "
                + "(or to desktop-mcp itself when it runs as a Launch Agent) in "
                + "System Settings > Privacy & Security > Accessibility, then restart that app.")
        }
        return granted
    }

    func run() throws {
        let accessibility = Self.checkAccessibility(prompt: true)
        let screen = Screenshot.ensurePermission()
        print("Accessibility:    \(accessibility ? "granted" : "NOT granted")")
        print("Screen Recording: \(screen ? "granted" : "NOT granted (needed only for Snapshot use_vision)")")
        if !accessibility { throw ExitCode.failure }
    }
}
