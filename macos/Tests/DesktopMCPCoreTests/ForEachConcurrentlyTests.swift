import Foundation
import DesktopMCPCore
import XCTest

/// The macOS 13 path of `forEachConcurrently` (macOS 14 and later use `DiscardingTaskGroup`),
/// in both overloads: the throwing one for any `AsyncSequence`, the non-throwing one for an
/// `AsyncStream`, which wraps it. Each case runs through both.
final class ForEachConcurrentlyTests: XCTestCase {
    typealias Each = @Sendable (AsyncStream<Int>, @escaping @Sendable (Int) async -> Void) async throws -> Void

    /// The non-throwing overload, and the throwing one reached through an `AsyncSequence` that
    /// is not an `AsyncStream`.
    static let overloads: [(String, Each)] = [
        ("stream", { source, body in await Concurrency.forEachConcurrently(source, discardingGroup: false, body) }),
        ("sequence", { source, body in
            try await Concurrency.forEachConcurrently(source.map { $0 }, discardingGroup: false, body)
        }),
    ]

    func testRunsBodiesConcurrentlyAndEndsWhenTheSourceAndWorkEnd() async throws {
        for (name, each) in Self.overloads {
            let (source, feed) = AsyncStream.makeStream(of: Int.self)
            for i in 1...50 { feed.yield(i) }
            feed.finish()
            let done = Locked<Set<Int>>([])
            let waiting = Locked<[CheckedContinuation<Void, Never>]>([])

            try await each(source) { i in
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
            XCTAssertEqual(done.current, Set(1...50), name)
        }
    }

    func testCancellationCancelsTheWorkAndReturns() async throws {
        for (name, each) in Self.overloads {
            let (source, feed) = AsyncStream.makeStream(of: Int.self)
            let cancelled = Locked(0)
            feed.yield(1); feed.yield(2); feed.yield(3)   // the source itself never ends

            let task = Task {
                try await each(source) { _ in
                    do { try await Task.sleep(for: .seconds(60)) } catch { cancelled.withLock { $0 += 1 } }
                }
            }
            try await Task.sleep(for: .milliseconds(200))
            task.cancel()
            let started = Date()
            do { try await task.value } catch is CancellationError {} catch { XCTFail("\(name): \(error)") }
            XCTAssertLessThan(Date().timeIntervalSince(started), 2, name)
            XCTAssertEqual(cancelled.current, 3, name)
        }
    }

    /// Many bodies that end at once: the relay must reap them all and return when the source
    /// ends, rather than wait on a child that never finishes.
    func testManyQuickBodiesAllRunAndTheLoopReturns() async throws {
        for (name, each) in Self.overloads {
            let (source, feed) = AsyncStream.makeStream(of: Int.self)
            for i in 1...2_000 { feed.yield(i) }
            feed.finish()
            let count = Locked(0)
            let started = Date()
            try await each(source) { _ in count.withLock { $0 += 1 } }
            XCTAssertEqual(count.current, 2_000, name)
            XCTAssertLessThan(Date().timeIntervalSince(started), 5, name)
        }
    }

    func testAFailingSourceCancelsTheWorkAndThrows() async throws {
        struct Boom: Error {}
        let (source, feed) = AsyncThrowingStream.makeStream(of: Int.self)
        let cancelled = Locked(0)
        feed.yield(1); feed.yield(2)
        Task { try? await Task.sleep(for: .milliseconds(200)); feed.finish(throwing: Boom()) }

        do {
            try await Concurrency.forEachConcurrently(source, discardingGroup: false) { _ in
                do { try await Task.sleep(for: .seconds(60)) } catch { cancelled.withLock { $0 += 1 } }
            }
            XCTFail("should throw")
        } catch is Boom {}
        XCTAssertEqual(cancelled.current, 2)
    }
}
