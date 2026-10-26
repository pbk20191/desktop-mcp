namespace DesktopMcp.Desktop.Dispatching;

/// <summary>
/// The <see cref="SynchronizationContext"/> of the dispatcher thread: an <c>await</c> inside work on the dispatcher resumes
/// there. <see cref="Post"/> enqueues on the dispatcher queue; it never throws, because the runtime posts await
/// continuations and would rethrow a thrown exception on the thread pool, killing the process. Work the queue rejects (it
/// has shut down) is dropped and reported, so an <c>InvokeAsync</c> still awaiting past the dispatcher's shutdown never
/// completes. A posted callback that throws is reported too, never thrown into the queue. <see cref="Send"/> runs inline on
/// the dispatcher thread and is not supported from any other thread (blocking another thread on the dispatcher is never
/// wanted).
/// </summary>
public sealed class DispatcherQueueSynchronizationContext : SynchronizationContext
{
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Func<bool> _hasThreadAccess;
    private readonly Action<Exception> _report;

    /// <param name="report">Receives dropped work (on the posting thread) and exceptions thrown by posted callbacks (on the
    /// dispatcher thread); an exception it throws is swallowed.</param>
    internal DispatcherQueueSynchronizationContext(Func<Action, bool> tryEnqueue, Func<bool> hasThreadAccess, Action<Exception>? report = null)
    {
        _tryEnqueue = tryEnqueue;
        _hasThreadAccess = hasThreadAccess;
        _report = report ?? (static _ => { });
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        Exception? dropped;
        try
        {
            dropped = _tryEnqueue(() => TimerTicks.Guard(() => d(state), _report))
                ? null
                : new ObjectDisposedException(nameof(DispatcherQueueSynchronizationContext), "The dispatcher queue has shut down; posted work was dropped.");
        }
        catch (Exception e)
        {
            dropped = e;
        }
        if (dropped is not null) TimerTicks.Report(_report, dropped);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (!_hasThreadAccess())
            throw new NotSupportedException("Send is supported only on the dispatcher thread; use Post.");
        d(state);
    }

    public override SynchronizationContext CreateCopy() => this;
}

/// <summary>Runs dispatcher timer ticks (and posted callbacks) so that an exception never escapes into the dispatcher queue.</summary>
internal static class TimerTicks
{
    /// <summary>Runs <paramref name="tick"/>; an exception is passed to <paramref name="report"/>, and an exception thrown by
    /// <paramref name="report"/> is swallowed.</summary>
    public static void Guard(Action tick, Action<Exception> report)
    {
        try { tick(); }
        catch (Exception e)
        {
            Report(report, e);
        }
    }

    /// <summary>Passes <paramref name="error"/> to <paramref name="report"/>, swallowing an exception it throws.</summary>
    public static void Report(Action<Exception> report, Exception error)
    {
        try { report(error); }
        catch (Exception) { }
    }
}
