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

    func testTypedMessage() {
        XCTAssertEqual(SecureField.typedMessage(count: 7, x: 10, y: 20),
                       "Typed 7 characters into a secure field at (10,20).")
    }
}
