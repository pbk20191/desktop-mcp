import AppKit
import Foundation
import ImageIO
import MCP
import UniformTypeIdentifiers

/// Reads and writes a pasteboard. Only the pasteboard's name travels between threads; the
/// `NSPasteboard` itself is created inside `offload` each time.
enum ClipboardTool {
    static let textLimit = 100_000
    static let imageMaxSide = 1568

    static func read(_ name: NSPasteboard.Name) async -> [MCP.Tool.Content] {
        await offload { () -> [MCP.Tool.Content] in
            let pasteboard = NSPasteboard(name: name)

            var sections: [String] = []
            if let text = pasteboard.string(forType: .string), !text.isEmpty {
                let count = text.count
                sections.append(count > textLimit
                    ? String(text.prefix(textLimit)) + "… (truncated, \(count) characters)"
                    : text)
            }
            let urls = pasteboard.readObjects(forClasses: [NSURL.self],
                                              options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
            if !urls.isEmpty {
                sections.append("Files:\n" + urls.map(\.path).joined(separator: "\n"))
            }

            var image: (data: Data, width: Int, height: Int)?
            if let data = pasteboard.data(forType: .png) ?? pasteboard.data(forType: .tiff) {
                image = scaledPNG(from: data, isPNG: pasteboard.data(forType: .png) != nil)
            }

            var content: [MCP.Tool.Content] = []
            if let image {
                if sections.isEmpty { sections.append("Image (\(image.width)×\(image.height)).") }
                content.append(.text(sections.joined(separator: "\n\n")))
                content.append(.image(data: image.data, mimeType: "image/png"))
            } else if sections.isEmpty {
                content.append(.text("The clipboard is empty."))
            } else {
                content.append(.text(sections.joined(separator: "\n\n")))
            }
            return content
        }
    }

    static func write(text: String?, paths: [String]?, to name: NSPasteboard.Name) async throws -> String {
        let paths = paths?.isEmpty == true ? nil : paths
        if text != nil, paths != nil { throw ToolError("Pass either text or paths, not both.") }
        guard text != nil || paths != nil else { throw ToolError("write needs text or paths.") }

        if let text {
            try await offload {
                let pasteboard = NSPasteboard(name: name)
                pasteboard.clearContents()
                guard pasteboard.setString(text, forType: .string) else { throw ToolError("Could not write to the clipboard.") }
            }
            return "Copied \(text.count) characters."
        }

        let files = try (paths ?? []).map { path -> URL in
            let expanded = (path as NSString).expandingTildeInPath
            guard FileManager.default.fileExists(atPath: expanded) else { throw ToolError("No such file: \(expanded)", label: "No such file") }
            return URL(fileURLWithPath: expanded)
        }
        try await offload {
            let pasteboard = NSPasteboard(name: name)
            pasteboard.clearContents()
            guard pasteboard.writeObjects(files as [NSURL]) else { throw ToolError("Could not write to the clipboard.") }
        }
        return "Copied \(files.count) \(files.count == 1 ? "file" : "files")."
    }

    /// PNG bytes for `data` (PNG or TIFF), scaled down so the longer side is at most `imageMaxSide`.
    private static func scaledPNG(from data: Data, isPNG: Bool) -> (data: Data, width: Int, height: Int)? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return nil }
        let longest = max(image.width, image.height)
        if longest <= imageMaxSide {
            if isPNG { return (data, image.width, image.height) }
            guard let png = encodePNG(image) else { return nil }
            return (png, image.width, image.height)
        }
        let ratio = Double(imageMaxSide) / Double(longest)
        let width = image.width >= image.height ? imageMaxSide : max(1, Int((Double(image.width) * ratio).rounded()))
        let height = image.height > image.width ? imageMaxSide : max(1, Int((Double(image.height) * ratio).rounded()))
        guard let context = Screenshot.makeContext(width: width, height: height) else { return nil }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        guard let scaled = context.makeImage(), let png = encodePNG(scaled) else { return nil }
        return (png, width, height)
    }

    private static func encodePNG(_ image: CGImage) -> Data? {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, image, nil)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }

    static func tool(pasteboard: NSPasteboard.Name = .general) -> Tool {
        Tool(
            name: "Clipboard",
            description: "Reads or writes the clipboard. mode='read' (default) returns the copied text, file paths and image; mode='write' replaces the clipboard with 'text' or with the files in 'paths' (which must exist), not both.",
            inputSchema: DesktopMCP.schema([
                "mode": DesktopMCP.string(default: "read", enum: ["read", "write"]),
                "text": DesktopMCP.nullableString(),
                "paths": DesktopMCP.stringArray(),
            ]),
            annotations: ToolAnnotations(title: "Clipboard", readOnly: false, destructive: true, idempotent: false, openWorld: false),
            handler: { args in
                let mode = try args.choice("mode", in: ["read", "write"], default: "read")
                if mode == "read" { return await read(pasteboard) }
                let text = try args.string("text")
                let paths = try args.stringList("paths")
                return [.text(try await write(text: text, paths: paths, to: pasteboard))]
            })
    }
}
