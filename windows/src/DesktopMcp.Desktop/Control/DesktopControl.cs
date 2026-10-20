using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Control;

/// <summary>
/// Desktop ownership as the server sees it (spec §7; upstream <c>desktop/control.py</c>): the status, gated calls and
/// their checkpoints, and the ledger every AI key or button hold goes through.
/// </summary>
public interface IDesktopControl : IAsyncDisposable
{
    ControlSettings Settings { get; }

    /// <summary>The ledger the desktop input must use, so a takeover, fault or stop releases what the AI holds.</summary>
    InputLedger Ledger { get; }

    /// <summary>Checks the current call (<see cref="ControlCall.Current"/>) before each input step; nothing outside a call.</summary>
    IActionCheckpoint Checkpoint { get; }

    /// <summary>The current status; ticks first when control is enabled (<c>control.py:174-186</c>).</summary>
    ControlStatus Status();

    /// <summary>
    /// Opens a gated call: shows the indicator and arms suppression when it enters <c>ai</c>. Throws
    /// <see cref="ControlRejectedException"/> when the desktop is not the AI's to use.
    /// </summary>
    Task<ControlLease> BeginCallAsync(CancellationToken ct);

    /// <summary>
    /// The rejection a call reports for an interruption (Decisions 17): a <see cref="ControlRejectedException"/> as is;
    /// an <see cref="InputUnavailableException"/> becomes <c>CONTROL_UNAVAILABLE</c> when the status is
    /// <c>unavailable</c>, else <c>CONTROL_PREEMPTED</c>, with the lease's executed steps.
    /// </summary>
    ControlRejectedException Interrupted(Exception interruption, ControlLease? lease);

    Task StartAsync(CancellationToken ct);

    Task StopAsync();

    /// <summary>
    /// Raised off the caller's lock, in generation order, for every state change. Handlers must be fast and must not
    /// block; intermediate states inside one call or beat collapse into the final one (the generation may skip).
    /// </summary>
    event Action<ControlStatus>? StateChanged;
}

/// <summary>One gated call. <see cref="Dispose"/> closes it and ends the call once.</summary>
public sealed class ControlLease(long token, Action<ControlLease> onEnd) : IDisposable
{
    private long _steps;
    private int _closed;

    /// <summary>The state generation the call began in (0 when control is disabled).</summary>
    public long Token { get; } = token;

    public long ExecutedSteps => Interlocked.Read(ref _steps);

    public bool Closed => Volatile.Read(ref _closed) != 0;

    /// <summary>Counts one executed input step (the checkpoint's <c>RecordStep</c>).</summary>
    public void RecordStep() => Interlocked.Increment(ref _steps);

    /// <summary>
    /// Makes this lease <see cref="ControlCall.Current"/> in the caller's execution context (it flows into the call's
    /// awaits and tasks); disposing the scope restores the previous value.
    /// </summary>
    public IDisposable Enter()
    {
        var previous = ControlCall.Current;
        ControlCall.Set(this);
        return new Scope(previous);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) onEnd(this);
    }

    private sealed class Scope(ControlLease? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) ControlCall.Set(previous);
        }
    }
}

/// <summary>The gated call the current execution context belongs to (upstream's call context, Decisions 16).</summary>
public static class ControlCall
{
    private static readonly AsyncLocal<ControlLease?> s_current = new();

    public static ControlLease? Current => s_current.Value;

    internal static void Set(ControlLease? lease) => s_current.Value = lease;
}

public static class ControlErrors
{
    /// <summary>True for the errors that mean the desktop was taken from the call (Decisions 17).</summary>
    public static bool IsInterruption(Exception e) => e is ControlRejectedException or InputUnavailableException;

    internal static ControlRejectedException Interrupted(Exception interruption, ControlLease? lease, Func<ControlStatus> status)
    {
        ArgumentNullException.ThrowIfNull(interruption);
        return interruption switch
        {
            ControlRejectedException rejection => rejection,
            InputUnavailableException => Rejection(status(), lease),
            _ => throw new ArgumentException("Not a control interruption.", nameof(interruption), interruption),
        };
    }

    private static ControlRejectedException Rejection(ControlStatus status, ControlLease? lease) =>
        new(status.State == ControlState.Unavailable ? ControlCodes.ControlUnavailable : ControlCodes.ControlPreempted, status, lease?.ExecutedSteps);
}

/// <summary>
/// Control turned off (<c>--no-control</c>): state <c>disabled</c>, generation 0. Calls get a lease with token 0 whose
/// checkpoints do nothing, the ledger is always enabled and has no physical-key predicate, and start and stop touch
/// nothing (no hook, no window, no Windows App SDK). Its <see cref="Settings"/> always say <c>Enabled = false</c>, whatever
/// it was built with, so whoever reads them takes the disabled path.
/// </summary>
public sealed class DisabledControl(ControlSettings settings) : IDesktopControl
{
    private readonly ControlStatus _status = new(ControlState.Disabled, 0, 0, false, settings.Policy, settings.IdleTimeoutSeconds);

    public ControlSettings Settings { get; } = settings with { Enabled = false };

    public InputLedger Ledger { get; } = new();

    public IActionCheckpoint Checkpoint => NoCheckpoint.Instance;

    public event Action<ControlStatus>? StateChanged
    {
        add { }
        remove { }
    }

    public ControlStatus Status() => _status;

    public Task<ControlLease> BeginCallAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ControlLease(0, static _ => { }));
    }

    public ControlRejectedException Interrupted(Exception interruption, ControlLease? lease) =>
        ControlErrors.Interrupted(interruption, lease, Status);

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
