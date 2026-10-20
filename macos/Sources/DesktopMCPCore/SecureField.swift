import ApplicationServices

/// Password fields. Their value is never read or emitted, not even its length.
enum SecureField {
    static let role = "AXSecureTextField"
    /// What is shown in place of a secure field's value.
    static let hidden = "(hidden)"

    static func isSecure(role: String?, subrole: String?) -> Bool {
        role == Self.role || subrole == Self.role
    }

    /// Reads only the role and subrole, never the value.
    static func isSecure(_ element: AXUIElement) -> Bool {
        let raw = AX.attributes(element, [kAXRoleAttribute, kAXSubroleAttribute])
        return isSecure(role: raw[kAXRoleAttribute] as? String, subrole: raw[kAXSubroleAttribute] as? String)
    }

    /// Like `isSecure(_:)`, but an element whose role cannot be read counts as secure: for
    /// deciding whether to read a value.
    static func withholdsValue(_ element: AXUIElement) -> Bool {
        let raw = AX.attributes(element, [kAXRoleAttribute, kAXSubroleAttribute])
        let role = raw[kAXRoleAttribute] as? String
        return role?.isEmpty != false || isSecure(role: role, subrole: raw[kAXSubroleAttribute] as? String)
    }

    /// How far `receivesSecureInput` climbs from the element under the point.
    static let maxAncestors = 5
    /// Where the climb stops: past these is no longer the field.
    static let climbStops: Set<String> = [kAXWindowRole, "AXWebArea"]

    /// Whether keys typed now go to a password field: the focused element is one, or the
    /// element under the point is, or one of its first `maxAncestors` ancestors below the
    /// window or web area (a click often lands on an inner element of a web input).
    static func receivesSecureInput<Node: SecureFieldNode>(focused: Node?, hit: Node?) -> Bool {
        if let roles = focused?.roles(), isSecure(role: roles.role, subrole: roles.subrole) { return true }
        var node = hit
        var climbed = 0
        while let current = node {
            let (role, subrole) = current.roles()
            if isSecure(role: role, subrole: subrole) { return true }
            if let role, climbStops.contains(role) { return false }
            guard climbed < maxAncestors else { return false }
            node = current.parent()
            climbed += 1
        }
        return false
    }

    /// `Type`'s answer for a secure field: the length, never the text.
    static func typedMessage(count: Int, x: Int, y: Int) -> String {
        "Typed \(count) characters into a secure field at (\(x),\(y))."
    }
}

/// What the password-field check reads of an element: its role and subrole, and its parent.
protocol SecureFieldNode {
    func roles() -> (role: String?, subrole: String?)
    func parent() -> Self?
}

/// An element under the point or with focus, with a 0.5 s messaging timeout set before its
/// first read.
struct AXSecureFieldNode: SecureFieldNode {
    let element: AXUIElement

    init(_ element: AXUIElement) {
        AX.setMessagingTimeout(element, 0.5)
        self.element = element
    }

    func roles() -> (role: String?, subrole: String?) {
        let raw = AX.attributes(element, [kAXRoleAttribute, kAXSubroleAttribute])
        return (raw[kAXRoleAttribute] as? String, raw[kAXSubroleAttribute] as? String)
    }

    func parent() -> AXSecureFieldNode? {
        AX.element(AX.attribute(element, kAXParentAttribute)).map(AXSecureFieldNode.init)
    }
}
