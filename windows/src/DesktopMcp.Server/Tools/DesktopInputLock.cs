namespace DesktopMcp.Server.Tools;

/// <summary>
/// The one input lock until P4 (P3 Decisions 18): every input tool holds it for its whole call, so two calls never
/// interleave their events. P4 replaces it with the control gate's lock.
/// </summary>
public sealed class DesktopInputLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Waits for the lock with the call's token; disposing the result releases it (once).</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Holder(_gate);
    }

    private sealed class Holder(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
