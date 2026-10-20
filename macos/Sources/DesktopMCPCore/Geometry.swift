import AppKit
import CoreGraphics
import Foundation

/// A rectangle in macOS logical (point) coordinates, top-left origin.
public struct Rect: Equatable, Sendable {
    public var left: Double
    public var top: Double
    public var right: Double
    public var bottom: Double

    public init(left: Double, top: Double, right: Double, bottom: Double) {
        self.left = left
        self.top = top
        self.right = right
        self.bottom = bottom
    }

    public init(position: CGPoint, size: CGSize) {
        self.init(left: position.x, top: position.y,
                  right: position.x + size.width, bottom: position.y + size.height)
    }

    public var width: Double { right - left }
    public var height: Double { bottom - top }
}

/// Integer box, as reported in snapshots. Mirrors the Python `BoundingBox`.
public struct BoundingBox: Equatable, Hashable, Sendable {
    public var left: Int
    public var top: Int
    public var right: Int
    public var bottom: Int
    public var width: Int
    public var height: Int

    public static let empty = BoundingBox(left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0)

    public init(left: Int, top: Int, right: Int, bottom: Int, width: Int, height: Int) {
        self.left = left
        self.top = top
        self.right = right
        self.bottom = bottom
        self.width = width
        self.height = height
    }

    public init(_ rect: Rect) {
        func safe(_ v: Double) -> Int { v.isNaN || v.isInfinite ? 0 : Int(v) }
        self.init(left: safe(rect.left), top: safe(rect.top), right: safe(rect.right),
                  bottom: safe(rect.bottom), width: safe(rect.width), height: safe(rect.height))
    }

    public var center: Center {
        Center(x: left + floorDiv(width, 2), y: top + floorDiv(height, 2))
    }

    /// The part of `element` that lies inside `self`, or `.empty` when they do not overlap.
    public func intersection(_ element: BoundingBox) -> BoundingBox {
        let l = max(left, element.left)
        let t = max(top, element.top)
        let r = min(right, element.right)
        let b = min(bottom, element.bottom)
        guard r > l, b > t else { return .empty }
        return BoundingBox(left: l, top: t, right: r, bottom: b, width: r - l, height: b - t)
    }
}

public struct Center: Equatable, Sendable {
    public var x: Int
    public var y: Int
    public var description: String { "(\(x),\(y))" }
}

/// Python-style floor division.
@inline(__always) func floorDiv(_ a: Int, _ b: Int) -> Int {
    let q = a / b
    return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q
}

/// Geometry of one active display.
public struct DisplayInfo: Sendable {
    public var id: CGDirectDisplayID
    public var logicalLeft: Double
    public var logicalTop: Double
    public var logicalWidth: Double
    public var logicalHeight: Double
    public var pixelWidth: Int
    public var pixelHeight: Int
    public var scale: Double
    /// Horizontal offset of this display inside a stitched all-displays capture.
    public var pixelLeft: Int = 0

    public func contains(x: Double, y: Double) -> Bool {
        logicalLeft <= x && x < logicalLeft + logicalWidth
            && logicalTop <= y && y < logicalTop + logicalHeight
    }
}

/// One display as windows see it: points, origin top-left of the main display, y down (the
/// Accessibility system, like `WindowInfo.frame`).
public struct DisplayFrame: Sendable, Equatable {
    /// Position in `Displays.all()` order (left to right).
    public var index: Int
    public var name: String
    public var frame: CGRect
    /// The frame without the menu bar and the Dock.
    public var visible: CGRect
    public var scale: Double
    public var main: Bool

    public init(index: Int, name: String, frame: CGRect, visible: CGRect, scale: Double, main: Bool) {
        self.index = index
        self.name = name
        self.frame = frame
        self.visible = visible
        self.scale = scale
        self.main = main
    }

    /// From `NSScreen` rects (origin bottom-left of the main display, y up); `mainHeight` is the
    /// main display's height.
    public static func fromCocoa(index: Int, name: String, frame: CGRect, visible: CGRect,
                                 mainHeight: Double, scale: Double, main: Bool) -> DisplayFrame {
        func flip(_ r: CGRect) -> CGRect {
            CGRect(x: r.origin.x, y: mainHeight - (r.origin.y + r.height), width: r.width, height: r.height)
        }
        return DisplayFrame(index: index, name: name, frame: flip(frame), visible: flip(visible), scale: scale, main: main)
    }
}

public enum Displays {
    /// Every screen with its visible frame, in `all()` order. Reads `NSScreen`, so main actor.
    @MainActor
    public static func frames() -> [DisplayFrame] {
        let screens = NSScreen.screens
        // The first screen is the one with the menu bar, whose bottom-left is the Cocoa origin.
        guard let mainHeight = screens.first?.frame.height else { return [] }
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        let byID = Dictionary(screens.compactMap { screen in
            (screen.deviceDescription[key] as? NSNumber).map { ($0.uint32Value, screen) }
        }, uniquingKeysWith: { first, _ in first })
        let mainID = CGMainDisplayID()
        return all().enumerated().compactMap { index, display in
            guard let screen = byID[display.id] else { return nil }
            return .fromCocoa(index: index, name: screen.localizedName, frame: screen.frame,
                              visible: screen.visibleFrame, mainHeight: mainHeight,
                              scale: screen.backingScaleFactor, main: display.id == mainID)
        }
    }

    /// Active displays sorted left to right, with `pixelLeft` filled in for a left-to-right stitch.
    public static func all() -> [DisplayInfo] {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return [] }
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetActiveDisplayList(count, &ids, &count) == .success else { return [] }
        var displays = ids.prefix(Int(count)).map { id -> DisplayInfo in
            let bounds = CGDisplayBounds(id)
            let mode = CGDisplayCopyDisplayMode(id)
            let pw = mode?.pixelWidth ?? CGDisplayPixelsWide(id)
            let ph = mode?.pixelHeight ?? CGDisplayPixelsHigh(id)
            return DisplayInfo(
                id: id,
                logicalLeft: bounds.origin.x, logicalTop: bounds.origin.y,
                logicalWidth: bounds.width, logicalHeight: bounds.height,
                pixelWidth: pw, pixelHeight: ph,
                scale: bounds.width > 0 ? Double(pw) / bounds.width : 1.0
            )
        }
        displays.sort { $0.logicalLeft < $1.logicalLeft }
        var acc = 0
        for i in displays.indices {
            displays[i].pixelLeft = acc
            acc += displays[i].pixelWidth
        }
        return displays
    }

    public static func containing(x: Double, y: Double, in displays: [DisplayInfo]? = nil) -> DisplayInfo? {
        (displays ?? all()).first { $0.contains(x: x, y: y) }
    }

    /// Virtual screen size (all displays combined) in logical points.
    public static func virtualScreenSize() -> (width: Int, height: Int) {
        let union = all().reduce(CGRect.null) {
            $0.union(CGRect(x: $1.logicalLeft, y: $1.logicalTop, width: $1.logicalWidth, height: $1.logicalHeight))
        }
        return union.isNull ? (0, 0) : (Int(union.width), Int(union.height))
    }
}
