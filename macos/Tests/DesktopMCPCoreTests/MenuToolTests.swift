import XCTest
@testable import DesktopMCPCore

/// An in-memory menu item; `presses` and `cancels` count how often it was pressed and cancelled.
/// A `lazy` item reports no children until pressed, like a bar item whose menu is built when it
/// opens. File-private, as Core has its own `TreeNode` (the accessibility tree).
private struct TreeNode: MenuNode {
    var title: String
    var enabled = true
    var shortcut: String?
    var isSeparator = false
    var kids: [TreeNode] = []
    var lazy = false
    let presses = Locked(0)
    let cancels = Locked(0)
    let populated = Locked(false)

    init(_ title: String, enabled: Bool = true, shortcut: String? = nil, _ kids: [TreeNode] = []) {
        self.title = title
        self.enabled = enabled
        self.shortcut = shortcut
        self.kids = kids
    }

    static var separator: TreeNode {
        var node = TreeNode("", enabled: false)
        node.isSeparator = true
        return node
    }

    func children() -> [TreeNode] {
        if lazy, !populated.current, press(), !kids.isEmpty { populated.current = true }
        return kids
    }

    var openedMenu: Bool { populated.current }

    func cancel() { cancels.withLock { $0 += 1 } }

    func press() -> Bool {
        presses.withLock { $0 += 1 }
        return true
    }
}

final class MenuToolTests: XCTestCase {
    /// Apple / File[New ⌘N, —, Export… (disabled), Open Recent > [a, b]] / Edit[Copy, copy]
    private func bar() -> [TreeNode] {
        [
            TreeNode("Apple", [TreeNode("About This Mac")]),
            TreeNode("File", [
                TreeNode("New", shortcut: "⌘N"),
                .separator,
                TreeNode("Export…", enabled: false),
                TreeNode("Open Recent", [TreeNode("a"), TreeNode("b")]),
            ]),
            TreeNode("Edit", [TreeNode("Copy", shortcut: "⌘C"), TreeNode("copy")]),
        ]
    }

    private func titles(_ nodes: [TreeNode]) -> [String] { nodes.map(\.title) }

    private func assertError(_ message: String, file: StaticString = #filePath, line: UInt = #line,
                             _ body: () throws -> Void) {
        XCTAssertThrowsError(try body(), file: file, line: line) { error in
            XCTAssertEqual((error as? ToolError)?.message, message, file: file, line: line)
        }
    }

    func testExactThenCaseInsensitiveThenEllipsis() throws {
        XCTAssertEqual(titles(try MenuTool.resolve(["File", "Export…"], in: bar())), ["File", "Export…"])
        XCTAssertEqual(titles(try MenuTool.resolve(["file", "export…"], in: bar())), ["File", "Export…"])
        XCTAssertEqual(titles(try MenuTool.resolve(["file", "Export..."], in: bar())), ["File", "Export…"])
        XCTAssertEqual(titles(try MenuTool.resolve(["FILE ", "open recent", "B"], in: bar())),
                       ["File", "Open Recent", "b"])
    }

    func testAppleAlias() throws {
        let roots = bar()
        XCTAssertEqual(titles(try MenuTool.resolve(["Apple"], in: roots)), ["Apple"])
        XCTAssertEqual(titles(try MenuTool.resolve(["apple", "About This Mac"], in: roots)), ["Apple", "About This Mac"])
        // Whatever title root 0 has, "Apple" names it.
        var other = bar()
        other[0].title = ""
        XCTAssertEqual(titles(try MenuTool.resolve(["Apple", "About This Mac"], in: other)), ["", "About This Mac"])
    }

    func testAmbiguousErrors() {
        assertError("Menu item 'COPY' is ambiguous: Copy, copy") {
            _ = try MenuTool.resolve(["Edit", "COPY"], in: bar())
        }
    }

    func testExactBeatsCaseInsensitive() throws {
        let chain = try MenuTool.resolve(["Edit", "copy"], in: bar())
        XCTAssertEqual(titles(chain), ["Edit", "copy"])
        XCTAssertNil(chain[1].shortcut)
    }

    func testMissingListsSiblings() {
        assertError("No menu item 'Save' under File. Available: New, Export…, Open Recent") {
            _ = try MenuTool.resolve(["File", "Save"], in: bar())
        }
        assertError("No menu item 'c' under File > Open Recent. Available: a, b") {
            _ = try MenuTool.resolve(["File", "Open Recent", "c"], in: bar())
        }
        assertError("No menu item 'x' under File > New; it has no items.") {
            _ = try MenuTool.resolve(["File", "New", "x"], in: bar())
        }
    }

    func testMissingTopLevel() {
        assertError("No menu item 'View' under the menu bar. Available: Apple, File, Edit") {
            _ = try MenuTool.resolve(["View"], in: bar())
        }
    }

    func testDisabledIsNotPressed() {
        let roots = bar()
        assertError("Menu item File > Export… is disabled.") {
            _ = try MenuTool.select(["File", "Export..."], in: roots)
        }
        XCTAssertEqual(roots[1].kids[2].presses.current, 0)
    }

    func testSelectPressesLeafOnly() throws {
        let roots = bar()
        XCTAssertEqual(try MenuTool.select(["File", "Open Recent", "b"], in: roots), "Selected File > Open Recent > b.")
        let recent = roots[1].kids[3]
        XCTAssertEqual(recent.kids[1].presses.current, 1)
        XCTAssertEqual(recent.kids[0].presses.current, 0)
        XCTAssertEqual(recent.presses.current, 0)
        XCTAssertEqual(roots[1].presses.current, 0)
    }

    func testSelectRefusesSubmenu() {
        let roots = bar()
        assertError("Menu item File > Open Recent opens a submenu; add the item to select.") {
            _ = try MenuTool.select(["File", "Open Recent"], in: roots)
        }
        XCTAssertEqual(roots[1].kids[3].presses.current, 0)
    }

    /// File as a bar item that builds its menu only when pressed.
    private func lazyBar() -> [TreeNode] {
        var roots = bar()
        roots[1].lazy = true
        return roots
    }

    func testFailureAfterPopulateCancelsOnce() {
        let roots = lazyBar()
        assertError("No menu item 'Save' under File. Available: New, Export…, Open Recent") {
            _ = try MenuTool.select(["File", "Save"], in: roots)
        }
        XCTAssertEqual(roots[1].presses.current, 1)
        XCTAssertEqual(roots[1].cancels.current, 1)

        let disabled = lazyBar()
        assertError("Menu item File > Export… is disabled.") {
            _ = try MenuTool.select(["File", "Export…"], in: disabled)
        }
        XCTAssertEqual(disabled[1].cancels.current, 1)
    }

    func testNoCancelWithoutPopulateOrOnSuccess() throws {
        let roots = bar()
        assertError("No menu item 'Save' under File. Available: New, Export…, Open Recent") {
            _ = try MenuTool.select(["File", "Save"], in: roots)
        }
        XCTAssertEqual(roots.map { $0.cancels.current }, [0, 0, 0])

        let lazy = lazyBar()
        XCTAssertEqual(try MenuTool.select(["File", "New"], in: lazy), "Selected File > New.")
        XCTAssertEqual(lazy[1].presses.current, 1)
        XCTAssertEqual(lazy[1].kids[0].presses.current, 1)
        XCTAssertEqual(lazy.map { $0.cancels.current }, [0, 0, 0])

        let top = lazyBar()
        assertError("No menu item 'View' under the menu bar. Available: Apple, File, Edit") {
            _ = try MenuTool.select(["View"], in: top)
        }
        XCTAssertEqual(top.map { $0.cancels.current }, [0, 0, 0])
    }

    func testListIndentsAndMarks() {
        XCTAssertEqual(MenuTool.list(bar(), depth: 2), """
            Apple
              About This Mac
            File
              New  ⌘N
              Export…  [disabled]
              Open Recent
            Edit
              Copy  ⌘C
              copy
            """)
        XCTAssertEqual(MenuTool.list(bar()[1].kids, depth: 2), """
            New  ⌘N
            Export…  [disabled]
            Open Recent
              a
              b
            """)
        XCTAssertEqual(MenuTool.list(bar(), depth: 1), "Apple\nFile\nEdit")
        XCTAssertEqual(MenuTool.list([TreeNode](), depth: 2), "No menu items.")
    }

    func testDepthIsCapped() {
        let deep = [TreeNode("1", [TreeNode("2", [TreeNode("3", [TreeNode("4", [TreeNode("5", [TreeNode("6")])])])])])]
        XCTAssertEqual(MenuTool.list(deep, depth: 9), MenuTool.list(deep, depth: 4))
        XCTAssertEqual(MenuTool.list(deep, depth: 9), "1\n  2\n    3\n      4")
        XCTAssertEqual(MenuTool.list(deep, depth: 0), "1")
    }

    func testShortcutLabels() {
        XCTAssertEqual(MenuTool.shortcutLabel(char: "E", modifiers: 0), "⌘E")
        XCTAssertEqual(MenuTool.shortcutLabel(char: "E", modifiers: 1), "⇧⌘E")
        XCTAssertEqual(MenuTool.shortcutLabel(char: "E", modifiers: 6), "⌃⌥⌘E")
        XCTAssertEqual(MenuTool.shortcutLabel(char: "E", modifiers: 8), "E")
    }
}
