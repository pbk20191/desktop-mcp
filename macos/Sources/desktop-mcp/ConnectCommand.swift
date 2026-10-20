import ArgumentParser
import Foundation
import DesktopMCPCore

/// `connect`: stdio in, the Launch Agent out. What a client that can only spawn stdio servers
/// (Claude Desktop's config file) runs to reach the agent, so the agent, not the client, is the
/// process asking for Accessibility and Screen Recording. It talks to the agent's Unix socket when
/// there is one, and posts to its HTTP endpoint otherwise.
struct Connect: AsyncParsableCommand {
    static let configuration = CommandConfiguration(
        abstract: "Bridge stdio to the running server, for clients that only spawn stdio servers.",
        discussion: """
        Without --url or --socket the installed Launch Agent is used (desktop-mcp install): its Unix \
        socket when it has one, with HTTP as the fallback if the socket refuses the connection, else \
        its HTTP address. Over the socket, stdin and stdout are piped to the server unchanged. Over \
        HTTP the auth key comes from ~/.desktop-mcp/config.toml or $DESKTOP_MCP_AUTH_KEY, each line on \
        stdin is posted to the server and every reply is written back as one line. The bridge itself \
        needs no permissions.
        """)

    @Option(help: "The streamable-http endpoint. (default: the installed Launch Agent's socket, else its /mcp URL)")
    var url: String?

    @Option(name: .customLong("socket"),
            help: "Connect over this Unix domain socket instead of HTTP (no fallback).")
    var socket: String?

    @Option(name: .customLong("auth-key"),
            help: ArgumentHelp("Bearer token for the server (HTTP only).", discussion: "Or $DESKTOP_MCP_AUTH_KEY, else auth_key from config.toml."))
    var authKey: String?

    func validate() throws {
        if url != nil, socket != nil {
            throw ValidationError("--url and --socket are exclusive.")
        }
    }

    func run() async throws {
        let config = (try? DesktopMCPConfig.load(DesktopMCPConfig.discover(explicit: nil))) ?? DesktopMCPConfig()
        let fromEnvironment = ProcessInfo.processInfo.environment[ServePlan.environmentKeys.authKey]
        let key = [authKey, fromEnvironment, config.server.authKey].lazy.compactMap { $0 }.first { !$0.isEmpty }
        var socketPath: String?
        var target: StdioBridge.Target?
        if let url {
            guard let parsed = URL(string: url), parsed.scheme != nil, parsed.host != nil else {
                throw ValidationError("--url must be an absolute http(s) URL.")
            }
            target = StdioBridge.Target(url: parsed, authKey: key)
        } else if let socket {
            do {
                try LaunchAgent.validateSocketPath(socket)
            } catch {
                throw CLIError(error)
            }
            socketPath = socket
        } else {
            guard let plist = try? Data(contentsOf: LaunchAgent.plistURL) else {
                throw CLIError("No Launch Agent is installed (\(LaunchAgent.plistURL.path)). Run `desktop-mcp install` first, or pass --url.")
            }
            do {
                switch try ConnectPlan.installed(plist: plist, config: config) {
                case .socket(let path, var fallback):
                    fallback.authKey = key
                    socketPath = path
                    target = fallback
                case .http(var http):
                    http.authKey = key
                    target = http
                }
            } catch {
                throw CLIError(error)
            }
        }
        // A write to a closed stdout must surface as EPIPE, not kill the process.
        signal(SIGPIPE, SIG_IGN)
        if let socketPath {
            do {
                let connection = try await SocketBridge(path: socketPath).connect()
                switch await connection.run(input: .standardInput, output: { await StdoutWriter.shared.write($0) }) {
                case .finished, .outputClosed:
                    return
                case .serverClosed:
                    log("desktop-mcp connect: the server closed the connection")
                    throw ExitCode(1)
                }
            } catch let refused as SocketBridge.Refused {
                guard let target else {
                    log("desktop-mcp connect: \(refused.reason)")
                    throw ExitCode(1)
                }
                log("desktop-mcp connect: \(refused.reason); using HTTP at \(target.url.absoluteString)")
            }
        }
        guard let target else { return }
        // False once stdout is gone: the bridge then cancels what it is still forwarding.
        await StdioBridge(target: target).run(input: .standardInput) { line in
            await StdoutWriter.shared.write(line + "\n")
        }
    }
}
