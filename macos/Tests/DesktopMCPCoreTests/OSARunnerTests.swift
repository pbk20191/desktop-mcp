import AppKit
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

    func testScriptsRunOneAtATime() async {
        // Two instances in use at once corrupt the AppleScript component's switched globals
        // (seen under Guard Malloc), so execution is serial: two 1-second scripts take 2 seconds.
        let started = Date()
        async let a = Self.run("delay 1\nreturn \"a\"")
        async let b = Self.run("delay 1\nreturn \"b\"")
        let (ra, rb) = await (a, b)
        XCTAssertEqual([ra.output, rb.output], ["a", "b"])
        XCTAssertGreaterThanOrEqual(Date().timeIntervalSince(started), 1.9)
    }

    func testAQueuedScriptStillHonoursItsOwnDeadline() async {
        async let long = Self.run("delay 2\nreturn \"long\"", timeout: 10)
        try? await Task.sleep(for: .milliseconds(200))
        let started = Date()
        let short = await Self.run("return \"short\"", timeout: 1)   // waits behind `long`, deadline passes
        XCTAssertLessThan(Date().timeIntervalSince(started), 2.5)
        XCTAssertEqual(short.status, -1)
        XCTAssertTrue(short.output.hasPrefix("Command timed out after 1 seconds"), short.output)
        let longResult = await long
        XCTAssertEqual(longResult.output, "long")
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

    func testEventsToTheCurrentApplicationAreRecorded() async {
        // `path to` is a StandardAdditions command handled inside this process.
        let execution = await OSARunner.execute("POSIX path of (path to home folder)", timeout: 10, policy: .init())
        XCTAssertNil(execution.errorNumber, execution.errorMessage ?? "")
        XCTAssertTrue(execution.result.hasSuffix("/"), execution.result)
        let event = execution.events.first
        XCTAssertEqual(execution.events.count, 1)
        XCTAssertTrue(event?.pid == nil || event?.pid == getpid(), "\(String(describing: event))")
    }

    func testNoUserInteractionErrorsCarryAHintTowardsSystemEvents() {
        // Not run against a real dialog (it would need a UI host); the mapping itself is what matters.
        var execution = OSARunner.Execution()
        execution.errorNumber = Int(errAENoUserInteraction)
        execution.errorMessage = "No user interaction allowed."
        let result = OSARunner.result(of: execution, timeout: 10, requested: 10)
        XCTAssertEqual(result.status, 1)
        XCTAssertTrue(result.output.hasPrefix("execution error: No user interaction allowed. (-1713)\nHint: "), result.output)
        XCTAssertTrue(result.output.contains("System Events"))
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
