import Foundation

/// How `connect` reaches the installed Launch Agent: over its Unix socket when the plist says it
/// serves one (the HTTP target stays as the fallback), over HTTP for an agent that predates it.
public enum ConnectPlan: Equatable, Sendable {
    case socket(path: String, fallback: StdioBridge.Target)
    case http(StdioBridge.Target)

    public static func installed(plist: Data, config: DesktopMCPConfig) throws -> ConnectPlan {
        let target = try StdioBridge.Target.installed(plist: plist, config: config)
        guard let path = socketPath(plist: plist) else { return .http(target) }
        return .socket(path: path, fallback: target)
    }

    /// The socket a Launch Agent plist's `ProgramArguments` name; nil when they name none, or
    /// the plist cannot be read.
    public static func socketPath(plist: Data) -> String? {
        guard let dictionary = try? PropertyListSerialization.propertyList(from: plist, format: nil) as? [String: Any],
              let arguments = dictionary["ProgramArguments"] as? [String] else { return nil }
        return socketPath(in: arguments)
    }

    /// The value after `--socket`, if the agent was installed with one.
    public static func socketPath(in arguments: [String]) -> String? {
        StdioBridge.Target.value(after: "--socket", in: arguments)
    }
}
