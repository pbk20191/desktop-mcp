import Foundation

/// `~/.desktop-mcp/config.toml`, read and written in the same shape as the Python server's.
public struct DesktopMCPConfig: Equatable, Sendable {
    public struct Server: Equatable, Sendable {
        public var transport = "stdio"
        public var host = "localhost"
        public var port = 8000
        public var allowInsecureRemote = false
        public var authKey: String?
        public var sslCertfile: String?
        public var sslKeyfile: String?
    }

    public struct Security: Equatable, Sendable {
        public var ipAllowlist: [String] = []
        public var oauthClientID: String?
        public var oauthClientSecret: String?
    }

    public struct Tools: Equatable, Sendable {
        public var exclude: [String] = []
    }

    public var server = Server()
    public var security = Security()
    public var tools = Tools()
    public var sourcePath: String?
    /// Non-fatal notes from `load` (a stored value that was mapped rather than rejected).
    public var warnings: [String] = []

    public init() {}

    public static let transports: Set<String> = ["stdio", "streamable-http"]
    /// `~/.desktop-mcp`, honouring $HOME as Python's `expanduser` does.
    public static var directory: URL {
        let home = ProcessInfo.processInfo.environment["HOME"].flatMap(\.nilIfEmpty) ?? NSHomeDirectory()
        return URL(fileURLWithPath: home).appendingPathComponent(".desktop-mcp")
    }
    public static var defaultFile: URL { directory.appendingPathComponent("config.toml") }

    public struct ConfigError: Error, CustomStringConvertible, Sendable {
        public let description: String
        init(_ description: String) { self.description = description }
    }

    /// Explicit path, else `~/.desktop-mcp/config.toml` when it exists, else none.
    public static func discover(explicit: String?) -> URL? {
        if let explicit {
            return URL(fileURLWithPath: expandTilde(explicit))
        }
        return FileManager.default.fileExists(atPath: defaultFile.path) ? defaultFile : nil
    }

    /// Defaults when `url` is nil; otherwise the parsed and validated file.
    public static func load(_ url: URL?) throws -> DesktopMCPConfig {
        var config = DesktopMCPConfig()
        guard let url else { return config }
        guard FileManager.default.fileExists(atPath: url.path) else {
            throw ConfigError("Config file not found: \(url.path)")
        }
        let tables = try TOML.parse(try String(contentsOf: url, encoding: .utf8))
        let server = tables["server"] ?? [:]
        let security = tables["security"] ?? [:]
        let tools = tables["tools"] ?? [:]
        let base = url.deletingLastPathComponent()

        if let value = server["transport"] {
            var transport = value.text
            if transport == "sse" {
                // Written by an older version; the server now only speaks streamable-http.
                transport = "streamable-http"
                config.warnings.append(ServePlan.legacyTransportWarning)
            }
            guard transports.contains(transport) else {
                throw ConfigError("server.transport must be one of \(transports.sorted()), got '\(transport)'")
            }
            config.server.transport = transport
        }
        if let value = server["host"] { config.server.host = value.text }
        if let value = server["port"] {
            guard let port = value.integer else { throw ConfigError("server.port must be an integer") }
            config.server.port = port
        }
        if let value = server["allow_insecure_remote"] {
            guard case .bool(let flag) = value else {
                throw ConfigError("server.allow_insecure_remote must be a TOML boolean")
            }
            config.server.allowInsecureRemote = flag
        }
        if let value = server["auth_key"] { config.server.authKey = value.text.nilIfEmpty }
        // TLS paths are relative to the config file.
        if let value = server["ssl_certfile"], let path = value.text.nilIfEmpty {
            config.server.sslCertfile = resolve(path, against: base)
        }
        if let value = server["ssl_keyfile"], let path = value.text.nilIfEmpty {
            config.server.sslKeyfile = resolve(path, against: base)
        }
        if let value = security["ip_allowlist"] { config.security.ipAllowlist = try strings(value, "security.ip_allowlist") }
        if let value = security["oauth_client_id"] { config.security.oauthClientID = value.text.nilIfEmpty }
        if let value = security["oauth_client_secret"] { config.security.oauthClientSecret = value.text.nilIfEmpty }
        if let value = tools["exclude"] { config.tools.exclude = try strings(value, "tools.exclude") }
        config.sourcePath = url.path
        return config
    }

    /// `~` expanded against $HOME (NSString's version ignores it).
    static func expandTilde(_ path: String) -> String {
        guard path == "~" || path.hasPrefix("~/") else { return path }
        return directory.deletingLastPathComponent().path + path.dropFirst()
    }

    static func resolve(_ path: String, against base: URL) -> String {
        let expanded = expandTilde(path)
        return URL(fileURLWithPath: expanded, relativeTo: base).standardizedFileURL.path
    }

    static func strings(_ value: TOML.Value, _ key: String) throws -> [String] {
        guard case .array(let items) = value else { throw ConfigError("\(key) must be an array of strings") }
        return try items.map {
            guard case .string(let s) = $0 else { throw ConfigError("\(key) must be an array of strings") }
            return s
        }
    }

    /// TOML holding only the values that differ from the defaults.
    public func serialized() -> String {
        func quoted(_ s: String) -> String { TOML.quote(s) }
        var lines: [String] = []
        let defaults = Server()
        var server: [String] = []
        if self.server.transport != defaults.transport { server.append("transport = \(quoted(self.server.transport))") }
        if self.server.host != defaults.host { server.append("host = \(quoted(self.server.host))") }
        if self.server.port != defaults.port { server.append("port = \(self.server.port)") }
        if self.server.allowInsecureRemote { server.append("allow_insecure_remote = true") }
        if let v = self.server.authKey { server.append("auth_key = \(quoted(v))") }
        if let v = self.server.sslCertfile { server.append("ssl_certfile = \(quoted(v))") }
        if let v = self.server.sslKeyfile { server.append("ssl_keyfile = \(quoted(v))") }
        if !server.isEmpty { lines += ["[server]"] + server + [""] }

        var security: [String] = []
        if !self.security.ipAllowlist.isEmpty {
            security.append("ip_allowlist = [\(self.security.ipAllowlist.map(quoted).joined(separator: ", "))]")
        }
        if let v = self.security.oauthClientID { security.append("oauth_client_id = \(quoted(v))") }
        if let v = self.security.oauthClientSecret { security.append("oauth_client_secret = \(quoted(v))") }
        if !security.isEmpty { lines += ["[security]"] + security + [""] }

        if !tools.exclude.isEmpty {
            lines += ["[tools]", "exclude = [\(tools.exclude.map(quoted).joined(separator: ", "))]", ""]
        }
        return lines.joined(separator: "\n")
    }

    /// Writes the file owner-readable only; it holds secrets, so it is never world-readable, not
    /// even between creation and a later chmod.
    public func write(to url: URL) throws {
        let directory = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let data = Data(serialized().utf8)
        guard FileManager.default.createFile(atPath: url.path, contents: data, attributes: [.posixPermissions: 0o600]) else {
            throw ConfigError("Could not write \(url.path)")
        }
        chmod(url.path, 0o600)
    }
}

extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}

/// The subset of TOML the config uses: `[table]` headers, `key = value` with strings, integers,
/// booleans and (possibly multi-line) arrays of those, and `#` comments.
public enum TOML {
    public enum Value: Equatable, Sendable {
        case string(String)
        case integer(Int)
        case bool(Bool)
        case array([Value])

        /// Python's `str(value)` as the original applies it to string settings.
        var text: String {
            switch self {
            case .string(let s): return s
            case .integer(let n): return String(n)
            case .bool(let b): return b ? "True" : "False"
            case .array(let items): return "[" + items.map(\.text).joined(separator: ", ") + "]"
            }
        }

        var integer: Int? {
            switch self {
            case .integer(let n): return n
            case .string(let s): return Int(s)
            default: return nil
            }
        }
    }

    public struct ParseError: Error, CustomStringConvertible, Sendable {
        public let description: String
    }

    /// Tables keyed by name; top-level keys live under "".
    public static func parse(_ text: String) throws -> [String: [String: Value]] {
        var tables: [String: [String: Value]] = ["": [:]]
        var current = ""
        var pending = ""
        var pendingLine = 0

        for (offset, rawLine) in text.components(separatedBy: .newlines).enumerated() {
            let lineNumber = offset + 1
            let line = stripComment(rawLine).trimmingCharacters(in: .whitespaces)
            if !pending.isEmpty {
                pending += " " + line
                if bracketsBalanced(pending) {
                    try assign(pending, into: &tables, table: current, line: pendingLine)
                    pending = ""
                }
                continue
            }
            if line.isEmpty { continue }
            if line.hasPrefix("[") && !line.hasPrefix("[[") {
                guard line.hasSuffix("]") else { throw ParseError(description: "line \(lineNumber): malformed table header") }
                current = String(line.dropFirst().dropLast()).trimmingCharacters(in: .whitespaces)
                tables[current, default: [:]] = tables[current] ?? [:]
                continue
            }
            if !bracketsBalanced(line) {
                pending = line
                pendingLine = lineNumber
                continue
            }
            try assign(line, into: &tables, table: current, line: lineNumber)
        }
        if !pending.isEmpty { throw ParseError(description: "line \(pendingLine): unterminated array") }
        return tables
    }

    private static func assign(_ line: String, into tables: inout [String: [String: Value]], table: String, line number: Int) throws {
        guard let eq = line.firstIndex(of: "=") else { throw ParseError(description: "line \(number): expected key = value") }
        var key = line[..<eq].trimmingCharacters(in: .whitespaces)
        if key.count >= 2, key.first == "\"" || key.first == "'", key.last == key.first {
            key = String(key.dropFirst().dropLast())
        }
        guard !key.isEmpty else { throw ParseError(description: "line \(number): empty key") }
        var scanner = Scanner(String(line[line.index(after: eq)...]), line: number)
        let value = try scanner.value()
        scanner.skipSpaces()
        guard scanner.atEnd else { throw ParseError(description: "line \(number): unexpected text after value") }
        tables[table, default: [:]][key] = value
    }

    /// Drops a `#` comment that is not inside a string.
    static func stripComment(_ line: String) -> String {
        var quote: Character?
        var escaped = false
        for (i, c) in line.enumerated() {
            if let q = quote {
                if escaped { escaped = false } else if c == "\\" && q == "\"" { escaped = true } else if c == q { quote = nil }
            } else if c == "\"" || c == "'" {
                quote = c
            } else if c == "#" {
                return String(line.prefix(i))
            }
        }
        return line
    }

    static func bracketsBalanced(_ line: String) -> Bool {
        var depth = 0
        var quote: Character?
        var escaped = false
        for c in line {
            if let q = quote {
                if escaped { escaped = false } else if c == "\\" && q == "\"" { escaped = true } else if c == q { quote = nil }
            } else if c == "\"" || c == "'" {
                quote = c
            } else if c == "[" {
                depth += 1
            } else if c == "]" {
                depth -= 1
            }
        }
        return depth <= 0
    }

    public static func quote(_ s: String) -> String {
        var out = "\""
        for c in s.unicodeScalars {
            switch c {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\t": out += "\\t"
            case "\r": out += "\\r"
            default:
                if c.value < 0x20 { out += String(format: "\\u%04X", c.value) } else { out.unicodeScalars.append(c) }
            }
        }
        return out + "\""
    }

    struct Scanner {
        let chars: [Character]
        var i = 0
        let line: Int

        init(_ text: String, line: Int) {
            chars = Array(text)
            self.line = line
        }

        var atEnd: Bool { i >= chars.count }

        mutating func skipSpaces() {
            while i < chars.count, chars[i].isWhitespace { i += 1 }
        }

        func fail(_ message: String) -> ParseError { ParseError(description: "line \(line): \(message)") }

        mutating func value() throws -> Value {
            skipSpaces()
            guard i < chars.count else { throw fail("missing value") }
            switch chars[i] {
            case "\"": return .string(try basicString())
            case "'": return .string(try literalString())
            case "[": return try array()
            default: return try bare()
            }
        }

        mutating func basicString() throws -> String {
            i += 1
            var out = ""
            while i < chars.count {
                let c = chars[i]
                i += 1
                if c == "\"" { return out }
                if c == "\\" {
                    guard i < chars.count else { break }
                    let e = chars[i]
                    i += 1
                    switch e {
                    case "n": out += "\n"
                    case "t": out += "\t"
                    case "r": out += "\r"
                    case "\"": out += "\""
                    case "\\": out += "\\"
                    case "u", "U":
                        let length = e == "u" ? 4 : 8
                        guard i + length <= chars.count,
                              let code = UInt32(String(chars[i..<i + length]), radix: 16),
                              let scalar = Unicode.Scalar(code) else { throw fail("bad unicode escape") }
                        out.unicodeScalars.append(scalar)
                        i += length
                    default: throw fail("unknown escape \\\(e)")
                    }
                } else {
                    out.append(c)
                }
            }
            throw fail("unterminated string")
        }

        mutating func literalString() throws -> String {
            i += 1
            let start = i
            while i < chars.count, chars[i] != "'" { i += 1 }
            guard i < chars.count else { throw fail("unterminated string") }
            defer { i += 1 }
            return String(chars[start..<i])
        }

        mutating func array() throws -> Value {
            i += 1
            var items: [Value] = []
            while true {
                skipSpaces()
                guard i < chars.count else { throw fail("unterminated array") }
                if chars[i] == "]" {
                    i += 1
                    return .array(items)
                }
                items.append(try value())
                skipSpaces()
                guard i < chars.count else { throw fail("unterminated array") }
                if chars[i] == "," { i += 1 } else if chars[i] != "]" { throw fail("expected , or ]") }
            }
        }

        mutating func bare() throws -> Value {
            let start = i
            while i < chars.count, !chars[i].isWhitespace, chars[i] != ",", chars[i] != "]" { i += 1 }
            let token = String(chars[start..<i])
            if token == "true" { return .bool(true) }
            if token == "false" { return .bool(false) }
            if let n = Int(token.replacingOccurrences(of: "_", with: "")) { return .integer(n) }
            throw fail("unsupported value '\(token)'")
        }
    }
}
