import Darwin
import Foundation

public enum Scrape {
    public static let maxChars = 20_000
    /// Download cap. Pages are truncated to `maxChars` of Markdown anyway, so a response that
    /// keeps streaming cannot fill memory.
    public static let maxDownloadBytes = 8 * 1024 * 1024
    static let userAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"

    /// Fetches a page and returns it as Markdown, truncated like the original. Fetch failures are
    /// returned as the content, as the original did; unsafe URLs throw.
    public static func run(_ url: String) async throws -> String {
        try await validate(url)
        var content = await fetchMarkdown(url)
        if content.count > maxChars {
            let omitted = content.count - maxChars
            content = String(content.prefix(maxChars))
                + "\n\n...[truncated — \(omitted) chars omitted. Scroll the page and scrape again to read more.]"
        }
        return "URL:\(url)\nContent:\n\(content)"
    }

    static func fetchMarkdown(_ url: String) async -> String {
        guard let target = URL(string: url) else { return "Invalid URL: \(url)" }
        var request = URLRequest(url: target, timeoutInterval: 10)
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        do {
            // Redirects are re-validated by the delegate: a public page must not be able to bounce
            // the fetch to a private address.
            let (bytes, response) = try await URLSession.shared.bytes(for: request, delegate: RedirectGuard())
            var data = Data()
            for try await byte in bytes {
                data.append(byte)
                if data.count >= maxDownloadBytes { break }
            }
            if let http = response as? HTTPURLResponse, http.statusCode >= 400 {
                let kind = http.statusCode < 500 ? "Client" : "Server"
                let reason = HTTPURLResponse.localizedString(forStatusCode: http.statusCode).capitalized
                return "\(http.statusCode) \(kind) Error: \(reason) for url: \(url)"
            }
            let html = decode(data, encodingName: response.textEncodingName)
            return HTMLToMarkdown.convert(html)
        } catch {
            return error.localizedDescription
        }
    }

    /// Applies `validate` to every redirect target; refused redirects end the fetch with the
    /// 3xx response itself.
    final class RedirectGuard: NSObject, URLSessionTaskDelegate {
        func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                        newRequest request: URLRequest) async -> URLRequest? {
            guard let url = request.url?.absoluteString else { return nil }
            do {
                try await Scrape.validate(url)
                return request
            } catch {
                log("Scrape: refused redirect to \(url): \(error)")
                return nil
            }
        }
    }

    static func decode(_ data: Data, encodingName: String?) -> String {
        if let encodingName {
            let cf = CFStringConvertIANACharSetNameToEncoding(encodingName as CFString)
            if cf != kCFStringEncodingInvalidId,
               let text = String(data: data, encoding: String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(cf))) {
                return text
            }
        }
        return String(data: data, encoding: .utf8) ?? String(decoding: data, as: UTF8.self)
    }

    // MARK: SSRF protection

    /// Rejects non-http(s) schemes, embedded credentials, and hosts resolving to private,
    /// loopback, link-local, multicast, reserved or unspecified addresses.
    public static func validate(_ url: String) async throws {
        guard let components = URLComponents(string: url) else {
            throw ToolError("Invalid URL: \(url)", label: "Invalid URL")
        }
        let scheme = components.scheme?.lowercased() ?? ""
        guard scheme == "http" || scheme == "https" else {
            throw ToolError("URL scheme '\(scheme)' is not allowed; use http or https.", label: "URL scheme not allowed")
        }
        guard let host = components.host, !host.isEmpty else {
            throw ToolError("URL has no hostname: \(url)", label: "URL has no hostname")
        }
        if components.user != nil || components.password != nil {
            throw ToolError("URLs with embedded credentials are not allowed.")
        }
        let addresses = try await resolve(host.trimmingCharacters(in: CharacterSet(charactersIn: "[]")))
        for address in addresses where isBlocked(address) {
            throw ToolError("Private, loopback, link-local, multicast, and reserved addresses are blocked: \(address)",
                            label: "Blocked address")
        }
    }

    static func resolve(_ host: String) async throws -> [String] {
        try await offload {
            var hints = addrinfo()
            hints.ai_socktype = SOCK_STREAM
            var result: UnsafeMutablePointer<addrinfo>?
            let rc = getaddrinfo(host, nil, &hints, &result)
            guard rc == 0, let first = result else {
                throw ToolError("Could not resolve hostname '\(host)': \(String(cString: gai_strerror(rc)))",
                                label: "Could not resolve hostname")
            }
            defer { freeaddrinfo(first) }
            var addresses: [String] = []
            var cursor: UnsafeMutablePointer<addrinfo>? = first
            while let info = cursor {
                var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                if getnameinfo(info.pointee.ai_addr, info.pointee.ai_addrlen, &buffer, socklen_t(buffer.count),
                               nil, 0, NI_NUMERICHOST) == 0 {
                    addresses.append(String(decoding: buffer.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self))
                }
                cursor = info.pointee.ai_next
            }
            return addresses
        }
    }

    /// Python's `is_private or is_loopback or is_link_local or is_multicast or is_reserved or
    /// is_unspecified`, for a numeric IPv4 or IPv6 address.
    public static func isBlocked(_ address: String) -> Bool {
        let bare = address.split(separator: "%").first.map(String.init) ?? address
        var v4 = in_addr()
        if inet_pton(AF_INET, bare, &v4) == 1 {
            return isBlockedV4(UInt32(bigEndian: v4.s_addr))
        }
        var v6 = in6_addr()
        guard inet_pton(AF_INET6, bare, &v6) == 1 else { return true }
        let b = withUnsafeBytes(of: v6) { Array($0) }
        if b.allSatisfy({ $0 == 0 }) { return true }                        // ::
        if b[0..<15].allSatisfy({ $0 == 0 }) && b[15] == 1 { return true }   // ::1
        if b[0..<10].allSatisfy({ $0 == 0 }) && b[10] == 0xff && b[11] == 0xff {
            // IPv4-mapped.
            return isBlockedV4(UInt32(b[12]) << 24 | UInt32(b[13]) << 16 | UInt32(b[14]) << 8 | UInt32(b[15]))
        }
        if b[0..<12].allSatisfy({ $0 == 0 }) { return true }                // ::/96 IPv4-compatible (reserved)
        if b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..<12].allSatisfy({ $0 == 0 }) {
            return true                                                     // 64:ff9b::/96 NAT64
        }
        if b[0] == 0xff { return true }                                     // multicast
        if b[0] == 0xfe && b[1] & 0xc0 == 0x80 { return true }              // fe80::/10 link-local
        if b[0] & 0xfe == 0xfc { return true }                              // fc00::/7 unique local
        if b[0] == 0x20 && b[1] == 0x01 && b[2] & 0xfe == 0x00 { return true }  // 2001::/23 IETF (Teredo, ORCHID, ...)
        if b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8 { return true }  // 2001:db8::/32
        if b[0] == 0x01 && b[1..<8].allSatisfy({ $0 == 0 }) { return true } // 100::/64 discard
        return false
    }

    static func isBlockedV4(_ ip: UInt32) -> Bool {
        func within(_ network: UInt32, _ bits: UInt32) -> Bool {
            let mask: UInt32 = bits == 0 ? 0 : ~UInt32(0) << (32 - bits)
            return ip & mask == network & mask
        }
        func net(_ a: UInt32, _ b: UInt32, _ c: UInt32, _ d: UInt32) -> UInt32 { a << 24 | b << 16 | c << 8 | d }
        let blocked: [(UInt32, UInt32)] = [
            (net(0, 0, 0, 0), 8),        // "this" network / unspecified
            (net(10, 0, 0, 0), 8),       // private
            (net(127, 0, 0, 0), 8),      // loopback
            (net(169, 254, 0, 0), 16),   // link-local
            (net(172, 16, 0, 0), 12),    // private
            (net(192, 0, 0, 0), 24),     // IETF protocol assignments
            (net(192, 0, 2, 0), 24),     // documentation
            (net(192, 168, 0, 0), 16),   // private
            (net(198, 18, 0, 0), 15),    // benchmarking
            (net(198, 51, 100, 0), 24),  // documentation
            (net(203, 0, 113, 0), 24),   // documentation
            (net(224, 0, 0, 0), 4),      // multicast
            (net(240, 0, 0, 0), 4),      // reserved, includes broadcast
        ]
        return blocked.contains { within($0.0, $0.1) }
    }
}
