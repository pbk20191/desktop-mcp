/// Structured-concurrency helpers shared by the HTTP server and the stdio bridge.
public enum Concurrency {
    /// Runs `body` for every element of `source` as a child task of the caller, without waiting
    /// for one element's work before taking the next, and without keeping finished children
    /// around. Ends when `source` ends and all work is done; cancellation cancels the work.
    ///
    /// macOS 14 has `DiscardingTaskGroup` for this. A plain task group keeps a finished child
    /// until `next()` is called, and the loop that reads `source` cannot also sit in `next()`,
    /// so on macOS 13 reading `source` is done by children as well: one child owns the (non
    /// Sendable) iterator and relays elements, one child at a time takes the next element, and
    /// the body only ever awaits `next()`, which reaps whatever finished.
    ///
    /// `discardingGroup: false` forces the macOS 13 path, so tests can cover it on any system.
    public static func forEachConcurrently<Source: AsyncSequence & Sendable>(
        _ source: Source, discardingGroup: Bool = true, _ body: @escaping @Sendable (Source.Element) async -> Void
    ) async throws where Source.Element: Sendable {
        try await each(source, discardingGroup: discardingGroup, body)
    }

    /// The implementation behind both overloads, under its own name so the `AsyncStream`
    /// overload can reach it without resolving back to itself.
    private static func each<Source: AsyncSequence & Sendable>(
        _ source: Source, discardingGroup: Bool, _ body: @escaping @Sendable (Source.Element) async -> Void
    ) async throws where Source.Element: Sendable {
        if discardingGroup, #available(macOS 14.0, *) {
            try await withThrowingDiscardingTaskGroup { group in
                for try await element in source {
                    group.addTask { await body(element) }
                }
            }
            return
        }
        try await withThrowingTaskGroup(of: ServeEvent<Source.Element>.self) { group in
            let (elements, relay) = AsyncStream.makeStream(of: Source.Element.self)
            group.addTask {
                defer { relay.finish() }
                for try await element in source { relay.yield(element) }
                return .handled
            }
            group.addTask { var next = elements.makeAsyncIterator(); return .took(await next.next()) }
            while let event = try await group.next() {
                switch event {
                case .took(let element?):
                    group.addTask { await body(element); return .handled }
                    group.addTask { var next = elements.makeAsyncIterator(); return .took(await next.next()) }
                case .took(nil), .handled:
                    break
                }
            }
        }
    }

    /// The same for an `AsyncStream`, which cannot fail, so neither can this: the caller needs
    /// no `try`.
    public static func forEachConcurrently<Element: Sendable>(
        _ source: AsyncStream<Element>, discardingGroup: Bool = true, _ body: @escaping @Sendable (Element) async -> Void
    ) async {
        do {
            try await each(source, discardingGroup: discardingGroup, body)
        } catch {
            // Unreachable: only the source can throw (bodies cannot), and an AsyncStream does not.
            log("forEachConcurrently: unexpected error \(error)")
        }
    }
}

/// What a child of `Concurrency.forEachConcurrently` reports back on macOS 13.
enum ServeEvent<Element: Sendable>: Sendable {
    /// The next element, or nil when the source has ended.
    case took(Element?)
    /// Work for one element finished (or the relay ended).
    case handled
}
