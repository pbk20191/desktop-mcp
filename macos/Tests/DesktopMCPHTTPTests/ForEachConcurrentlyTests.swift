import Foundation
import DesktopMCPCore
@testable import DesktopMCPHTTP
import XCTest

/// The macOS 13 path of `forEachConcurrently` (macOS 14 and later use `DiscardingTaskGroup`).
final class ForEachConcurrentlyTests: XCTestCase {
    func testRunsBodiesConcurrentlyAndEndsWhenTheSourceAndWorkEnd() async throws {
        let (source, feed) = AsyncStream.makeStream(of: Int.self)
        for i in 1...50 { feed.yield(i) }
        feed.finish()
        let done = Locked<Set<Int>>([])
        let waiting = Locked<[CheckedContinuation<Void, Never>]>([])

        try await NIOHTTPServer.forEachConcurrently(source, discardingGroup: false) { i in
            // Every body waits until all 50 are running at once: they overlap, or this never ends.
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                let ready: [CheckedContinuation<Void, Never>] = waiting.withLock { list in
                    list.append(continuation)
                    guard list.count == 50 else { return [] }
                    defer { list.removeAll() }
                    return list
                }
                for c in ready { c.resume() }
            }
            _ = done.withLock { $0.insert(i) }
        }
        XCTAssertEqual(done.current, Set(1...50))
    }

    func testCancellationCancelsTheWorkAndReturns() async throws {
        let (source, feed) = AsyncStream.makeStream(of: Int.self)
        let cancelled = Locked(0)
        feed.yield(1); feed.yield(2); feed.yield(3)   // the source itself never ends

        let task = Task {
            try await NIOHTTPServer.forEachConcurrently(source, discardingGroup: false) { _ in
                do { try await Task.sleep(for: .seconds(60)) } catch { cancelled.withLock { $0 += 1 } }
            }
        }
        try await Task.sleep(for: .milliseconds(200))
        task.cancel()
        let started = Date()
        do { try await task.value } catch is CancellationError {} catch { XCTFail("\(error)") }
        XCTAssertLessThan(Date().timeIntervalSince(started), 2)
        XCTAssertEqual(cancelled.current, 3)
    }

    func testAFailingSourceCancelsTheWorkAndThrows() async throws {
        struct Boom: Error {}
        let (source, feed) = AsyncThrowingStream.makeStream(of: Int.self)
        let cancelled = Locked(0)
        feed.yield(1); feed.yield(2)
        Task { try? await Task.sleep(for: .milliseconds(200)); feed.finish(throwing: Boom()) }

        do {
            try await NIOHTTPServer.forEachConcurrently(source, discardingGroup: false) { _ in
                do { try await Task.sleep(for: .seconds(60)) } catch { cancelled.withLock { $0 += 1 } }
            }
            XCTFail("should throw")
        } catch is Boom {}
        XCTAssertEqual(cancelled.current, 2)
    }
}
