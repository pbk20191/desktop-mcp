import AppKit
import ApplicationServices
import CoreGraphics
import CoreServices
import MCP

/// The macOS privacy grants desktop-mcp depends on, as seen right now.
public struct PermissionState: Sendable, Equatable {
    public var accessibility: Bool
    public var screenRecording: Bool
    public var automation: Automation

    /// Apple Events to System Events. Reported, never requested.
    public enum Automation: String, Sendable {
        case granted
        case denied = "not granted"
        case notDetermined = "not determined"
    }

    public init(accessibility: Bool, screenRecording: Bool, automation: Automation) {
        self.accessibility = accessibility
        self.screenRecording = screenRecording
        self.automation = automation
    }
}

/// Permission checks, the `Permissions` tool, and the guard for tools that need Accessibility.
public enum Permissions {
    public static let accessibilityMessage =
        "Accessibility is not granted to desktop-mcp. Grant it in System Settings > Privacy & Security > Accessibility, then restart desktop-mcp."

    /// Reads the full state, for the `Permissions` tool. Tests replace it and put the original back.
    public static let probe = Locked<@Sendable () -> PermissionState>(check)

    /// Whether Accessibility is granted, for the guard and Snapshot: only this check, never the
    /// Apple Events one, which is a round trip to tccd. Tests replace it and put the original back.
    public static let trusted = Locked<@Sendable () -> Bool>({ AX.isTrusted(prompt: false) })

    /// The real checks; none of them prompts.
    @Sendable static func check() -> PermissionState {
        PermissionState(accessibility: AX.isTrusted(prompt: false),
                        screenRecording: CGPreflightScreenCaptureAccess(),
                        automation: automationOfSystemEvents())
    }

    static func automationOfSystemEvents() -> PermissionState.Automation {
        let target = NSAppleEventDescriptor(bundleIdentifier: "com.apple.systemevents")
        guard let desc = target.aeDesc else { return .notDetermined }
        switch AEDeterminePermissionToAutomateTarget(desc, typeWildCard, typeWildCard, false) {
        case noErr: return .granted
        case OSStatus(errAEEventNotPermitted): return .denied
        case OSStatus(errAEEventWouldRequireUserConsent): return .notDetermined
        default: return .notDetermined  // procNotFound when System Events is not running, among others
        }
    }

    public static func current() -> PermissionState {
        probe.current()
    }

    public static func accessibilityTrusted() -> Bool {
        trusted.current()
    }

    /// Throws the Accessibility copy when the grant is missing. Called first by every tool that
    /// works through Accessibility or posts events, which would otherwise answer empty or do nothing.
    public static func requireAccessibility() throws {
        guard accessibilityTrusted() else {
            throw ToolError(accessibilityMessage, telemetry: "Accessibility not granted")
        }
    }

    public static func format(_ s: PermissionState) -> String {
        func granted(_ on: Bool) -> String { on ? "granted" : "not granted" }
        return [
            "Accessibility: \(granted(s.accessibility))",
            "Screen Recording: \(granted(s.screenRecording))",
            "Automation (System Events): \(s.automation.rawValue)",
            "Needs Accessibility: App (resize, move), Click, Type, Scroll, Move, Shortcut, Snapshot, Desktop, Window, Menu, Wait (until)",
            "Needs Screen Recording: Snapshot (use_vision), OCR",
        ].joined(separator: "\n")
    }

    public static func tool() -> Tool {
        Tool(
            name: "Permissions",
            description: "Reports which macOS permissions desktop-mcp holds: Accessibility, Screen Recording, and Automation of System Events (granted, not granted or not determined), and which tools need each. Never prompts. A tool that needs a missing permission fails with a message saying how to grant it.",
            inputSchema: DesktopMCP.schema([:]),
            annotations: ToolAnnotations(title: "Permissions", readOnly: true, destructive: false, idempotent: true, openWorld: false),
            handler: { _ in
                // The Apple Events check is IPC to tccd; keep it off the cooperative pool.
                [.text(format(await offload { current() }))]
            })
    }
}
