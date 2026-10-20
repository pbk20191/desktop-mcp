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
        for kind in ["any", "pdf"] {
            do {
                _ = try await SpotlightTool.tool().handler(ToolArguments(["query": "kMDItem ==", "raw": true, "kind": .string(kind)]))
                XCTFail("expected an error")
            } catch {
                XCTAssertEqual(message(of: error), "Invalid Spotlight query: kMDItem ==", "kind \(kind)")
            }
        }
    }

    private func item(_ path: String, _ modified: TimeInterval?, size: Int? = 10, text: String? = nil) -> SpotlightTool.Item {
        SpotlightTool.Item(path: path, kind: "Plain Text", modified: modified.map(Date.init(timeIntervalSince1970:)),
                           size: size, text: text)
    }

    func testFormatSortsNewestFirstAndCountsTheRest() {
        let items = [
            item("/a.txt", 1_700_000_000),
            item("/b", 1_800_000_000, size: nil),
            item("/c.txt", 1_750_000_000, size: 2048),
        ]
        XCTAssertEqual(SpotlightTool.format(items, total: 4, content: false, stopped: false), """
        /b\tPlain Text\t2027-01-15T08:00:00Z\t-
        /c.txt\tPlain Text\t2025-06-15T15:06:40Z\t2048
        /a.txt\tPlain Text\t2023-11-14T22:13:20Z\t10
        1 more not shown
        """)
    }

    func testMissingDatePrintsDashAndSortsLast() {
        let items = [item("/undated", nil), item("/dated", 0)]
        XCTAssertEqual(SpotlightTool.format(items, total: 2, content: false, stopped: false), """
        /dated\tPlain Text\t1970-01-01T00:00:00Z\t10
        /undated\tPlain Text\t-\t10
        """)
    }

    func testStoppedSearchSaysSo() {
        XCTAssertEqual(SpotlightTool.format([item("/a", 0)], total: 5, content: false, stopped: true), """
        /a\tPlain Text\t1970-01-01T00:00:00Z\t10
        4 more not shown
        (search stopped after 10 s; results may be incomplete)
        """)
        XCTAssertEqual(SpotlightTool.format([], total: 0, content: false, stopped: true),
                       "No matches.\n(search stopped after 10 s; results may be incomplete)")
    }

    func testFormatWithContent() {
        let formatted = SpotlightTool.format([item("/a.txt", 0, text: "hello there"), item("/b.png", -1)],
                                             total: 2, content: true, stopped: false)
        XCTAssertEqual(formatted, """
        /a.txt\tPlain Text\t1970-01-01T00:00:00Z\t10
          hello there
        /b.png\tPlain Text\t1969-12-31T23:59:59Z\t10
        """)
        XCTAssertEqual(SpotlightTool.format([item("/a.txt", 0, text: "hello")], total: 1, content: false, stopped: false),
                       "/a.txt\tPlain Text\t1970-01-01T00:00:00Z\t10")
    }

    func testPreviewReadsTheStartOfATextFile() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("desktop-mcp-preview-\(UUID().uuidString).txt")
        defer { try? FileManager.default.removeItem(at: file) }
        var data = Data("first\nsecond\r\n".utf8)
        data.append(contentsOf: [0xFF, 0xFE])
        data.append(Data(String(repeating: "x", count: 10_000).utf8))
        try data.write(to: file)

        let preview = try XCTUnwrap(SpotlightTool.preview(path: file.path))
        XCTAssertEqual(preview.count, 300)
        XCTAssertTrue(preview.hasPrefix("first second  \u{FFFD}\u{FFFD}xxx"), preview)
        XCTAssertFalse(preview.contains("\n"))

        try Data("  \n\n ".utf8).write(to: file)
        XCTAssertNil(SpotlightTool.preview(path: file.path))
        XCTAssertNil(SpotlightTool.preview(path: file.path + ".missing"))
    }

    func testNoMatches() {
        XCTAssertEqual(SpotlightTool.format([], total: 0, content: false, stopped: false), "No matches.")
    }

    func testLiveQueryFindsSafari() async throws {
        let found = try await SpotlightTool.search(#"kMDItemFSName == "Safari.app""#,
                                                   scope: [URL(fileURLWithPath: "/Applications")], limit: 20, timeout: 10)
        guard !found.items.isEmpty else { throw XCTSkip("Spotlight returned nothing (indexing disabled?)") }
        XCTAssertEqual(found.items.count, 1)
        XCTAssertEqual(found.total, 1)
        XCTAssertFalse(found.stopped)
        let safari = found.items[0]
        XCTAssertTrue(safari.path.hasSuffix("/Safari.app"), safari.path)
        XCTAssertFalse(safari.kind.isEmpty)
        XCTAssertNotNil(safari.modified)
    }

    func testLiveQueryIsNewestFirstAndLimited() async throws {
        let found = try await SpotlightTool.search(#"kMDItemContentTypeTree == "com.apple.application""#,
                                                   scope: [URL(fileURLWithPath: "/Applications")], limit: 5, timeout: 10)
        guard found.items.count > 1 else { throw XCTSkip("Spotlight returned too little (indexing disabled?)") }
        XCTAssertLessThanOrEqual(found.items.count, 5)
        XCTAssertGreaterThanOrEqual(found.total, found.items.count)
        let dates = found.items.compactMap(\.modified)
        XCTAssertEqual(dates, dates.sorted(by: >))
    }

    func testLiveSearchStopsAtTheDeadline() async throws {
        let start = Date()
        let found = try await SpotlightTool.search(try SpotlightTool.wordsQuery("a"),
                                                   scope: [FileManager.default.homeDirectoryForCurrentUser], limit: 5, timeout: 0.2)
        let elapsed = Date().timeIntervalSince(start)
        if !found.stopped && found.total == 0 { throw XCTSkip("Spotlight returned nothing (indexing disabled?)") }
        XCTAssertTrue(found.stopped)
        XCTAssertLessThan(elapsed, 3)
        XCTAssertLessThanOrEqual(found.items.count, 5)
    }
}
