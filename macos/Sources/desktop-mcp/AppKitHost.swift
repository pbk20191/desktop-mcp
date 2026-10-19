import AppKit
import ArgumentParser
import DesktopMCPCore

/// The process entry point: parses the command, runs it as the one root task, and keeps the main
/// thread pumping *at top level* while it runs.
///
/// Why not the async `main()` ArgumentParser offers: that runs everything inside a job on the
/// main dispatch queue, and a run loop nested inside such a job cannot drain that queue again. The
/// Shell tool's osascript mode needs AppKit's event loop on the main thread (StandardAdditions'
/// dialogs are AppKit windows, and once AppKit is up, Apple Events addressed to this process are
/// delivered only through `NSApp.run()`'s event queue), so for `serve` in a window server session
/// the main thread runs `NSApp.run()` as an accessory application (no Dock icon or menu bar);
/// otherwise it runs the plain main run loop. Both drain the main dispatch queue, so MainActor
/// work keeps flowing, in modal panels too. Everything else is structured under the root task.
@main
enum Entry {
    @MainActor
    static func main() {
        let command: ParsableCommand
        do {
            command = try DesktopMCPCommand.parseAsRoot()
        } catch {
            DesktopMCPCommand.exit(withError: error)
        }
        guard let asyncCommand = command as? AsyncParsableCommand else {
            do {
                var command = command
                try command.run()
            } catch {
                DesktopMCPCommand.exit(withError: error)
            }
            return
        }
        let useAppKit = command is Serve && GUISession.isAvailable
        // Parsed commands are plain values; the root task is their only user.
        let parsed = UncheckedSendable(asyncCommand)
        let failure = MainThreadHost.run(appKit: useAppKit) {
            do {
                var command = parsed.value
                try await command.run()
                return nil
            } catch {
                return error
            }
        }
        if let failure { DesktopMCPCommand.exit(withError: failure) }
    }
}

enum MainThreadHost {
    /// Runs `body` as the root task while the main thread pumps; returns what `body` returned
    /// once the pump has been stopped. Called on the main thread at startup, outside any task.
    /// The root task is detached so the command runs off the main actor, as under an async
    /// `main()`; a plain `Task` here would inherit the main actor and pull the whole server onto
    /// the main thread.
    @MainActor
    static func run(appKit: Bool, _ body: @escaping @Sendable () async -> Error?) -> Error? {
        let result = Locked<Error?>(nil)
        if appKit {
            let app = NSApplication.shared
            app.setActivationPolicy(.accessory)
            Task.detached {
                let error = await body()
                result.withLock { $0 = error }
                await MainActor.run {
                    // `stop` takes effect when the loop next handles an event; give it one.
                    app.stop(nil)
                    if let wake = NSEvent.otherEvent(with: .applicationDefined, location: .zero, modifierFlags: [],
                                                     timestamp: 0, windowNumber: 0, context: nil, subtype: 0, data1: 0, data2: 0) {
                        app.postEvent(wake, atStart: true)
                    }
                }
            }
            app.run()
        } else {
            let done = Locked(false)
            Task.detached {
                let error = await body()
                result.withLock { $0 = error }
                done.withLock { $0 = true }
                CFRunLoopStop(CFRunLoopGetMain())
            }
            while !done.withLock({ $0 }) {
                RunLoop.main.run(mode: .default, before: .distantFuture)
            }
        }
        return result.withLock { $0 }
    }
}
