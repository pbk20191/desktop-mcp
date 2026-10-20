import CoreGraphics
import CoreText
import Foundation
import MCP
import Vision
import XCTest
@testable import DesktopMCPCore

final class OCRToolTests: XCTestCase {
    // MARK: Coordinates

    func testNormalizedBoxMapsToScreenPoints() {
        // A Retina (scale 2) capture of a region whose top-left is (100, 50) in points.
        let box = OCRTool.toScreen(CGRect(x: 0, y: 0, width: 0.5, height: 0.5),
                                   imagePixels: CGSize(width: 200, height: 100),
                                   origin: CGPoint(x: 100, y: 50), scale: 2)
        XCTAssertEqual(box, CGRect(x: 100, y: 75, width: 50, height: 25))
    }

    func testSecondDisplayOffset() {
        // A display to the left of the main one, at scale 1.
        let box = OCRTool.toScreen(CGRect(x: 0.5, y: 0.5, width: 0.1, height: 0.1),
                                   imagePixels: CGSize(width: 1440, height: 900),
                                   origin: CGPoint(x: -1440, y: 0), scale: 1)
        XCTAssertEqual(box.origin.x, -720, accuracy: 1e-9)
        XCTAssertEqual(box.origin.y, 360, accuracy: 1e-9)
        XCTAssertEqual(box.width, 144, accuracy: 1e-9)
        XCTAssertEqual(box.height, 90, accuracy: 1e-9)
    }

    // MARK: Recognition

    /// "Hello 안녕하세요" in 48 pt black on white, 1200×200 pixels, and where the text was drawn
    /// (top-left origin, pixels).
    private func renderedText() throws -> (image: CGImage, textBox: CGRect) {
        let width = 1200, height = 200
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                              bytesPerRow: 0, space: space,
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let font = CTFontCreateWithName("AppleSDGothicNeo-Regular" as CFString, 48, nil)
        let attributes: [NSAttributedString.Key: Any] = [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(red: 0, green: 0, blue: 0, alpha: 1),
        ]
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: "Hello 안녕하세요", attributes: attributes))
        let baseline = CGPoint(x: 60, y: 80)
        context.textPosition = baseline
        // Before drawing: CTLineDraw advances the text position, which these bounds include.
        let bounds = CTLineGetImageBounds(line, context)
        CTLineDraw(line, context)
        let image = try XCTUnwrap(context.makeImage())
        // Core Graphics is bottom-left; flip to top-left pixels.
        let textBox = CGRect(x: bounds.minX, y: CGFloat(height) - bounds.maxY, width: bounds.width, height: bounds.height)
        return (image, textBox)
    }

    func testRecognizesRenderedKoreanAndEnglish() throws {
        let (image, textBox) = try renderedText()
        let found = try OCRTool.recognize(image, languages: ["ko-KR", "en-US"], fast: false, minConfidence: 0.3)
        let joined = found.map(\.text).joined(separator: " ")
        XCTAssertTrue(joined.contains("Hello"), joined)

        // Scale 1, origin 0: the converted centres are pixels and must land in the drawn text.
        let pixels = CGSize(width: image.width, height: image.height)
        for line in found {
            let box = OCRTool.toScreen(line.normalized, imagePixels: pixels, origin: .zero, scale: 1)
            XCTAssertTrue(textBox.insetBy(dx: -4, dy: -4).contains(CGPoint(x: box.midX, y: box.midY)),
                          "\(line.text) centre \(box) outside \(textBox)")
        }

        let supported = (try? VNRecognizeTextRequest().supportedRecognitionLanguages()) ?? []
        guard supported.contains("ko-KR") else {
            throw XCTSkip("Vision has no Korean text recognition on this machine")
        }
        XCTAssertTrue(joined.contains("안녕하세요"), joined)
    }

    func testMinConfidenceDropsLines() throws {
        let (image, _) = try renderedText()
        XCTAssertTrue(try OCRTool.recognize(image, languages: ["ko-KR", "en-US"], fast: false, minConfidence: 1.01).isEmpty)
    }

    // MARK: Output

    private func line(_ text: String, x: Double, y: Double, width: Double = 40, height: Double = 20,
                      confidence: Float = 0.9) -> OCRTool.Line {
        OCRTool.Line(text: text, box: CGRect(x: x, y: y, width: width, height: height), confidence: confidence)
    }

    func testOrderTopToBottomThenLeftToRight() {
        let lines = [line("x50", x: 50, y: 10), line("y40", x: 0, y: 40), line("x5", x: 5, y: 12)]
        XCTAssertEqual(OCRTool.order(lines).map(\.text), ["x5", "x50", "y40"])
    }

    func testFormatAndCap() {
        XCTAssertEqual(OCRTool.format([line("Hi", x: 100, y: 75, width: 50, height: 25, confidence: 0.93)]),
                       "[125,87] \"Hi\" (50×25, confidence 0.93)")

        let many = (0..<501).map { line("l\($0)", x: 0, y: Double($0) * 30) }
        let lines = OCRTool.format(many).components(separatedBy: "\n")
        XCTAssertEqual(lines.count, 501)
        XCTAssertTrue(lines[499].contains("\"l499\""), lines[499])
        XCTAssertEqual(lines.last, "1 more lines not shown")
        XCTAssertEqual(OCRTool.lineLimit, 500)

        XCTAssertEqual(OCRTool.format([]), "No text found.")
    }

    // MARK: Arguments

    func testRegionValidation() async {
        for region: [Value] in [[0, 0, 0, 10], [0, 0, 10, -1], [0, 0, 10]] {
            do {
                _ = try await OCRTool.tool().handler(ToolArguments(["region": .array(region)]))
                XCTFail("region \(region) must be refused")
            } catch {
                XCTAssertEqual("\(error)", "region must be [x, y, width, height] with positive width and height")
            }
        }
    }
}
