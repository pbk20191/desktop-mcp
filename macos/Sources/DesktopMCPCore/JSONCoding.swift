import Foundation
import MCP

/// The one JSON encoder/decoder configuration for everything that goes on the wire: compact,
/// keys sorted (so output is deterministic), slashes and non-ASCII text unescaped, dates as
/// ISO 8601, `Data` as base64.
public enum JSONCoding {
    public static func makeEncoder(pretty: Bool = false) -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = pretty ? [.sortedKeys, .withoutEscapingSlashes, .prettyPrinted] : [.sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }

    public static func makeDecoder() -> JSONDecoder {
        JSONDecoder()
    }

    public static func encode<T: Encodable>(_ value: T, pretty: Bool = false) throws -> Data {
        try makeEncoder(pretty: pretty).encode(value)
    }

    /// Encoding our own message types cannot fail short of a programming error, which is logged
    /// rather than crashing the server.
    public static func encodeString<T: Encodable>(_ value: T, pretty: Bool = false) -> String {
        do {
            return String(decoding: try encode(value, pretty: pretty), as: UTF8.self)
        } catch {
            log("Could not encode \(T.self): \(error)")
            return "null"
        }
    }

    public static func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        try makeDecoder().decode(type, from: data)
    }

    public static func decode<T: Decodable>(_ type: T.Type, from text: String) throws -> T {
        try decode(type, from: Data(text.utf8))
    }
}

/// A JSON object that keeps insertion order, rendered like Python's `json.dumps`
/// (`{"key": value, "other": value}`). Snapshot metadata is text the model reads, formatted to
/// match the original server, so it does not go through the wire encoder.
public struct OrderedJSON: Equatable, Sendable {
    public private(set) var entries: [(key: String, value: Value)] = []

    public init() {}

    public var isEmpty: Bool { entries.isEmpty }

    public subscript(key: String) -> Value? {
        get { entries.first(where: { $0.key == key })?.value }
        set {
            if let i = entries.firstIndex(where: { $0.key == key }) {
                if let newValue { entries[i].value = newValue } else { entries.remove(at: i) }
            } else if let newValue {
                entries.append((key, newValue))
            }
        }
    }

    public func serialized() -> String {
        var out = "{"
        for (i, entry) in entries.enumerated() {
            if i > 0 { out += ", " }
            Self.write(.string(entry.key), to: &out)
            out += ": "
            Self.write(entry.value, to: &out)
        }
        return out + "}"
    }

    static func write(_ value: Value, to out: inout String) {
        switch value {
        case .null:
            out += "null"
        case .bool(let b):
            out += b ? "true" : "false"
        case .int(let i):
            out += String(i)
        case .double(let n):
            out += formatNumber(n)
        case .string(let s):
            out += JSONCoding.encodeString(s)
        case .data(let mimeType, let data):
            out += JSONCoding.encodeString(data.dataURLEncoded(mimeType: mimeType))
        case .array(let items):
            out += "["
            for (i, item) in items.enumerated() {
                if i > 0 { out += ", " }
                write(item, to: &out)
            }
            out += "]"
        case .object(let dict):
            out += "{"
            for (i, key) in dict.keys.sorted().enumerated() {
                if i > 0 { out += ", " }
                write(.string(key), to: &out)
                out += ": "
                write(dict[key]!, to: &out)
            }
            out += "}"
        }
    }

    /// Python's float formatting for the common cases: integral values without a fraction.
    static func formatNumber(_ n: Double) -> String {
        if n.isNaN || n.isInfinite { return "null" }
        if n == n.rounded(), abs(n) < 9_007_199_254_740_992 {
            return String(Int64(n))
        }
        return String(n)
    }

    public static func == (lhs: OrderedJSON, rhs: OrderedJSON) -> Bool {
        lhs.entries.count == rhs.entries.count
            && zip(lhs.entries, rhs.entries).allSatisfy { $0.key == $1.key && $0.value == $1.value }
    }
}
