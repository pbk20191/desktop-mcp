import AppKit
import Carbon
import Foundation
import OSAKit

/// The Shell tool's `osascript` mode, run in-process through OSAKit instead of a child process.
///
/// What this buys over `/usr/bin/osascript -e`: the script can be interrupted (a timeout or an
/// MCP cancellation stops `delay`, loops and pending Apple Events within a fraction of a second,
/// through the OSA active and send procs), every Apple Event the script sends is seen (so the
/// configured deny list can refuse events to particular applications, and the per-call deadline
/// bounds each send), and compile errors come with their source range. The text produced is the
/// same as osascript's: results rendered with OSADisplay, errors as `start:end: kind: message
/// (number)`, `log` output appended as osascript's stderr would be.
///
/// Scripts run one at a time on a serial queue, each with a fresh language instance. The
/// AppleScript component switches a process-wide pointer to the current instance's globals on
/// every entry, so two instances in use on two threads read each other's (freed) memory; Guard
/// Malloc caught it in `OSADispose` and `OpenComponent`. Serial it is, as in Script Editor. The
/// Swift concurrency pool is never blocked: the queue's thread does the waiting.
public enum OSARunner {
    public enum Language: String, Sendable {
        case appleScript = "AppleScript"
        case javaScript = "JavaScript"
    }

    /// What the send proc enforces, from `[osascript]` in config.toml.
    public struct Policy: Equatable, Sendable {
        /// Bundle identifiers that must not receive Apple Events from scripts.
        public var deniedBundleIDs: Set<String> = []
        public init() {}
    }

    /// One Apple Event the script tried to send.
    public struct SentEvent: Equatable, Sendable {
        public var eventClass: String
        public var eventID: String
        public var bundleID: String?
        public var pid: pid_t?
        public var denied: Bool
    }

    public struct Execution: Sendable {
        /// The result as osascript prints it; empty when the script produced none.
        public var result = ""
        public var errorMessage: String?
        public var errorNumber: Int?
        public var errorRange: NSRange?
        public var isSyntaxError = false
        /// `log` statements, in order.
        public var logs: [String] = []
        public var events: [SentEvent] = []
        public var timedOut = false
        public var cancelled = false
    }

    /// Serial: see the type comment. A script queued behind a long one may reach its deadline
    /// while waiting; it is then stopped at its first instruction and reported as timed out.
    static let queue = DispatchQueue(label: "desktop-mcp.osa", qos: .userInitiated)

    /// With `serve --debug`: every script start and end, and every Apple Event it sends, on stderr.
    public static let trace = Locked(false)
    static func traceLog(_ message: @autoclosure () -> String) {
        if trace.withLock({ $0 }) { log("[osa] " + message()) }
    }

    // MARK: Shell tool entry point

    /// The Shell tool's result for an osascript-mode command, shaped like the child-process path.
    public static func run(_ source: String, timeout: Double, requested: Int, policy: Policy) async -> Shell.Result {
        result(of: await execute(source, timeout: timeout, policy: policy), timeout: timeout, requested: requested)
    }

    /// The Shell tool's text and status for a finished execution.
    static func result(of execution: Execution, timeout: Double, requested: Int) -> Shell.Result {
        let logs = execution.logs.joined(separator: "\n")
        if execution.cancelled { return Shell.Result(output: "Command cancelled", status: -1) }
        if execution.timedOut {
            var message = "Command timed out after \(Int(timeout)) seconds"
            if requested > Int(timeout) { message += " (requested \(requested)s, capped by \(Shell.maxTimeoutEnv))" }
            if !logs.isEmpty { message += "\n--- partial output before timeout ---\n" + logs }
            return Shell.Result(output: message, status: -1)
        }
        if let number = execution.errorNumber {
            var prefix = ""
            if let range = execution.errorRange, range.location != 0 || range.length != 0 {
                prefix = "\(range.location):\(range.location + range.length): "
            }
            let kind = execution.isSyntaxError ? "syntax error" : "execution error"
            var text = "\(prefix)\(kind): \(execution.errorMessage ?? "") (\(number))"
            if number == errAENoUserInteraction { text += "\n" + userInteractionHint }
            return Shell.Result(output: Shell.combine("", logs.isEmpty ? text : logs + "\n" + text), status: 1)
        }
        return Shell.Result(output: Shell.combine(execution.result, logs).trimmingCharacters(in: .whitespacesAndNewlines), status: 0)
    }

    /// Dialogs need AppKit, which needs a window server session; without one the error gets a
    /// pointer to what works.
    static let userInteractionHint = "Hint: this server has no window server session, so it cannot show dialogs itself "
        + "(run it from a logged-in GUI session, e.g. as the Launch Agent). As a fallback, address user-interface commands "
        + "to System Events: tell application \"System Events\" to display alert \"Title\" message \"Text\". "
        + "For display notification use the Notification tool."

    // MARK: Language

    /// osascript's `-l JavaScript`, spelled inside the source: a shebang naming JavaScript, or a
    /// `// language: JavaScript` first line. Anything else is AppleScript.
    public static func language(of source: String) -> Language {
        let first = source.prefix { $0 != "\n" && $0 != "\r\n" }.lowercased()
        if first.hasPrefix("#!"), first.contains("javascript") { return .javaScript }
        if first.hasPrefix("//"), first.contains("javascript"), first.contains("lang") { return .javaScript }
        return .appleScript
    }

    /// A `#!` line is for the shell, not the interpreter; osascript drops it from files too.
    static func stripShebang(_ source: String) -> String {
        guard source.hasPrefix("#!") else { return source }
        guard let newline = source.firstIndex(where: { $0 == "\n" || $0 == "\r\n" }) else { return "" }
        return String(source[source.index(after: newline)...])
    }

    // MARK: Execution

    public static func execute(_ source: String, timeout: Double, policy: Policy) async -> Execution {
        let language = language(of: source)
        let body = stripShebang(source)
        let context = Context(policy: policy, deadline: Date().addingTimeInterval(timeout))
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                queue.async {
                    continuation.resume(returning: runBlocking(body, language: language, context: context))
                }
            }
        } onCancel: {
            context.cancel()
        }
    }

    /// Everything the OSA callbacks need, reachable through their refCon.
    final class Context: @unchecked Sendable {
        let policy: Policy
        let deadline: Date
        private let state = Locked(State())

        struct State {
            var cancelled = false
            var timedOut = false
            var logs: [String] = []
            var events: [SentEvent] = []
        }

        init(policy: Policy, deadline: Date) {
            self.policy = policy
            self.deadline = deadline
        }

        func cancel() { state.withLock { $0.cancelled = true } }

        /// True once the script must stop; notes a passed deadline as a timeout.
        func shouldStop() -> Bool {
            state.withLock { state in
                if state.cancelled { return true }
                if Date() > deadline { state.timedOut = true; return true }
                return state.timedOut
            }
        }

        var remaining: TimeInterval { deadline.timeIntervalSinceNow }
        func log(_ text: String) { state.withLock { $0.logs.append(text) } }
        func record(_ event: SentEvent) { state.withLock { $0.events.append(event) } }
        var snapshot: State { state.withLock { $0 } }
    }

    static func runBlocking(_ source: String, language: Language, context: Context) -> Execution {
        var execution = Execution()
        // Queued behind another script past its own deadline, or cancelled meanwhile: a short
        // script would otherwise run to completion before the active proc ever gets a say.
        if context.shouldStop() {
            let state = context.snapshot
            execution.cancelled = state.cancelled
            execution.timedOut = !state.cancelled && state.timedOut
            traceLog("\(language.rawValue) script not started: " + (state.cancelled ? "cancelled" : "deadline passed while queued"))
            return execution
        }
        guard let osaLanguage = OSALanguage(forName: language.rawValue) else {
            execution.errorMessage = "\(language.rawValue) is not available on this system"
            execution.errorNumber = -1
            return execution
        }
        // A fresh instance per run: the component is thread-safe only one instance per thread.
        traceLog("\(language.rawValue) script starting (\(source.count) chars, deadline in \(Int(context.remaining))s)")
        let instance = OSALanguageInstance(language: osaLanguage)
        let component = instance.componentInstance
        let refCon = Unmanaged.passUnretained(context).toOpaque()
        OSASetActiveProc(component, activeProc, refCon)
        OSASetSendProc(component, sendProc, refCon)
        let script = OSAScript(source: source, from: nil, languageInstance: instance, using: [])
        var errorInfo: NSDictionary?
        if !script.compileAndReturnError(&errorInfo) {
            execution.isSyntaxError = true
            fill(&execution, from: errorInfo)
        } else {
            let result = script.executeAndReturnError(&errorInfo)
            if let result {
                execution.result = result.descriptorType == typeNull ? "" : display(result, instance)
            } else {
                fill(&execution, from: errorInfo)
            }
        }
        // Detach the callbacks before the context can go away.
        OSASetActiveProc(component, nil, nil)
        OSASetSendProc(component, nil, nil)

        let state = context.snapshot
        execution.logs = state.logs
        execution.events = state.events
        execution.cancelled = state.cancelled
        execution.timedOut = !state.cancelled && state.timedOut
        traceLog("script finished: " + (execution.errorNumber.map { "error \($0) \(execution.errorMessage ?? "")" } ?? "ok")
                 + (execution.timedOut ? " (timed out)" : "") + (execution.cancelled ? " (cancelled)" : ""))
        return execution
    }

    static func fill(_ execution: inout Execution, from errorInfo: NSDictionary?) {
        guard let errorInfo else {
            execution.errorMessage = "unknown error"
            execution.errorNumber = -1
            return
        }
        execution.errorNumber = (errorInfo[OSAScriptErrorNumberKey] as? NSNumber)?.intValue ?? -1
        // The full message names the application that failed, as osascript prints it; the brief
        // one does not.
        execution.errorMessage = (errorInfo[OSAScriptErrorMessageKey] as? String)
            ?? (errorInfo[OSAScriptErrorBriefMessageKey] as? String) ?? ""
        execution.errorRange = (errorInfo[OSAScriptErrorRangeKey] as? NSValue)?.rangeValue
    }

    /// osascript's rendering (`-s h`, "for humans") of a result.
    static func display(_ value: NSAppleEventDescriptor, _ instance: OSALanguageInstance) -> String {
        let component = instance.componentInstance
        var scriptID = OSAID(kOSANullScript)
        guard OSACoerceFromDesc(component, value.aeDesc, 0, &scriptID) == noErr else { return value.stringValue ?? "" }
        defer { OSADispose(component, scriptID) }
        var text = AEDesc()
        guard OSADisplay(component, scriptID, typeUnicodeText, Int32(kOSAModeDisplayForHumans), &text) == noErr else {
            return value.stringValue ?? ""
        }
        return NSAppleEventDescriptor(aeDescNoCopy: &text).stringValue ?? ""
    }

    // MARK: OSA callbacks

    /// Called by the interpreter between statements and while it waits (`delay`, event replies):
    /// a non-zero return aborts the script, which then fails with -128 "User canceled".
    private static let activeProc: OSAActiveProcPtr = { refCon in
        let context = Unmanaged<Context>.fromOpaque(refCon!).takeUnretainedValue()
        return context.shouldStop() ? OSErr(truncatingIfNeeded: userCanceledErr) : OSErr(noErr)
    }

    /// Every Apple Event the script sends passes through here: `log` output is collected instead
    /// of being sent, denied targets are refused with a privilege error, and each send gets at
    /// most the time left before the deadline.
    ///
    /// Events addressed to this process (StandardAdditions: `display alert`, `path to`, ...) are
    /// sent like any other; their handlers run on the main thread through AppKit's event loop
    /// (see AppKitHost in the executable), which is what lets dialogs work in process.
    private static let sendProc: OSASendProcPtr = { event, reply, sendMode, _, timeoutTicks, _, _, refCon in
        let context = Unmanaged<Context>.fromOpaque(refCon!).takeUnretainedValue()
        guard let event else { return OSErr(truncatingIfNeeded: paramErr) }
        // The caller owns `event`; the wrapper must own a copy, or it would dispose theirs.
        var copy = AEDesc()
        guard AEDuplicateDesc(event, &copy) == noErr else { return OSErr(truncatingIfNeeded: memFullErr) }
        let descriptor = NSAppleEventDescriptor(aeDescNoCopy: &copy)

        // AESendMessage always fills `reply` (typeNull when there is none); paths that answer
        // without sending must do the same, or the caller disposes whatever was there.
        func noReply() { if let reply { AEInitializeDesc(reply) } }
        if descriptor.eventClass == AEEventClass(kASAppleScriptSuite), descriptor.eventID == AEEventID(kASCommentEvent) {
            context.log(descriptor.paramDescriptor(forKeyword: AEKeyword(keyDirectObject))?.stringValue ?? "")
            noReply()
            return OSErr(noErr)
        }

        let (pid, bundleID) = target(of: descriptor)
        let denied = bundleID.map { context.policy.deniedBundleIDs.contains($0) } ?? false
        let toSelf = pid == nil || pid == getpid()
        context.record(SentEvent(eventClass: fourCC(descriptor.eventClass), eventID: fourCC(descriptor.eventID),
                                 bundleID: bundleID, pid: pid, denied: denied))
        traceLog("send \(fourCC(descriptor.eventClass))/\(fourCC(descriptor.eventID)) -> "
                 + (bundleID ?? (toSelf ? "self" : "pid \(pid.map(String.init) ?? "?")"))
                 + (denied ? " DENIED" : "") + " timeout=\(timeoutTicks)")
        if denied {
            noReply()
            return OSErr(truncatingIfNeeded: errAEPrivilegeError)
        }

        let remaining = context.remaining
        guard remaining > 0 else {
            _ = context.shouldStop()
            noReply()
            return OSErr(truncatingIfNeeded: userCanceledErr)
        }
        var ticks = Int(remaining * 60)
        if timeoutTicks > 0 { ticks = min(ticks, Int(timeoutTicks)) }
        return OSErr(truncatingIfNeeded: AESendMessage(event, reply, sendMode, max(ticks, 1)))
    }

    /// Who the event is addressed to. AppleScript addresses running applications by process
    /// serial number; the Apple Event Manager coerces that to a pid, and AppKit names the pid.
    static func target(of event: NSAppleEventDescriptor) -> (pid_t?, String?) {
        guard let address = event.attributeDescriptor(forKeyword: AEKeyword(keyAddressAttr)) else { return (nil, nil) }
        if address.descriptorType == typeApplicationBundleID { return (nil, address.stringValue) }
        guard let coerced = address.coerce(toDescriptorType: typeKernelProcessID), coerced.data.count == MemoryLayout<pid_t>.size else {
            return (nil, nil)
        }
        let pid = coerced.data.withUnsafeBytes { $0.loadUnaligned(as: pid_t.self) }
        return (pid, NSRunningApplication(processIdentifier: pid)?.bundleIdentifier)
    }

    static func fourCC(_ value: UInt32) -> String {
        String(bytes: [24, 16, 8, 0].map { UInt8((value >> $0) & 0xFF) }, encoding: .macOSRoman) ?? "????"
    }
}
