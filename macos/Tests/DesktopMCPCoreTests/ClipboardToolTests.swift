import AppKit
import ImageIO
import MCP
import UniformTypeIdentifiers
import XCTest
@testable import DesktopMCPCore

final class ClipboardToolTests: XCTestCase {
    private var board = NSPasteboard.Name("desktop-mcp-test-\(UUID())")

    override func setUp() {
        board = NSPasteboard.Name("desktop-mcp-test-\(UUID())")
    }

    override func tearDown() {
        NSPasteboard(name: board).releaseGlobally()
    }

    private func texts(_ content: [MCP.Tool.Content]) -> [String] {
        content.compactMap { if case .text(let text, _, _) = $0 { text } else { nil } }
    }

    private func temporaryFile(_ fileName: String) throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        let file = dir.appendingPathComponent(fileName)
        try Data("x".utf8).write(to: file)
        return file
    }

    private func writeError(text: String? = nil, paths: [String]? = nil) async -> String? {
        do { _ = try await ClipboardTool.write(text: text, paths: paths, to: board); return nil } catch { return "\(error)" }
    }

    private func pngData(width: Int, height: Int) throws -> Data {
        let context = try XCTUnwrap(Screenshot.makeContext(width: width, height: height))
        context.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let image = try XCTUnwrap(context.makeImage())
        let data = NSMutableData()
        let destination = try XCTUnwrap(CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        return data as Data
    }

    func testReadEmpty() async {
        let content = await ClipboardTool.read(board)
        XCTAssertEqual(texts(content), ["The clipboard is empty."])
    }

    func testWriteThenReadText() async throws {
        let result = try await ClipboardTool.write(text: "안녕 hi", paths: nil, to: board)
        XCTAssertEqual(result, "Copied 5 characters.")
        let content = await ClipboardTool.read(board)
        XCTAssertEqual(texts(content), ["안녕 hi"])
    }

    func testTextIsTruncated() async throws {
        let long = String(repeating: "a", count: 100_010)
        _ = try await ClipboardTool.write(text: long, paths: nil, to: board)
        let content = await ClipboardTool.read(board)
        XCTAssertEqual(texts(content), [String(repeating: "a", count: 100_000) + "… (truncated, 100010 characters)"])
    }

    func testWritePathsRequiresExisting() async {
        let message = await writeError(paths: ["/nope"])
        XCTAssertEqual(message, "No such file: /nope")
    }

    func testWritePathsReadsBackAsFiles() async throws {
        let first = try temporaryFile("a.txt")
        let second = try temporaryFile("문서 b.txt")
        let result = try await ClipboardTool.write(text: nil, paths: [first.path, second.path], to: board)
        XCTAssertEqual(result, "Copied 2 files.")
        let content = await ClipboardTool.read(board)
        XCTAssertEqual(texts(content).count, 1)
        XCTAssertTrue(try XCTUnwrap(texts(content).first).contains("Files:\n\(first.path)\n\(second.path)"))
    }

    func testReadReportsTextAndFilesTogether() async throws {
        let file = try temporaryFile("a.txt")
        let pasteboard = NSPasteboard(name: board)
        pasteboard.clearContents()
        pasteboard.setString("a.txt", forType: .string)
        XCTAssertTrue(pasteboard.writeObjects([file as NSURL]))
        let content = await ClipboardTool.read(board)
        XCTAssertEqual(texts(content), ["a.txt\n\nFiles:\n\(file.path)"])
    }

    func testImageIsPNGScaledToMaxSide() async throws {
        let pasteboard = NSPasteboard(name: board)
        pasteboard.clearContents()
        XCTAssertTrue(pasteboard.setData(try pngData(width: 3000, height: 1000), forType: .png))
        let content = await ClipboardTool.read(board)
        XCTAssertEqual(texts(content), ["Image (1568×523)."])
        guard case .image(let base64, let mimeType, _, _) = content.last else { return XCTFail("no image") }
        XCTAssertEqual(mimeType, "image/png")
        let data = try XCTUnwrap(Data(base64Encoded: base64))
        let source = try XCTUnwrap(CGImageSourceCreateWithData(data as CFData, nil))
        let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        XCTAssertEqual(image.width, 1568)
        XCTAssertEqual(image.height, 523)
    }

    func testSmallImageIsNotScaled() async throws {
        let pasteboard = NSPasteboard(name: board)
        pasteboard.clearContents()
        XCTAssertTrue(pasteboard.setData(try pngData(width: 40, height: 20), forType: .png))
        let content = await ClipboardTool.read(board)
        XCTAssertEqual(texts(content), ["Image (40×20)."])
    }

    func testTextAndPathsAreExclusive() async {
        let message = await writeError(text: "a", paths: ["/tmp"])
        XCTAssertEqual(message, "Pass either text or paths, not both.")
    }

    func testWriteNeedsSomething() async {
        let message = await writeError()
        XCTAssertEqual(message, "write needs text or paths.")
    }

    func testToolWriteAndReadThroughHandler() async throws {
        let tool = ClipboardTool.tool(pasteboard: board)
        let written = try await tool.handler(ToolArguments(["mode": "write", "text": "hello"]))
        XCTAssertEqual(texts(written), ["Copied 5 characters."])
        let read = try await tool.handler(ToolArguments([:]))
        XCTAssertEqual(texts(read), ["hello"])
    }
}
