import ApplicationServices
import Foundation

/// Mission Control Spaces. There is no public API to create a Space, so this is GUI automation:
/// open Mission Control, press the Dock's "Add Desktop" control, and verify the count rose.
public enum Spaces {
    static let addDesktopLabels = ["add desktop", "new desktop", "add space", "new space", "add space desktop"]

    public static func createDesktop(openDelay: Double = 0.9, closeAfter: Bool = true) async -> String {
        let (ok, message) = await create(openDelay: openDelay, closeAfter: closeAfter)
        return ok ? message : "Failed: \(message)"
    }

    static func create(openDelay: Double, closeAfter: Bool) async -> (Bool, String) {
        do {
            try await Input.hotKey(["control", "up"])
            try await Input.pause(openDelay)

            if let button = await findAddDesktopButton(waitFor: max(1.0, openDelay)) {
                let before = await offload { countSpaces(button) }
                if await activate(button) {
                    try await Input.pause(0.35)
                    let afterButton = await findAddDesktopButton(waitFor: 0.5)
                    let after = await afterButton.asyncMap { b in await offload { countSpaces(b) } } ?? nil
                    if closeAfter { try await close() }
                    if let before, let after {
                        if after > before { return (true, "Created new desktop space (\(after) total).") }
                        return (false, "Add Desktop control was activated but the space count did not increase (\(before) -> \(after)).")
                    }
                    // Could not verify the count via AX; trust the activation.
                    return (true, "Created new desktop space.")
                }
            }
            if closeAfter { try await close() }
            return (false, "Could not find Mission Control 'Add Desktop' button via Accessibility. "
                + "Ensure Accessibility is granted and Mission Control is available.")
        } catch {
            try? await close(wait: 0.1)
            return (false, "Failed to create desktop space: \(error)")
        }
    }

    static func close(wait: Double = 0.3) async throws {
        try await Input.keyPress(KeyMap.escape)
        try await Input.pause(wait)
    }

    /// Polls the Dock's AX tree (Mission Control's Spaces bar lives there) for the add control.
    static func findAddDesktopButton(waitFor seconds: Double) async -> AXUIElement? {
        let deadline = Date().addingTimeInterval(max(0, seconds))
        while true {
            let found = await offload { () -> AXUIElement? in
                guard let dock = App.byBundleID("com.apple.dock") ?? App.byName("Dock") else { return nil }
                let matches = findAll(dock.element, maxDepth: 12, where: isAddDesktop)
                return matches.first { AX.string(AX.attribute($0, kAXRoleAttribute)) == kAXButtonRole } ?? matches.first
            }
            if let found { return found }
            if Date() >= deadline || Task.isCancelled { return nil }
            try? await Task.sleep(for: .milliseconds(150))
        }
    }

    static func textBlob(_ element: AXUIElement) -> String {
        let raw = AX.attributes(element, [kAXDescriptionAttribute, kAXTitleAttribute, AXName.help,
                                          AXName.identifier, kAXRoleDescriptionAttribute])
        return raw.values.map { AX.string($0) }.joined(separator: " ").lowercased()
    }

    static func isAddDesktop(_ element: AXUIElement) -> Bool {
        let text = textBlob(element)
        return !text.trimmingCharacters(in: .whitespaces).isEmpty && addDesktopLabels.contains { text.contains($0) }
    }

    static func findAll(_ root: AXUIElement, maxDepth: Int, where predicate: (AXUIElement) -> Bool) -> [AXUIElement] {
        var results: [AXUIElement] = []
        var queue: [(AXUIElement, Int)] = [(root, 0)]
        while !queue.isEmpty {
            let (element, depth) = queue.removeFirst()
            if depth > 0 && predicate(element) { results.append(element) }
            if depth < maxDepth {
                queue += AX.children(element).map { ($0, depth + 1) }
            }
        }
        return results
    }

    /// AXPress first, then a click at the control's centre.
    static func activate(_ button: AXUIElement) async -> Bool {
        if await offload({ AX.perform(button, kAXPressAction) }) { return true }
        guard let rect = await offload({ AX.rect(button) }) else { return false }
        try? await Input.click(x: Int((rect.left + rect.right) / 2), y: Int((rect.top + rect.bottom) / 2))
        return true
    }

    /// Spaces = siblings of the add control that are not themselves the add control.
    static func countSpaces(_ button: AXUIElement) -> Int? {
        guard let parent = AX.element(AX.attribute(button, kAXParentAttribute)) else { return nil }
        return AX.children(parent).filter { !isAddDesktop($0) }.count
    }
}

extension Optional {
    func asyncMap<U>(_ transform: (Wrapped) async -> U) async -> U? {
        guard let self else { return nil }
        return await transform(self)
    }
}
