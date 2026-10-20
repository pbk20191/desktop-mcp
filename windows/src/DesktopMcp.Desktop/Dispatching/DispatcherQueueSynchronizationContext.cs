namespace DesktopMcp.Desktop.Dispatching;

/// <summary>
/// The <see cref="SynchronizationContext"/> of the dispatcher thread: an <c>await</c> inside work on the dispatcher resumes
/// there. <see cref="Post"/> enqueues on the dispatcher queue and throws <see cref="ObjectDisposedException"/> when the
/// queue rejects the work (it has shut down); <see cref="Send"/> runs inline on the dispatcher thread and is not supported
/// from any other thread (blocking another thread on the dispatcher is never wanted).
/// </summary>
public sealed class DispatcherQueueSynchronizationContext : SynchronizationContext
{
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Func<bool> _hasThreadAccess;

    internal DispatcherQueueSynchronizationContext(Func<Action, bool> tryEnqueue, Func<bool> hasThreadAccess)
    {
        _tryEnqueue = tryEnqueue;
        _hasThreadAccess = hasThreadAccess;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (!_tryEnqueue(() => d(state)))
            throw new ObjectDisposedException(nameof(DispatcherQueueSynchronizationContext), "The dispatcher queue has shut down.");
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

/// <summary>Runs dispatcher timer ticks so that an exception never escapes into the dispatcher queue.</summary>
internal static class TimerTicks
{
    /// <summary>Runs <paramref name="tick"/>; an exception is passed to <paramref name="report"/>, and an exception thrown by
    /// <paramref name="report"/> is swallowed.</summary>
    public static void Guard(Action tick, Action<Exception> report)
    {
        try { tick(); }
        catch (Exception e)
        {
            try { report(e); }
            catch (Exception) { }
        }
    }
}
