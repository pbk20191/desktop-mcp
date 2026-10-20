import Foundation
import MCP
import XCTest
@testable import DesktopMCPCore

actor RecordingWorkspace: Workspace {
    var opened: [(url: URL, app: URL?)] = []
    var revealed: [URL] = []
    let apps: [String: URL]

    init(apps: [String: URL] = [:]) { self.apps = apps }

    func open(_ url: URL, withApplicationAt app: URL?) async throws { opened.append((url, app)) }
    func reveal(_ url: URL) async { revealed.append(url) }
    func applicationURL(for nameOrBundleID: String) async -> URL? { apps[nameOrBundleID] }
}

final class OpenToolTests: XCTestCase {
    private func run(_ workspace: RecordingWorkspace, _ arguments: [String: Value]) async throws -> String {
        let content = try await OpenTool.tool(workspace: workspace).handler(ToolArguments(arguments))
        guard case .text(let text, _, _) = content.first else { return "" }
        return text
    }

    private func error(_ workspace: RecordingWorkspace, _ arguments: [String: Value]) async -> String? {
        do { _ = try await run(workspace, arguments); return nil } catch { return "\(error)" }
    }

    private func temporaryFile(_ name: String) throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        let file = dir.appendingPathComponent(name)
        try Data().write(to: file)
        return file
    }

    func testTildeIsExpanded() throws {
        XCTAssertEqual(try OpenTool.resolve("~"), .file(URL(fileURLWithPath: NSHomeDirectory())))
    }

    func testResolvesNonASCIIPathWithSpaces() throws {
        let file = try temporaryFile("문서 a b.txt")
        guard case .file(let url) = try OpenTool.resolve(file.path) else { return XCTFail("not a file") }
        XCTAssertEqual(url.path, file.path)
    }

    func testFileURLIsTreatedAsAPath() throws {
        let file = try temporaryFile("문서 a b.txt")
        guard case .file(let url) = try OpenTool.resolve(file.absoluteString) else { return XCTFail("not a file") }
        XCTAssertEqual(url.path, file.path)
    }

    func testMissingFileErrors() {
        XCTAssertThrowsError(try OpenTool.resolve("/nope/x")) {
            XCTAssertEqual("\($0)", "No such file: /nope/x")
        }
    }

    /// Telemetry sees fixed labels, never the path or app name.
    func testErrorsCarryFixedTelemetryLabels() async {
        XCTAssertThrowsError(try OpenTool.resolve("/nope/x")) {
            XCTAssertEqual(($0 as? ToolError)?.telemetry, "No such file")
        }
        do {
            _ = try await OpenTool.tool(workspace: RecordingWorkspace())
                .handler(ToolArguments(["target": "https://example.com", "app": "NoSuchApp"]))
            XCTFail("expected an error")
        } catch {
            XCTAssertEqual((error as? ToolError)?.telemetry, "Application not found")
        }
    }

    func testURLIsNotCheckedOnDisk() throws {
        let target = try OpenTool.resolve("https://example.com/a?b=1")
        guard case .url(let url) = target else { return XCTFail("not a URL") }
        XCTAssertEqual(url.absoluteString, "https://example.com/a?b=1")
    }

    func testEmptyTargetErrors() {
        for target in ["", "  "] {
            XCTAssertThrowsError(try OpenTool.resolve(target)) { XCTAssertEqual("\($0)", "target is required.") }
        }
    }

    func testOpenWithUnknownAppErrors() async {
        let message = await error(RecordingWorkspace(), ["target": "https://example.com", "app": "NoSuchApp"])
        XCTAssertEqual(message, "Application 'NoSuchApp' not found.")
    }

    func testOpenWithAppPassesAppURL() async throws {
        let file = try temporaryFile("a.txt")
        let app = URL(fileURLWithPath: "/System/Applications/TextEdit.app")
        let workspace = RecordingWorkspace(apps: ["TextEdit": app])
        _ = try await run(workspace, ["target": .string(file.path), "app": "TextEdit"])
        let opened = await workspace.opened
        XCTAssertEqual(opened.count, 1)
        XCTAssertEqual(opened.first?.url.path, file.path)
        XCTAssertEqual(opened.first?.app, app)
    }

    func testRevealRequiresAnExistingPath() async {
        let message = await error(RecordingWorkspace(), ["target": "https://example.com", "mode": "reveal"])
        XCTAssertEqual(message, "reveal needs an existing file or folder path.")
    }

    func testOutputs() async throws {
        let file = try temporaryFile("a.txt")
        let workspace = RecordingWorkspace(apps: ["TextEdit": URL(fileURLWithPath: "/System/Applications/TextEdit.app")])
        let plain = try await run(workspace, ["target": .string(file.path)])
        XCTAssertEqual(plain, "Opened \(file.path).")
        let with = try await run(workspace, ["target": .string(file.path), "app": "TextEdit"])
        // The display name is localized ("텍스트 편집기" on a Korean system).
        let display = FileManager.default.displayName(atPath: "/System/Applications/TextEdit.app")
        XCTAssertEqual(with, "Opened \(file.path) with \(display.replacingOccurrences(of: ".app", with: "")).")
        let reveal = try await run(workspace, ["target": .string(file.path), "mode": "reveal"])
        XCTAssertEqual(reveal, "Revealed \(file.path) in Finder.")
        let revealed = await workspace.revealed
        XCTAssertEqual(revealed.map(\.path), [file.path])
    }

    func testStringListValidation() throws {
        XCTAssertEqual(try ToolArguments(["a": ["x", "y"]]).stringList("a"), ["x", "y"])
        XCTAssertNil(try ToolArguments([:]).stringList("a"))
        XCTAssertNil(try ToolArguments(["a": .null]).stringList("a"))
        for bad: Value in ["x", [1], ["x", 2]] {
            XCTAssertThrowsError(try ToolArguments(["a": bad]).stringList("a")) {
                XCTAssertEqual("\($0)", "a must be a list of strings")
            }
        }
    }
}
