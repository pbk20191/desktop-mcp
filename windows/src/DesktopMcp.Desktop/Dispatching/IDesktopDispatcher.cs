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
    /// <summary>Starts a repeating timer whose ticks run on the dispatcher thread. Dispose to stop.</summary>
    Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick);
}
