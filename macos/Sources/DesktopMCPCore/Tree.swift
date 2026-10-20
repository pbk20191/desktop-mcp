import AppKit
import ApplicationServices

public struct TreeNode: Equatable, Sendable {
    public var boundingBox: BoundingBox
    public var center: Center
    public var name: String
    public var controlType: String
    public var windowName: String
    public var metadata: OrderedJSON

    init(box: BoundingBox, name: String, controlType: String, windowName: String, metadata: OrderedJSON = OrderedJSON()) {
        self.boundingBox = box
        self.center = box.center
        self.name = name
        self.controlType = controlType
        self.windowName = windowName
        self.metadata = metadata
    }

    func row(_ index: Int) -> String {
        "\(index)|\(windowName)|\(controlType)|\(name)|\(center.description)|\(metadata.serialized())"
    }
}

public struct TreeState: Sendable {
    public var interactiveNodes: [TreeNode] = []
    public var scrollableNodes: [TreeNode] = []

    static let header = "# id|window|control_type|name|coords|metadata"

    public func interactiveElementsString() -> String {
        guard !interactiveNodes.isEmpty else { return "No elements found" }
        return ([Self.header] + interactiveNodes.enumerated().map { $1.row($0) }).joined(separator: "\n")
    }

    /// Scrollable ids continue after the interactive ones so every id in a snapshot is unique.
    public func scrollableElementsString() -> String {
        guard !scrollableNodes.isEmpty else { return "No elements found" }
        let base = interactiveNodes.count
        return ([Self.header] + scrollableNodes.enumerated().map { $1.row(base + $0) }).joined(separator: "\n")
    }
}

/// How much of one application to walk. With no flag set the whole app is scanned.
struct Scan: Sendable {
    var bundleID: String
    var isBrowser: Bool
    var desktopOnly = false
    var extrasOnly = false
    var dialogOnly = false
}

/// Phase-1 attributes, fetched for every element.
struct EarlyAttributes {
    static let names = [
        kAXRoleAttribute, kAXHiddenAttribute, kAXPositionAttribute, kAXSizeAttribute,
        kAXEnabledAttribute, AXName.help, AXName.hasPopup, AXName.titleUIElement,
        kAXSubroleAttribute, AXName.index, kAXChildrenAttribute,
    ]

    var role = ""
    var hidden = false
    var enabled = true
    var help = ""
    var hasPopup = false
    var titleUIElement: AXUIElement?
    var subrole = ""
    var hasIndex = false
    var rect: Rect?
    var children: [AXUIElement] = []

    /// A password field, or an element whose role could not be read (fail closed: its value
    /// is never fetched).
    var isSecure: Bool { role.isEmpty || SecureField.isSecure(role: role, subrole: subrole) }
}

extension EarlyAttributes {
    init(_ element: AXUIElement) {
        let raw = AX.attributes(element, Self.names)
        role = AX.string(raw[kAXRoleAttribute])
        hidden = AX.isTrue(raw[kAXHiddenAttribute])
        enabled = (raw[kAXEnabledAttribute] as? NSNumber)?.boolValue != false
        help = AX.string(raw[AXName.help])
        hasPopup = AX.isTrue(raw[AXName.hasPopup])
        titleUIElement = AX.element(raw[AXName.titleUIElement])
        subrole = AX.string(raw[kAXSubroleAttribute])
        hasIndex = AX.truthy(raw[AXName.index])
        if let p = AX.point(raw[kAXPositionAttribute]), let s = AX.size(raw[kAXSizeAttribute]) {
            rect = Rect(position: p, size: s)
        }
        children = AX.elements(raw[kAXChildrenAttribute])
    }
}

/// Phase-2 attributes, fetched only for elements already judged interactive, and only those
/// the role can actually carry (an unsupported attribute still costs the app a round-trip).
struct LateAttributes {
    static let always = [kAXTitleAttribute, kAXDescriptionAttribute, AXName.identifier, kAXValueAttribute]
    static let textual = [AXName.placeholder, kAXSelectedTextAttribute, kAXSelectedTextRangeAttribute, AXName.expanded]
    static let linkLike = [AXName.url, AXName.filename]
    static let menu = [AXName.menuCmdChar, AXName.menuCmdModifiers, AXName.menuCmdVirtualKey]
    static let textualRoles: Set<String> = ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField", "AXRow"]
    static let linkLikeRoles: Set<String> = ["AXLink", "AXImage"]
    static let menuRoles: Set<String> = ["AXMenuItem", "AXMenuBarItem"]

    /// What a secure field is never asked for: its value and anything cut from it.
    static let secret: Set<String> = [kAXValueAttribute, kAXSelectedTextAttribute, kAXSelectedTextRangeAttribute]

    static func names(for role: String?, secure: Bool) -> [String] {
        var names: [String]
        if let role {
            names = always
            if textualRoles.contains(role) { names += textual }
            if linkLikeRoles.contains(role) { names += linkLike }
            if menuRoles.contains(role) { names += menu }
        } else {
            names = always + textual + linkLike
        }
        return secure ? names.filter { !secret.contains($0) } : names
    }

    /// What the walker asks an interactive element for, from its phase-1 attributes.
    static func names(for early: EarlyAttributes) -> [String] {
        names(for: early.role, secure: early.isSecure)
    }

    var title = ""
    var description = ""
    var identifier = ""
    var value: AnyObject?
    var placeholder: String?
    var url: String?
    var filename: String?
    var expanded: Bool?
    var shortcut = ""
    var selectedText = ""
    var selectedRange: CFRange?
    /// A secure field's value is never fetched; this also keeps it out of anything built here.
    var secure = false

    var label: String {
        let valueString = secure ? "" : value.map { AX.describe($0) } ?? ""
        return [title, description, valueString, placeholder ?? "", identifier].first { !$0.isEmpty } ?? ""
    }
}

extension LateAttributes {
    /// `secure` comes from the role and subrole, read before this so the value is never fetched.
    init(_ element: AXUIElement, role: String?, secure: Bool) {
        self.init(element, names: Self.names(for: role, secure: secure), secure: secure)
    }

    /// For an interactive element: `names(for: early)`, secure as `early` says.
    init(_ element: AXUIElement, early: EarlyAttributes) {
        self.init(element, names: Self.names(for: early), secure: early.isSecure)
    }

    private init(_ element: AXUIElement, names: [String], secure: Bool) {
        self.secure = secure
        let raw = AX.attributes(element, names)
        title = AX.string(raw[kAXTitleAttribute])
        description = AX.string(raw[kAXDescriptionAttribute])
        identifier = AX.string(raw[AXName.identifier])
        value = raw[kAXValueAttribute]
        placeholder = raw[AXName.placeholder].map { AX.describe($0) }
        url = raw[AXName.url].map { AX.describe($0) }
        filename = raw[AXName.filename].map { AX.describe($0) }
        expanded = (raw[AXName.expanded] as? NSNumber)?.boolValue
        shortcut = formatMenuShortcut(
            char: raw[AXName.menuCmdChar].map { AX.describe($0) },
            modifiers: (raw[AXName.menuCmdModifiers] as? NSNumber)?.intValue,
            virtualKey: (raw[AXName.menuCmdVirtualKey] as? NSNumber)?.intValue)
        selectedText = raw[kAXSelectedTextAttribute].map { AX.describe($0) } ?? ""
        selectedRange = AX.range(raw[kAXSelectedTextRangeAttribute])
    }
}

public final class Tree: Sendable {
    public init() {}

    private let extrasCache = Locked<(pids: Set<pid_t>, bundles: [String])?>(nil)

    /// Bundle ids of running apps that put an icon in the menu bar. Their own UI is not on
    /// screen, so only that icon is scanned. Cached against the set of running processes.
    func bundlesWithMenuBarExtras() async -> [String] {
        let running = App.runningApplications().filter {
            ($0.activationPolicy == .regular || $0.activationPolicy == .accessory) && $0.bundleIdentifier != nil
        }
        let key = Set(running.map(\.processIdentifier))
        if let cached = extrasCache.current, cached.pids == key {
            return cached.bundles
        }

        let systemUI = Set(BundleIDs.systemUI)
        let candidates = running.filter { !systemUI.contains($0.bundleIdentifier!) }
        let found = await concurrentMap(candidates) { app -> String? in
            let element = AX.application(app.processIdentifier)
            AX.setMessagingTimeout(element, 0.5)
            guard let extras = AX.element(AX.attribute(element, AXName.extrasMenuBar)),
                  !AX.children(extras).isEmpty else { return nil }
            return app.bundleIdentifier
        }
        var seen = Set<String>()
        let bundles = found.compactMap { $0 }.filter { seen.insert($0).inserted }
        extrasCache.current = (key, bundles)
        return bundles
    }

    public func state(activeWindow: WindowView?) async -> TreeState {
        // Ordered: the active app first so its elements get the lowest ids.
        var order: [String] = []
        var scans: [String: Scan] = [:]
        func full(_ bundleID: String) {
            guard scans[bundleID] == nil else { return }
            order.append(bundleID)
            scans[bundleID] = Scan(bundleID: bundleID, isBrowser: BundleIDs.browsers.contains(bundleID))
        }
        var fullyScanned = Set<String>()
        func narrow(_ bundleID: String, _ apply: (inout Scan) -> Void) {
            guard !fullyScanned.contains(bundleID) else { return }
            if scans[bundleID] == nil {
                order.append(bundleID)
                scans[bundleID] = Scan(bundleID: bundleID, isBrowser: BundleIDs.browsers.contains(bundleID))
            }
            apply(&scans[bundleID]!)
        }

        if let activeWindow {
            await offload {
                if let app = App.byBundleID(activeWindow.bundleID) {
                    AX.set(app.element, AXName.enhancedUserInterface, kCFBooleanTrue)
                }
            }
            full(activeWindow.bundleID)
        }
        for bundleID in BundleIDs.systemUI where App.byBundleID(bundleID) != nil {
            full(bundleID)
        }
        fullyScanned = Set(order)

        // A non-Finder app with no visible window: also scan the desktop icons, but not
        // Finder's menus, which are not showing.
        if let activeWindow, activeWindow.bundleID != BundleIDs.finder, activeWindow.isWindowless {
            narrow(BundleIDs.finder) { $0.desktopOnly = true }
        }
        for bundleID in await bundlesWithMenuBarExtras() {
            narrow(bundleID) { $0.extrasOnly = true }
        }
        // A background app's dialog is scanned only while the user can reach it.
        for dialog in await offload({ DialogInfo.all() }) where dialog.reachable && !dialog.frontmost {
            if let bundleID = dialog.app.bundleID {
                narrow(bundleID) { $0.dialogOnly = true }
            }
        }

        let results = await concurrentMap(order.compactMap { scans[$0] }) { [self] scan in
            nodes(for: scan) ?? TreeState()
        }
        var state = TreeState()
        for result in results {
            state.interactiveNodes += result.interactiveNodes
            state.scrollableNodes += result.scrollableNodes
        }
        return state
    }

    /// Nodes for one app, or nil when it is not running.
    func nodes(for scan: Scan) -> TreeState? {
        guard let app = App.byBundleID(scan.bundleID) else { return nil }
        // Keep an unresponsive app from stalling the snapshot.
        AX.setMessagingTimeout(app.element, 0.5)
        let appName = app.name.isEmpty ? scan.bundleID : app.name
        var state = TreeState()

        if scan.extrasOnly || scan.dialogOnly {
            if scan.extrasOnly, let extras = app.extrasMenuBar {
                traverse(extras, windowName: appName, into: &state, isBrowser: scan.isBrowser)
            }
            if scan.dialogOnly, let dialog = app.dialog {
                traverseWindows([dialog.window], windowName: appName, into: &state, isBrowser: scan.isBrowser)
            }
            return state
        }

        var menuBar: AXUIElement?
        var extrasMenuBar: AXUIElement?
        // Menus are scanned even while a dialog is up: AppKit keeps modal-safe items live.
        if !scan.desktopOnly {
            menuBar = app.menuBar
            extrasMenuBar = app.extrasMenuBar
            if let menuBar { traverse(menuBar, windowName: appName, into: &state, isBrowser: scan.isBrowser) }
            if let extrasMenuBar { traverse(extrasMenuBar, windowName: appName, into: &state, isBrowser: scan.isBrowser) }
        }

        if let dialog = app.dialog {
            // The window behind a sheet or modal still reports itself enabled, but clicks on it
            // do nothing. Only the dialog is walked.
            traverseWindows([dialog.window], windowName: appName, into: &state, isBrowser: scan.isBrowser)
        } else if let main = app.mainWindow {
            if let rect = AX.rect(main) {
                traverse(main, windowName: appName, into: &state, clip: BoundingBox(rect), isBrowser: scan.isBrowser)
            }
        } else {
            let all = app.windows
            let visible = all.filter { !AX.isTrue(AX.attribute($0, kAXMinimizedAttribute)) }
            if !visible.isEmpty {
                // Finder's desktop, for instance.
                traverseWindows(visible, windowName: appName, into: &state, isBrowser: scan.isBrowser)
            } else if all.isEmpty {
                // Windowless apps such as the Dock expose their UI as top-level children.
                let scanned = [menuBar, extrasMenuBar].compactMap { $0 }
                for child in AX.children(app.element) where !scanned.contains(where: { CFEqual($0, child) }) {
                    traverse(child, windowName: appName, into: &state, isBrowser: scan.isBrowser)
                }
            }
        }
        return state
    }

    private func traverseWindows(_ windows: [AXUIElement], windowName: String, into state: inout TreeState, isBrowser: Bool) {
        for window in windows {
            traverse(window, windowName: windowName, into: &state, clip: AX.rect(window).map(BoundingBox.init), isBrowser: isBrowser)
        }
    }

    /// Iterative depth-first walk collecting interactive and scrollable nodes.
    func traverse(_ root: AXUIElement, windowName: String, into state: inout TreeState,
                  clip: BoundingBox? = nil, isBrowser: Bool) {
        // Third element: whether the parent had a usable box. A collapsed wrapper directly inside
        // a real container is an overlay worth descending into; one inside another collapsed
        // wrapper is far more likely hidden.
        var stack: [(AXUIElement, Bool)] = [(root, true)]

        while let (element, parentBoxOK) = stack.popLast() {
            let early = EarlyAttributes(element)
            let role = early.role
            if early.hidden || TreeConfig.prunableRoles.contains(role) { continue }

            guard let rect = early.rect else {
                stack += early.children.reversed().map { ($0, parentBoxOK) }
                continue
            }

            let isVisible = rect.width > 1 && rect.height > 1
            // A closed submenu reports a 0-size rect for its whole subtree.
            if role == "AXMenu" && !isVisible { continue }

            let hasRole = TreeConfig.interactiveRoles.contains(role) || role == "AXImage"
            let hasListTitle = role == "AXList"
                && !AX.string(AX.attribute(element, kAXTitleAttribute)).isEmpty
            let isInteractive = isVisible && (
                (hasRole && early.enabled)
                    || !early.help.isEmpty
                    || early.hasPopup
                    || early.hasIndex
                    || early.titleUIElement != nil
                    || TreeConfig.interactiveSubroles.contains(early.subrole)
                    || hasListTitle
            )

            var box = BoundingBox(rect)
            if let clip {
                box = clip.intersection(box)
                if box.width == 0 || box.height == 0 {
                    // The element has no clickable area, but its descendants might (a floating
                    // button inside a zero-height wrapper).
                    if parentBoxOK {
                        stack += early.children.reversed().map { ($0, false) }
                    }
                    continue
                }
            }

            if isInteractive {
                if let node = interactiveNode(element, early: early, box: box, windowName: windowName,
                                              clip: clip, isBrowser: isBrowser),
                   !node.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    state.interactiveNodes.append(node)
                }
                if role == "AXTextArea" && !early.isSecure {
                    appendWordNodes(element, windowName: windowName, clip: clip, into: &state)
                }
            }

            if TreeConfig.scrollableRoles.contains(role) && isVisible {
                let label = early.children.first.map {
                    LateAttributes($0, role: nil, secure: SecureField.withholdsValue($0)).label
                } ?? ""
                state.scrollableNodes.append(TreeNode(box: box, name: label, controlType: role, windowName: windowName))
            }

            stack += early.children.reversed().map { ($0, true) }
        }
    }

    private func interactiveNode(_ element: AXUIElement, early: EarlyAttributes, box: BoundingBox,
                                 windowName: String, clip: BoundingBox?, isBrowser: Bool) -> TreeNode? {
        let late = LateAttributes(element, early: early)
        let linkedTitle = early.titleUIElement.flatMap(Self.linkedTitle)
        let node = Self.describe(early: early, late: late, linkedTitle: linkedTitle, box: box, windowName: windowName)
        return isBrowser
            ? domCorrection(node, early: early, windowName: windowName, clip: clip)
            : desktopCorrection(node, early: early, windowName: windowName, clip: clip)
    }

    /// The text of an element's AXTitleUIElement: its title, else its value, else its description.
    /// The value is fetched only when the title is empty and the element is not a secure field
    /// (nor one whose role could not be read).
    static func linkedTitle(_ titleElement: AXUIElement) -> String? {
        let raw = AX.attributes(titleElement, [kAXRoleAttribute, kAXSubroleAttribute, kAXTitleAttribute, kAXDescriptionAttribute])
        let title = AX.describe(raw[kAXTitleAttribute])
        if !title.isEmpty { return title }
        // Fail closed: an element whose role could not be read is not asked for its value.
        let role = raw[kAXRoleAttribute] as? String ?? ""
        if !role.isEmpty && !SecureField.isSecure(role: role, subrole: raw[kAXSubroleAttribute] as? String) {
            let value = AX.describe(AX.attribute(titleElement, kAXValueAttribute))
            if !value.isEmpty { return value }
        }
        let description = AX.describe(raw[kAXDescriptionAttribute])
        return description.isEmpty ? nil : description
    }

    /// Name and metadata of an interactive element from attributes already read. A secure
    /// field shows `value: "(hidden)"` and nothing derived from its value or selection.
    static func describe(early: EarlyAttributes, late: LateAttributes, linkedTitle titleUIText: String?,
                         box: BoundingBox, windowName: String) -> TreeNode {
        let role = early.role
        var late = late
        late.secure = late.secure || early.isSecure
        let secure = late.secure
        let linkedTitle = titleUIText ?? ""

        var label: String
        if TreeConfig.stateValuedRoles.contains(role) {
            // AXValue is the state here; a switch reporting 1 would otherwise be named "1".
            label = [late.title, late.description, linkedTitle, late.identifier].first { !$0.isEmpty } ?? ""
        } else {
            label = late.label.isEmpty ? linkedTitle : late.label
        }

        var metadata = OrderedJSON()
        let valueIsSet = AX.truthy(late.value)
        // A secure field takes its own branch whatever its role: nothing value-derived is emitted.
        switch secure ? SecureField.role : role {
        case SecureField.role:
            if let p = late.placeholder, !p.isEmpty { metadata["placeholder"] = .string(p) }
            metadata["value"] = .string(SecureField.hidden)
        case "AXTextField":
            if let p = late.placeholder, !p.isEmpty { metadata["placeholder"] = .string(p) }
            if valueIsSet { metadata["value"] = AX.json(late.value) }
        case "AXComboBox", "AXTextArea", "AXRow":
            if let p = late.placeholder, !p.isEmpty { metadata["placeholder"] = .string(p) }
            if valueIsSet { metadata["value"] = AX.json(late.value) }
            if let expanded = late.expanded { metadata["expanded"] = .bool(expanded) }
            if early.hasPopup { metadata["has_popup"] = true }
        case "AXRadioButton":
            if valueIsSet { metadata["selected"] = AX.json(late.value) }
        case "AXMenuItem", "AXMenuBarItem":
            if !late.shortcut.isEmpty { metadata["shortcut"] = .string(late.shortcut) }
        case "AXCheckBox":
            // 0 off, 1 on, 2 mixed.
            if let state = (late.value as? NSNumber)?.intValue ?? Int(AX.describe(late.value)) {
                metadata["checked"] = state == 2 ? "mixed" : .bool(state != 0)
            }
        case "AXPopUpButton":
            if let titleUIText { metadata["title"] = .string(titleUIText) }
            if valueIsSet { metadata["selected"] = AX.json(late.value) }
        case "AXLink":
            if let url = late.url, Self.isReportableURL(url) { metadata["url"] = .string(url) }
        case "AXImage":
            if let f = late.filename, !f.isEmpty { metadata["filename"] = .string(f) }
            if let url = late.url, Self.isReportableURL(url) { metadata["url"] = .string(url) }
        default:
            break
        }

        // Only a real selection is reported: it tells the caller typing would replace content.
        if !secure, TreeConfig.textInputRoles.contains(role) || early.subrole == "AXSearchField",
           let selection = late.selectedRange, selection.length > 0 {
            metadata["selected_text"] = .string(late.selectedText)
            let total = AX.describe(late.value).utf16.count
            if selection.location == 0 && total > 0 && selection.length == total {
                metadata["all_selected"] = true
            }
        }
        if !late.identifier.isEmpty { metadata["axidentifier"] = .string(late.identifier) }

        // Window frame buttons expose no name of their own.
        if label.isEmpty, let name = TreeConfig.windowControlSubroles[early.subrole] { label = name }
        // Third-party menu bar extras are bare icons; the owning app's name is all there is.
        if label.isEmpty && role == "AXMenuBarItem" { label = windowName }
        // An unlabelled password field would otherwise be dropped as nameless.
        if label.isEmpty && secure { label = SecureField.hidden }

        return TreeNode(box: box, name: label, controlType: role, windowName: windowName, metadata: metadata)
    }

    static func isReportableURL(_ url: String) -> Bool {
        url.hasPrefix("file://") || url.hasPrefix("http://") || url.hasPrefix("https://")
    }

    /// A link wrapping a heading is reported as the heading.
    private func domCorrection(_ node: TreeNode, early: EarlyAttributes, windowName: String, clip: BoundingBox?) -> TreeNode? {
        guard early.role == "AXLink", let child = early.children.first else { return node }
        let childEarly = EarlyAttributes(child)
        guard childEarly.role == "AXHeading" else { return node }
        guard let rect = childEarly.rect else { return nil }
        let childLate = LateAttributes(child, role: nil, secure: childEarly.isSecure)
        var box = BoundingBox(rect)
        if let clip { box = clip.intersection(box) }
        var metadata = OrderedJSON()
        if !childLate.identifier.isEmpty { metadata["axidentifier"] = .string(childLate.identifier) }
        return TreeNode(box: box, name: childLate.label, controlType: childEarly.role, windowName: windowName, metadata: metadata)
    }

    /// Cells and groups take the text of their first static-text descendant; window frame
    /// buttons get their friendly names.
    private func desktopCorrection(_ node: TreeNode, early: EarlyAttributes, windowName: String, clip: BoundingBox?) -> TreeNode? {
        guard let rect = early.rect else { return node }
        func relabelled(_ name: String) -> TreeNode {
            var box = BoundingBox(rect)
            if let clip { box = clip.intersection(box) }
            return TreeNode(box: box, name: name, controlType: early.role, windowName: windowName, metadata: node.metadata)
        }

        if early.role == "AXCell" || early.role == "AXGroup" {
            var current = early.children.first
            while let element = current {
                let raw = AX.attributes(element, [kAXRoleAttribute, kAXChildrenAttribute])
                if AX.string(raw[kAXRoleAttribute]) == kAXStaticTextRole {
                    // An empty label still replaces the node, as in the original. The value is
                    // fetched only for static text, so a password field's never is.
                    return relabelled(AX.describe(AX.attribute(element, kAXValueAttribute)))
                }
                current = AX.elements(raw[kAXChildrenAttribute]).first
            }
        } else if early.role == "AXButton", let name = TreeConfig.windowControlSubroles[early.subrole] {
            return relabelled(name)
        }
        return node
    }

    // MARK: Word nodes

    /// One node per whitespace-delimited word of a text area, each individually clickable.
    /// A word that soft-wraps yields one box per line.
    private func appendWordNodes(_ element: AXUIElement, windowName: String, clip: BoundingBox?, into state: inout TreeState) {
        guard TreeConfig.maxWordNodesPerElement > 0 else { return }
        for (word, rects) in wordBoundingBoxes(element, limit: TreeConfig.maxWordNodesPerElement) {
            for rect in rects {
                var box = BoundingBox(rect)
                if let clip { box = clip.intersection(box) }
                guard box.width > 0, box.height > 0 else { continue }
                state.interactiveNodes.append(TreeNode(box: box, name: word, controlType: "Word", windowName: windowName))
            }
        }
    }

    func wordBoundingBoxes(_ element: AXUIElement, limit: Int) -> [(String, [Rect])] {
        // Without AXBoundsForRange there is no geometry; some apps answer with a degenerate
        // rect anyway, which would invite a misdirected click.
        guard AX.parameterizedNames(element).contains(AXName.boundsForRange) else { return [] }
        guard let count = (AX.attribute(element, AXName.numberOfCharacters) as? NSNumber)?.intValue, count > 0,
              let fullRange = AX.makeRange(location: 0, length: count) else { return [] }
        let text = AX.string(AX.parameterized(element, AXName.stringForRange, fullRange))
        guard !text.isEmpty else { return [] }
        let ns = text as NSString
        let lines = lineTable(element, total: ns.length)
        let fontSize = documentFontSize(element)

        var words: [(String, [Rect])] = []
        let regex = try! NSRegularExpression(pattern: "\\S+")
        for match in regex.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
            if words.count >= limit { break }
            var rects: [Rect] = []
            for (location, length) in split(match.range.location, match.range.length, by: lines) {
                guard let range = AX.makeRange(location: location, length: length),
                      let cg = AX.cgRect(AX.parameterized(element, AXName.boundsForRange, range)) else { continue }
                var rect = Rect(position: cg.origin, size: cg.size)
                if rect.right <= rect.left || rect.bottom <= rect.top { continue }
                if let fontSize, fontSize > 0, fontSize < rect.height {
                    // AXBoundsForRange reports the whole line box; trim the leading above the glyphs.
                    rect.top = rect.bottom - fontSize
                }
                rects.append(rect)
            }
            if !rects.isEmpty { words.append((ns.substring(with: match.range), rects)) }
        }
        return words
    }

    private func lineTable(_ element: AXUIElement, total: Int) -> [(Int, Int)] {
        var lines: [(Int, Int)] = []
        var offset = 0
        while offset < total {
            guard let range = AX.range(AX.parameterized(element, AXName.rangeForLine, lines.count as CFNumber)) else {
                return []
            }
            if range.length <= 0 || range.location + range.length <= offset { break }
            lines.append((range.location, range.length))
            offset = range.location + range.length
        }
        return lines
    }

    /// AXBoundsForRange merges a multi-line range into one useless union rect, so split first.
    private func split(_ location: Int, _ length: Int, by lines: [(Int, Int)]) -> [(Int, Int)] {
        guard !lines.isEmpty else { return [(location, length)] }
        let end = location + length
        let segments = lines
            .filter { $0.0 < end && $0.0 + $0.1 > location }
            .map { (max(location, $0.0), min(end, $0.0 + $0.1)) }
            .filter { $0.1 > $0.0 }
            .map { ($0.0, $0.1 - $0.0) }
        return segments.isEmpty ? [(location, length)] : segments
    }

    private func documentFontSize(_ element: AXUIElement) -> Double? {
        guard let range = AX.makeRange(location: 0, length: 1),
              let attributed = AX.parameterized(element, AXName.attributedStringForRange, range) as? NSAttributedString,
              attributed.length > 0 else { return nil }
        let font = attributed.attributes(at: 0, effectiveRange: nil)[NSAttributedString.Key("AXFont")] as? [String: Any]
        return (font?["AXFontSize"] as? NSNumber)?.doubleValue
    }
}
