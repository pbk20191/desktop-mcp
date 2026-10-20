namespace DesktopMcp.Desktop.Dispatching;

/// <summary>
/// The one dispatcher thread the input monitor, the control indicator and the capture flash share. Nothing is started
/// (neither the thread nor the Windows App SDK runtime) until the first <see cref="GetAsync"/>.
/// </summary>
public sealed class DispatcherHost
{
    /// <summary>
    /// The process's shared host, starting a <see cref="DispatcherQueueThread"/>. One-shot: after
    /// <see cref="ShutdownAsync"/> it never starts again, so tests that shut a host down use their own instance.
    /// </summary>
    public static DispatcherHost Shared { get; } = new();

    private readonly Func<IDesktopDispatcher> _start;
    private readonly Lock _gate = new();
    private Task<IDesktopDispatcher>? _starting;
    private Task? _stopping;
    private Action<Exception>? _report;

    /// <summary>A host that starts a <see cref="DispatcherQueueThread"/>.</summary>
    public DispatcherHost() : this(DispatcherQueueThread.Start) { }

    /// <summary>A host whose dispatcher <paramref name="start"/> creates (called on a thread-pool thread).</summary>
    public DispatcherHost(Func<IDesktopDispatcher> start) => _start = start;

    /// <summary>
    /// The sink for the started dispatcher's <see cref="IDesktopDispatcher.UnhandledException"/> (timer ticks, posted
    /// callbacks, dropped posts); the server's composition root logs them. May be set before or after the start; an
    /// exception it throws is swallowed.
    /// </summary>
    public Action<Exception>? Report
    {
        get => Volatile.Read(ref _report);
        set => Volatile.Write(ref _report, value);
    }

    /// <summary>Whether a dispatcher has started and the host has not been shut down.</summary>
    public bool IsStarted
    {
        get { lock (_gate) return _stopping is null && _starting is { IsCompletedSuccessfully: true }; }
    }

    /// <summary>
    /// Returns the shared dispatcher, starting it on a thread-pool thread on first use. Concurrent callers share one start;
    /// a failed start is not kept, so the next call starts again. <paramref name="ct"/> cancels only this caller's wait, never
    /// the shared start. After <see cref="ShutdownAsync"/> the task faults with <see cref="ObjectDisposedException"/>.
    /// </summary>
    public Task<IDesktopDispatcher> GetAsync(CancellationToken ct)
    {
        // An already-cancelled caller starts nothing (neither the thread nor the Windows App SDK runtime).
        if (ct.IsCancellationRequested) return Task.FromCanceled<IDesktopDispatcher>(ct);
        Task<IDesktopDispatcher> starting;
        lock (_gate)
        {
            if (_stopping is not null) return Task.FromException<IDesktopDispatcher>(new ObjectDisposedException(nameof(DispatcherHost)));
            if (_starting is null || _starting.IsFaulted || _starting.IsCanceled) _starting = Task.Run(StartAsync);
            starting = _starting;
        }
        return starting.WaitAsync(ct);
    }

    /// <summary>
    /// Disposes the started dispatcher (a start still in progress is waited for and its dispatcher disposed). Idempotent:
    /// every call returns the same shutdown, and a host that never started has nothing to do.
    /// </summary>
    public ValueTask ShutdownAsync()
    {
        lock (_gate)
        {
            if (_stopping is null)
            {
                var starting = _starting;
                _stopping = starting is null ? Task.CompletedTask : Task.Run(() => StopAsync(starting));
            }
            return new ValueTask(_stopping);
        }
    }

    private async Task<IDesktopDispatcher> StartAsync()
    {
        var dispatcher = _start();
        dispatcher.UnhandledException += ReportUnhandled;
        lock (_gate)
        {
            if (_stopping is null) return dispatcher;
        }
        // Shut down while starting: the late dispatcher is not handed out.
        await dispatcher.DisposeAsync().ConfigureAwait(false);
        throw new ObjectDisposedException(nameof(DispatcherHost));
    }

    private void ReportUnhandled(Exception error)
    {
        if (Report is { } report) TimerTicks.Report(report, error);
    }

    private static async Task StopAsync(Task<IDesktopDispatcher> starting)
    {
        IDesktopDispatcher dispatcher;
        try { dispatcher = await starting.ConfigureAwait(false); }
        catch (Exception) { return; }  // a failed start left nothing; a start that saw the shutdown disposed its own dispatcher
        await dispatcher.DisposeAsync().ConfigureAwait(false);
    }
}
