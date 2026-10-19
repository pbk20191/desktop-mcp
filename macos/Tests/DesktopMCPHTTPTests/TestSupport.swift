import Foundation
import MCP

enum JSON {
    static func parse(_ text: String) throws -> Value { try JSONDecoder().decode(Value.self, from: Data(text.utf8)) }
    static func parse(_ data: Data) throws -> Value { try JSONDecoder().decode(Value.self, from: data) }
}

extension Value {
    subscript(key: String) -> Value? { objectValue?[key] }
}
