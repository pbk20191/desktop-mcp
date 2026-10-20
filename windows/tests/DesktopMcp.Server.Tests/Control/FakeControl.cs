using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Server.Tests;

/// <summary>
/// Desktop control for the always-run tests: a settable status, leases counted when they end, a recording checkpoint and
/// a real ledger. Nothing here installs a hook, shows a window or injects input.
/// </summary>
internal sealed class FakeControl : IDesktopControl
{
    private readonly Lock _lock = new();
    private readonly List<string> _events = [];
    private int _endedCalls;
    private int _begunCalls;

    public FakeControl()
    {
        Checkpoint = new RecordingCheckpoint(this);
    }

    /// <summary>What <see cref="Status"/> returns; <see cref="Settings"/>' <c>Enabled</c> and policy follow it.</summary>
    public ControlStatus Current { get; set; } = new(ControlState.Ready, 1, 0, true, "relaxed", 10);

    /// <summary>When set, <see cref="BeginCallAsync"/> throws it.</summary>
    public ControlRejectedException? BeginRejection { get; set; }

    /// <summary>When set, the checkpoint throws it (and counts nothing).</summary>
    public Exception? CheckpointThrows { get; set; }

    public ControlSettings Settings => new(Current.Enabled, Current.Policy, Current.IdleTimeoutSeconds, 500, 500);

    public InputLedger Ledger { get; } = new();

    public IActionCheckpoint Checkpoint { get; }

    public int BegunCalls => Volatile.Read(ref _begunCalls);

    public int EndedCalls => Volatile.Read(ref _endedCalls);

    public int Checkpoints => Volatile.Read(ref _checkpoints);

    private int _checkpoints;

    /// <summary>The lease <see cref="ControlCall.Current"/> named at each checkpoint (null outside a call).</summary>
    public IReadOnlyList<ControlLease?> CheckpointLeases
    {
        get
        {
            lock (_lock) return [.. _checkpointLeases];
        }
    }

    private readonly List<ControlLease?> _checkpointLeases = [];

    /// <summary><c>start</c> and <c>stop</c> in call order, and whatever else the tests record through <see cref="Record"/>.</summary>
    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_lock) return [.. _events];
        }
    }

    public void Record(string text)
    {
        lock (_lock) _events.Add(text);
    }

    public event Action<ControlStatus>? StateChanged;

    public void RaiseStateChanged(ControlStatus status)
    {
        Current = status;
        StateChanged?.Invoke(status);
    }

    public ControlStatus Status() => Current;

    public Task<ControlLease> BeginCallAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (BeginRejection is { } rejection) throw rejection;
        Interlocked.Increment(ref _begunCalls);
        return Task.FromResult(new ControlLease(Current.Generation, _ => Interlocked.Increment(ref _endedCalls)));
    }

    public ControlRejectedException Interrupted(Exception interruption, ControlLease? lease) => interruption switch
    {
        ControlRejectedException rejection => rejection,
        _ => new ControlRejectedException(ControlCodes.ControlPreempted, Current, lease?.ExecutedSteps),
    };

    public Task StartAsync(CancellationToken ct)
    {
        Record("start");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Record("stop");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class RecordingCheckpoint(FakeControl owner) : IActionCheckpoint
    {
        public void Checkpoint()
        {
            Interlocked.Increment(ref owner._checkpoints);
            lock (owner._lock) owner._checkpointLeases.Add(ControlCall.Current);
            if (owner.CheckpointThrows is { } error) throw error;
        }

        public void RecordStep() => ControlCall.Current?.RecordStep();
    }
}
