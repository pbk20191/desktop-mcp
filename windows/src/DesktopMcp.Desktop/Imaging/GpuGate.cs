using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Imaging;

/// <summary>
/// One slot for everything that uses the shared Direct3D device (Win2D rendering and screen capture). A call waits for a live
/// holder; while a holder that passed its deadline is still running (<see cref="GpuLease.AbandonUntil"/>) a new call fails
/// at once instead of piling up behind a wedged device lock.
/// </summary>
internal sealed class GpuGate
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private volatile Task? _abandoned;

    /// <summary>The gate rendering and capture share in the server process.</summary>
    public static GpuGate Shared { get; } = new();

    public async Task<GpuLease> EnterAsync(string operation, TimeSpan wait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_abandoned is { IsCompleted: false })
            throw new DesktopException($"{operation} failed: an earlier graphics call is still running after its deadline; try again later.");
        if (!await _slot.WaitAsync(wait, ct).ConfigureAwait(false))
            throw new DesktopException($"{operation} failed: the graphics device stayed busy for {wait.TotalSeconds:0.###} s.");
        return new GpuLease(this);
    }

    internal void Abandon(Task stillRunning)
    {
        _abandoned = stillRunning;
        // The continuation observes a failure too, so the abandoned work's exception is not left unobserved.
        stillRunning.ContinueWith(static (task, state) =>
        {
            _ = task.Exception;
            ((SemaphoreSlim)state!).Release();
        }, _slot, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void Free() => _slot.Release();
}

/// <summary>A held <see cref="GpuGate"/> slot. <see cref="Dispose"/> frees it, unless <see cref="AbandonUntil"/> was called.</summary>
internal sealed class GpuLease : IDisposable
{
    private readonly GpuGate _gate;
    private int _done;

    internal GpuLease(GpuGate gate) => _gate = gate;

    /// <summary>The call passed its deadline but its work still runs: the slot is freed when <paramref name="stillRunning"/> completes.</summary>
    public void AbandonUntil(Task stillRunning)
    {
        ArgumentNullException.ThrowIfNull(stillRunning);
        if (Interlocked.Exchange(ref _done, 1) == 0) _gate.Abandon(stillRunning);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _done, 1) == 0) _gate.Free();
    }
}
