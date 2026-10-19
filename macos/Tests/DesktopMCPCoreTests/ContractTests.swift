import Foundation
import DesktopMCPCore
import XCTest

/// `spec/tools.json` at the repository root is the tool contract every desktop-mcp port must
/// match. This port generated it; this test fails when `tools/list` drifts from it, so a change
/// to a tool's name, parameters or description is a deliberate change to the contract.
final class ContractTests: XCTestCase {
    static let specURL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()  // DesktopMCPCoreTests
        .deletingLastPathComponent()  // Tests
        .deletingLastPathComponent()  // macos
        .deletingLastPathComponent()  // repository root
        .appendingPathComponent("spec/tools.json")

    func testToolsListMatchesTheSharedContract() async throws {
        let spec = try JSONValue.parse(Data(contentsOf: Self.specURL))
        let expected = try XCTUnwrap(spec["tools"]?.arrayValue, "spec/tools.json has no tools array")

        let server = await DesktopMCP.makeServer()
        let response = await server.respond(to: ["jsonrpc": "2.0", "id": 1, "method": "tools/list"], client: nil)
        let actual = try XCTUnwrap(response?["result"]?["tools"]?.arrayValue)

        let byName = { (tools: [JSONValue]) in Dictionary(uniqueKeysWithValues: tools.map { ($0["name"]?.stringValue ?? "", $0) }) }
        let expectedByName = byName(expected), actualByName = byName(actual)
        XCTAssertEqual(Set(actualByName.keys), Set(expectedByName.keys), "tool set differs from spec/tools.json")
        for (name, tool) in expectedByName {
            XCTAssertEqual(actualByName[name], tool, "\(name) differs from spec/tools.json")
        }
    }
}
