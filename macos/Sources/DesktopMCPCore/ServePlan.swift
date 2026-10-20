import Foundation

/// The settings `serve` runs with, merged as in the original: a value given on the command line
/// (or through its environment variable) wins, then config.toml, then the default.
public struct ServePlan: Equatable, Sendable {
    public var transport: String
    public var host: String
    public var port: Int
    public var authKey: String?
    public var allowInsecureRemote: Bool
    public var ipAllowlist: [IPNetwork]
    public var tls: (certificate: String, privateKey: String)?
    public var oauth: (clientID: String, clientSecret: String)?
    public var excludedTools: [String]
    /// Opt-in tools to register (`exclude` still wins).
    public var includedTools: [String] = []
    /// Seconds an HTTP session may stay idle before it is dropped; 0 keeps sessions until the
    /// client ends them. Command line only.
    public var sessionTimeout: TimeInterval = 1800
    /// Unix socket to serve `/mcp` on as well (streamable-http only); command line only.
    public var socketPath: String?
    /// Non-fatal notes for the log.
    public var warnings: [String] = []

    public static let legacyTransportWarning = "transport \"sse\" is no longer supported; serving streamable-http (POST /mcp) instead. Re-run `desktop-mcp install` or `desktop-mcp auth` to update the stored setting."

    public static let environmentKeys = (
        authKey: "DESKTOP_MCP_AUTH_KEY",
        ipAllowlist: "DESKTOP_MCP_IP_ALLOWLIST",
        sslCertfile: "DESKTOP_MCP_SSL_CERTFILE",
        sslKeyfile: "DESKTOP_MCP_SSL_KEYFILE",
        oauthClientID: "DESKTOP_MCP_OAUTH_CLIENT_ID",
        oauthClientSecret: "DESKTOP_MCP_OAUTH_CLIENT_SECRET",
        excludeTools: "DESKTOP_MCP_EXCLUDE_TOOLS",
        includeTools: "DESKTOP_MCP_INCLUDE_TOOLS"
    )

    /// What was given on the command line; nil means "not given".
    public struct CommandLineValues: Sendable {
        public var transport: String?
        public var host: String?
        public var port: Int?
        public var authKey: String?
        public var allowInsecureRemote = false
        public var ipAllowlist: String?
        public var sslCertfile: String?
        public var sslKeyfile: String?
        public var oauthClientID: String?
        public var oauthClientSecret: String?
        public var excludeTools: String?
        public var includeTools: String?
        public var sessionTimeout: Int?
        public var socketPath: String?

        public init() {}
    }

    public static func resolve(_ cli: CommandLineValues, environment env: [String: String],
                               config: DesktopMCPConfig) throws -> ServePlan {
        let keys = environmentKeys
        // An empty value, wherever it comes from, means "not set".
        func pick(_ flag: String?, _ envKey: String, _ fromConfig: String?) -> String? {
            flag.flatMap(\.nilIfEmpty) ?? env[envKey].flatMap(\.nilIfEmpty) ?? fromConfig
        }
        func list(_ text: String) -> [String] {
            text.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        }

        var transport = cli.transport ?? config.server.transport
        var warnings = config.warnings
        if transport == "sse" {
            // A Launch Agent plist from an older version still passes `--transport sse`; rejecting
            // it would crash-loop the agent under KeepAlive.
            transport = "streamable-http"
            if !warnings.contains(legacyTransportWarning) { warnings.append(legacyTransportWarning) }
        }
        guard DesktopMCPConfig.transports.contains(transport) else {
            throw ConfigErrorText("Invalid transport: \(transport)")
        }
        let socketPath = cli.socketPath.flatMap(\.nilIfEmpty)
        if let socketPath {
            guard transport == "streamable-http" else {
                throw ConfigErrorText("--socket requires --transport streamable-http.")
            }
            try LaunchAgent.validateSocketPath(socketPath)
        }
        let host = cli.host ?? config.server.host
        let port = cli.port ?? config.server.port
        let authKey = pick(cli.authKey, keys.authKey, config.server.authKey)
        let allowInsecureRemote = cli.allowInsecureRemote || config.server.allowInsecureRemote
        let certificate = pick(cli.sslCertfile, keys.sslCertfile, config.server.sslCertfile)
        let privateKey = pick(cli.sslKeyfile, keys.sslKeyfile, config.server.sslKeyfile)
        let clientID = pick(cli.oauthClientID, keys.oauthClientID, config.security.oauthClientID)
        let clientSecret = pick(cli.oauthClientSecret, keys.oauthClientSecret, config.security.oauthClientSecret)
        let allowlistEntries = (cli.ipAllowlist.flatMap(\.nilIfEmpty) ?? env[keys.ipAllowlist].flatMap(\.nilIfEmpty))
            .map(list) ?? config.security.ipAllowlist
        var excluded = config.tools.exclude
        for name in (cli.excludeTools ?? env[keys.excludeTools]).map(list) ?? [] where !excluded.contains(name) {
            excluded.append(name)
        }
        var included = config.tools.include
        for name in (cli.includeTools ?? env[keys.includeTools]).map(list) ?? [] where !included.contains(name) {
            included.append(name)
        }

        guard (certificate == nil) == (privateKey == nil) else {
            throw ConfigErrorText("--ssl-certfile and --ssl-keyfile must be provided together.")
        }
        guard (clientID == nil) == (clientSecret == nil) else {
            throw ConfigErrorText("OAuth requires both --oauth-client-id and --oauth-client-secret.")
        }
        let allowlist: [IPNetwork]
        do {
            allowlist = try IPNetwork.parseAllowlist(allowlistEntries)
        } catch {
            throw ConfigErrorText("Invalid ip_allowlist: \(error)")
        }
        let oauth = clientID.flatMap { id in clientSecret.map { (id, $0) } }

        if transport != "stdio", !SecurityUtil.isLoopbackHost(host), authKey == nil, oauth == nil, !allowInsecureRemote {
            throw ConfigErrorText("""
            Refusing to bind HTTP transport to '\(host)' without authentication.
              Use --auth-key <token> or --oauth-client-id/--oauth-client-secret.
              Or pass --allow-insecure-remote to explicitly allow unauthenticated access (not recommended).
            """)
        }

        var plan = ServePlan(
            transport: transport, host: host, port: port, authKey: authKey,
            allowInsecureRemote: allowInsecureRemote, ipAllowlist: allowlist,
            tls: certificate.flatMap { c in privateKey.map { (c, $0) } },
            oauth: oauth, excludedTools: excluded)
        plan.includedTools = included
        plan.warnings = warnings
        plan.socketPath = socketPath
        if let sessionTimeout = cli.sessionTimeout { plan.sessionTimeout = TimeInterval(sessionTimeout) }
        if transport == "stdio" && (authKey != nil || !allowlist.isEmpty) {
            plan.warnings.append("auth_key / ip_allowlist have no effect on stdio transport")
        }
        return plan
    }

    public var scheme: String { tls == nil ? "http" : "https" }

    public static func == (lhs: ServePlan, rhs: ServePlan) -> Bool {
        lhs.transport == rhs.transport && lhs.host == rhs.host && lhs.port == rhs.port
            && lhs.authKey == rhs.authKey && lhs.allowInsecureRemote == rhs.allowInsecureRemote
            && lhs.ipAllowlist == rhs.ipAllowlist
            && lhs.tls?.certificate == rhs.tls?.certificate && lhs.tls?.privateKey == rhs.tls?.privateKey
            && lhs.oauth?.clientID == rhs.oauth?.clientID && lhs.oauth?.clientSecret == rhs.oauth?.clientSecret
            && lhs.excludedTools == rhs.excludedTools && lhs.includedTools == rhs.includedTools
            && lhs.sessionTimeout == rhs.sessionTimeout
            && lhs.socketPath == rhs.socketPath && lhs.warnings == rhs.warnings
    }
}
