using Microsoft.UI.Dispatching;

namespace DesktopMcp.Desktop.Dispatching;

/// <summary><see cref="IDesktopDispatcher"/> on a dedicated Windows App SDK (Microsoft.UI.Dispatching) DispatcherQueue thread.</summary>
public sealed class DispatcherQueueThread : IDesktopDispatcher
{
    private readonly DispatcherQueueController _controller;
    private readonly DispatcherQueue _queue;
    private int _disposed;

    private DispatcherQueueThread(DispatcherQueueController controller, int threadId)
    {
        _controller = controller;
        _queue = controller.DispatcherQueue;
        ManagedThreadId = threadId;
    }

    /// <summary>
    /// Starts a dedicated Windows App SDK DispatcherQueue thread. Initialises the Windows App SDK runtime first
    /// (throws <see cref="InvalidOperationException"/> with an install hint when it is missing).
    /// </summary>
    public static DispatcherQueueThread Start()
    {
        AppRuntime.EnsureInitialized();
        var controller = DispatcherQueueController.CreateOnDedicatedThread();

        var threadId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!controller.DispatcherQueue.TryEnqueue(() => threadId.SetResult(Environment.CurrentManagedThreadId)))
            throw new InvalidOperationException("The new DispatcherQueue rejected work.");
        return new DispatcherQueueThread(controller, threadId.Task.GetAwaiter().GetResult());
    }

    public int ManagedThreadId { get; }

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _queue.TryEnqueue(() =>
        {
            try { completion.SetResult(func()); }
            catch (Exception e) { completion.SetException(e); }
        });
        if (!queued) throw new ObjectDisposedException(nameof(DispatcherQueueThread));
        return completion.Task;
    }

    public Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick) => InvokeAsync<IDisposable>(() =>
    {
        var timer = _queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) => tick();
        timer.Start();
        return new TimerHandle(this, timer);
    });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _controller.ShutdownQueueAsync();
    }

    private sealed class TimerHandle(DispatcherQueueThread owner, DispatcherQueueTimer timer) : IDisposable
    {
        public void Dispose()
        {
            if (owner.HasThreadAccess) { timer.Stop(); return; }
            if (Volatile.Read(ref owner._disposed) == 0) owner._queue.TryEnqueue(() => timer.Stop());  // a lambda: CsWinRT would QI the timer RCW for a bare timer.Stop method group
        }
    }
}
