import XCTest
@testable import DesktopMCPCore

final class ShellTests: XCTestCase {
    func testCombinesStdoutAndStderrAndReportsExitCode() async {
        let result = await Shell.execute("echo out; echo err >&2; exit 3", mode: "shell", timeout: 10)
        XCTAssertEqual(result.output, "out\nerr")
        XCTAssertEqual(result.status, 3)
    }

    func testTimeoutKillsTheProcessGroupAndKeepsPartialOutput() async {
        let started = Date()
        let result = await Shell.execute("echo before; sleep 30", mode: "shell", timeout: 1)
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
        XCTAssertEqual(result.status, -1)
        XCTAssertTrue(result.output.hasPrefix("Command timed out after 1 seconds"), result.output)
        XCTAssertTrue(result.output.contains("--- partial output before timeout ---\nbefore"), result.output)
    }

    func testChildrenDoNotInheritIgnoredSignals() async {
        // The server ignores SIGTERM/SIGINT/SIGPIPE for its own shutdown handling; a command it
        // runs must still be killable the ordinary way.
        let previous = signal(SIGTERM, SIG_IGN)
        defer { signal(SIGTERM, previous) }
        let result = await Shell.execute("kill -TERM $$; echo survived", mode: "shell", timeout: 10)
        XCTAssertEqual(result.status, -15, result.output)
        XCTAssertFalse(result.output.contains("survived"), result.output)
    }

    func testBackgroundedGrandchildDoesNotHoldTheCallOpen() async {
        let started = Date()
        let result = await Shell.execute("sleep 30 & echo done", mode: "shell", timeout: 10)
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
        XCTAssertEqual(result.output, "done")
        XCTAssertEqual(result.status, 0)
    }

    func testStdinIsNotInherited() async {
        // `cat` would block (or eat the protocol stream) if stdin were inherited.
        let result = await Shell.execute("cat; echo eof", mode: "shell", timeout: 5)
        XCTAssertEqual(result.output, "eof")
    }

    func testCancellationKillsTheChild() async throws {
        let marker = NSTemporaryDirectory() + "desktop-mcp-cancel-\(UUID().uuidString)"
        let task = Task { await Shell.execute("sleep 3; touch \(marker)", mode: "shell", timeout: 30) }
        try await Task.sleep(nanoseconds: 300_000_000)
        task.cancel()
        let result = await task.value
        XCTAssertEqual(result.status, -1)
        try await Task.sleep(nanoseconds: 3_500_000_000)
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker), "the shell kept running after cancellation")
    }

    func testOsascriptMode() async {
        let result = await Shell.execute("return 1 + 2", mode: "osascript", timeout: 10)
        XCTAssertEqual(result.output, "3")
        XCTAssertEqual(result.status, 0)
    }

    func testOsascriptLanguageIsChosenByTheFirstLine() {
        XCTAssertEqual(Shell.osascriptArguments("return 1"), ["-e", "return 1"])
        XCTAssertEqual(Shell.osascriptArguments("#!/usr/bin/osascript\nreturn 1"), ["-e", "return 1"])
        XCTAssertEqual(Shell.osascriptArguments("#!/usr/bin/osascript -l JavaScript\n1 + 1"),
                       ["-l", "JavaScript", "-e", "1 + 1"])
        XCTAssertEqual(Shell.osascriptArguments("#!/usr/bin/env osascript -l javascript\n1 + 1"),
                       ["-l", "JavaScript", "-e", "1 + 1"])
        // A comment stays in the source: it is valid JavaScript, and offsets in errors stay right.
        XCTAssertEqual(Shell.osascriptArguments("// language: JavaScript\n1 + 1"),
                       ["-l", "JavaScript", "-e", "// language: JavaScript\n1 + 1"])
        XCTAssertEqual(Shell.osascriptArguments("-- language: JavaScript\nreturn 1"), ["-e", "-- language: JavaScript\nreturn 1"])
    }

    func testOsascriptRunsJavaScriptWhenAsked() async {
        let shebang = await Shell.execute("#!/usr/bin/osascript -l JavaScript\n[1, 2, 3].map(x => x * 2)", mode: "osascript", timeout: 10)
        XCTAssertEqual(shebang.output, "2, 4, 6")
        XCTAssertEqual(shebang.status, 0)
        let comment = await Shell.execute("// language: JavaScript\nJSON.stringify({a: 1})", mode: "osascript", timeout: 10)
        XCTAssertEqual(comment.output, "{\"a\":1}")
        XCTAssertEqual(comment.status, 0)
        let failure = await Shell.execute("// language: JavaScript\nthrow new Error(\"js boom\")", mode: "osascript", timeout: 10)
        XCTAssertEqual(failure.output, "execution error: Error: Error: js boom (-2700)")
        XCTAssertEqual(failure.status, 1)
    }

    func testTimeoutIsClampedByCap() {
        unsetenv(Shell.maxTimeoutEnv)
        XCTAssertEqual(Shell.clampTimeout(10), 10)
        XCTAssertEqual(Shell.clampTimeout(10_000), 240)
    }

    func testDecodeStatus() {
        XCTAssertEqual(Shell.decode(3 << 8), 3)
        XCTAssertEqual(Shell.decode(9), -9)
    }
}
