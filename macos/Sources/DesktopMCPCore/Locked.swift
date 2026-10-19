import Foundation

/// A value guarded by a lock, usable from any isolation domain. For small caches and flags
/// where an actor would force every reader to suspend.
public final class Locked<Value>: @unchecked Sendable {
    private var value: Value
    private let lock = NSLock()

    public init(_ value: Value) { self.value = value }

    public func withLock<T>(_ body: (inout Value) throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body(&value)
    }

    public var current: Value {
        get { withLock { $0 } }
        set { withLock { $0 = newValue } }
    }
}

/// Carries a value the compiler cannot prove `Sendable` across a task boundary. Only for values
/// documented as safe to use from any thread; the comment at the use site must say why.
public struct UncheckedSendable<Value>: @unchecked Sendable {
    public let value: Value
    public init(_ value: Value) { self.value = value }
}
