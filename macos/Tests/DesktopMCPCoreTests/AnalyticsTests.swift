import MCP
import XCTest
@testable import DesktopMCPCore

final class AnalyticsTests: XCTestCase {
    actor Outbox {
        var requests: [URLRequest] = []
        func add(_ request: URLRequest) { requests.append(request) }
    }

    func testEventsMatchTheOriginalPostHogPayload() async throws {
        let outbox = Outbox()
        let analytics = PostHogAnalytics(send: { await outbox.add($0) })
        let client = Client.Info(name: "claude-code", version: "2.1")
        // Events captured before delivery starts are queued, not lost.
        await analytics.toolSucceeded("Click", durationMs: 42, client: client)
        let delivery = Task { await analytics.run() }
        await analytics.toolFailed("Type", error: "boom", durationMs: 7, client: nil)
        await analytics.close()
        await delivery.value

        let requests = await outbox.requests
        XCTAssertEqual(requests.count, 2)
        XCTAssertEqual(requests[0].url?.absoluteString, "https://us.i.posthog.com/batch/")
        XCTAssertEqual(requests[0].httpMethod, "POST")

        let bodies = try requests.map { try JSON.parse($0.httpBody!) }
        let byEvent = Dictionary(uniqueKeysWithValues: bodies.map { ($0["batch"]!.arrayValue![0]["event"]!.stringValue!, $0) })
        let executed = try XCTUnwrap(byEvent["tool_executed"])
        XCTAssertEqual(executed["api_key"]?.stringValue, PostHogAnalytics.apiKey)
        let event = executed["batch"]!.arrayValue![0]
        let properties = event["properties"]!
        XCTAssertEqual(properties["tool_name"], "Click")
        XCTAssertEqual(properties["duration_ms"], 42)
        XCTAssertEqual(properties["success"], true)
        XCTAssertEqual(properties["client_name"], "claude-code")
        XCTAssertEqual(properties["client_version"], "2.1")
        XCTAssertEqual(properties["process_person_profile"], true)
        XCTAssertTrue(properties["session_id"]?.stringValue?.hasPrefix("mcp_") == true)
        XCTAssertNotNil(event["distinct_id"]?.stringValue)
        XCTAssertEqual(properties["$lib"], "desktop-mcp-swift")
        XCTAssertNotNil(ISO8601DateFormatter().date(from: event["timestamp"]?.stringValue ?? ""), "\(event)")
        XCTAssertEqual(Set(properties.objectValue!.keys), ["tool_name", "duration_ms", "success", "client_name", "client_version",
                                                           "session_id", "process_person_profile", "$lib", "$lib_version"])

        let failed = try XCTUnwrap(byEvent["exception"])["batch"]!.arrayValue![0]["properties"]!
        XCTAssertEqual(failed["exception"], "boom")
        XCTAssertEqual(failed["tool_name"], "Type")
        XCTAssertNil(failed["client_name"])
    }

    func testCloseWaitsForSendsOnlyUpToTheTimeout() async {
        let analytics = PostHogAnalytics(send: { _ in try? await Task.sleep(for: .seconds(30)) })
        let delivery = Task { await analytics.run() }
        await analytics.toolSucceeded("Wait", durationMs: 1, client: nil)
        let started = Date()
        await analytics.close(timeout: 0.3)
        XCTAssertLessThan(Date().timeIntervalSince(started), 3)
        // The hosting group decides the fate of the stuck send; cancelling it ends `run()`.
        delivery.cancel()
        await delivery.value
    }

    func testOptIn() {
        XCTAssertFalse(PostHogAnalytics.isEnabled([:]))
        XCTAssertFalse(PostHogAnalytics.isEnabled(["ANONYMIZED_TELEMETRY": "false"]))
        XCTAssertFalse(PostHogAnalytics.isEnabled(["ANONYMIZED_TELEMETRY": "1"]))
        XCTAssertTrue(PostHogAnalytics.isEnabled(["ANONYMIZED_TELEMETRY": "True"]))
    }

    func testUUID7Shape() {
        let id = PostHogAnalytics.uuid7()
        XCTAssertNotNil(UUID(uuidString: id))
        XCTAssertEqual(Array(id)[14], "7")
        XCTAssertTrue("89ab".contains(Array(id)[19]))
        // Time-ordered: the leading 48 bits are the current Unix milliseconds.
        let millis = UInt64(id.replacingOccurrences(of: "-", with: "").prefix(12), radix: 16)!
        XCTAssertLessThan(abs(Double(millis) - Date().timeIntervalSince1970 * 1000), 5000)
    }
}
