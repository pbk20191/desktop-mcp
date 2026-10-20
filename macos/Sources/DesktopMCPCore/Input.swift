import Carbon.HIToolbox
import CoreGraphics
import Foundation

/// Synthesised mouse and keyboard input via Quartz events.
public enum Input {
    static func post(_ event: CGEvent?) {
        event?.post(tap: .cghidEventTap)
    }

    static func pause(_ seconds: Double) async throws {
        if seconds > 0 { try await Task.sleep(for: .seconds(seconds)) }
    }

    // MARK: Mouse

    public static func cursorPosition() -> CGPoint {
        CGEvent(source: nil)?.location ?? .zero
    }

    public static func move(to x: Int, _ y: Int) {
        post(CGEvent(mouseEventSource: nil, mouseType: .mouseMoved,
                     mouseCursorPosition: CGPoint(x: x, y: y), mouseButton: .left))
    }

    public enum Button: String, Sendable {
        case left, right, middle

        var cg: CGMouseButton {
            switch self {
            case .left: return .left
            case .right: return .right
            case .middle: return .center
            }
        }

        var downType: CGEventType {
            switch self {
            case .left: return .leftMouseDown
            case .right: return .rightMouseDown
            case .middle: return .otherMouseDown
            }
        }

        var upType: CGEventType {
            switch self {
            case .left: return .leftMouseUp
            case .right: return .rightMouseUp
            case .middle: return .otherMouseUp
            }
        }
    }

    /// Clicks `count` times at a point. Each press carries its click state, so a count of 2
    /// is delivered as a real double-click (state 1 then state 2).
    public static func click(x: Int, y: Int, button: Button = .left, count: Int = 1, hold: Double = 0.05) async throws {
        let point = CGPoint(x: x, y: y)
        move(to: x, y)
        for state in 1...max(1, count) {
            let down = CGEvent(mouseEventSource: nil, mouseType: button.downType,
                               mouseCursorPosition: point, mouseButton: button.cg)
            let up = CGEvent(mouseEventSource: nil, mouseType: button.upType,
                             mouseCursorPosition: point, mouseButton: button.cg)
            down?.setIntegerValueField(.mouseEventClickState, value: Int64(state))
            up?.setIntegerValueField(.mouseEventClickState, value: Int64(state))
            post(down)
            do {
                try await pause(hold)
            } catch {
                post(up)  // Never leave the button held.
                throw error
            }
            post(up)
            if state < count { try await pause(0.03) }
        }
    }

    public static func drag(fromX sx: Int, fromY sy: Int, toX ex: Int, toY ey: Int,
                            duration: Double = 0.5, steps: Int = 20) async throws {
        post(CGEvent(mouseEventSource: nil, mouseType: .leftMouseDown,
                     mouseCursorPosition: CGPoint(x: sx, y: sy), mouseButton: .left))
        // Whatever happens, the button must not stay held down.
        defer {
            post(CGEvent(mouseEventSource: nil, mouseType: .leftMouseUp,
                         mouseCursorPosition: CGPoint(x: ex, y: ey), mouseButton: .left))
        }
        try await pause(0.05)
        let delay = duration / Double(steps)
        for i in 1...steps {
            let progress = Double(i) / Double(steps)
            let p = CGPoint(x: Double(sx) + Double(ex - sx) * progress,
                            y: Double(sy) + Double(ey - sy) * progress)
            post(CGEvent(mouseEventSource: nil, mouseType: .leftMouseDragged,
                         mouseCursorPosition: p, mouseButton: .left))
            try await pause(delay)
        }
    }

    /// One wheel notch. Matches the original's line-unit deltas of 3.
    public static func wheel(vertical: Int32 = 0, horizontal: Int32 = 0) {
        post(CGEvent(scrollWheelEvent2Source: nil, units: .line, wheelCount: 2,
                     wheel1: vertical, wheel2: horizontal, wheel3: 0))
    }

    // MARK: Keyboard

    public static func keyDown(_ code: CGKeyCode, flags: CGEventFlags = []) {
        let event = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: true)
        // Set explicitly: a nil-source event inherits whatever modifiers are physically held.
        event?.flags = flags
        post(event)
    }

    public static func keyUp(_ code: CGKeyCode, flags: CGEventFlags = []) {
        let event = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: false)
        event?.flags = flags
        post(event)
    }

    public static func keyPress(_ code: CGKeyCode, flags: CGEventFlags = [], hold: Double = 0.05) async throws {
        keyDown(code, flags: flags)
        defer { keyUp(code, flags: flags) }
        try await pause(hold)
    }

    /// Presses a combination such as ["command", "shift", "t"].
    /// Throws for names it does not know rather than silently pressing a partial chord.
    public static func hotKey(_ keys: [String], hold: Double = 0.05) async throws {
        var flags: CGEventFlags = []
        var mainKey: CGKeyCode?
        for raw in keys {
            let key = raw.lowercased().trimmingCharacters(in: .whitespaces)
            if let flag = KeyMap.modifiers[key] {
                flags.insert(flag)
            } else if let code = KeyMap.codes[key] {
                mainKey = code
            } else {
                throw ToolError("Unknown key: \(raw)", label: "Unknown key")
            }
        }
        if let mainKey {
            try await keyPress(mainKey, flags: flags, hold: hold)
        } else {
            // Modifiers alone: press and release each one in order.
            let codes = keys.compactMap { KeyMap.codes[$0.lowercased().trimmingCharacters(in: .whitespaces)] }
            codes.forEach { keyDown($0) }
            defer { codes.reversed().forEach { keyUp($0) } }
            try await pause(hold)
        }
    }

    static func releaseModifiers() {
        for code in [KeyMap.command, KeyMap.shift, KeyMap.option, KeyMap.control,
                     KeyMap.rightCommand, KeyMap.rightShift, KeyMap.rightOption, KeyMap.rightControl] {
            keyUp(code)
        }
    }

    /// Types text as Unicode keyboard events, independent of the active keyboard layout.
    ///
    /// The Python server sent US virtual key codes for ASCII letters, which come out as the
    /// wrong characters under a non-US layout (Korean 2-Set turns "hello" into "ㅗㄷㅣㅣㅐ").
    /// Here every character carries its own Unicode string, and an input method that would
    /// otherwise intercept key events is swapped for an ASCII-capable layout while typing.
    public static func type(_ text: String, interval: Double = 0.01) async throws {
        guard !text.isEmpty else { return }
        releaseModifiers()
        try await pause(0.02)
        try await InputSource.withASCIICapableLayout {
            for character in text {
                switch character {
                case "\n", "\r\n", "\r":
                    try await keyPress(KeyMap.returnKey, hold: interval)
                case "\t":
                    try await keyPress(KeyMap.tab, hold: interval)
                default:
                    typeUnicode(String(character))
                    try await pause(interval)
                }
            }
        }
    }

    static func typeUnicode(_ string: String) {
        let units = Array(string.utf16)
        let down = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: true)
        down?.flags = []
        units.withUnsafeBufferPointer { buffer in
            down?.keyboardSetUnicodeString(stringLength: buffer.count, unicodeString: buffer.baseAddress)
        }
        let up = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: false)
        up?.flags = []
        units.withUnsafeBufferPointer { buffer in
            up?.keyboardSetUnicodeString(stringLength: buffer.count, unicodeString: buffer.baseAddress)
        }
        post(down)
        post(up)
    }
}

/// Immutable CF reference; TIS only requires that the *calls* happen on the main thread.
extension TISInputSource: @unchecked @retroactive Sendable {}

/// Keyboard input source switching (Text Input Sources, part of HIToolbox).
enum InputSource {
    /// Runs `body` with an ASCII-capable keyboard layout selected, restoring the previous
    /// source afterwards (also on error or cancellation).
    static func withASCIICapableLayout(_ body: () async throws -> Void) async throws {
        // Text Input Source calls assert the main thread on recent macOS.
        let previous = await MainActor.run { () -> TISInputSource? in
            guard let current = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(),
                  !isASCIICapable(current),
                  let ascii = TISCopyCurrentASCIICapableKeyboardLayoutInputSource()?.takeRetainedValue(),
                  TISSelectInputSource(ascii) == noErr else { return nil }
            return current
        }
        guard let previous else {
            try await body()
            return
        }
        do {
            // Give the text system a moment to see the switch before events arrive.
            try await Input.pause(0.05)
            try await body()
            try await Input.pause(0.05)
        } catch {
            await restore(previous)
            throw error
        }
        await restore(previous)
    }

    private static func restore(_ source: TISInputSource) async {
        _ = await MainActor.run { TISSelectInputSource(source) }
    }

    static func isASCIICapable(_ source: TISInputSource) -> Bool {
        guard let raw = TISGetInputSourceProperty(source, kTISPropertyInputSourceIsASCIICapable) else {
            return false
        }
        let value = Unmanaged<CFBoolean>.fromOpaque(raw).takeUnretainedValue()
        guard CFBooleanGetValue(value) else { return false }
        // An input method (Korean, Japanese, Chinese) can report ASCII-capable while still
        // composing key events; only a plain keyboard layout passes Unicode events through.
        guard let typeRaw = TISGetInputSourceProperty(source, kTISPropertyInputSourceType) else { return true }
        let type = Unmanaged<CFString>.fromOpaque(typeRaw).takeUnretainedValue() as String
        return type == (kTISTypeKeyboardLayout as String)
    }
}

/// Virtual key codes (ANSI layout) and modifier flags, keyed by the names the Shortcut tool accepts.
public enum KeyMap {
    static let returnKey: CGKeyCode = 36
    static let tab: CGKeyCode = 48
    static let escape: CGKeyCode = 53
    static let command: CGKeyCode = 55
    static let shift: CGKeyCode = 56
    static let option: CGKeyCode = 58
    static let control: CGKeyCode = 59
    static let rightCommand: CGKeyCode = 54
    static let rightShift: CGKeyCode = 60
    static let rightOption: CGKeyCode = 61
    static let rightControl: CGKeyCode = 62

    public static let modifiers: [String: CGEventFlags] = [
        "command": .maskCommand, "cmd": .maskCommand,
        "shift": .maskShift,
        "option": .maskAlternate, "alt": .maskAlternate,
        "control": .maskControl, "ctrl": .maskControl,
        "fn": .maskSecondaryFn,
    ]

    public static let codes: [String: CGKeyCode] = [
        "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8, "v": 9,
        "b": 11, "q": 12, "w": 13, "e": 14, "r": 15, "y": 16, "t": 17,
        "1": 18, "2": 19, "3": 20, "4": 21, "6": 22, "5": 23, "=": 24, "9": 25, "7": 26,
        "-": 27, "8": 28, "0": 29, "]": 30, "o": 31, "u": 32, "[": 33, "i": 34, "p": 35,
        "l": 37, "j": 38, "'": 39, "k": 40, ";": 41, "\\": 42, ",": 43, "/": 44,
        "n": 45, "m": 46, ".": 47, "`": 50,
        "return": 36, "enter": 36, "tab": 48, "space": 49,
        "delete": 51, "backspace": 51, "forwarddelete": 117,
        "escape": 53, "esc": 53,
        "command": 55, "cmd": 55, "shift": 56, "capslock": 57, "option": 58, "alt": 58,
        "control": 59, "ctrl": 59, "fn": 63,
        "left": 123, "right": 124, "down": 125, "up": 126,
        "f1": 122, "f2": 120, "f3": 99, "f4": 118, "f5": 96, "f6": 97,
        "f7": 98, "f8": 100, "f9": 101, "f10": 109, "f11": 103, "f12": 111,
        "home": 115, "end": 119, "pageup": 116, "pagedown": 121,
        "volumeup": 72, "volumedown": 73, "mute": 74,
    ]
}
