import ApplicationServices
import XCTest
@testable import DesktopMCPCore

final class SecureFieldTests: XCTestCase {
    private let box = BoundingBox(left: 0, top: 0, right: 100, bottom: 20, width: 100, height: 20)

    func testIsSecure() {
        XCTAssertTrue(SecureField.isSecure(role: "AXTextField", subrole: "AXSecureTextField"))
        XCTAssertTrue(SecureField.isSecure(role: "AXSecureTextField", subrole: nil))
        XCTAssertFalse(SecureField.isSecure(role: "AXTextField", subrole: nil))
        XCTAssertFalse(SecureField.isSecure(role: nil, subrole: nil))
    }

    /// Even if a value reached the formatter, a secure field's output carries neither it nor its length.
    func testSecureFieldValueNeverAppears() {
        let early = EarlyAttributes(role: "AXTextField", subrole: "AXSecureTextField")
        let late = LateAttributes(value: "hunter2" as NSString, selectedText: "hunter2",
                                  selectedRange: CFRange(location: 0, length: 7), secure: true)
        let node = Tree.describe(early: early, late: late, linkedTitle: nil, box: box, windowName: "Login")
        let output = TreeState(interactiveNodes: [node]).interactiveElementsString()

        XCTAssertTrue(output.contains(SecureField.hidden), output)
        XCTAssertFalse(output.contains("hunter2"), output)
        XCTAssertFalse(output.contains("•"), output)
        XCTAssertFalse(output.contains("7"), output)
        XCTAssertFalse(output.contains("selected"), output)
        XCTAssertEqual(node.metadata["value"], .string(SecureField.hidden))
    }

    /// A secure field's own label still shows; only the value is withheld.
    func testSecureFieldKeepsItsLabel() {
        let early = EarlyAttributes(role: "AXTextField", subrole: "AXSecureTextField")
        let late = LateAttributes(description: "Password", placeholder: "Required", secure: true)
        let node = Tree.describe(early: early, late: late, linkedTitle: nil, box: box, windowName: "Login")
        XCTAssertEqual(node.name, "Password")
        XCTAssertEqual(node.metadata.serialized(), #"{"placeholder": "Required", "value": "(hidden)"}"#)
    }

    /// The same text field without the secure subrole still reports its value.
    func testPlainFieldStillShowsItsValue() {
        let early = EarlyAttributes(role: "AXTextField")
        let late = LateAttributes(value: "hello" as NSString)
        let node = Tree.describe(early: early, late: late, linkedTitle: nil, box: box, windowName: "App")
        XCTAssertEqual(node.name, "hello")
        XCTAssertEqual(node.metadata["value"], .string("hello"))
    }

    /// The walker never asks a secure element for its value or selected text.
    func testSecureElementsAreNeverAskedForTheirValue() {
        let withheld = [kAXValueAttribute, kAXSelectedTextAttribute, kAXSelectedTextRangeAttribute]
        for role in ["AXTextField", "AXSecureTextField", nil] as [String?] {
            let names = LateAttributes.names(for: role, secure: true)
            for name in withheld {
                XCTAssertFalse(names.contains(name), "\(role ?? "nil") asks for \(name)")
            }
            XCTAssertTrue(names.contains(kAXTitleAttribute))
        }
        XCTAssertTrue(LateAttributes.names(for: "AXTextField", secure: false).contains(kAXValueAttribute))
        XCTAssertEqual(EarlyAttributes(role: "AXTextField", subrole: "AXSecureTextField").isSecure, true)
        XCTAssertEqual(EarlyAttributes(role: "AXTextField").isSecure, false)
    }

    /// The walker asks an interactive element for what its phase-1 attributes allow: never the
    /// value of a secure field, nor of one whose role could not be read.
    func testWalkerAsksBySecureFromEarlyAttributes() {
        let withheld: Set<String> = [kAXValueAttribute, kAXSelectedTextAttribute, kAXSelectedTextRangeAttribute]
        for early in [EarlyAttributes(role: "AXTextField", subrole: "AXSecureTextField"),
                      EarlyAttributes(role: "AXSecureTextField"), EarlyAttributes(role: "")] {
            XCTAssertTrue(early.isSecure, early.role)
            XCTAssertTrue(withheld.isDisjoint(with: LateAttributes.names(for: early)), early.role)
        }
        XCTAssertTrue(LateAttributes.names(for: EarlyAttributes(role: "AXTextField")).contains(kAXValueAttribute))
    }

    /// An element whose role could not be read is shown as a secure field.
    func testUnreadableRoleFailsClosed() {
        let node = Tree.describe(early: EarlyAttributes(role: ""), late: LateAttributes(value: "hunter2" as NSString),
                                 linkedTitle: nil, box: box, windowName: "Login")
        XCTAssertFalse(TreeState(interactiveNodes: [node]).interactiveElementsString().contains("hunter2"))
        XCTAssertEqual(node.metadata["value"], .string(SecureField.hidden))
    }

    /// A fake element for the password-field climb; `parentNode` is its parent.
    final class Node: SecureFieldNode {
        let role: String?
        let subrole: String?
        let parentNode: Node?
        let reads: Locked<Int>

        init(_ role: String?, subrole: String? = nil, parent: Node? = nil, reads: Locked<Int> = Locked(0)) {
            self.role = role
            self.subrole = subrole
            self.parentNode = parent
            self.reads = reads
        }

        func roles() -> (role: String?, subrole: String?) {
            reads.withLock { $0 += 1 }
            return (role, subrole)
        }

        func parent() -> Node? { parentNode }
    }

    /// `inner` under `depth` plain groups under `top`.
    private func chain(depth: Int, top: Node, reads: Locked<Int> = Locked(0)) -> Node {
        var node = top
        for _ in 0..<depth { node = Node("AXGroup", parent: node, reads: reads) }
        return node
    }

    func testClimbFindsAPasswordFieldThreeLevelsUp() {
        let field = Node("AXTextField", subrole: "AXSecureTextField", parent: Node("AXWebArea"))
        XCTAssertTrue(SecureField.receivesSecureInput(focused: nil, hit: chain(depth: 3, top: field)))
        XCTAssertFalse(SecureField.receivesSecureInput(focused: nil, hit: chain(depth: 3, top: Node("AXTextField"))))
        // No more than five ancestors.
        XCTAssertTrue(SecureField.receivesSecureInput(focused: nil, hit: chain(depth: 5, top: field)))
        XCTAssertFalse(SecureField.receivesSecureInput(focused: nil, hit: chain(depth: 6, top: field)))
    }

    func testClimbStopsAtTheWindowOrWebArea() {
        let reads = Locked(0)
        let field = Node("AXSecureTextField")
        for stop in ["AXWindow", "AXWebArea"] {
            reads.current = 0
            let hit = chain(depth: 1, top: Node(stop, parent: field, reads: reads), reads: reads)
            XCTAssertFalse(SecureField.receivesSecureInput(focused: nil, hit: hit), stop)
            XCTAssertEqual(reads.current, 2, stop)  // the group and the stop, never the field above
        }
    }

    func testFocusedPasswordFieldWins() {
        let hit = Node("AXTextField", parent: Node("AXWindow"))
        XCTAssertTrue(SecureField.receivesSecureInput(focused: Node("AXSecureTextField"), hit: hit))
        XCTAssertTrue(SecureField.receivesSecureInput(focused: Node("AXTextField", subrole: "AXSecureTextField"), hit: nil))
        XCTAssertFalse(SecureField.receivesSecureInput(focused: Node("AXTextField"), hit: hit))
        XCTAssertFalse(SecureField.receivesSecureInput(focused: Node?.none, hit: nil))
    }

    func testTypedMessage() {
        XCTAssertEqual(SecureField.typedMessage(count: 7, x: 10, y: 20),
                       "Typed 7 characters into a secure field at (10,20).")
    }
}
