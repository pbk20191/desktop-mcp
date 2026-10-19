import Foundation

/// A JSON document of unknown shape: tool arguments, input schemas, request ids. Messages with a
/// fixed shape are `Codable` types (see `JSONRPC.swift`) and only embed a `JSONValue` where the
/// protocol leaves the shape open. Objects are unordered; use `OrderedJSON` where key order is
/// visible to callers.
///
/// Integers keep their exact value (request ids may exceed 2^53); `1` and `1.0` compare equal.
public enum JSONValue: Sendable {
    case null
    case bool(Bool)
    case integer(Int64)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    public subscript(key: String) -> JSONValue? {
        if case .object(let dict) = self { return dict[key] }
        return nil
    }

    public var stringValue: String? {
        if case .string(let s) = self { return s }
        return nil
    }

    /// Any number, integral or not.
    public var doubleValue: Double? {
        switch self {
        case .integer(let i): return Double(i)
        case .number(let n): return n
        default: return nil
        }
    }

    /// An integer, or a number with no fractional part.
    public var intValue: Int? {
        switch self {
        case .integer(let i): return Int(exactly: i)
        case .number(let n): return n == n.rounded() ? Int(exactly: n) : nil
        default: return nil
        }
    }

    public var boolValue: Bool? {
        if case .bool(let b) = self { return b }
        return nil
    }

    public var arrayValue: [JSONValue]? {
        if case .array(let a) = self { return a }
        return nil
    }

    public var objectValue: [String: JSONValue]? {
        if case .object(let o) = self { return o }
        return nil
    }

    public var isNull: Bool {
        if case .null = self { return true }
        return false
    }

    // MARK: Parsing and serialisation (Foundation's JSON coders)

    public static func parse(_ data: Data) throws -> JSONValue {
        try JSONCoding.decode(JSONValue.self, from: data)
    }

    public static func parse(_ text: String) throws -> JSONValue {
        try parse(Data(text.utf8))
    }

    /// Compact JSON with sorted keys. Non-ASCII text is written as UTF-8 rather than \u escapes.
    public func serialized() -> String {
        JSONCoding.encodeString(self)
    }
}

extension JSONValue: Equatable {
    public static func == (lhs: JSONValue, rhs: JSONValue) -> Bool {
        switch (lhs, rhs) {
        case (.null, .null): return true
        case (.bool(let a), .bool(let b)): return a == b
        case (.integer(let a), .integer(let b)): return a == b
        case (.number(let a), .number(let b)): return a == b
        case (.integer(let a), .number(let b)), (.number(let b), .integer(let a)): return Double(a) == b
        case (.string(let a), .string(let b)): return a == b
        case (.array(let a), .array(let b)): return a == b
        case (.object(let a), .object(let b)): return a == b
        default: return false
        }
    }
}

extension JSONValue: Codable {
    public init(from decoder: any Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let b = try? container.decode(Bool.self) {
            self = .bool(b)
        } else if let i = try? container.decode(Int64.self) {
            self = .integer(i)
        } else if let n = try? container.decode(Double.self) {
            self = .number(n)
        } else if let s = try? container.decode(String.self) {
            self = .string(s)
        } else if let a = try? container.decode([JSONValue].self) {
            self = .array(a)
        } else if let o = try? container.decode([String: JSONValue].self) {
            self = .object(o)
        } else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Not a JSON value")
        }
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let b): try container.encode(b)
        case .integer(let i): try container.encode(i)
        case .number(let n): if n.isFinite { try container.encode(n) } else { try container.encodeNil() }
        case .string(let s): try container.encode(s)
        case .array(let a): try container.encode(a)
        case .object(let o): try container.encode(o)
        }
    }
}

extension JSONValue: ExpressibleByStringLiteral, ExpressibleByBooleanLiteral,
    ExpressibleByIntegerLiteral, ExpressibleByFloatLiteral, ExpressibleByArrayLiteral,
    ExpressibleByDictionaryLiteral, ExpressibleByNilLiteral
{
    public init(stringLiteral value: String) { self = .string(value) }
    public init(booleanLiteral value: Bool) { self = .bool(value) }
    public init(integerLiteral value: Int64) { self = .integer(value) }
    public init(floatLiteral value: Double) { self = .number(value) }
    public init(arrayLiteral elements: JSONValue...) { self = .array(elements) }
    public init(dictionaryLiteral elements: (String, JSONValue)...) {
        self = .object(Dictionary(elements, uniquingKeysWith: { _, last in last }))
    }
    public init(nilLiteral: ()) { self = .null }
}

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
    public private(set) var entries: [(key: String, value: JSONValue)] = []

    public init() {}

    public var isEmpty: Bool { entries.isEmpty }

    public subscript(key: String) -> JSONValue? {
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

    static func write(_ value: JSONValue, to out: inout String) {
        switch value {
        case .null:
            out += "null"
        case .bool(let b):
            out += b ? "true" : "false"
        case .integer(let i):
            out += String(i)
        case .number(let n):
            out += formatNumber(n)
        case .string(let s):
            out += JSONCoding.encodeString(s)
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
