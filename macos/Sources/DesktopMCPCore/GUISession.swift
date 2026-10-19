import CoreGraphics
import Foundation

/// Whether this process runs inside a logged-in window server session, which is what AppKit
/// needs. A Launch Agent in the user's GUI domain or a process started from a terminal window
/// has one; an SSH login, a pre-login daemon or CI usually does not.
public enum GUISession {
    public static var isAvailable: Bool {
        guard let session = CGSessionCopyCurrentDictionary() as? [String: Any] else { return false }
        let onConsole = session[kCGSessionOnConsoleKey as String] as? Bool ?? false
        let loginDone = session[kCGSessionLoginDoneKey as String] as? Bool ?? false
        return onConsole && loginDone
    }
}
