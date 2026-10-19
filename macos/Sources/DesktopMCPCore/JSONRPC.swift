import Foundation
import MCP

// The JSON-RPC 2.0 and MCP message shapes, as Codable types. Decoding classifies each incoming
// message (request, notification, response, or invalid) and gives the parameters of the methods
// this server implements a typed shape; everything else stays a `JSONValue`.

// MARK: Incoming

/// One body as received: a single message or a batch.
public enum JSONRPCIncoming: Decodable, Sendable {
    case single(JSONRPCMessage)
    case batch([JSONRPCMessage])

    public init(from decoder: any Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let batch = try? container.decode([JSONRPCMessage].self) {
            self = .batch(batch)
        } else {
            self = .single(try container.decode(JSONRPCMessage.self))
        }
    }

    public var messages: [JSONRPCMessage] {
        switch self {
        case .single(let message): return [message]
        case .batch(let messages): return messages
        }
    }

    public var isBatch: Bool {
        if case .batch = self { return true }
        return false
    }
}

/// One JSON-RPC message, classified. Decoding never fails: anything that is not a well-formed
/// request, notification or response becomes `.invalid`, which earns an Invalid Request error.
public enum JSONRPCMessage: Decodable, Sendable {
    case request(JSONRPCRequest)
    case notification(JSONRPCRequest)
    /// A reply from the client; nothing to do with it.
    case response
    case invalid(id: JSONValue)

    enum CodingKeys: String, CodingKey {
        case jsonrpc, id, method, params, result, error
    }

    public init(from decoder: any Decoder) throws {
        guard let container = try? decoder.container(keyedBy: CodingKeys.self) else {
            self = .invalid(id: .null)
            return
        }
        // `id: null` is present (and answered with a null id); an absent id makes a notification.
        // (Spelled out: a `nil` literal here would become `JSONValue.null`, not an absent id.)
        var id: JSONValue? = .none
        if container.contains(.id) { id = try container.decode(JSONValue.self, forKey: .id) }
        guard container.contains(.method) else {
            self = container.contains(.result) || container.contains(.error) ? .response : .invalid(id: id ?? .null)
            return
        }
        guard let method = try? container.decode(String.self, forKey: .method) else {
            self = .invalid(id: id ?? .null)
            return
        }
        let request = JSONRPCRequest(id: id, method: method, params: RequestParams(method: method, in: container))
        self = id == nil ? .notification(request) : .request(request)
    }

    /// The request or notification, if that is what this is.
    public var request: JSONRPCRequest? {
        switch self {
        case .request(let request), .notification(let request): return request
        default: return nil
        }
    }

    /// Whether a response is due: requests get one, and so does anything malformed.
    public var needsResponse: Bool {
        switch self {
        case .request, .invalid: return true
        case .notification, .response: return false
        }
    }
}

public struct JSONRPCRequest: Sendable {
    public var id: JSONValue?
    public var method: String
    public var params: RequestParams
}

/// The parameters of a request, typed for the methods this server implements.
public enum RequestParams: Sendable {
    case initialize(InitializeParams)
    case toolCall(ToolCallParams)
    case cancelled(CancelledParams)
    /// Anything else, as received.
    case other(JSONValue?)
    /// Present but not the shape the method requires (an Invalid Params error).
    case invalid(String)

    init(method: String, in container: KeyedDecodingContainer<JSONRPCMessage.CodingKeys>) {
        do {
            switch method {
            case "initialize":
                self = .initialize(try container.decodeIfPresent(InitializeParams.self, forKey: .params) ?? InitializeParams())
            case "tools/call":
                self = .toolCall(try container.decodeIfPresent(ToolCallParams.self, forKey: .params) ?? ToolCallParams())
            case "notifications/cancelled":
                self = .cancelled(try container.decode(CancelledParams.self, forKey: .params))
            default:
                self = .other(try container.decodeIfPresent(JSONValue.self, forKey: .params))
            }
        } catch {
            self = .invalid(Self.describe(error))
        }
    }

    /// "params.name: Expected to decode String but found a number instead."
    static func describe(_ error: any Error) -> String {
        guard let error = error as? DecodingError else { return "\(error)" }
        let context: DecodingError.Context
        var path: [any CodingKey]
        switch error {
        case .typeMismatch(_, let c), .valueNotFound(_, let c), .dataCorrupted(let c):
            context = c
            path = c.codingPath
        case .keyNotFound(let key, let c):
            context = c
            path = c.codingPath + [key]
        @unknown default:
            return "\(error)"
        }
        path.removeAll { $0.stringValue == "params" && $0.intValue == nil }
        let location = (["params"] + path.map(\.stringValue)).joined(separator: ".")
        return "\(location): \(context.debugDescription)"
    }
}

public struct InitializeParams: Decodable, Sendable {
    public var protocolVersion: String?
    public var clientInfo: ClientInfo?

    public init(protocolVersion: String? = nil, clientInfo: ClientInfo? = nil) {
        self.protocolVersion = protocolVersion
        self.clientInfo = clientInfo
    }
}

/// The client named in `initialize`, carried along for telemetry.
public struct ClientInfo: Codable, Equatable, Sendable {
    public var name: String
    public var version: String

    public init(name: String, version: String) {
        self.name = name
        self.version = version
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        name = try container.decode(String.self, forKey: .name)
        version = try container.decodeIfPresent(String.self, forKey: .version) ?? ""
    }
}

public struct ToolCallParams: Decodable, Sendable {
    public var name: String?
    public var arguments: [String: JSONValue]?

    public init(name: String? = nil, arguments: [String: JSONValue]? = nil) {
        self.name = name
        self.arguments = arguments
    }
}

public struct CancelledParams: Decodable, Sendable {
    public var requestId: JSONValue
    public var reason: String?
}

// MARK: Outgoing

public struct JSONRPCError: Codable, Equatable, Sendable {
    public var code: Int
    public var message: String

    public init(code: Int, message: String) {
        self.code = code
        self.message = message
    }
}

/// A response to one request: a result of whatever shape the method defines, or an error.
public struct JSONRPCResponse: Encodable, Sendable {
    public enum Outcome: Sendable {
        case result(any Encodable & Sendable)
        case error(JSONRPCError)
    }

    public var id: JSONValue
    public var outcome: Outcome

    public init(id: JSONValue, result: any Encodable & Sendable) {
        self.id = id
        self.outcome = .result(result)
    }

    public init(id: JSONValue, error: JSONRPCError) {
        self.id = id
        self.outcome = .error(error)
    }

    public init(id: JSONValue, code: Int, message: String) {
        self.init(id: id, error: JSONRPCError(code: code, message: message))
    }

    enum CodingKeys: String, CodingKey {
        case jsonrpc, id, result, error
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode("2.0", forKey: .jsonrpc)
        try container.encode(id, forKey: .id)
        switch outcome {
        case .result(let result): try container.encode(result, forKey: .result)
        case .error(let error): try container.encode(error, forKey: .error)
        }
    }

    public func serialized() -> String {
        JSONCoding.encodeString(self)
    }
}

public struct InitializeResult: Encodable, Sendable {
    public struct ServerInfo: Encodable, Sendable {
        public var name: String
        public var version: String
    }

    public struct Capabilities: Encodable, Sendable {
        public struct Tools: Encodable, Sendable {
            public var listChanged = false
        }

        public var tools = Tools()
    }

    public var protocolVersion: String
    public var capabilities = Capabilities()
    public var serverInfo: ServerInfo
    public var instructions: String
}

public struct ToolsListResult: Encodable, Sendable {
    public var tools: [ToolDescriptor]
}

/// A tool as advertised by `tools/list`.
public struct ToolDescriptor: Encodable, Sendable {
    public var name: String
    public var description: String
    public var inputSchema: JSONValue
    public var annotations: ToolAnnotations
}

public struct ToolCallResult: Encodable, Sendable {
    public var content: [MCP.Tool.Content]
    public var isError: Bool
}

/// `{}`, the result of `ping`.
public struct EmptyResult: Encodable, Sendable {
    public init() {}
}
