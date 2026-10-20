import Foundation
import MCP
import XCTest
@testable import DesktopMCPCore

final class SpotlightToolTests: XCTestCase {
    private func message(of error: Error) -> String { (error as? ToolError)?.message ?? "\(error)" }

    func testWordsQuery() throws {
        XCTAssertEqual(
            try SpotlightTool.wordsQuery("tax 2025"),
            #"(kMDItemDisplayName == "*tax*"cdw || kMDItemTextContent == "tax"cdw) && (kMDItemDisplayName == "*2025*"cdw || kMDItemTextContent == "2025"cdw)"#)
    }

    func testWordsWithQuerySyntaxAreEscaped() throws {
        XCTAssertEqual(
            try SpotlightTool.wordsQuery(#"a"b\c*="#),
            #"(kMDItemDisplayName == "*a\"b\\c\*=*"cdw || kMDItemTextContent == "a\"b\\c\*="cdw)"#)
    }

    func testEmptyQueryErrors() {
        for query in ["", "   \n\t"] {
            XCTAssertThrowsError(try SpotlightTool.wordsQuery(query)) { error in
                XCTAssertEqual(message(of: error), "query is required.")
            }
        }
    }

    func testKindClauses() {
        XCTAssertEqual(SpotlightTool.kindClause("pdf"), #"kMDItemContentTypeTree == "com.adobe.pdf""#)
        XCTAssertNil(SpotlightTool.kindClause("any"))
        XCTAssertEqual(SpotlightTool.kindClause("document"), #"kMDItemContentTypeTree == "public.content""#)
        XCTAssertEqual(SpotlightTool.kindClause("image"), #"kMDItemContentTypeTree == "public.image""#)
        XCTAssertEqual(SpotlightTool.kindClause("folder"), #"kMDItemContentTypeTree == "public.folder""#)
        XCTAssertEqual(SpotlightTool.kindClause("application"), #"kMDItemContentTypeTree == "com.apple.application""#)
    }

    func testInvalidRawQueryErrors() async {
        do {
            _ = try await SpotlightTool.tool().handler(ToolArguments(["query": "kMDItem ==", "raw": true]))
            XCTFail("expected an error")
        } catch {
            XCTAssertEqual(message(of: error), "Invalid Spotlight query: kMDItem ==")
        }
    }

    private func item(_ path: String, _ modified: TimeInterval, size: Int? = 10, text: String? = nil) -> SpotlightTool.Item {
        SpotlightTool.Item(path: path, kind: "Plain Text", modified: Date(timeIntervalSince1970: modified), size: size, text: text)
    }

    func testFormatSortsNewestFirstAndTruncates() {
        let items = [
            item("/a.txt", 1_700_000_000),
            item("/b", 1_800_000_000, size: nil),
            item("/c.txt", 1_750_000_000, size: 2048),
        ]
        XCTAssertEqual(SpotlightTool.format(items, limit: 2, content: false), """
        /b\tPlain Text\t2027-01-15T08:00:00Z\t-
        /c.txt\tPlain Text\t2025-06-15T15:06:40Z\t2048
        1 more not shown
        """)
    }

    func testFormatWithContent() {
        let text = "first\nsecond " + String(repeating: "x", count: 400)
        let formatted = SpotlightTool.format([item("/a.txt", 0, text: text)], limit: 20, content: true)
        let lines = formatted.components(separatedBy: "\n")
        XCTAssertEqual(lines.count, 2)
        XCTAssertEqual(lines[0], "/a.txt\tPlain Text\t1970-01-01T00:00:00Z\t10")
        XCTAssertEqual(lines[1], "  " + String(("first second " + String(repeating: "x", count: 400)).prefix(300)))
        XCTAssertEqual(lines[1].count, 302)

        XCTAssertEqual(SpotlightTool.format([item("/a.txt", 0, text: text)], limit: 20, content: false),
                       "/a.txt\tPlain Text\t1970-01-01T00:00:00Z\t10")
    }

    func testNoMatches() {
        XCTAssertEqual(SpotlightTool.format([], limit: 20, content: false), "No matches.")
    }

    func testLiveQueryFindsACreatedFile() async throws {
        let directory = FileManager.default.temporaryDirectory
        let name = "desktop-mcp-spotlight-\(UUID().uuidString).txt"
        let file = directory.appendingPathComponent(name)
        try "desktop-mcp spotlight test".write(to: file, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: file) }

        let deadline = Date().addingTimeInterval(5)
        repeat {
            let items = try await SpotlightTool.search(try SpotlightTool.wordsQuery(name), scope: [directory], timeout: 10)
            if let found = items.first(where: { $0.path.hasSuffix("/" + name) }) {
                XCTAssertEqual(found.size, 26)
                return
            }
            try await Task.sleep(nanoseconds: 250_000_000)
        } while Date() < deadline
        throw XCTSkip("not indexed yet")
    }
}
