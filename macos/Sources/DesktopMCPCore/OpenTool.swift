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

    /// By bundle id, then by name through LaunchServices (as `open -a` finds it), then by
    /// searching the application folders.
    public func applicationURL(for nameOrBundleID: String) async -> URL? {
        await offload {
            if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: nameOrBundleID) { return url }
            if let path = (NSWorkspace.shared as any ApplicationPathLookup).fullPath(forApplication: nameOrBundleID) {
                return URL(fileURLWithPath: path)
            }
            return OpenTool.findApplication(nameOrBundleID, in: OpenTool.applicationFolders)
        }
    }
}

/// `fullPath(forApplication:)` is deprecated without a replacement that takes a name; calling it
/// through this protocol keeps the build free of the deprecation warning.
private protocol ApplicationPathLookup {
    func fullPath(forApplication appName: String) -> String?
}

extension NSWorkspace: ApplicationPathLookup {}

enum OpenTool {
    enum Target: Equatable {
        case url(URL)
        case file(URL)
    }

    static let applicationFolders = [
        "/Applications", "/Applications/Utilities", "/System/Applications", "/System/Applications/Utilities",
        NSHomeDirectory() + "/Applications",
    ].map { URL(fileURLWithPath: $0) }

    /// `<name>.app` directly in one of `roots` (in order), else the first one found in their
    /// subfolders, up to three levels down, ignoring case. App bundles are not searched inside.
    static func findApplication(_ name: String, in roots: [URL]) -> URL? {
        let bundle = name.lowercased().hasSuffix(".app") ? name : name + ".app"
        let fileManager = FileManager.default
        for root in roots {
            let direct = root.appendingPathComponent(bundle)
            if fileManager.fileExists(atPath: direct.path) { return direct }
        }
        let wanted = bundle.lowercased()
        for root in roots {
            guard let walk = fileManager.enumerator(at: root, includingPropertiesForKeys: [.isDirectoryKey],
                                                    options: [.skipsPackageDescendants, .skipsHiddenFiles]) else { continue }
            for case let url as URL in walk {
                if walk.level >= 3 { walk.skipDescendants() }
                if url.lastPathComponent.lowercased() == wanted { return url }
            }
        }
        return nil
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
        guard FileManager.default.fileExists(atPath: path) else { throw ToolError("No such file: \(path)", label: "No such file") }
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
                        throw ToolError("Application '\(appName)' not found.", label: "Application not found")
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
