import Foundation
import MCP
import XCTest
@testable import DesktopMCPCore

final class ToolRegistryTests: XCTestCase {
    static let specURL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()  // DesktopMCPCoreTests
        .deletingLastPathComponent()  // Tests
        .deletingLastPathComponent()  // macos
        .deletingLastPathComponent()  // repository root
        .appendingPathComponent("spec/tools.json")

    func text(_ content: [MCP.Tool.Content]) -> String? {
        guard case .text(let text, _, _)? = content.first else { return nil }
        return text
    }

    func testToolsListMatchesTheSharedContract() async throws {
        let spec = try JSON.parse(Data(contentsOf: Self.specURL))
        let expected = try XCTUnwrap(spec["tools"]?.arrayValue, "spec/tools.json has no tools array")

        let (client, started) = try await connect(await DesktopMCP.makeRegistry())
        defer { Task { await started.shutdown() } }
        let actual = try await client.listTools().tools.map { try JSON.parse(JSONEncoder().encode($0)) }

        let byName = { (tools: [Value]) in Dictionary(uniqueKeysWithValues: tools.map { ($0["name"]?.stringValue ?? "", $0) }) }
        let expectedByName = byName(expected), actualByName = byName(actual)
        XCTAssertEqual(Set(actualByName.keys), Set(expectedByName.keys), "tool set differs from spec/tools.json")
        for (name, tool) in expectedByName {
            XCTAssertEqual(actualByName[name], tool, "\(name) differs from spec/tools.json")
        }
    }

    func testExcludedToolsAreRemoved() async throws {
        let (client, started) = try await connect(await DesktopMCP.makeRegistry(excluding: ["Shell", "Scrape"]))
        defer { Task { await started.shutdown() } }
        let names = try await client.listTools().tools.map(\.name)
        XCTAssertEqual(names.count, 10)
        XCTAssertFalse(names.contains("Shell"))
        XCTAssertFalse(names.contains("Scrape"))
    }

    func testToolCallSuccessAndErrors() async throws {
        let (client, started) = try await connect(await DesktopMCP.makeRegistry())
        defer { Task { await started.shutdown() } }

        let wait = try await client.callTool(name: "Wait", arguments: ["duration": 0])
        XCTAssertNotEqual(wait.isError, true)
        XCTAssertTrue(text(wait.content)?.hasPrefix("Waited") == true, "\(wait.content)")

        let click = try await client.callTool(name: "Click", arguments: ["loc": [1]])
        XCTAssertEqual(click.isError, true)
        XCTAssertEqual(text(click.content), "Error executing tool Click: Location must be a list of exactly 2 integers [x, y]")

        do {
            _ = try await client.callTool(name: "Nope", arguments: [:])
            XCTFail("an unknown tool must be a protocol error")
        } catch let error as MCPError {
            XCTAssertEqual(error.code, -32602)
        }

        // The SDK prefixes every standard error message ("Invalid params: …"); the old server's
        // exact text travels as `data.detail`.
        let reply = try JSON.parse(try await exchangeRaw(await DesktopMCP.makeRegistry(),
                                                         #"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Nope"}}"#))
        XCTAssertEqual(reply["error"]?["code"], -32602)
        XCTAssertEqual(reply["error"]?["message"], "Invalid params: Unknown tool: Nope")
        XCTAssertEqual(reply["error"]?["data"]?["detail"], "Unknown tool: Nope")
    }

    func testArgumentsThatAreNotAnObjectGetAnError() async throws {
        let reply = try await exchangeRaw(await DesktopMCP.makeRegistry(),
                                          #"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Wait","arguments":"nope"}}"#)
        let json = try JSON.parse(reply)
        XCTAssertNotNil(json["error"], reply)
        XCTAssertNil(json["result"], reply)
    }

    func testPingCompletesWhileWaitIsInFlightAndCancelStopsIt() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let (client, started) = try await connect(registry)
        defer { Task { await started.shutdown() } }

        let context = try await client.send(CallTool.request(.init(name: "Wait", arguments: ["duration": 30])))
        let wait = Task { try? await context.value }

        // A long Wait must not hold up a ping sent after it.
        let pingStarted = Date()
        try await client.ping()
        XCTAssertLessThan(Date().timeIntervalSince(pingStarted), 1)

        // Cancelling ends the call on the server promptly rather than after 30 seconds.
        let cancelled = Date()
        try await client.cancelRequest(context.requestID)
        _ = await wait.value
        try await observer.waitFor(error: "Cancelled.")
        XCTAssertLessThan(Date().timeIntervalSince(cancelled), 5)
        let events = await observer.events
        XCTAssertEqual(events, ["fail Wait test"])
    }

    func testLargeRequestIdsRoundTrip() async throws {
        // Ids beyond 2^53 come back exactly as sent (JavaScript clients cannot send them, but
        // others can).
        let reply = try await exchangeRaw(await DesktopMCP.makeRegistry(),
                                          #"{"jsonrpc":"2.0","id":9007199254740993,"method":"ping"}"#)
        XCTAssertTrue(reply.contains("\"id\":9007199254740993"), reply)
    }

    func testToolOutcomesReachTheObserverWithClientInfo() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let (client, started) = try await connect(registry, clientName: "claude-code")
        defer { Task { await started.shutdown() } }
        _ = try await client.callTool(name: "Wait", arguments: ["duration": 0])
        _ = try await client.callTool(name: "Click", arguments: ["loc": [1]])
        let events = await observer.events.sorted()
        XCTAssertEqual(events, ["fail Click claude-code", "ok Wait claude-code"])
    }

    func testTelemetryNeverSeesArgumentText() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let (client, started) = try await connect(registry)
        defer { Task { await started.shutdown() } }
        _ = try await client.callTool(name: "Scrape", arguments: ["url": "http://intranet.corp.example/secret?token=abc"])
        _ = try await client.callTool(name: "Shortcut", arguments: ["shortcut": "command+zzz"])
        let errors = await observer.errors.sorted()
        XCTAssertEqual(errors, ["Could not resolve hostname", "Unknown key"])
    }

    func testDataURLStringsStayStrings() throws {
        // `Value` decodes a string that parses as a data URL into `.data`; a tool argument that
        // happens to look like one is still the text the client sent.
        let args = ToolArguments(["url": try JSON.parse("\"data:text/html,hi\"")])
        XCTAssertEqual(try args.string("url"), "data:text/html,hi")
    }

    func testEmptyListsForPromptsAndResources() async throws {
        let (client, started) = try await connect(await DesktopMCP.makeRegistry())
        defer { Task { await started.shutdown() } }
        let prompts = try await client.listPrompts().prompts
        XCTAssertTrue(prompts.isEmpty)
        let resources = try await client.listResources().resources
        XCTAssertTrue(resources.isEmpty)
        let templates = try await client.listResourceTemplates().templates
        XCTAssertTrue(templates.isEmpty)
    }

    func testShutdownCancelsInFlightCalls() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let (client, started) = try await connect(registry)
        let context = try await client.send(CallTool.request(.init(name: "Wait", arguments: ["duration": 30])))
        let wait = Task { try? await context.value }
        try await Task.sleep(for: .milliseconds(200))

        // `Server.stop()` alone leaves handler tasks running; shutdown must end the Wait too.
        let shutdownStarted = Date()
        await started.shutdown()
        XCTAssertLessThan(Date().timeIntervalSince(shutdownStarted), 2)
        let ended = Date()
        try await observer.waitFor(error: "Cancelled.", timeout: 2)
        XCTAssertLessThan(Date().timeIntervalSince(ended), 2)

        // A call that registers after shutdown (it was past the tool lookup) is cancelled at once.
        let waitTool = try XCTUnwrap(DesktopMCP.tools(Desktop()).first { $0.name == "Wait" })
        let late = Date()
        let result = await ToolRegistry.call(waitTool, arguments: ToolArguments(["duration": 30]), client: nil,
                                             observer: nil, inFlight: started.inFlight)
        XCTAssertLessThan(Date().timeIntervalSince(late), 2)
        XCTAssertEqual(result.isError, true)
        XCTAssertEqual(result.content, [.text("Cancelled.")])

        // The server is gone, so the client gives up its pending request on disconnect.
        await client.disconnect()
        _ = await wait.value
    }

    func testCancelByRequestHandleEndsOnlyThatCall() async throws {
        let observer = RecordingObserver()
        let registry = await DesktopMCP.makeRegistry()
        await registry.setObserver(observer)
        let (clientA, startedA) = try await connect(registry, clientName: "A", handle: "a")
        let (clientB, startedB) = try await connect(registry, clientName: "B", handle: "b")
        let contextA = try await clientA.send(CallTool.request(.init(name: "Wait", arguments: ["duration": 30])))
        let contextB = try await clientB.send(CallTool.request(.init(name: "Wait", arguments: ["duration": 30])))
        let waitA = Task { try await contextA.value }
        let waitB = Task { try? await contextB.value }
        try await Task.sleep(for: .milliseconds(200))

        // Both servers are asked, so only the handle decides which call ends.
        let cancelled = Date()
        await startedA.cancel(request: "a")
        await startedB.cancel(request: "a")
        let result = try await waitA.value
        XCTAssertLessThan(Date().timeIntervalSince(cancelled), 2)
        XCTAssertEqual(result.isError, true)
        XCTAssertEqual(result.content, [.text("Cancelled.")])

        try await Task.sleep(for: .seconds(1))
        let events = await observer.events
        XCTAssertEqual(events, ["fail Wait A"], "the call with handle b must still be running")

        await startedA.shutdown()
        await startedB.shutdown()
        await clientA.disconnect()
        await clientB.disconnect()
        _ = await waitB.value
    }

    func testCancelBeforeRegistrationStillCancels() async throws {
        let (client, started) = try await connect(await DesktopMCP.makeRegistry(), handle: "x")
        defer { Task { await started.shutdown() } }

        // The connection dropped before the call reached the tool: it must still not run.
        await started.cancel(request: "x")
        let sent = Date()
        let result = try await client.callTool(name: "Wait", arguments: ["duration": 30])
        XCTAssertLessThan(Date().timeIntervalSince(sent), 2)
        XCTAssertEqual(result.isError, true)
        XCTAssertEqual(result.content, [.text("Cancelled.")])
    }

    func testCallsWithoutAHandleAreUnaffectedByCancelByHandle() async throws {
        let (client, started) = try await connect(await DesktopMCP.makeRegistry())
        defer { Task { await started.shutdown() } }

        await started.cancel(request: "nope")
        let result = try await client.callTool(name: "Wait", arguments: ["duration": 0])
        XCTAssertNotEqual(result.isError, true)
        XCTAssertTrue(text(result.content)?.hasPrefix("Waited") == true, "\(result.content)")
    }
}
