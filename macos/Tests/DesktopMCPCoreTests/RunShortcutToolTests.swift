import Darwin
import Foundation
import MCP
import XCTest
@testable import DesktopMCPCore

/// Stands in for `Shell.run`: records every argv, answers `list`, and plays a Shortcut for `run`.
final class FakeShortcuts: Sendable {
    struct RunRecord: Sendable {
        var inputPath: String?
        var outputPath: String?
        var inputText: String?
        var inputMode: UInt16?
    }

    private struct State {
        var calls: [[String]] = []
        var timeouts: [Double] = []
        var runs: [RunRecord] = []
    }

    private let state = Locked(State())
    let names: String
    let output: String
    let status: Int32
    let stderr: String
    let timedOut: Bool
    let listStatus: Int32
    let listStderr: String

    init(names: String = "", output: String = "", status: Int32 = 0, stderr: String = "", timedOut: Bool = false,
         listStatus: Int32 = 0, listStderr: String = "") {
        self.names = names
        self.output = output
        self.status = status
        self.stderr = stderr
        self.timedOut = timedOut
        self.listStatus = listStatus
        self.listStderr = listStderr
    }

    var calls: [[String]] { state.withLock { $0.calls } }
    var timeouts: [Double] { state.withLock { $0.timeouts } }
    var runs: [RunRecord] { state.withLock { $0.runs } }

    var runner: RunShortcutTool.Runner {
        { argv, timeout in
            self.state.withLock { $0.calls.append(argv); $0.timeouts.append(timeout) }
            if argv.count >= 2, argv[1] == "list" {
                return Shell.Run(stdout: self.names, stderr: self.listStderr, status: self.listStatus, timedOut: false)
            }
            var record = RunRecord()
            if let i = argv.firstIndex(of: "--input-path") {
                let path = argv[i + 1]
                record.inputPath = path
                record.inputText = try? String(contentsOfFile: path, encoding: .utf8)
                var info = stat()
                if stat(path, &info) == 0 { record.inputMode = info.st_mode & 0o777 }
            }
            if let i = argv.firstIndex(of: "--output-path") {
                let path = argv[i + 1]
                record.outputPath = path
                try? Data(self.output.utf8).write(to: URL(fileURLWithPath: path))
            }
            self.state.withLock { $0.runs.append(record) }
            return Shell.Run(stdout: "", stderr: self.stderr, status: self.status, timedOut: self.timedOut)
        }
    }
}

final class RunShortcutToolTests: XCTestCase {
    private let binary = RunShortcutTool.binary

    private func text(_ fake: FakeShortcuts, _ arguments: [String: Value]) async throws -> String {
        let content = try await RunShortcutTool.tool(runner: fake.runner).handler(ToolArguments(arguments))
        guard case .text(let text, _, _) = content.first else { return "" }
        return text
    }

    private func error(_ fake: FakeShortcuts, _ arguments: [String: Value]) async -> String? {
        do { _ = try await text(fake, arguments); return nil } catch { return "\(error)" }
    }

    func testListReturnsNamesOnePerLine() async throws {
        let fake = FakeShortcuts(names: "A\n\n B \n")
        let result = try await text(fake, ["mode": "list"])
        XCTAssertEqual(result, "A\nB")
        XCTAssertEqual(fake.calls, [[binary, "list"]])
    }

    func testListWithFolder() async throws {
        let fake = FakeShortcuts(names: "A\n")
        _ = try await text(fake, ["mode": "list", "folder": "Work"])
        XCTAssertEqual(fake.calls, [[binary, "list", "--folder-name", "Work"]])
    }

    func testListFailureIsAnError() async {
        let fake = FakeShortcuts(listStatus: 1, listStderr: " boom\n")
        let message = await error(fake, ["mode": "list"])
        XCTAssertEqual(message, "shortcuts list failed: boom")
    }

    func testRunPassesInputAndReturnsOutput() async throws {
        let fake = FakeShortcuts(names: "Resize\nOther\n", output: "<out>")
        let result = try await text(fake, ["name": "Resize", "input": "hello"])
        XCTAssertEqual(result, "Shortcut 'Resize' finished with status 0.\n<out>")
        let run = try XCTUnwrap(fake.runs.first)
        XCTAssertEqual(run.inputText, "hello")
        XCTAssertEqual(run.inputMode, 0o600)
        XCTAssertEqual(fake.calls.first, [binary, "list"])
        XCTAssertEqual(Array(try XCTUnwrap(fake.calls.last).prefix(3)), [binary, "run", "Resize"])
    }

    func testRunWithoutInputPassesNoInputPath() async throws {
        let fake = FakeShortcuts(names: "Resize\n")
        _ = try await text(fake, ["name": "Resize"])
        XCTAssertNil(fake.runs.first?.inputPath)
        XCTAssertNotNil(fake.runs.first?.outputPath)
    }

    func testRunWithoutOutputSaysSo() async throws {
        let fake = FakeShortcuts(names: "Resize\n")
        let result = try await text(fake, ["name": "Resize"])
        XCTAssertEqual(result, "Shortcut 'Resize' finished with status 0.\n(no output)")
    }

    func testNonZeroStatusWithoutOutputIncludesStderr() async throws {
        let fake = FakeShortcuts(names: "Resize\n", status: 1, stderr: " Error: nope\n")
        let result = try await text(fake, ["name": "Resize"])
        XCTAssertEqual(result, "Shortcut 'Resize' finished with status 1.\n(no output)\nstderr:\nError: nope")
    }

    func testNonZeroStatusWithOutputKeepsOutput() async throws {
        let fake = FakeShortcuts(names: "Resize\n", output: "partial", status: 1, stderr: "Error")
        let result = try await text(fake, ["name": "Resize"])
        XCTAssertEqual(result, "Shortcut 'Resize' finished with status 1.\npartial")
    }

    func testUnknownNameRunsNothing() async {
        let fake = FakeShortcuts(names: "Resize Image\nMake GIF\n")
        let message = await error(fake, ["name": "resize"])
        XCTAssertEqual(message, "No shortcut named 'resize'. Close matches: Resize Image")
        XCTAssertEqual(fake.calls, [[binary, "list"]])
    }

    /// Telemetry sees fixed labels, never shortcut names or `shortcuts` stderr.
    func testErrorsCarryFixedTelemetryLabels() async {
        let tool = RunShortcutTool.tool(runner: FakeShortcuts(names: "Resize Image\n").runner)
        do {
            _ = try await tool.handler(ToolArguments(["name": "resize"]))
            XCTFail("expected an error")
        } catch {
            XCTAssertEqual((error as? ToolError)?.telemetry, "No such shortcut")
        }
        let failing = RunShortcutTool.tool(runner: FakeShortcuts(listStatus: 1, listStderr: "secret").runner)
        do {
            _ = try await failing.handler(ToolArguments(["mode": "list"]))
            XCTFail("expected an error")
        } catch {
            XCTAssertEqual((error as? ToolError)?.telemetry, "shortcuts list failed")
        }
    }

    func testUnknownNameWithoutMatches() async {
        let fake = FakeShortcuts(names: "Resize Image\n")
        let message = await error(fake, ["name": "zzz"])
        XCTAssertEqual(message, "No shortcut named 'zzz'.")
    }

    func testRunNeedsAName() async {
        let message = await error(FakeShortcuts(), [:])
        XCTAssertEqual(message, "name is required for run.")
    }

    func testTempFilesAreRemoved() async throws {
        let fake = FakeShortcuts(names: "Resize\n", output: "x")
        _ = try await text(fake, ["name": "Resize", "input": "hello"])
        let run = try XCTUnwrap(fake.runs.first)
        XCTAssertFalse(FileManager.default.fileExists(atPath: try XCTUnwrap(run.inputPath)))
        XCTAssertFalse(FileManager.default.fileExists(atPath: try XCTUnwrap(run.outputPath)))
    }

    func testTimeoutIsClamped() async throws {
        let fake = FakeShortcuts(names: "Resize\n")
        _ = try await text(fake, ["name": "Resize", "timeout": 100000])
        XCTAssertEqual(fake.timeouts.last, Double(Shell.clampTimeout(100000)))
    }

    func testTimedOutRun() async throws {
        let fake = FakeShortcuts(names: "Resize\n", timedOut: true)
        let result = try await text(fake, ["name": "Resize", "timeout": 5])
        XCTAssertEqual(result, "Stopped waiting for shortcut 'Resize' after 5s; it may still be running.")
        let run = try XCTUnwrap(fake.runs.first)
        XCTAssertFalse(FileManager.default.fileExists(atPath: try XCTUnwrap(run.outputPath)))
    }

    func testCloseMatchesOrderAndCap() {
        let names = ["My Resize", "Resize B", "resize a", "Other"]
        XCTAssertEqual(RunShortcutTool.closeMatches("RESIZE", in: names), ["Resize B", "resize a", "My Resize"])
        let many = (0..<30).map { "item \($0)" }
        XCTAssertEqual(RunShortcutTool.closeMatches("item", in: many).count, 20)
        XCTAssertEqual(RunShortcutTool.closeMatches("item", in: many).first, "item 0")
        XCTAssertEqual(RunShortcutTool.closeMatches("x", in: ["x", "x"]), ["x"])
    }
}
