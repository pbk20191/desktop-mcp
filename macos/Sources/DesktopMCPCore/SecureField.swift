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

    /// `Type`'s answer for a secure field: the length, never the text.
    static func typedMessage(count: Int, x: Int, y: Int) -> String {
        "Typed \(count) characters into a secure field at (\(x),\(y))."
    }
}
