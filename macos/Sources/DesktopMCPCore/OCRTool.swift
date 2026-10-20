import CoreGraphics
import Foundation
import MCP
import Vision

/// Reads the text on screen with Vision and reports where each line is, in the screen points
/// `Click` takes. Opt-in (`DesktopMCP.optInTools`): recognition is slow.
enum OCRTool {
    /// One recognized line, `box` in screen points (origin top-left of the main display).
    struct Line: Sendable, Equatable {
        var text: String
        var box: CGRect
        var confidence: Float
    }

    static let lineLimit = 500
    static let defaultLanguages = ["ko-KR", "en-US"]
    static let levels = ["accurate", "fast"]
    static let regionError = "region must be [x, y, width, height] with positive width and height"
    static let captureError = "Screen capture failed. If this persists, grant Screen Recording in System Settings > Privacy & Security."

    /// Text lines in `image` at or above `minConfidence`, each with Vision's normalized,
    /// bottom-left-origin box.
    static func recognize(_ image: CGImage, languages: [String], fast: Bool,
                          minConfidence: Float) throws -> [(text: String, normalized: CGRect, confidence: Float)] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = fast ? .fast : .accurate
        request.recognitionLanguages = languages
        request.usesLanguageCorrection = true
        do {
            try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
        } catch {
            throw ToolError("Text recognition failed: \(error.localizedDescription)", telemetry: "Text recognition failed")
        }
        return (request.results ?? []).compactMap { observation in
            guard let best = observation.topCandidates(1).first, best.confidence >= minConfidence else { return nil }
            return (best.string, observation.boundingBox, best.confidence)
        }
    }

    /// A Vision box (normalized, bottom-left origin) in an image of `imagePixels` that shows the
    /// screen from `origin` (points, top-left) at `scale` pixels per point, as screen points.
    static func toScreen(_ normalized: CGRect, imagePixels: CGSize, origin: CGPoint, scale: Double) -> CGRect {
        let w = imagePixels.width, h = imagePixels.height
        return CGRect(x: origin.x + normalized.minX * w / scale,
                      y: origin.y + (1 - normalized.minY - normalized.height) * h / scale,
                      width: normalized.width * w / scale,
                      height: normalized.height * h / scale)
    }

    /// Top to bottom, then left to right: lines whose vertical centres differ by less than half
    /// the median line height share a row.
    static func order(_ lines: [Line]) -> [Line] {
        guard !lines.isEmpty else { return [] }
        let heights = lines.map(\.box.height).sorted()
        let tolerance = heights[heights.count / 2] / 2
        var rows: [[Line]] = []
        for line in lines.sorted(by: { $0.box.midY < $1.box.midY }) {
            if let anchor = rows.last?.first, abs(line.box.midY - anchor.box.midY) < tolerance {
                rows[rows.count - 1].append(line)
            } else {
                rows.append([line])
            }
        }
        return rows.flatMap { $0.sorted { $0.box.minX < $1.box.minX } }
    }

    /// `[cx,cy] "<text>" (w×h, confidence 0.93)` per line, at most `lineLimit` lines.
    static func format(_ lines: [Line]) -> String {
        guard !lines.isEmpty else { return "No text found." }
        var out = lines.prefix(lineLimit).map { line in
            let cx = Int(line.box.midX.rounded(.down)), cy = Int(line.box.midY.rounded(.down))
            let w = Int(line.box.width.rounded()), h = Int(line.box.height.rounded())
            return "[\(cx),\(cy)] \"\(line.text)\" (\(w)×\(h), confidence \(String(format: "%.2f", line.confidence)))"
        }
        if lines.count > lineLimit { out.append("\(lines.count - lineLimit) more lines not shown") }
        return out.joined(separator: "\n")
    }

    /// The focused window's frame, else the main display's, in screen points.
    private static func defaultRegion() async -> CGRect {
        let window = await offload { () -> CGRect? in
            guard let rect = App.frontmost()?.mainWindow.flatMap(AX.rect), rect.width > 0, rect.height > 0 else { return nil }
            return CGRect(x: rect.left, y: rect.top, width: rect.width, height: rect.height)
        }
        return window ?? CGDisplayBounds(CGMainDisplayID())
    }

    private static func read(region: CGRect, languages: [String], fast: Bool, minConfidence: Float) async throws -> [Line] {
        let displays = Displays.all().filter {
            CGRect(x: $0.logicalLeft, y: $0.logicalTop, width: $0.logicalWidth, height: $0.logicalHeight).intersects(region)
        }
        guard !displays.isEmpty else { throw ToolError("region is outside every display.") }
        guard CGPreflightScreenCaptureAccess() else { throw ToolError(captureError) }

        var lines: [Line] = []
        var captured = false
        for display in displays {
            guard let shot = await Screenshot.capture(display) else { continue }
            captured = true
            let bounds = CGRect(x: display.logicalLeft, y: display.logicalTop,
                                width: display.logicalWidth, height: display.logicalHeight)
            let part = bounds.intersection(region)
            // Pixels per point of this capture (the screencapture fallback may differ from the mode).
            let scale = Double(shot.width) / display.logicalWidth
            let x0 = max(0, ((part.minX - bounds.minX) * scale).rounded(.down))
            let y0 = max(0, ((part.minY - bounds.minY) * scale).rounded(.down))
            let x1 = min(Double(shot.width), ((part.maxX - bounds.minX) * scale).rounded(.up))
            let y1 = min(Double(shot.height), ((part.maxY - bounds.minY) * scale).rounded(.up))
            guard x1 > x0, y1 > y0,
                  let image = shot.cropping(to: CGRect(x: x0, y: y0, width: x1 - x0, height: y1 - y0)) else { continue }
            // Where the cropped pixels start, in points (the intersection's top-left, to the pixel).
            let origin = CGPoint(x: bounds.minX + x0 / scale, y: bounds.minY + y0 / scale)
            let pixels = CGSize(width: image.width, height: image.height)
            let found = try await offload {
                try recognize(image, languages: languages, fast: fast, minConfidence: minConfidence)
            }
            lines += found.map {
                Line(text: $0.text, box: toScreen($0.normalized, imagePixels: pixels, origin: origin, scale: scale),
                     confidence: $0.confidence)
            }
        }
        guard captured else { throw ToolError(captureError) }
        return order(lines)
    }

    static func tool() -> Tool {
        Tool(
            name: "OCR",
            description: "Reads the text shown on screen with on-device text recognition and returns one line per text line, top to bottom: '[cx,cy] \"<text>\" (w×h, confidence 0.93)', where [cx,cy] is the line's centre in the screen coordinates Click takes. 'region' [x, y, width, height] in screen points (default the focused window, else the main display); 'languages' (default [\"ko-KR\", \"en-US\"]); 'level' 'accurate' (default) or 'fast'; lines below 'min_confidence' (0-1, default 0.3) are dropped. Needs Screen Recording.",
            inputSchema: DesktopMCP.schema([
                "region": ["anyOf": [["type": "array", "items": ["type": "integer"], "minItems": 4, "maxItems": 4],
                                     ["type": "null"]], "default": nil],
                "languages": ["type": "array", "items": ["type": "string"],
                              "default": .array(defaultLanguages.map { .string($0) })],
                "level": DesktopMCP.string(default: "accurate", enum: levels),
                "min_confidence": ["type": "number", "minimum": 0, "maximum": 1, "default": 0.3],
            ]),
            annotations: ToolAnnotations(title: "OCR", readOnly: true, destructive: false, idempotent: false, openWorld: false),
            handler: { args in
                var region: CGRect?
                if let values = try args.intList("region") {
                    guard values.count == 4, values[2] > 0, values[3] > 0 else { throw ToolError(regionError) }
                    region = CGRect(x: values[0], y: values[1], width: values[2], height: values[3])
                }
                let languages = try args.stringList("languages") ?? defaultLanguages
                let fast = try args.choice("level", in: levels, default: "accurate") == "fast"
                let minConfidence = try args.double("min_confidence") ?? 0.3
                guard (0...1).contains(minConfidence) else { throw ToolError("min_confidence must be between 0 and 1") }

                let area = if let region { region } else { await defaultRegion() }
                let lines = try await read(region: area, languages: languages, fast: fast, minConfidence: Float(minConfidence))
                return [.text(format(lines))]
            })
    }
}
