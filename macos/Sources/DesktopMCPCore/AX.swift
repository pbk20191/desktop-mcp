import ApplicationServices
import Foundation

/// AXUIElement is an immutable CF reference that HIServices lets any thread message.
extension AXUIElement: @unchecked @retroactive Sendable {}

/// Thin helpers over the C Accessibility API.
public enum AX {
    public static func application(_ pid: pid_t) -> AXUIElement {
        AXUIElementCreateApplication(pid)
    }

    public static let systemWide = AXUIElementCreateSystemWide()

    public static func isTrusted(prompt: Bool) -> Bool {
        // The value of kAXTrustedCheckOptionPrompt, which Swift 6 can't read as a global `var`.
        return AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": prompt] as CFDictionary)
    }

    // MARK: Reading

    public static func attribute(_ element: AXUIElement, _ name: String) -> AnyObject? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success,
              let value, !isError(value) else { return nil }
        return value
    }

    /// One round-trip for several attributes. Missing or unsupported attributes are absent.
    public static func attributes(_ element: AXUIElement, _ names: [String]) -> [String: AnyObject] {
        var values: CFArray?
        let err = AXUIElementCopyMultipleAttributeValues(
            element, names as CFArray, AXCopyMultipleAttributeOptions(rawValue: 0), &values)
        guard err == .success, let array = values as? [AnyObject] else { return [:] }
        var result: [String: AnyObject] = [:]
        for (name, value) in zip(names, array) where !isError(value) && !(value is NSNull) {
            result[name] = value
        }
        return result
    }

    public static func parameterized(_ element: AXUIElement, _ name: String, _ parameter: CFTypeRef) -> AnyObject? {
        var value: CFTypeRef?
        guard AXUIElementCopyParameterizedAttributeValue(element, name as CFString, parameter, &value) == .success,
              let value, !isError(value) else { return nil }
        return value
    }

    public static func parameterizedNames(_ element: AXUIElement) -> [String] {
        var names: CFArray?
        guard AXUIElementCopyParameterizedAttributeNames(element, &names) == .success else { return [] }
        return (names as? [String]) ?? []
    }

    public static func children(_ element: AXUIElement) -> [AXUIElement] {
        elements(attribute(element, kAXChildrenAttribute))
    }

    public static func pid(_ element: AXUIElement) -> pid_t? {
        var pid: pid_t = 0
        return AXUIElementGetPid(element, &pid) == .success ? pid : nil
    }

    public static func rect(_ element: AXUIElement) -> Rect? {
        let raw = attributes(element, [kAXPositionAttribute, kAXSizeAttribute])
        guard let p = point(raw[kAXPositionAttribute]), let s = size(raw[kAXSizeAttribute]) else { return nil }
        return Rect(position: p, size: s)
    }

    // MARK: Writing

    @discardableResult
    public static func set(_ element: AXUIElement, _ name: String, _ value: CFTypeRef) -> Bool {
        AXUIElementSetAttributeValue(element, name as CFString, value) == .success
    }

    @discardableResult
    public static func setPosition(_ element: AXUIElement, x: Double, y: Double) -> Bool {
        var p = CGPoint(x: x, y: y)
        guard let value = AXValueCreate(.cgPoint, &p) else { return false }
        return set(element, kAXPositionAttribute, value)
    }

    @discardableResult
    public static func setSize(_ element: AXUIElement, width: Double, height: Double) -> Bool {
        var s = CGSize(width: width, height: height)
        guard let value = AXValueCreate(.cgSize, &s) else { return false }
        return set(element, kAXSizeAttribute, value)
    }

    @discardableResult
    public static func perform(_ element: AXUIElement, _ action: String) -> Bool {
        AXUIElementPerformAction(element, action as CFString) == .success
    }

    @discardableResult
    public static func setMessagingTimeout(_ element: AXUIElement, _ seconds: Float) -> Bool {
        AXUIElementSetMessagingTimeout(element, seconds) == .success
    }

    // MARK: Value conversion

    static func isError(_ value: AnyObject) -> Bool {
        CFGetTypeID(value) == AXValueGetTypeID() && AXValueGetType(value as! AXValue) == .axError
    }

    public static func point(_ value: AnyObject?) -> CGPoint? {
        guard let value, CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var p = CGPoint.zero
        return AXValueGetValue(value as! AXValue, .cgPoint, &p) ? p : nil
    }

    public static func size(_ value: AnyObject?) -> CGSize? {
        guard let value, CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var s = CGSize.zero
        return AXValueGetValue(value as! AXValue, .cgSize, &s) ? s : nil
    }

    public static func cgRect(_ value: AnyObject?) -> CGRect? {
        guard let value, CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var r = CGRect.zero
        return AXValueGetValue(value as! AXValue, .cgRect, &r) ? r : nil
    }

    public static func range(_ value: AnyObject?) -> CFRange? {
        guard let value, CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var r = CFRange()
        return AXValueGetValue(value as! AXValue, .cfRange, &r) ? r : nil
    }

    public static func makeRange(location: Int, length: Int) -> AXValue? {
        var r = CFRange(location: location, length: length)
        return AXValueCreate(.cfRange, &r)
    }

    public static func element(_ value: AnyObject?) -> AXUIElement? {
        guard let value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return (value as! AXUIElement)
    }

    public static func elements(_ value: AnyObject?) -> [AXUIElement] {
        guard let array = value as? [AnyObject] else { return [] }
        return array.compactMap(element)
    }

    public static func isTrue(_ value: AnyObject?) -> Bool {
        (value as? NSNumber)?.boolValue == true
    }

    /// Text of a string-ish attribute; "" for anything that is not text.
    public static func string(_ value: AnyObject?) -> String {
        switch value {
        case let s as String: return s
        case let a as NSAttributedString: return a.string
        case let u as URL: return u.absoluteString
        default: return ""
        }
    }

    /// Python `str(value)` for an attribute value, used where the original stringifies AXValue.
    public static func describe(_ value: AnyObject?) -> String {
        switch value {
        case nil: return ""
        case let s as String: return s
        case let a as NSAttributedString: return a.string
        case let u as URL: return u.absoluteString
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return n.boolValue ? "True" : "False" }
            return n.stringValue
        default: return String(describing: value!)
        }
    }

    /// A JSON form of an attribute value for snapshot metadata.
    public static func json(_ value: AnyObject?) -> JSONValue {
        switch value {
        case nil: return .null
        case let s as String: return .string(s)
        case let a as NSAttributedString: return .string(a.string)
        case let u as URL: return .string(u.absoluteString)
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return .bool(n.boolValue) }
            return CFNumberIsFloatType(n) ? .number(n.doubleValue) : .integer(n.int64Value)
        default: return .string(describe(value))
        }
    }

    /// Python truthiness of an attribute value.
    public static func truthy(_ value: AnyObject?) -> Bool {
        switch value {
        case nil: return false
        case let s as String: return !s.isEmpty
        case let n as NSNumber: return n.doubleValue != 0
        case let a as [AnyObject]: return !a.isEmpty
        default: return true
        }
    }
}

/// Attribute names not exported as constants by the SDK.
enum AXName {
    static let help = "AXHelp"
    static let hasPopup = "AXHasPopup"
    static let titleUIElement = "AXTitleUIElement"
    static let index = "AXIndex"
    static let identifier = "AXIdentifier"
    static let placeholder = "AXPlaceholderValue"
    static let url = "AXURL"
    static let filename = "AXFilename"
    static let expanded = "AXExpanded"
    static let menuCmdChar = "AXMenuItemCmdChar"
    static let menuCmdModifiers = "AXMenuItemCmdModifiers"
    static let menuCmdVirtualKey = "AXMenuItemCmdVirtualKey"
    static let extrasMenuBar = "AXExtrasMenuBar"
    static let enhancedUserInterface = "AXEnhancedUserInterface"
    static let fullScreen = "AXFullScreen"
    static let modal = "AXModal"
    static let frontmost = "AXFrontmost"
    static let boundsForRange = "AXBoundsForRange"
    static let rangeForLine = "AXRangeForLine"
    static let stringForRange = "AXStringForRange"
    static let attributedStringForRange = "AXAttributedStringForRange"
    static let numberOfCharacters = "AXNumberOfCharacters"
}
