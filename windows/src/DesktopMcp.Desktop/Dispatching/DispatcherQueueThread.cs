using DesktopMcp.Desktop.Errors;
using Microsoft.UI.Dispatching;

namespace DesktopMcp.Desktop.Dispatching;

/// <summary>
/// <see cref="IDesktopDispatcher"/> on a dedicated Windows App SDK (Microsoft.UI.Dispatching) DispatcherQueue thread.
/// The thread is the process's main STA (APTTYPE_MAINSTA) because nothing else in the process is STA first; P2 does not
/// start it (Decisions 21), so no later phase has to share the main STA with it.
/// </summary>
public sealed class DispatcherQueueThread : IDesktopDispatcher
{
    /// <summary>How long <see cref="Start"/> waits for the new thread to run its first work item.</summary>
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

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
    /// (throws <see cref="InvalidOperationException"/> with an install hint when it is missing); a thread that cannot be
    /// created or does not run work within <see cref="StartTimeout"/> gives a <see cref="DesktopException"/>.
    /// </summary>
    public static DispatcherQueueThread Start()
    {
        AppRuntime.EnsureInitialized();
        DispatcherQueueController controller;
        try
        {
            controller = DispatcherQueueController.CreateOnDedicatedThread();
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate("Starting the dispatcher thread", e);
        }
        var threadId = WaitForThreadId(
            work => controller.DispatcherQueue.TryEnqueue(() => work()),
            () => controller.ShutdownQueueAsync().AsTask(),
            StartTimeout);
        return new DispatcherQueueThread(controller, threadId);
    }

    /// <summary>
    /// Runs one work item on the new thread to learn its id. When the queue rejects it or it does not run in time, the
    /// queue is shut down (so the controller and its thread are not leaked) and a <see cref="DesktopException"/> is thrown.
    /// </summary>
    internal static int WaitForThreadId(Func<Action, bool> tryEnqueue, Func<Task> shutdown, TimeSpan timeout)
    {
        var threadId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? failure = null;
        if (!tryEnqueue(() => threadId.TrySetResult(Environment.CurrentManagedThreadId)))
            failure = "The new DispatcherQueue rejected work.";
        else if (!threadId.Task.Wait(timeout))
            failure = $"The dispatcher thread did not start within {timeout.TotalSeconds:0.#} s.";
        if (failure is null) return threadId.Task.Result;
        // Best effort: the queue may already be unusable, and the shutdown (which can fail synchronously or as a faulted
        // task) must not hide the original failure.
        try { shutdown().Wait(timeout); }
        catch (Exception) { }
        throw new DesktopException(failure);
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
        // The IAsyncAction completes on the dying dispatcher thread, and an await (with ConfigureAwait(false) or ForceYielding,
        // which only yields for an already-completed task) would resume inline there and run the caller's continuation on it.
        // A continuation on the default scheduler always runs on the thread pool; it rethrows the shutdown's exception.
        await _controller.ShutdownQueueAsync().AsTask().ContinueWith(
            static shutdown => shutdown.GetAwaiter().GetResult(), CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).ConfigureAwait(false);
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
