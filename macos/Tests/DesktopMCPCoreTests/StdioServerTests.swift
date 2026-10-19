import Foundation
import MCP
import System
import XCTest
@testable import DesktopMCPCore

final class StdioServerTests: XCTestCase {
    override func setUp() {
        super.setUp()
        // A write to a closed pipe must fail with EPIPE rather than kill the test process; the
        // CLI sets the same disposition before serving.
        signal(SIGPIPE, SIG_IGN)
    }

    /// The server's stdin and stdout, as pipes the test holds the other ends of.
    struct Pipes {
        let input = Pipe()
        let output = Pipe()

        func send(_ text: String) {
            input.fileHandleForWriting.write(Data(text.utf8))
        }

        /// Every line written to the server's stdout. Only once `run` has returned: closes the
        /// write end so the read sees EOF.
        func responses() throws -> [Value] {
            try output.fileHandleForWriting.close()
            let data = output.fileHandleForReading.readDataToEndOfFile()
            return try data.split(separator: 0x0A).map { try JSON.parse(Data($0)) }
        }
    }

    func serve(_ registry: ToolRegistry, _ pipes: Pipes) -> Task<Void, Never> {
        let input = FileDescriptor(rawValue: pipes.input.fileHandleForReading.fileDescriptor)
        let output = FileDescriptor(rawValue: pipes.output.fileHandleForWriting.fileDescriptor)
        return Task { await StdioServer.run(registry, input: input, output: output) }
    }

    /// Whether `task` finishes within `seconds`.
    func finishes(_ task: Task<Void, Never>, within seconds: Double) async -> Bool {
        await withTaskGroup(of: Bool.self) { group in
            group.addTask { await task.value; return true }
            group.addTask { try? await Task.sleep(for: .seconds(seconds)); return false }
            let first = await group.next() ?? false
            group.cancelAll()
            return first
        }
    }

    func testServesUntilStdinCloses() async throws {
        let pipes = Pipes()
        let run = serve(await DesktopMCP.makeRegistry(), pipes)
        pipes.send(#"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"# + "\n")
        pipes.send(#"{"jsonrpc":"2.0","id":2,"method":"ping"}"# + "\n")
        try pipes.input.fileHandleForWriting.close()
        let finished = await finishes(run, within: 5)
        XCTAssertTrue(finished, "run() must return once stdin closes")
        let responses = try pipes.responses()
        XCTAssertEqual(responses.count, 2)
        XCTAssertEqual(Set(responses.compactMap { $0["id"]?.intValue }), [1, 2])
        XCTAssertTrue(responses.allSatisfy { $0["result"] != nil })
    }

    func testStopsWhenStdoutIsClosed() async throws {
        let pipes = Pipes()
        let run = serve(await DesktopMCP.makeRegistry(), pipes)
        try pipes.output.fileHandleForReading.close()
        pipes.send(#"{"jsonrpc":"2.0","id":1,"method":"ping"}"# + "\n")
        let finished = await finishes(run, within: 5)
        XCTAssertTrue(finished, "run() must return once a write to stdout fails")
        try pipes.input.fileHandleForWriting.close()
        await run.value
    }

    func testCancellationEndsRunAndTheCallInFlight() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let pipes = Pipes()
        let run = serve(registry, pipes)
        pipes.send(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":30}}}"# + "\n")
        try await Task.sleep(for: .milliseconds(200))
        let cancelled = Date()
        run.cancel()  // stdin stays open: only cancellation can end the run
        let finished = await finishes(run, within: 5)
        XCTAssertTrue(finished, "run() must return once cancelled")
        try await observer.waitFor(error: "Cancelled.")
        XCTAssertLessThan(Date().timeIntervalSince(cancelled), 5)
        try pipes.input.fileHandleForWriting.close()
    }

    func testSplitsOnNewlineOnly() async throws {
        // JSON strings may contain U+2028, U+2029 and NEL unescaped (JavaScript clients emit them
        // that way); none of them ends a message.
        let pipes = Pipes()
        let run = serve(await DesktopMCP.makeRegistry(), pipes)
        pipes.send("{\"jsonrpc\":\"2.0\",\"id\":\"a\u{2028}b\u{2029}c\u{0085}d\",\"method\":\"ping\"}\n")
        pipes.send(#"{"jsonrpc":"2.0","id":2,"method":"ping"}"# + "\n")
        try pipes.input.fileHandleForWriting.close()
        let finished = await finishes(run, within: 5)
        XCTAssertTrue(finished, "run() must return once stdin closes")
        let responses = try pipes.responses()
        XCTAssertEqual(responses.count, 2)
        XCTAssertEqual(Set(responses.compactMap { $0["id"]?.stringValue }), ["a\u{2028}b\u{2029}c\u{0085}d"])
        XCTAssertTrue(responses.allSatisfy { $0["result"] != nil })
    }
}
