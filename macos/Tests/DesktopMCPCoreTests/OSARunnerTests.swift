import XCTest
@testable import DesktopMCPCore

/// In-process OSA execution for the Shell tool's osascript mode: same text as osascript, plus
/// interruptible timeouts and an Apple Event policy.
final class OSARunnerTests: XCTestCase {
    static func run(_ source: String, timeout: Double = 10, policy: OSARunner.Policy = .init()) async -> Shell.Result {
        await OSARunner.run(source, timeout: timeout, requested: Int(timeout), policy: policy)
    }

    func testRendersResultsLikeOsascript() async {
        let result = await Self.run("return {1, \"two\", 3.5, {a:1}}")
        XCTAssertEqual(result.output, "1, two, 3.5, a:1")
        XCTAssertEqual(result.status, 0)
        let text = await Self.run("return \"hello\"")
        XCTAssertEqual(text.output, "hello")
        let assignment = await Self.run("set x to 1")  // osascript prints the statement's value
        XCTAssertEqual(assignment.output, "1")
        XCTAssertEqual(assignment.status, 0)
    }

    func testExecutionErrorsUseOsascriptsFormatAndStatus() async {
        let result = await Self.run("error \"boom\" number 42")
        XCTAssertEqual(result.output, "6:12: execution error: boom (42)")
        XCTAssertEqual(result.status, 1)
    }

    func testSyntaxErrorsCarryTheSourceRange() async {
        let result = await Self.run("return 1 +")
        XCTAssertTrue(result.output.hasPrefix("10:10: syntax error: "), result.output)
        XCTAssertTrue(result.output.hasSuffix("(-2741)"), result.output)
        XCTAssertEqual(result.status, 1)
    }

    func testTimeoutInterruptsDelayAndLoops() async {
        for source in ["delay 100", "repeat\nend repeat"] {
            let started = Date()
            let result = await Self.run(source, timeout: 1)
            XCTAssertLessThan(Date().timeIntervalSince(started), 3, source)
            XCTAssertEqual(result.status, -1, source)
            XCTAssertTrue(result.output.hasPrefix("Command timed out after 1 seconds"), result.output)
        }
    }

    func testCancellationInterruptsTheScript() async {
        let task = Task { await Self.run("delay 100", timeout: 60) }
        try? await Task.sleep(for: .milliseconds(300))
        let started = Date()
        task.cancel()
        let result = await task.value
        XCTAssertLessThan(Date().timeIntervalSince(started), 3)
        XCTAssertEqual(result, Shell.Result(output: "Command cancelled", status: -1))
    }

    func testScriptsRunConcurrently() async {
        let started = Date()
        async let a = Self.run("delay 1\nreturn \"a\"")
        async let b = Self.run("delay 1\nreturn \"b\"")
        let (ra, rb) = await (a, b)
        XCTAssertEqual([ra.output, rb.output], ["a", "b"])
        XCTAssertLessThan(Date().timeIntervalSince(started), 1.9)
    }

    func testLogStatementsAreCapturedLikeOsascriptsStderr() async {
        let result = await Self.run("log \"to stderr\"\nreturn \"ok\"")
        XCTAssertEqual(result.output, "ok\nto stderr")
        XCTAssertEqual(result.status, 0)
    }

    func testJavaScriptIsSelectedByAShebangOrLanguageComment() async {
        XCTAssertEqual(OSARunner.language(of: "#!/usr/bin/osascript -l JavaScript\n1"), .javaScript)
        XCTAssertEqual(OSARunner.language(of: "// language: javascript\n1"), .javaScript)
        XCTAssertEqual(OSARunner.language(of: "#!/usr/bin/osascript\nreturn 1"), .appleScript)
        XCTAssertEqual(OSARunner.language(of: "return 1"), .appleScript)

        let result = await Self.run("#!/usr/bin/osascript -l JavaScript\n[1, \"two\", {a: 1}]")
        XCTAssertEqual(result.output, "1, two, a:1")
        XCTAssertEqual(result.status, 0)
        let failure = await Self.run("// language: JavaScript\nthrow new Error(\"js boom\")")
        XCTAssertEqual(failure.output, "execution error: Error: Error: js boom (-2700)")
        XCTAssertEqual(failure.status, 1)
    }

    func testDeniedApplicationsCannotBeSentEvents() async {
        var policy = OSARunner.Policy()
        policy.deniedBundleIDs = ["com.apple.finder"]
        let execution = await OSARunner.execute("tell application id \"com.apple.finder\" to get name of startup disk",
                                                timeout: 10, policy: policy)
        XCTAssertEqual(execution.events.count, 1)
        XCTAssertEqual(execution.events.first?.bundleID, "com.apple.finder")
        XCTAssertEqual(execution.events.first?.eventClass, "core")
        XCTAssertEqual(execution.events.first?.eventID, "getd")
        XCTAssertEqual(execution.events.first?.denied, true)
        XCTAssertEqual(execution.errorNumber, -10004)
        XCTAssertTrue(execution.errorMessage?.contains("Finder") == true, execution.errorMessage ?? "")
    }
}
