import Foundation

/// Role tables that decide what a snapshot reports. Ported verbatim from the Python tree config.
enum TreeConfig {
    static let interactiveRoles: Set<String> = [
        "AXButton", "AXCheckBox", "AXRadioButton", "AXTextField", "AXTextArea", "AXComboBox",
        "AXPopUpButton", "AXSlider", "AXIncrementor", "AXLink", "AXMenuItem", "AXMenuButton",
        "AXMenuBarItem", "AXTab", "AXDockItem", "AXCell", "AXToggle", "AXSwitch",
        "AXDisclosureTriangle", "AXColorWell", "AXLevelIndicator", "AXValueIndicator",
    ]

    static let scrollableRoles: Set<String> = ["AXScrollArea", "AXScrollView", "AXWebArea", "AXBrowser"]

    /// Subroles that make an element interactive regardless of role (notification banners are
    /// AXGroups; some search fields report AXStaticText).
    static let interactiveSubroles: Set<String> = [
        "AXNotificationCenterBanner", "AXNotificationCenterBannerStack", "AXSearchField",
    ]

    /// Roles whose AXValue is a state (1 = checked), not a name.
    static let stateValuedRoles: Set<String> = ["AXCheckBox", "AXRadioButton", "AXDisclosureTriangle"]

    static let windowControlSubroles: [String: String] = [
        "AXCloseButton": "Close Button",
        "AXMinimizeButton": "Minimize Button",
        "AXZoomButton": "Zoom Button",
        "AXFullScreenButton": "Full Screen Button",
    ]

    static let textInputRoles: Set<String> = ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField"]

    /// Decorative or structural subtrees that never yield actionable nodes.
    static let prunableRoles: Set<String> = [
        "AXScrollBar", "AXGrowArea", "AXUnknown", "AXValueIndicator", "AXLevelIndicator",
        "AXProgressIndicator", "AXSeparator", "AXSplitter", "AXHandle", "AXRuler", "AXRulerMarker",
        "AXBusyIndicator", "AXRelevanceIndicator", "AXSizeHandle", "AXResizeIndicator",
    ]

    /// Cap on per-word nodes emitted for one text area; each costs an AXBoundsForRange round-trip.
    static let maxWordNodesPerElement = 200
}

/// Renders a menu item's keyboard equivalent, e.g. "⇧⌘N".
///
/// Bit 3 of the modifier mask means *no* command, so a mask of 0 is command alone.
/// Bit 4 is fn/globe (window tiling uses it: Fill is fn-control-F).
public func formatMenuShortcut(char: String?, modifiers: Int?, virtualKey: Int?) -> String {
    let virtualKeys: [Int: String] = [
        36: "\u{21a9}", 48: "\u{21e5}", 49: "Space", 51: "\u{232b}", 53: "\u{238b}", 76: "\u{2324}",
        115: "\u{2196}", 116: "\u{21de}", 117: "\u{2326}", 119: "\u{2198}", 121: "\u{21df}",
        123: "\u{2190}", 124: "\u{2192}", 125: "\u{2193}", 126: "\u{2191}",
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6",
        98: "F7", 100: "F8", 101: "F9", 109: "F10", 103: "F11", 111: "F12",
    ]
    let controlChars: [String: String] = [
        "\u{08}": "\u{232b}", "\u{1b}": "\u{238b}", "\r": "\u{21a9}", "\t": "\u{21e5}", "\u{03}": "\u{2324}",
    ]
    let mask = modifiers ?? 0

    var key = virtualKey.flatMap { virtualKeys[$0] }
    if key == nil, let char, !char.isEmpty {
        if let mapped = controlChars[char] {
            key = mapped
        } else if char.unicodeScalars.allSatisfy({ !CharacterSet.controlCharacters.contains($0) }),
                  !char.trimmingCharacters(in: .whitespaces).isEmpty {
            key = char
        }
    }
    guard let key, !key.isEmpty else { return "" }

    var parts = ""
    if mask & 16 != 0 { parts += "\u{1f310}" }
    if mask & 4 != 0 { parts += "\u{2303}" }
    if mask & 2 != 0 { parts += "\u{2325}" }
    if mask & 1 != 0 { parts += "\u{21e7}" }
    if mask & 8 == 0 { parts += "\u{2318}" }
    return parts + (key.count == 1 ? key.uppercased() : key)
}
