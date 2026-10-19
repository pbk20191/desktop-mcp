import Darwin
import Foundation
import Security

/// An IPv4 or IPv6 network (an address with a prefix length).
public struct IPNetwork: Equatable, Sendable, CustomStringConvertible {
    let bytes: [UInt8]
    let prefix: Int

    public var description: String {
        "\(IPNetwork.format(bytes))/\(prefix)"
    }

    /// "10.0.0.0/8", "192.168.1.5" (a /32), "2001:db8::/32" or "::1" (a /128).
    public init(_ text: String) throws {
        let parts = text.split(separator: "/", maxSplits: 1).map(String.init)
        guard let address = IPNetwork.parseAddress(parts[0]) else {
            throw ConfigErrorText("'\(text)' does not appear to be an IPv4 or IPv6 address or network")
        }
        let maxPrefix = address.count * 8
        var prefix = maxPrefix
        if parts.count == 2 {
            guard let p = Int(parts[1]), (0...maxPrefix).contains(p) else {
                throw ConfigErrorText("'\(text)' has an invalid prefix length")
            }
            prefix = p
        }
        self.bytes = IPNetwork.mask(address, prefix)  // strict=False: host bits are cleared.
        self.prefix = prefix
    }

    public func contains(_ address: String) -> Bool {
        guard var candidate = IPNetwork.parseAddress(address) else { return false }
        // An IPv4 client seen through a dual-stack listener arrives as ::ffff:a.b.c.d.
        if bytes.count == 4, candidate.count == 16, candidate[0..<10].allSatisfy({ $0 == 0 }),
           candidate[10] == 0xff, candidate[11] == 0xff {
            candidate = Array(candidate[12...])
        }
        guard candidate.count == bytes.count else { return false }
        return IPNetwork.mask(candidate, prefix) == bytes
    }

    static func parseAddress(_ text: String) -> [UInt8]? {
        let bare = text.split(separator: "%").first.map(String.init) ?? text
        var v4 = in_addr()
        if inet_pton(AF_INET, bare, &v4) == 1 { return withUnsafeBytes(of: v4) { Array($0) } }
        var v6 = in6_addr()
        if inet_pton(AF_INET6, bare, &v6) == 1 { return withUnsafeBytes(of: v6) { Array($0) } }
        return nil
    }

    static func mask(_ bytes: [UInt8], _ prefix: Int) -> [UInt8] {
        bytes.enumerated().map { index, byte in
            let bits = max(0, min(8, prefix - index * 8))
            return bits == 8 ? byte : byte & UInt8(truncatingIfNeeded: 0xff << (8 - bits))
        }
    }

    static func format(_ bytes: [UInt8]) -> String {
        var buffer = [CChar](repeating: 0, count: Int(INET6_ADDRSTRLEN))
        let family = bytes.count == 4 ? AF_INET : AF_INET6
        _ = bytes.withUnsafeBytes { inet_ntop(family, $0.baseAddress, &buffer, socklen_t(buffer.count)) }
        return String(decoding: buffer.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self)
    }

    /// Parses every entry, reporting all bad ones at once.
    public static func parseAllowlist(_ entries: [String]) throws -> [IPNetwork] {
        var networks: [IPNetwork] = []
        var errors: [String] = []
        for entry in entries {
            let value = entry.trimmingCharacters(in: .whitespaces)
            if value.isEmpty { continue }
            do {
                networks.append(try IPNetwork(value))
            } catch {
                errors.append("'\(entry)': \(error)")
            }
        }
        if !errors.isEmpty { throw ConfigErrorText("Invalid IP allowlist entries: " + errors.joined(separator: "; ")) }
        return networks
    }
}

public struct ConfigErrorText: Error, CustomStringConvertible, Sendable {
    public let description: String
    public init(_ description: String) { self.description = description }
}

public enum SecurityUtil {
    /// True when `host` names only a loopback interface.
    public static func isLoopbackHost(_ host: String) -> Bool {
        if host.lowercased() == "localhost" { return true }
        guard let bytes = IPNetwork.parseAddress(host) else { return false }
        if bytes.count == 4 { return bytes[0] == 127 }
        return bytes[0..<15].allSatisfy { $0 == 0 } && bytes[15] == 1
    }

    /// Python's `secrets.token_urlsafe(n)`: n random bytes, base64url without padding.
    public static func tokenURLSafe(_ byteCount: Int = 32) -> String {
        var bytes = [UInt8](repeating: 0, count: byteCount)
        let status = SecRandomCopyBytes(kSecRandomDefault, byteCount, &bytes)
        precondition(status == errSecSuccess, "SecRandomCopyBytes failed")
        return base64URL(Data(bytes))
    }

    public static func base64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    /// Comparison whose running time does not depend on where the inputs differ.
    public static func constantTimeEquals(_ a: String, _ b: String) -> Bool {
        let x = Array(a.utf8), y = Array(b.utf8)
        var diff = UInt8(x.count == y.count ? 0 : 1)
        for i in 0..<max(x.count, y.count) {
            diff |= (i < x.count ? x[i] : 0) ^ (i < y.count ? y[i] : 0)
        }
        return diff == 0
    }
}
