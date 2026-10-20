import AppKit
import ApplicationServices
import MCP

/// One item of an app's menu bar: a bar item or a menu item. Path matching, listing and the
/// error copy work on this, so they are tested with an in-memory tree.
protocol MenuNode {
    var title: String { get }
    var enabled: Bool { get }
    /// The keyboard equivalent as shown in the menu (e.g. "⇧⌘E"); nil when it has none.
    var shortcut: String? { get }
    var isSeparator: Bool { get }
    /// The items of this item's menu, separators included; empty for a leaf.
    func children() -> [Self]
    func press() -> Bool
    /// True when `children()` pressed this bar item to populate its menu and items appeared, so
    /// the menu is open.
    var openedMenu: Bool { get }
    /// Closes the menu this item opened.
    func cancel()
}

/// A menu bar item or menu item through the Accessibility API. Holds an `AXUIElement`, so it is
/// only created and used inside one `offload` call.
struct AXMenuNode: MenuNode {
    let element: AXUIElement
    let title: String
    let enabled: Bool
    let shortcut: String?
    let isSeparator: Bool
    /// Set for the bar items of a `select`: a bar item that reports no children is pressed once
    /// to populate its menu.
    private let populates: Bool
    /// Whether that press opened a menu with items. Shared by copies of this node.
    private let opened = Locked(false)

    init(_ element: AXUIElement, populates: Bool = false) {
        self.element = element
        self.populates = populates
        let raw = AX.attributes(element, [
            kAXTitleAttribute, kAXEnabledAttribute, kAXRoleAttribute, kAXRoleDescriptionAttribute,
            AXName.menuCmdChar, AXName.menuCmdModifiers, AXName.menuCmdVirtualKey,
        ])
        title = AX.string(raw[kAXTitleAttribute])
        // An item that does not report AXEnabled is treated as enabled.
        enabled = (raw[kAXEnabledAttribute] as? NSNumber)?.boolValue ?? true
        let label = formatMenuShortcut(
            char: raw[AXName.menuCmdChar].map { AX.describe($0) },
            modifiers: (raw[AXName.menuCmdModifiers] as? NSNumber)?.intValue,
            virtualKey: (raw[AXName.menuCmdVirtualKey] as? NSNumber)?.intValue)
        shortcut = label.isEmpty ? nil : label
        let role = AX.string(raw[kAXRoleAttribute])
        isSeparator = (title.isEmpty && role == kAXMenuItemRole && !enabled)
            || AX.string(raw[kAXRoleDescriptionAttribute]).lowercased() == "separator"
    }

    /// The menu bar's items.
    static func roots(_ menuBar: AXUIElement, populate: Bool) -> [AXMenuNode] {
        AX.children(menuBar).map { AXMenuNode($0, populates: populate) }
    }

    /// The items of the single `AXMenu` child, read without opening the menu.
    private func items() -> [AXUIElement] {
        let kids = AX.children(element)
        if kids.count == 1, AX.string(AX.attribute(kids[0], kAXRoleAttribute)) == kAXMenuRole {
            return AX.children(kids[0])
        }
        return kids
    }

    func children() -> [AXMenuNode] {
        var found = items()
        // Some apps build a bar item's menu only when it opens.
        if found.isEmpty, populates, press() {
            for _ in 0..<10 where found.isEmpty {
                usleep(50_000)
                found = items()
            }
            if !found.isEmpty { opened.current = true }
        }
        return found.map { AXMenuNode($0) }
    }

    var openedMenu: Bool { opened.current }

    /// Cancels the open menu through this app's AX tree (its `AXMenu` child, else the bar item),
    /// so no key event reaches whatever app is frontmost.
    func cancel() {
        let menu = AX.children(element).first { AX.string(AX.attribute($0, kAXRoleAttribute)) == kAXMenuRole }
        let result = AXUIElementPerformAction(menu ?? element, kAXCancelAction as CFString)
        if result != .success, menu != nil {
            AXUIElementPerformAction(element, kAXCancelAction as CFString)
        }
    }

    func press() -> Bool {
        // An item that opens a modal panel can hold the reply past the messaging timeout; the
        // press was delivered all the same.
        let result = AXUIElementPerformAction(element, kAXPressAction as CFString)
        return result == .success || result == .cannotComplete
    }
}

enum MenuTool {
    static let modes = ["list", "select"]
    static let maxDepth = 4

    /// A keyboard equivalent as the menu shows it: modifiers in the order ⌃⌥⇧⌘. Modifier bits:
    /// 1 shift, 2 option, 4 control, 8 no command.
    static func shortcutLabel(char: String, modifiers: Int) -> String {
        formatMenuShortcut(char: char, modifiers: modifiers, virtualKey: nil)
    }

    /// Lowercased, `…` as `...`, trailing whitespace dropped.
    private static func normalized(_ s: String) -> String {
        var s = s.lowercased().replacingOccurrences(of: "…", with: "...")
        while let last = s.last, last.isWhitespace { s.removeLast() }
        return s
    }

    private static func describe(_ chain: [some MenuNode]) -> String {
        chain.map(\.title).joined(separator: " > ")
    }

    /// The item `element` names among `siblings`: exact title, then case-insensitive, then
    /// normalized; more than one match at the first rule that matches is an error.
    private static func match<N: MenuNode>(_ element: String, in siblings: [N], under parent: String) throws -> N {
        let items = siblings.filter { !$0.isSeparator }
        let rules: [(N) -> Bool] = [
            { $0.title == element },
            { $0.title.lowercased() == element.lowercased() },
            { normalized($0.title) == normalized(element) },
        ]
        for rule in rules {
            let found = items.filter(rule)
            if found.count == 1 { return found[0] }
            if found.count > 1 {
                throw ToolError("Menu item '\(element)' is ambiguous: \(found.map(\.title).joined(separator: ", "))",
                                telemetry: "Menu item is ambiguous")
            }
        }
        let available = items.isEmpty ? "; it has no items." : ". Available: " + items.map(\.title).joined(separator: ", ")
        throw ToolError("No menu item '\(element)' under \(parent)\(available)", telemetry: "No such menu item")
    }

    /// The items `path` names, from the bar item down. The Apple menu (root 0) is also "Apple".
    static func resolve<N: MenuNode>(_ path: [String], in roots: [N]) throws -> [N] {
        var chain: [N] = []
        try resolve(path, in: roots, into: &chain)
        return chain
    }

    /// `resolve`, appending to `chain` as it goes so a caller sees how far it got on an error.
    private static func resolve<N: MenuNode>(_ path: [String], in roots: [N], into chain: inout [N]) throws {
        for element in path {
            let node: N
            if let parent = chain.last {
                node = try match(element, in: parent.children(), under: describe(chain))
            } else if normalized(element) == "apple", let apple = roots.first {
                node = apple
            } else {
                node = try match(element, in: roots, under: "the menu bar")
            }
            chain.append(node)
        }
    }

    /// Presses the item `path` names, after checking every item on the way is enabled and the
    /// last one is a command rather than a submenu. On an error, a bar item menu this call opened
    /// to populate it is cancelled.
    static func select<N: MenuNode>(_ path: [String], in roots: [N]) throws -> String {
        var chain: [N] = []
        do {
            try resolve(path, in: roots, into: &chain)
            return try press(chain)
        } catch {
            if let bar = chain.first, bar.openedMenu { bar.cancel() }
            throw error
        }
    }

    private static func press<N: MenuNode>(_ chain: [N]) throws -> String {
        for end in chain.indices where !chain[end].enabled {
            throw ToolError("Menu item \(describe(Array(chain[...end]))) is disabled.", telemetry: "Menu item is disabled")
        }
        guard let item = chain.last else { throw ToolError("path required for select mode.") }
        if item.children().contains(where: { !$0.isSeparator }) {
            throw ToolError("Menu item \(describe(chain)) opens a submenu; add the item to select.",
                            telemetry: "Menu item opens a submenu")
        }
        guard item.press() else {
            throw ToolError("Could not press menu item \(describe(chain)).", telemetry: "Could not press menu item")
        }
        return "Selected \(describe(chain))."
    }

    /// An indented tree of titles, `depth` levels deep (1...4), with shortcuts and `[disabled]`;
    /// separators omitted.
    static func list<N: MenuNode>(_ roots: [N], depth: Int) -> String {
        let depth = min(max(depth, 1), maxDepth)
        var lines: [String] = []
        func walk(_ nodes: [N], level: Int) {
            for node in nodes where !node.isSeparator {
                var line = String(repeating: "  ", count: level) + WindowTool.flat(node.title)
                if let shortcut = node.shortcut { line += "  " + shortcut }
                if !node.enabled { line += "  [disabled]" }
                lines.append(line)
                if level + 1 < depth { walk(node.children(), level: level + 1) }
            }
        }
        walk(roots, level: 0)
        return lines.isEmpty ? "No menu items." : lines.joined(separator: "\n")
    }

    /// The app to read: the regular app named `app` (localized name, case-insensitive, or exact
    /// bundle id), else `menuBarOwner`, the app that owns the menu bar.
    private static func targetApp(_ app: String?, menuBarOwner: pid_t?) throws -> App {
        guard let app else {
            guard let menuBarOwner else { throw ToolError("No menu bar for the frontmost app.") }
            return App(pid: menuBarOwner)
        }
        let wanted = app.trimmingCharacters(in: .whitespaces).lowercased()
        guard let running = App.runningApplications().first(where: {
            $0.activationPolicy == .regular && ($0.bundleIdentifier == app || $0.localizedName?.lowercased() == wanted)
        }) else {
            throw ToolError("Application '\(app)' not found.", telemetry: "Application not found")
        }
        return App(running)
    }

    /// The bar items of the target app's menu bar, the messaging timeout set first.
    private static func roots(_ app: String?, menuBarOwner: pid_t?, populate: Bool) throws -> [AXMenuNode] {
        let target = try targetApp(app, menuBarOwner: menuBarOwner)
        AX.setMessagingTimeout(target.element, 0.5)
        guard let menuBar = target.menuBar else {
            throw ToolError("No menu bar for \(app ?? "the frontmost app").", telemetry: "No menu bar")
        }
        AX.setMessagingTimeout(menuBar, 0.5)
        return AXMenuNode.roots(menuBar, populate: populate)
    }

    static func tool() -> Tool {
        Tool(
            name: "Menu",
            description: "Reads and chooses commands from an app's menu bar ('app': name or bundle id; default the frontmost app). 'list' (default) returns an indented tree of item titles with keyboard shortcuts and [disabled], 'depth' levels deep (default 2, max 4), below the optional 'path' prefix. 'select' presses the item 'path' names from the bar item down, e.g. [\"File\", \"Export…\"]: titles match exactly, then ignoring case, then also treating '...' as '…'; the Apple menu is \"Apple\". A disabled item is not pressed.",
            inputSchema: DesktopMCP.schema([
                "mode": DesktopMCP.string(default: "list", enum: modes),
                "app": DesktopMCP.nullableString(),
                "path": DesktopMCP.stringArray(),
                "depth": DesktopMCP.integer(default: 2),
            ]),
            annotations: ToolAnnotations(title: "Menu", readOnly: false, destructive: true, idempotent: false, openWorld: false),
            handler: { args in
                let mode = try args.choice("mode", in: modes, default: "list")
                let app = try args.string("app")
                let path = try args.stringList("path") ?? []
                let depth = mode == "list" ? try args.int("depth") ?? 2 : 0
                if mode == "select" && path.isEmpty { throw ToolError("path required for select mode.") }
                try Permissions.requireAccessibility()
                // The active app owns the menu bar even when it has no window (e.g. Finder with
                // none open); the frontmost window can belong to another app.
                let owner: pid_t? = app != nil ? nil : await MainActor.run {
                    (NSWorkspace.shared.menuBarOwningApplication ?? NSWorkspace.shared.frontmostApplication)?
                        .processIdentifier
                }

                if mode == "list" {
                    let text = try await offload { () throws -> String in
                        let bar = try roots(app, menuBarOwner: owner, populate: false)
                        guard let parent = try resolve(path, in: bar).last else { return list(bar, depth: depth) }
                        return list(parent.children(), depth: depth)
                    }
                    return [.text(text)]
                }

                let text = try await offload { () throws -> String in
                    try select(path, in: try roots(app, menuBarOwner: owner, populate: true))
                }
                return [.text(text)]
            })
    }
}
