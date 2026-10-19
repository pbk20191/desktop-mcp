import MCP
import XCTest
@testable import DesktopMCPCore

final class FormattingTests: XCTestCase {
    func testJSONRoundTripKeepsTypesAndUTF8() throws {
        let text = #"{"a":1,"b":true,"c":"한글\n\"q\"","d":[1.5,null],"e":{}}"#
        let value = try JSON.parse(text)
        XCTAssertEqual(value["a"], 1)
        XCTAssertEqual(value["b"], true)
        XCTAssertEqual(value["c"], "한글\n\"q\"")
        XCTAssertEqual(JSONCoding.encodeString(value), text)
    }

    func testOrderedJSONRendersNumbersLikePython() throws {
        var metadata = OrderedJSON()
        metadata["big"] = .int(9_007_199_254_740_993)
        metadata["neg"] = .int(-3)
        metadata["one"] = .double(1.0)
        metadata["half"] = .double(0.5)
        metadata["nan"] = .double(.nan)
        metadata["list"] = [1, "a", nil, true, 2.5, ["k": [:]]]
        XCTAssertEqual(
            metadata.serialized(),
            #"{"big": 9007199254740993, "neg": -3, "one": 1, "half": 0.5, "nan": null, "list": [1, "a", null, true, 2.5, {"k": {}}]}"#)
    }

    func testOrderedJSONSortsNestedKeysAndRendersDataAsDataURL() {
        var metadata = OrderedJSON()
        metadata["o"] = ["b": 1, "a": 2]
        metadata["d"] = .data(mimeType: "text/plain", Data("hi".utf8))
        XCTAssertEqual(metadata.serialized(), #"{"o": {"a": 2, "b": 1}, "d": "data:text/plain;base64,aGk="}"#)
    }

    func testOrderedMetadataMatchesPythonJsonDumpsLayout() {
        var metadata = OrderedJSON()
        metadata["placeholder"] = "Search"
        metadata["checked"] = true
        metadata["value"] = 3
        XCTAssertEqual(metadata.serialized(), #"{"placeholder": "Search", "checked": true, "value": 3}"#)
        XCTAssertEqual(OrderedJSON().serialized(), "{}")
    }

    func testTreeStateRowsAndIdsContinueIntoScrollables() {
        let box = BoundingBox(left: 10, top: 20, right: 31, bottom: 41, width: 21, height: 21)
        var metadata = OrderedJSON()
        metadata["shortcut"] = "⌘N"
        let state = TreeState(
            interactiveNodes: [TreeNode(box: box, name: "New", controlType: "AXMenuItem", windowName: "Finder", metadata: metadata)],
            scrollableNodes: [TreeNode(box: box, name: "", controlType: "AXScrollArea", windowName: "Finder")])
        XCTAssertEqual(state.interactiveElementsString(),
                       "# id|window|control_type|name|coords|metadata\n0|Finder|AXMenuItem|New|(20,30)|{\"shortcut\": \"⌘N\"}")
        XCTAssertEqual(state.scrollableElementsString(),
                       "# id|window|control_type|name|coords|metadata\n1|Finder|AXScrollArea||(20,30)|{}")
        XCTAssertEqual(TreeState().interactiveElementsString(), "No elements found")
    }

    func testBoundingBoxTruncatesAndCentresWithFloorDivision() {
        let box = BoundingBox(Rect(left: 0.9, top: -5.5, right: 11, bottom: 4))
        XCTAssertEqual(box, BoundingBox(left: 0, top: -5, right: 11, bottom: 4, width: 10, height: 9))
        XCTAssertEqual(box.center, Center(x: 5, y: -1))
        XCTAssertEqual(floorDiv(-3, 2), -2)
        XCTAssertEqual(BoundingBox(Rect(left: .infinity, top: .nan, right: 1, bottom: 1)).left, 0)
    }

    func testIntersection() {
        let window = BoundingBox(left: 0, top: 0, right: 100, bottom: 100, width: 100, height: 100)
        let partly = BoundingBox(left: 90, top: 90, right: 120, bottom: 110, width: 30, height: 20)
        XCTAssertEqual(window.intersection(partly), BoundingBox(left: 90, top: 90, right: 100, bottom: 100, width: 10, height: 10))
        let outside = BoundingBox(left: 200, top: 0, right: 210, bottom: 10, width: 10, height: 10)
        XCTAssertEqual(window.intersection(outside), .empty)
    }

    func testMenuShortcutFormatting() {
        XCTAssertEqual(formatMenuShortcut(char: "c", modifiers: 0, virtualKey: nil), "⌘C")
        XCTAssertEqual(formatMenuShortcut(char: "N", modifiers: 1, virtualKey: nil), "⇧⌘N")
        XCTAssertEqual(formatMenuShortcut(char: "F", modifiers: 8 | 4 | 16, virtualKey: nil), "🌐⌃F")
        XCTAssertEqual(formatMenuShortcut(char: nil, modifiers: 2, virtualKey: 36), "⌥⌘↩")
        XCTAssertEqual(formatMenuShortcut(char: "\u{1b}", modifiers: nil, virtualKey: nil), "⌘⎋")
        XCTAssertEqual(formatMenuShortcut(char: nil, modifiers: nil, virtualKey: nil), "")
        XCTAssertEqual(formatMenuShortcut(char: " ", modifiers: 0, virtualKey: nil), "")
    }

    func testKeyMapCoversShortcutNames() {
        for name in ["command", "cmd", "shift", "option", "alt", "control", "ctrl", "fn"] {
            XCTAssertNotNil(KeyMap.modifiers[name], name)
        }
        XCTAssertEqual(KeyMap.codes["c"], 8)
        XCTAssertEqual(KeyMap.codes["space"], 49)
        XCTAssertEqual(KeyMap.codes["up"], 126)
        XCTAssertEqual(KeyMap.codes["f12"], 111)
    }

    func testMalformedShortcutsAreRejectedBeforeAnyKeyIsPressed() async {
        for (shortcut, expected) in [("command+nosuchkey", "Unknown key"), ("command++c", "non-empty")] {
            do {
                try await Desktop().shortcut(shortcut)
                XCTFail("expected an error for \(shortcut)")
            } catch {
                XCTAssertTrue("\(error)".contains(expected), "\(error)")
            }
        }
    }

    func testScaleFormatting() {
        XCTAssertEqual(Desktop().formatScale(2.0), "2")
        XCTAssertEqual(Desktop().formatScale(1.5), "1.5")
    }
}
