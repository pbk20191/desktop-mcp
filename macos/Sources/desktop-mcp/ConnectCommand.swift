import ArgumentParser
import Foundation
import DesktopMCPCore

/// `connect`: stdio in, HTTP out. What a client that can only spawn stdio servers (Claude
/// Desktop's config file) runs to reach the Launch Agent, so the agent, not the client, is the
/// process asking for Accessibility and Screen Recording.
struct Connect: AsyncParsableCommand {
    static let configuration = CommandConfiguration(
        abstract: "Bridge stdio to the running HTTP server, for clients that only spawn stdio servers.",
        discussion: """
        Without --url the installed Launch Agent's address is used (desktop-mcp install), and the \
        auth key comes from ~/.desktop-mcp/config.toml or $DESKTOP_MCP_AUTH_KEY. Each line on stdin \
        is posted to the server and every reply is written back as one line; the bridge itself \
        needs no permissions.
        """)

    @Option(help: "The streamable-http endpoint. (default: the installed Launch Agent's /mcp URL)")
    var url: String?

    @Option(name: .customLong("auth-key"),
            help: ArgumentHelp("Bearer token for the server.", discussion: "Or $DESKTOP_MCP_AUTH_KEY, else auth_key from config.toml."))
    var authKey: String?

    func run() async throws {
        let config = (try? DesktopMCPConfig.load(DesktopMCPConfig.discover(explicit: nil))) ?? DesktopMCPConfig()
        let fromEnvironment = ProcessInfo.processInfo.environment[ServePlan.environmentKeys.authKey]
        let key = [authKey, fromEnvironment, config.server.authKey].lazy.compactMap { $0 }.first { !$0.isEmpty }
        var target: StdioBridge.Target
        if let url {
            guard let parsed = URL(string: url), parsed.scheme != nil, parsed.host != nil else {
                throw ValidationError("--url must be an absolute http(s) URL.")
            }
            target = StdioBridge.Target(url: parsed, authKey: key)
        } else {
            guard let plist = try? Data(contentsOf: LaunchAgent.plistURL) else {
                throw CLIError("No Launch Agent is installed (\(LaunchAgent.plistURL.path)). Run `desktop-mcp install` first, or pass --url.")
            }
            do {
                target = try StdioBridge.Target.installed(plist: plist, config: config)
            } catch {
                throw CLIError(error)
            }
            target.authKey = key
        }
        // A write to a closed stdout must surface as EPIPE, not kill the process.
        signal(SIGPIPE, SIG_IGN)
        await StdioBridge(target: target).run(input: .standardInput) { line in
            await StdoutWriter.shared.write(line + "\n")
        }
    }
}
