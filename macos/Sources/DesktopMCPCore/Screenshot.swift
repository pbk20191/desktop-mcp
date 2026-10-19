import CoreGraphics
import CoreText
import Foundation
import ImageIO
import ScreenCaptureKit

/// Screen capture and the numbered-box annotation drawn for `Snapshot(use_vision=True)`.
public enum Screenshot {
    static let padding = 5

    /// Asks for Screen Recording once if it has not been granted. Returns whether it is granted.
    @discardableResult
    public static func ensurePermission() -> Bool {
        if CGPreflightScreenCaptureAccess() { return true }
        return CGRequestScreenCaptureAccess()
    }

    /// Every display captured at native resolution, stitched left to right in the order of
    /// `displays` (which is how `DisplayInfo.pixelLeft` was computed).
    static func captureAll(_ displays: [DisplayInfo]) async -> CGImage? {
        guard !displays.isEmpty else { return nil }
        let images = await captureEach(displays)
        guard images.contains(where: { $0 != nil }) else { return nil }

        let width = displays.reduce(0) { $0 + $1.pixelWidth }
        let height = displays.map(\.pixelHeight).max() ?? 0
        guard let context = makeContext(width: width, height: height) else { return nil }
        context.setFillColor(CGColor(gray: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        for (display, image) in zip(displays, images) {
            guard let image else { continue }
            // Bottom-left origin: a display's top edge sits at the top of the canvas.
            context.draw(image, in: CGRect(x: display.pixelLeft, y: height - display.pixelHeight,
                                           width: display.pixelWidth, height: display.pixelHeight))
        }
        return context.makeImage()
    }

    private static func captureEach(_ displays: [DisplayInfo]) async -> [CGImage?] {
        if #available(macOS 14.0, *) {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                return await withTaskGroup(of: (Int, CGImage?).self) { group in
                    for (i, display) in displays.enumerated() {
                        guard let found = content.displays.first(where: { $0.displayID == display.id }) else { continue }
                        // SCDisplay is an immutable description; ScreenCaptureKit accepts it from any thread.
                        let scDisplay = UncheckedSendable(found)
                        group.addTask {
                            let filter = SCContentFilter(display: scDisplay.value, excludingWindows: [])
                            let config = SCStreamConfiguration()
                            config.width = display.pixelWidth
                            config.height = display.pixelHeight
                            config.showsCursor = false
                            let image = try? await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
                            return (i, image)
                        }
                    }
                    var images = [CGImage?](repeating: nil, count: displays.count)
                    for await (i, image) in group { images[i] = image }
                    return images
                }
            } catch {
                log("ScreenCaptureKit capture failed: \(error)")
            }
        }
        // macOS 13, or ScreenCaptureKit refused: the system screencapture tool.
        var images: [CGImage?] = []
        for display in displays {
            images.append(await captureWithTool(display))
        }
        return images
    }

    private static func captureWithTool(_ display: DisplayInfo) async -> CGImage? {
        var count: UInt32 = 0
        CGGetActiveDisplayList(0, nil, &count)
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        CGGetActiveDisplayList(count, &ids, &count)
        guard let index = ids.firstIndex(of: display.id) else { return nil }
        let path = NSTemporaryDirectory() + "desktop-mcp-\(UUID().uuidString).png"
        defer { unlink(path) }
        guard let run = try? await Shell.run(["/usr/sbin/screencapture", "-x", "-D", "\(index + 1)", "-t", "png", path],
                                             timeout: 10),
              run.status == 0,
              let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil) else { return nil }
        return CGImageSourceCreateImageAtIndex(source, 0, nil)
    }

    /// Captures the screen and draws a numbered box over each node, ids matching the snapshot.
    /// `scale` < 1 shrinks the result (the snapshot passes 1/backing-scale to get logical points).
    public static func annotated(nodes: [TreeNode], scale: Double) async -> Data? {
        let displays = Displays.all()
        guard let shot = await captureAll(displays) else { return nil }
        let image = await offload { annotate(shot, nodes: nodes, displays: displays) }
        guard let image else { return nil }
        let final = scale > 0 && scale < 1 ? resize(image, scale: scale) ?? image : image
        return pngData(final)
    }

    static func annotate(_ shot: CGImage, nodes: [TreeNode], displays: [DisplayInfo]) -> CGImage? {
        let width = Int(Double(shot.width) + 1.5 * Double(padding))
        let height = Int(Double(shot.height) + 1.5 * Double(padding))
        guard let context = makeContext(width: width, height: height) else { return nil }
        context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.draw(shot, in: CGRect(x: padding, y: height - padding - shot.height, width: shot.width, height: shot.height))

        // Everything below is in top-left coordinates.
        context.translateBy(x: 0, y: CGFloat(height))
        context.scaleBy(x: 1, y: -1)
        context.textMatrix = CGAffineTransform(scaleX: 1, y: -1)

        let fontScale = displays.first?.scale ?? 1
        let font = CTFontCreateWithName("Helvetica" as CFString, CGFloat(max(12, Int(14 * fontScale))), nil)
        let virtualLeft = displays.first?.logicalLeft ?? 0
        let virtualTop = displays.map(\.logicalTop).min() ?? 0
        let averageScale = Double(shot.width) / max(displays.reduce(0) { $0 + $1.logicalWidth }, 1)

        func toPixels(_ x: Double, _ y: Double, on display: DisplayInfo?) -> (Int, Int) {
            if let d = display {
                return (d.pixelLeft + Int((x - d.logicalLeft) * d.scale) + padding,
                        Int((y - d.logicalTop) * d.scale) + padding)
            }
            return (Int((x - virtualLeft) * averageScale) + padding, Int((y - virtualTop) * averageScale) + padding)
        }

        var seen = Set<BoundingBox>()
        for (label, node) in nodes.enumerated() {
            let box = node.boundingBox
            guard box.width > 0, box.height > 0, seen.insert(box).inserted else { continue }
            let cx = Double(box.left + box.right) / 2
            let cy = Double(box.top + box.bottom) / 2
            let display = displays.first { $0.contains(x: cx, y: cy) }
            let (x1, y1) = toPixels(Double(box.left), Double(box.top), on: display)
            let (x2, y2) = toPixels(Double(box.right), Double(box.bottom), on: display)

            let color = labelColor(label)
            context.setStrokeColor(color)
            context.setLineWidth(2)
            context.stroke(CGRect(x: x1, y: y1, width: x2 - x1, height: y2 - y1).insetBy(dx: 1, dy: 1))

            let attributed = NSAttributedString(string: String(label), attributes: [
                NSAttributedString.Key(kCTFontAttributeName as String): font,
                NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(red: 1, green: 1, blue: 1, alpha: 1),
            ])
            let line = CTLineCreateWithAttributedString(attributed)
            var ascent: CGFloat = 0, descent: CGFloat = 0, leading: CGFloat = 0
            let textWidth = Int(CTLineGetTypographicBounds(line, &ascent, &descent, &leading).rounded(.up))
            let textHeight = Int((ascent + descent).rounded(.up))

            // Tag above the box's right edge, or below when there is no room.
            let tagX = x2 - textWidth - 4
            var tagY = y1 - textHeight - 4
            if tagY < padding { tagY = y2 }
            context.setFillColor(color)
            context.fill(CGRect(x: tagX, y: tagY, width: textWidth + 4, height: textHeight + 4))
            context.textPosition = CGPoint(x: CGFloat(tagX + 2), y: CGFloat(tagY + 2) + ascent)
            CTLineDraw(line, context)
        }
        return context.makeImage()
    }

    /// A stable, readable colour per label (each channel 50...255).
    static func labelColor(_ label: Int) -> CGColor {
        var state = UInt64(truncatingIfNeeded: label) &+ 0x9E37_79B9_7F4A_7C15
        func next() -> CGFloat {
            state = (state ^ (state >> 30)) &* 0xBF58_476D_1CE4_E5B9
            state = (state ^ (state >> 27)) &* 0x94D0_49BB_1331_11EB
            state ^= state >> 31
            return CGFloat(50 + state % 206) / 255
        }
        return CGColor(red: next(), green: next(), blue: next(), alpha: 1)
    }

    static func makeContext(width: Int, height: Int) -> CGContext? {
        guard width > 0, height > 0 else { return nil }
        return CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                         space: CGColorSpace(name: CGColorSpace.sRGB)!,
                         bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
    }

    static func resize(_ image: CGImage, scale: Double) -> CGImage? {
        let width = max(1, Int(Double(image.width) * scale))
        let height = max(1, Int(Double(image.height) * scale))
        guard let context = makeContext(width: width, height: height) else { return nil }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage()
    }

    public static func pngData(_ image: CGImage) -> Data? {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, "public.png" as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, image, nil)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }
}
