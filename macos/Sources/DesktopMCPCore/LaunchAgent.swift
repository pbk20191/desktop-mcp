import Foundation

/// The launchd Launch Agent that keeps the HTTP server running from login, as the original's
/// `install` / `uninstall`. Only the pure parts live here (what the plist says, which port to
/// use); the CLI writes the file and talks to `launchctl`.
///
/// Running as an agent is what lets desktop-mcp hold Accessibility and Screen Recording access
/// itself: launchd makes it the responsible process, so System Settings lists it under its own
/// name instead of attributing the access to whichever app spawned it.
public enum LaunchAgent {
    /// Also the code signing identifier, so TCC and launchd agree on who this is.
    public static let label = "com.desktop-mcp.server"
    /// Transports a background service can offer; stdio needs a parent process to talk to.
    public static let transports: Set<String> = ["sse", "streamable-http"]

    public static func plistURL(home: URL) -> URL {
        home.appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    /// `~/Library/LaunchAgents/com.desktop-mcp.server.plist`, honouring $HOME as the config dir does.
    public static var plistURL: URL {
        plistURL(home: DesktopMCPConfig.directory.deletingLastPathComponent())
    }

    public static func logURLs(directory: URL) -> (out: URL, error: URL) {
        (directory.appendingPathComponent("server.log"), directory.appendingPathComponent("server.error.log"))
    }

    public static func validateTransport(_ transport: String) throws {
        guard transports.contains(transport) else {
            throw ConfigErrorText(transport == "stdio"
                ? "stdio is not supported as a service; use --transport streamable-http (or sse)."
                : "Invalid transport: \(transport)")
        }
    }

    /// Everything explicit, so the agent does not depend on config.toml for where it listens.
    public static func programArguments(executable: String, transport: String, host: String, port: Int) -> [String] {
        [executable, "serve", "--transport", transport, "--host", host, "--port", String(port)]
    }

    /// XML plist with the same keys as the original (Label, ProgramArguments, RunAtLoad,
    /// KeepAlive, StandardOutPath, StandardErrorPath).
    public static func plist(programArguments: [String], logDirectory: URL) throws -> Data {
        let logs = logURLs(directory: logDirectory)
        let payload: [String: Any] = [
            "Label": label,
            "ProgramArguments": programArguments,
            "RunAtLoad": true,
            "KeepAlive": true,
            "StandardOutPath": logs.out.path,
            "StandardErrorPath": logs.error.path,
        ]
        return try PropertyListSerialization.data(fromPropertyList: payload, format: .xml, options: 0)
    }

    /// The first port at or above `preferred` that can be bound on `host`. Port 0 means "any",
    /// which is always free.
    public static func findFreePort(host: String, preferred: Int, maxTries: Int = 100) throws -> Int {
        for port in preferred..<(preferred + maxTries) where canBind(host: host, port: port) {
            return port
        }
        throw ConfigErrorText("No free port found in range \(preferred)–\(preferred + maxTries - 1) on \(host).")
    }

    static func canBind(host: String, port: Int) -> Bool {
        var hints = addrinfo()
        hints.ai_family = AF_UNSPEC
        hints.ai_socktype = SOCK_STREAM
        hints.ai_flags = AI_NUMERICSERV
        var list: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host == "localhost" ? "127.0.0.1" : host, String(port), &hints, &list) == 0, let first = list else {
            return false
        }
        defer { freeaddrinfo(list) }
        let fd = socket(first.pointee.ai_family, first.pointee.ai_socktype, first.pointee.ai_protocol)
        guard fd >= 0 else { return false }
        defer { close(fd) }
        return bind(fd, first.pointee.ai_addr, first.pointee.ai_addrlen) == 0
    }
}
