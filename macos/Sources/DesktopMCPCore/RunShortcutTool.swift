import Darwin
import Foundation
import MCP

enum RunShortcutTool {
    typealias Runner = @Sendable ([String], Double) async throws -> Shell.Run

    static let binary = "/usr/bin/shortcuts"
    private static let maxCloseMatches = 20

    /// Names that start with `name` first (in list order), then names that merely contain it,
    /// case-insensitively, without repeats, at most 20.
    static func closeMatches(_ name: String, in names: [String]) -> [String] {
        let query = name.lowercased()
        var seen = Set<String>()
        let unique = names.filter { seen.insert($0).inserted }
        let prefixed = unique.filter { $0.lowercased().hasPrefix(query) }
        let containing = unique.filter { !$0.lowercased().hasPrefix(query) && $0.lowercased().contains(query) }
        return Array((prefixed + containing).prefix(maxCloseMatches))
    }

    private static func list(folder: String?, timeout: Double, runner: Runner) async throws -> [String] {
        var argv = [binary, "list"]
        if let folder { argv += ["--folder-name", folder] }
        let run = try await runner(argv, timeout)
        if run.timedOut { throw ToolError("shortcuts list timed out.") }
        guard run.status == 0 else {
            throw ToolError("shortcuts list failed: \(run.stderr.trimmingCharacters(in: .whitespacesAndNewlines))",
                            telemetry: "shortcuts list failed")
        }
        return run.stdout.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }

    /// Creates `path` exclusively with mode 0600 from the start, so no other user can read it.
    private static func writePrivateFile(_ text: String, to path: String) throws {
        let fd = open(path, O_CREAT | O_EXCL | O_WRONLY, 0o600)
        guard fd >= 0 else {
            throw ToolError("Could not create a temporary file: \(String(cString: strerror(errno)))",
                            telemetry: "Could not create a temporary file")
        }
        defer { close(fd) }
        var remaining = Data(text.utf8)[...]
        while !remaining.isEmpty {
            let n = remaining.withUnsafeBytes { Darwin.write(fd, $0.baseAddress, $0.count) }
            if n < 0 && errno == EINTR { continue }
            guard n > 0 else {
                throw ToolError("Could not write a temporary file: \(String(cString: strerror(errno)))",
                                telemetry: "Could not write a temporary file")
            }
            remaining = remaining.dropFirst(n)
        }
    }

    private static func run(name: String, input: String?, timeout: Int, runner: Runner) async throws -> String {
        let seconds = Double(timeout)
        let names = try await list(folder: nil, timeout: seconds, runner: runner)
        guard names.contains(name) else {
            var message = "No shortcut named '\(name)'."
            let close = closeMatches(name, in: names)
            if !close.isEmpty { message += " Close matches: " + close.joined(separator: ", ") }
            throw ToolError(message, telemetry: "No such shortcut")
        }

        let directory = FileManager.default.temporaryDirectory
        let id = UUID().uuidString
        let inputPath = directory.appendingPathComponent("desktop-mcp-shortcut-\(id).in").path
        let outputPath = directory.appendingPathComponent("desktop-mcp-shortcut-\(id).out").path
        defer {
            unlink(inputPath)
            unlink(outputPath)
        }

        var argv = [binary, "run", name]
        if let input {
            try writePrivateFile(input, to: inputPath)
            argv += ["--input-path", inputPath]
        }
        argv += ["--output-path", outputPath]

        let result = try await runner(argv, seconds)
        if result.timedOut { return "Stopped waiting for shortcut '\(name)' after \(timeout)s; it may still be running." }

        let output = (try? String(contentsOfFile: outputPath, encoding: .utf8)) ?? ""
        var text = "Shortcut '\(name)' finished with status \(result.status).\n"
        if !output.isEmpty {
            text += output
        } else {
            text += "(no output)"
            let stderr = result.stderr.trimmingCharacters(in: .whitespacesAndNewlines)
            if !stderr.isEmpty { text += "\nstderr:\n" + stderr }
        }
        return text
    }

    static func tool(runner: @escaping Runner = Shell.run) -> Tool {
        Tool(
            name: "RunShortcut",
            description: "Lists or runs the user's Shortcuts (the Shortcuts app, not keyboard shortcuts). mode='list' returns the shortcut names, optionally only those in 'folder'. mode='run' runs the shortcut 'name' (exact name), passing 'input' text to it, and returns its output.",
            inputSchema: DesktopMCP.schema([
                "mode": DesktopMCP.string(default: "run", enum: ["run", "list"]),
                "name": DesktopMCP.nullableString(),
                "input": DesktopMCP.nullableString(),
                "folder": DesktopMCP.nullableString(),
                "timeout": DesktopMCP.integer(default: 60),
            ]),
            annotations: ToolAnnotations(title: "RunShortcut", readOnly: false, destructive: true, idempotent: false, openWorld: true),
            handler: { args in
                let mode = try args.choice("mode", in: ["run", "list"], default: "run")
                let timeout = Shell.clampTimeout(try args.int("timeout") ?? 60)
                if mode == "list" {
                    let names = try await list(folder: try args.string("folder"), timeout: Double(timeout), runner: runner)
                    return [.text(names.joined(separator: "\n"))]
                }
                guard let name = try args.string("name"), !name.isEmpty else {
                    throw ToolError("name is required for run.")
                }
                return [.text(try await run(name: name, input: try args.string("input"), timeout: timeout, runner: runner))]
            })
    }
}
