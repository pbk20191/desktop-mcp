import Darwin
import Foundation

/// Runs child processes without blocking a thread while they execute.
public enum Shell {
    public static let maxTimeoutEnv = "DESKTOP_MCP_MAX_TIMEOUT"
    static let defaultMaxTimeout = 240

    public struct Result: Equatable, Sendable {
        public var output: String
        public var status: Int
    }

    /// Caps the timeout below the MCP client's own wall-clock ceiling, so the server always
    /// answers before the client gives up on the call.
    static func clampTimeout(_ timeout: Int) -> Int {
        let cap = ProcessInfo.processInfo.environment[maxTimeoutEnv].flatMap(Int.init) ?? defaultMaxTimeout
        return cap > 0 ? min(timeout, cap) : timeout
    }

    /// The Shell tool: `/bin/bash -c` or `osascript -e`, stdout and stderr combined.
    public static func execute(_ command: String, mode: String, timeout requested: Int) async -> Result {
        let argv = mode == "osascript" ? ["/usr/bin/osascript", "-e", command] : ["/bin/bash", "-c", command]
        let timeout = clampTimeout(requested)
        do {
            let run = try await self.run(argv, timeout: Double(timeout))
            let combined = combine(run.stdout, run.stderr)
            if run.timedOut {
                var message = "Command timed out after \(timeout) seconds"
                if requested > timeout { message += " (requested \(requested)s, capped by \(maxTimeoutEnv))" }
                if !combined.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    message += "\n--- partial output before timeout ---\n"
                        + combined.replacingOccurrences(of: "\\n+$", with: "", options: .regularExpression)
                }
                return Result(output: message, status: -1)
            }
            return Result(output: combined.trimmingCharacters(in: .whitespacesAndNewlines), status: Int(run.status))
        } catch is CancellationError {
            return Result(output: "Command cancelled", status: -1)
        } catch {
            return Result(output: "\(error)", status: -1)
        }
    }

    /// Both streams: `stdout or stderr` would hide errors behind partial success output.
    static func combine(_ stdout: String, _ stderr: String) -> String {
        if !stdout.isEmpty && !stderr.isEmpty {
            return stdout.replacingOccurrences(of: "\n+$", with: "", options: .regularExpression) + "\n" + stderr
        }
        return stdout.isEmpty ? stderr : stdout
    }

    public struct Run: Sendable {
        public var stdout: String
        public var stderr: String
        /// Exit code, or the negated signal number when killed by a signal.
        public var status: Int32
        public var timedOut: Bool
    }

    struct SpawnError: Error, CustomStringConvertible {
        let description: String
    }

    /// Runs `argv` with stdin from /dev/null and output captured to temporary files.
    ///
    /// - stdin is /dev/null: under stdio transport fd 0 is the JSON-RPC stream, and a child that
    ///   reads stdin (sudo, ssh, a bare `cat`) would swallow the client's messages.
    /// - Output goes to files, not pipes: a backgrounded grandchild (`cmd &`) keeps a pipe open
    ///   after the direct child exits, which would make the call hang until the timeout.
    /// - The child gets its own session, so a timeout or cancellation kills the whole tree.
    public static func run(_ argv: [String], timeout: Double) async throws -> Run {
        let out = try TempFile()
        let err = try TempFile()
        let pid = try spawn(argv, stdout: out.fd, stderr: err.fd)

        enum Outcome { case exited(Int32), timedOut }
        let outcome: [Outcome] = await withTaskCancellationHandler {
            await withTaskGroup(of: Outcome?.self) { group in
                group.addTask { .exited(await waitForExit(pid)) }
                group.addTask {
                    do {
                        try await Task.sleep(for: .seconds(max(0, timeout)))
                    } catch {
                        return nil  // The process exited first and this sleep was cancelled.
                    }
                    killpg(pid, SIGKILL)
                    return .timedOut
                }
                var results: [Outcome] = []
                if let first = await group.next() ?? nil {
                    results.append(first)
                    if case .exited = first { group.cancelAll() }
                }
                for await result in group {
                    if let result { results.append(result) }
                }
                return results
            }
        } onCancel: {
            killpg(pid, SIGKILL)
        }
        try Task.checkCancellation()

        var status: Int32 = -1
        var timedOut = false
        for result in outcome {
            switch result {
            case .exited(let s): status = s
            case .timedOut: timedOut = true
            }
        }
        return Run(stdout: out.read(), stderr: err.read(), status: status, timedOut: timedOut)
    }

    static func spawn(_ argv: [String], stdout: Int32, stderr: Int32) throws -> pid_t {
        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        posix_spawn_file_actions_addopen(&actions, 0, "/dev/null", O_RDONLY, 0)
        posix_spawn_file_actions_adddup2(&actions, stdout, 1)
        posix_spawn_file_actions_adddup2(&actions, stderr, 2)

        var attributes: posix_spawnattr_t?
        posix_spawnattr_init(&attributes)
        defer { posix_spawnattr_destroy(&attributes) }
        // New session (so killpg reaches grandchildren) and close every descriptor not named
        // above, so the child cannot hold our stdout -- the protocol stream -- open. Signal
        // dispositions go back to default: this process ignores SIGINT/SIGTERM/SIGPIPE, and an
        // ignored disposition would otherwise survive exec into every command.
        // The signal mask is reset too: the spawning thread may have signals blocked, and a
        // child inherits that mask the same way.
        var allSignals = sigset_t()
        sigfillset(&allSignals)
        posix_spawnattr_setsigdefault(&attributes, &allSignals)
        var noSignals = sigset_t()
        sigemptyset(&noSignals)
        posix_spawnattr_setsigmask(&attributes, &noSignals)
        posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETSID | POSIX_SPAWN_CLOEXEC_DEFAULT
                                                    | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK))

        var pid: pid_t = 0
        let cArgs = argv.map { strdup($0) } + [nil]
        defer { cArgs.forEach { free($0) } }
        let rc = posix_spawn(&pid, argv[0], &actions, &attributes, cArgs, environ)
        guard rc == 0 else {
            throw SpawnError(description: "Failed to start \(argv[0]): \(String(cString: strerror(rc)))")
        }
        return pid
    }

    /// Resumes when the process exits, without parking a thread on waitpid.
    static func waitForExit(_ pid: pid_t) async -> Int32 {
        await withCheckedContinuation { continuation in
            let reaper = Reaper(pid: pid, continuation: continuation)
            let source = DispatchSource.makeProcessSource(identifier: pid, eventMask: .exit, queue: blockingQueue)
            reaper.source = source
            source.setEventHandler { reaper.reap() }
            source.resume()
            // The child may have exited before the source was armed.
            blockingQueue.async { reaper.reap() }
        }
    }

    /// Shared between the dispatch source handler and the arming block. Whichever of the two
    /// observes the exit first resumes the continuation; the other finds it already taken.
    private final class Reaper: Sendable {
        struct Pending {
            var continuation: CheckedContinuation<Int32, Never>
            var source: DispatchSourceProcess?
        }

        let pid: pid_t
        private let pending: Locked<Pending?>

        init(pid: pid_t, continuation: CheckedContinuation<Int32, Never>) {
            self.pid = pid
            self.pending = Locked(Pending(continuation: continuation))
        }

        var source: DispatchSourceProcess? {
            get { pending.withLock { $0?.source } }
            set { pending.withLock { $0?.source = newValue } }
        }

        func reap() {
            let taken: (Pending, Int32)? = pending.withLock { pending in
                guard let current = pending else { return nil }
                var status: Int32 = 0
                let result = waitpid(pid, &status, WNOHANG)
                guard result == pid || (result < 0 && errno == ECHILD) else { return nil }
                pending = nil
                return (current, result == pid ? Shell.decode(status) : -1)
            }
            guard let (current, exitCode) = taken else { return }
            current.source?.cancel()
            current.continuation.resume(returning: exitCode)
        }
    }

    /// Exit code, or the negated signal number (Python's `returncode` convention).
    static func decode(_ status: Int32) -> Int32 {
        let signal = status & 0x7f
        return signal == 0 ? (status >> 8) & 0xff : -signal
    }

    /// An unlinked temporary file: it lives exactly as long as its descriptor.
    final class TempFile {
        let fd: Int32

        init() throws {
            var template = Array((NSTemporaryDirectory() + "desktop-mcp.XXXXXX").utf8CString)
            fd = mkstemp(&template)
            guard fd >= 0 else { throw SpawnError(description: "Could not create a temporary file") }
            unlink(template.withUnsafeBufferPointer { String(cString: $0.baseAddress!) })
        }

        deinit { close(fd) }

        /// At most `maxOutputBytes`; a command that floods its output cannot exhaust memory.
        static let maxOutputBytes = 10 * 1024 * 1024

        func read() -> String {
            lseek(fd, 0, SEEK_SET)
            var data = Data()
            var buffer = [UInt8](repeating: 0, count: 65536)
            while data.count < Self.maxOutputBytes {
                let n = Darwin.read(fd, &buffer, min(buffer.count, Self.maxOutputBytes - data.count))
                if n < 0 && errno == EINTR { continue }
                if n <= 0 { break }
                data.append(buffer, count: n)
            }
            var text = String(decoding: data, as: UTF8.self)
            if data.count >= Self.maxOutputBytes { text += "\n...[output truncated at \(Self.maxOutputBytes) bytes]" }
            return text
        }
    }
}
