import AppKit
import Foundation
import MCP

/// The system calls `Open` makes, behind a seam so tests never open anything.
public protocol Workspace: Sendable {
    func open(_ url: URL, withApplicationAt app: URL?) async throws
    func reveal(_ url: URL) async
    func applicationURL(for nameOrBundleID: String) async -> URL?
}

public struct SystemWorkspace: Workspace {
    public init() {}

    public func open(_ url: URL, withApplicationAt app: URL?) async throws {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let done: @Sendable (NSRunningApplication?, Error?) -> Void = { _, error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            }
            if let app {
                NSWorkspace.shared.open([url], withApplicationAt: app, configuration: configuration, completionHandler: done)
            } else {
                NSWorkspace.shared.open(url, configuration: configuration, completionHandler: done)
            }
        }
    }

    public func reveal(_ url: URL) async {
        await MainActor.run { NSWorkspace.shared.activateFileViewerSelecting([url]) }
    }

    public func applicationURL(for nameOrBundleID: String) async -> URL? {
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: nameOrBundleID) { return url }
        let name = nameOrBundleID.hasSuffix(".app") ? nameOrBundleID : nameOrBundleID + ".app"
        let directories = ["/Applications", "/System/Applications", NSHomeDirectory() + "/Applications"]
        return directories.map { URL(fileURLWithPath: $0).appendingPathComponent(name) }
            .first { FileManager.default.fileExists(atPath: $0.path) }
    }
}

enum OpenTool {
    enum Target: Equatable {
        case url(URL)
        case file(URL)
    }

    /// A target with a URL scheme of two or more letters is a URL (`file:` ones are paths);
    /// anything else is a path, `~` expanded, which must exist.
    static func resolve(_ target: String) throws -> Target {
        let trimmed = target.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw ToolError("target is required.") }

        var path = trimmed
        if let url = URL(string: trimmed), let scheme = url.scheme, scheme.count >= 2 {
            if scheme.lowercased() != "file" { return .url(url) }
            path = url.path
        }
        path = (path as NSString).expandingTildeInPath
        guard FileManager.default.fileExists(atPath: path) else { throw ToolError("No such file: \(path)", telemetry: "No such file") }
        return .file(URL(fileURLWithPath: path))
    }

    static func tool(workspace: any Workspace) -> Tool {
        Tool(
            name: "Open",
            description: "Opens a file, folder or URL with the default application, or with the application given by 'app' (name or bundle id). mode='reveal' shows an existing file or folder in Finder instead.",
            inputSchema: DesktopMCP.schema([
                "target": DesktopMCP.string("Absolute path, ~-path, or URL"),
                "mode": DesktopMCP.string(default: "open", enum: ["open", "reveal"]),
                "app": DesktopMCP.nullableString(),
            ], required: ["target"]),
            annotations: ToolAnnotations(title: "Open", readOnly: false, destructive: false, idempotent: false, openWorld: true),
            handler: { args in
                let input = try args.requiredString("target")
                let mode = try args.choice("mode", in: ["open", "reveal"], default: "open")
                let appName = try args.string("app")
                let target = try resolve(input)

                if mode == "reveal" {
                    guard case .file(let url) = target else {
                        throw ToolError("reveal needs an existing file or folder path.")
                    }
                    await workspace.reveal(url)
                    return [.text("Revealed \(url.path) in Finder.")]
                }

                var appURL: URL?
                if let appName {
                    guard let found = await workspace.applicationURL(for: appName) else {
                        throw ToolError("Application '\(appName)' not found.", telemetry: "Application not found")
                    }
                    appURL = found
                }
                let url: URL
                let shown: String
                switch target {
                case .url(let u): url = u; shown = input.trimmingCharacters(in: .whitespacesAndNewlines)
                case .file(let u): url = u; shown = u.path
                }
                try await workspace.open(url, withApplicationAt: appURL)
                if let appURL {
                    let display = FileManager.default.displayName(atPath: appURL.path)
                    let name = display.hasSuffix(".app") ? String(display.dropLast(4)) : display
                    return [.text("Opened \(shown) with \(name).")]
                }
                return [.text("Opened \(shown).")]
            })
    }
}
