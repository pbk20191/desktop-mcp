import ArgumentParser
import Foundation
import DesktopMCPCore

/// `install` / `uninstall`, as the original: a launchd Launch Agent that starts the HTTP server
/// at login and keeps it running. The pure parts (plist contents, port choice) are in
/// `LaunchAgent` in the core module and tested there; this file does the file system and
/// `launchctl` work.
struct Install: ParsableCommand {
    static let configuration = CommandConfiguration(
        abstract: "Install desktop-mcp as a launchd Launch Agent that starts at login.",
        discussion: """
        The agent runs `serve` over HTTP so any MCP client can connect to one server, and because \
        launchd starts it, desktop-mcp holds the Accessibility and Screen Recording permissions \
        itself: System Settings lists it under its own name instead of the app that spawned it. \
        Other serve settings (auth key, TLS, allowlist) still come from ~/.desktop-mcp/config.toml.
        """)

    @Option(help: "Transport for the background server; stdio is not supported as a service.")
    var transport: TransportChoice = .streamableHTTP

    @Option(help: "Host to bind.")
    var host = "127.0.0.1"

    @Option(help: "Port to bind. (default: 8000, or the next free port above it)")
    var port: Int?

    @Flag(help: "Reinstall even if already installed.")
    var force = false

    func validate() throws {
        if let port, !(0...65535).contains(port) { throw ValidationError("--port must be between 0 and 65535.") }
        do {
            try LaunchAgent.validateTransport(transport.rawValue)
        } catch {
            throw ValidationError("\(error)")
        }
    }

    func run() throws {
        let plistURL = LaunchAgent.plistURL
        if FileManager.default.fileExists(atPath: plistURL.path) && !force {
            print("Launch agent already installed at \(plistURL.path).")
            print("Use --force to reinstall.")
            return
        }

        // Auto-select a free port only when the user did not ask for one.
        var port = self.port ?? 8000
        if self.port == nil {
            let selected: Int
            do {
                selected = try LaunchAgent.findFreePort(host: host, preferred: port)
            } catch {
                throw CLIError(error)
            }
            if selected != port { print("Port \(port) is in use — using \(selected) instead.") }
            port = selected
        }

        let configDirectory = DesktopMCPConfig.directory
        try FileManager.default.createDirectory(at: configDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: plistURL.deletingLastPathComponent(), withIntermediateDirectories: true)

        let executable = Self.executablePath()
        let arguments = LaunchAgent.programArguments(executable: executable, transport: transport.rawValue,
                                                     host: host, port: port)
        try LaunchAgent.plist(programArguments: arguments, logDirectory: configDirectory).write(to: plistURL)
        print("Wrote \(plistURL.path)")

        let domain = "gui/\(getuid())"
        // Unload first if already running (needed for --force).
        _ = Launchctl.run("bootout", "\(domain)/\(LaunchAgent.label)")
        let result = Launchctl.run("bootstrap", domain, plistURL.path)
        guard result.status == 0 else {
            throw CLIError("launchctl bootstrap failed:\n\(result.stderr)")
        }

        let logs = LaunchAgent.logURLs(directory: configDirectory)
        print("Launch agent loaded — server is starting now.")
        print("  Transport : \(transport.rawValue)")
        print("  Address   : \(host):\(port)")
        print("  Logs      : \(logs.out.path)")
        print("\nThe server will restart automatically at every login.")
        print("Run `desktop-mcp uninstall` to remove it.")

        // What a client needs to connect. The agent reads config.toml too, so an auth key set by
        // `auth` applies and must be sent.
        let config = (try? DesktopMCPConfig.load(DesktopMCPConfig.discover(explicit: nil))) ?? DesktopMCPConfig()
        let scheme = config.server.sslCertfile != nil ? "https" : "http"
        let path = transport == .sse ? "/sse" : "/mcp/"
        let url = "\(scheme)://\(host):\(port)\(path)"
        let headers = config.server.authKey.map { ["Authorization": "Bearer \($0)"] }
        let server = ClientConfig.Server(type: transport == .sse ? "sse" : "http", url: url, headers: headers)
        print("\n─── Client config (\(transport == .sse ? "SSE" : "Streamable HTTP")) ───")
        print(JSONCoding.encodeString(ClientConfig(mcpServers: ["desktop-mcp": server]), pretty: true))
        print("\nClaude Code: claude mcp add --transport \(transport == .sse ? "sse" : "http") desktop-mcp \(url)"
              + (config.server.authKey.map { " --header \"Authorization: Bearer \($0)\"" } ?? ""))
        print(Self.claudeDesktopNote(executable: executable, url: nil, authKey: nil))
        print("\nPermissions: the agent asks for Accessibility on its first start; grant it to \"desktop-mcp\" in "
              + "System Settings > Privacy & Security > Accessibility (and > Screen Recording for Snapshot use_vision). "
              + "Run `desktop-mcp permissions` from a terminal only to check your terminal's own access.")
    }

    /// Claude Desktop's config file accepts stdio servers only (an HTTP entry is skipped as
    /// invalid), so it runs `connect`, which bridges to the HTTP server. With no URL the bridge
    /// finds the installed agent itself; `auth` has no agent to point at, so it spells it out.
    static func claudeDesktopNote(executable: String, url: String?, authKey: String?) -> String {
        var arguments = ["connect"]
        if let url { arguments += ["--url", url] }
        if let authKey { arguments += ["--auth-key", authKey] }
        let server = ClientConfig.Server(command: executable, args: arguments)
        return "\nClaude Desktop's claude_desktop_config.json takes stdio servers only; use the built-in bridge:\n"
            + JSONCoding.encodeString(ClientConfig(mcpServers: ["desktop-mcp": server]), pretty: true)
    }

    /// The path launchd will run: this binary, as it was invoked, made absolute. Not resolved
    /// through symlinks, so a link in ~/.local/bin keeps pointing at whatever it links to later.
    static func executablePath() -> String {
        let invoked = CommandLine.arguments.first ?? "desktop-mcp"
        if invoked.contains("/") {
            return URL(fileURLWithPath: invoked).standardizedFileURL.path
        }
        // Found through $PATH: ask the kernel where that was.
        return Bundle.main.executablePath ?? invoked
    }
}

struct Uninstall: ParsableCommand {
    static let configuration = CommandConfiguration(
        abstract: "Remove the desktop-mcp Launch Agent and stop the background server.")

    func run() throws {
        let plistURL = LaunchAgent.plistURL
        let result = Launchctl.run("bootout", "gui/\(getuid())/\(LaunchAgent.label)")
        if result.status == 0 {
            print("Stopped the running server.")
        } else if !FileManager.default.fileExists(atPath: plistURL.path) {
            print("No active launch agent found.")
        }
        if FileManager.default.fileExists(atPath: plistURL.path) {
            try FileManager.default.removeItem(at: plistURL)
            print("Removed \(plistURL.path)")
        } else {
            print("Plist already removed.")
        }
        print("desktop-mcp will no longer start at login.")
    }
}

enum Launchctl {
    struct Result {
        var status: Int32
        var stdout: String
        var stderr: String
    }

    static func run(_ arguments: String...) -> Result {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        process.arguments = arguments
        let out = Pipe(), err = Pipe()
        process.standardOutput = out
        process.standardError = err
        do {
            try process.run()
        } catch {
            return Result(status: -1, stdout: "", stderr: "\(error)")
        }
        // Drain both before waiting so a chatty launchctl cannot block on a full pipe.
        let stdout = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        let stderr = String(decoding: err.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        process.waitUntilExit()
        return Result(status: process.terminationStatus, stdout: stdout,
                      stderr: stderr.trimmingCharacters(in: .whitespacesAndNewlines))
    }
}
