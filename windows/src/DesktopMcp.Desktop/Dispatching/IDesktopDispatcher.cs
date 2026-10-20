namespace DesktopMcp.Desktop.Dispatching;

/// <summary>
/// The one dedicated thread with a message loop (hooks, Raw Input, overlay windows, timers).
/// Work posted here must be short and must never block: a slow low-level hook callback delays
/// all input on the machine. UIA, capture, file, process and network work never run here.
/// </summary>
public interface IDesktopDispatcher : IAsyncDisposable
{
    bool HasThreadAccess { get; }
    int ManagedThreadId { get; }
    Task InvokeAsync(Action action);
    Task<T> InvokeAsync<T>(Func<T> func);
    /// <summary>
    /// Runs <paramref name="func"/> on the dispatcher thread with the dispatcher's <see cref="SynchronizationContext"/>, so
    /// every <c>await</c> inside it resumes on the dispatcher. The returned task completes when <paramref name="func"/>'s does.
    /// </summary>
    Task InvokeAsync(Func<Task> func);
    /// <summary>
    /// Starts a repeating timer whose ticks run on the dispatcher thread. Dispose to stop. An exception thrown by a tick is
    /// raised through <see cref="UnhandledException"/> and the timer keeps running.
    /// </summary>
    Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick);
    /// <summary>Raised on the dispatcher thread for an exception thrown by a timer tick.</summary>
    event Action<Exception>? UnhandledException;
}
