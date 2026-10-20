import AppKit

/// A running application reduced to what quit matching needs.
public struct RunningAppInfo: Sendable, Equatable {
    public var pid: pid_t
    public var name: String
    public var bundleID: String?

    public init(pid: pid_t, name: String, bundleID: String?) {
        self.pid = pid
        self.name = name
        self.bundleID = bundleID
    }
}

enum AppQuit {
    static let loginwindowBundleID = "com.apple.loginwindow"

    /// Every process matching `name` (a bundle id exactly, or a localized name ignoring case),
    /// except desktop-mcp itself and loginwindow.
    static func targets(_ name: String, running: [RunningAppInfo], ownPID: pid_t) throws -> [RunningAppInfo] {
        let found = running.filter {
            $0.pid != ownPID && $0.bundleID != loginwindowBundleID
                && App.matches(name: $0.name, bundleID: $0.bundleID, query: name)
        }
        guard !found.isEmpty else {
            throw ToolError("Application '\(name)' is not running.", telemetry: "App quit: not running")
        }
        return found
    }

    /// Regular and menu-bar (accessory) apps, read on the main actor.
    static func runningApps() async -> [RunningAppInfo] {
        await MainActor.run {
            NSWorkspace.shared.runningApplications
                .filter { $0.activationPolicy == .regular || $0.activationPolicy == .accessory }
                .map { RunningAppInfo(pid: $0.processIdentifier, name: $0.localizedName ?? "", bundleID: $0.bundleIdentifier) }
        }
    }
}

extension Desktop {
    static func quitMessage(name: String, force: Bool) -> String {
        force ? "Force-quit \(name)." : "Quit requested for \(name)."
    }

    func quit(name: String?, force: Bool) async throws -> String {
        let mode = force ? "force_quit" : "quit"
        guard let name = name?.trimmingCharacters(in: .whitespaces), !name.isEmpty else {
            throw ToolError("App name or bundle ID required for \(mode).", telemetry: "App \(mode): name missing")
        }
        let targets = try AppQuit.targets(name, running: await AppQuit.runningApps(),
                                          ownPID: ProcessInfo.processInfo.processIdentifier)
        let pids = targets.map(\.pid)
        let signalled = await MainActor.run {
            pids.map { pid -> Bool in
                guard let running = NSRunningApplication(processIdentifier: pid) else { return false }
                return force ? running.forceTerminate() : running.terminate()
            }
        }
        guard signalled.contains(true) else {
            throw ToolError(force ? "Could not force-quit \(name)." : "Could not quit \(name).",
                            telemetry: force ? "App force_quit failed" : "App quit failed")
        }
        return Self.quitMessage(name: name, force: force)
    }
}
