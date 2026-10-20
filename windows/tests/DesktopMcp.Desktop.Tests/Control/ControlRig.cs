using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop.Overlay;

namespace DesktopMcp.Desktop.Tests.Control;

/// <summary>
/// A coordinator over fakes and one manual clock (preflight F2): the heartbeat loop is off and the tests drive each
/// iteration with <see cref="ControlCoordinator.RunOnceAsync"/>. The filter is real; nothing touches the desktop.
/// </summary>
internal sealed class ControlRig
{
    public static readonly ControlSettings Settings = new(true, "relaxed", 10, 500, 500);

    private ControlRig(int monitorStartFailures)
    {
        Monitor = new FakeMonitor(Time, monitorStartFailures, () => Control!.Ledger.BlockNew());
        Control = new ControlCoordinator(Settings, Monitor, NewIndicator, () => Interactive, Time,
            (level, message, _) => { lock (Logs) Logs.Add((level, message)); }) { LoopEnabled = false };
    }

    public ManualTime Time { get; } = new();

    public FakeMonitor Monitor { get; }

    public ControlCoordinator Control { get; }

    /// <summary>The indicator the coordinator uses now (recovery may create a new one).</summary>
    public FakeIndicator Indicator { get; private set; } = null!;

    public List<FakeIndicator> Indicators { get; } = [];

    public bool Interactive { get; set; } = true;

    public List<string> Ups { get; } = [];

    public List<(DesktopLogLevel Level, string Message)> Logs { get; } = [];

    /// <summary>
    /// A chord the hook saw while suppressing: suppression is forced on for it (as if armed), then Ctrl+Shift+Alt+Backspace
    /// go down. The keys stay down.
    /// </summary>
    public void Chord()
    {
        var filter = Monitor.Filter;
        if (!filter.Suppressing) filter.SetSuppression(true, Stamp() + Seconds(10));
        foreach (var vk in new[] { 0xA2, 0xA0, 0xA4, 0x08 }) Assert.Equal(HookVerdict.Swallow, Key(vk, true));
        Assert.True(filter.TakeoverPending);
    }

    public static ControlRig Create(int monitorStartFailures = 0) => new(monitorStartFailures);

    public static async Task<ControlRig> StartedAsync()
    {
        var rig = Create();
        await rig.Control.StartAsync(default);
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);
        return rig;
    }

    public long Stamp() => Time.GetTimestamp();

    public long Seconds(double seconds) => (long)Math.Round(seconds * Time.TimestampFrequency);

    public void Advance(double seconds) => Time.Now += TimeSpan.FromSeconds(seconds);

    /// <summary>
    /// A physical key down that reached the system: <c>OnKey</c> and <c>AfterKeyForwarded</c> with result 0 while
    /// suppression was briefly off (then restored).
    /// </summary>
    public void PhysicalDownDelivered(int vk)
    {
        var filter = Monitor.Filter;
        var (on, deadline) = (filter.Suppressing, filter.Deadline);
        filter.SetSuppression(false, 0);
        Assert.Equal(HookVerdict.Pass, filter.OnKey((uint)vk, true, 0, 0));
        filter.AfterKeyForwarded((uint)vk, true, 0);
        filter.SetSuppression(on, deadline);
    }

    /// <summary>A physical key event as the hook sees it (forwarded with result 0 when passed).</summary>
    public HookVerdict Key(int vk, bool down)
    {
        var verdict = Monitor.Filter.OnKey((uint)vk, down, 0, 0);
        if (verdict == HookVerdict.Pass) Monitor.Filter.AfterKeyForwarded((uint)vk, down, 0);
        return verdict;
    }

    /// <summary>Relative motion from a real raw mouse device.</summary>
    public void RawMove(int dx, int dy) => Monitor.Filter.OnRawMouse(1, 0, dx, dy);

    private FakeIndicator NewIndicator()
    {
        Indicator = new FakeIndicator();
        Indicators.Add(Indicator);
        return Indicator;
    }
}

internal sealed class FakeMonitor : IInputMonitor
{
    private readonly ManualTime _time;
    private int _startFailures;
    private long? _lastBeat;
    private TaskCompletionSource? _holdStart;
    private readonly TaskCompletionSource _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeMonitor(ManualTime time, int startFailures, Action blockLedger)
    {
        _time = time;
        _startFailures = startFailures;
        Filter = new PhysicalInputFilter(time.GetTimestamp, time.TimestampFrequency, ForeignInjectedPolicy.Physical, blockLedger);
    }

    public PhysicalInputFilter Filter { get; }

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public int Rehooks { get; private set; }

    /// <summary>Follows the clock unless a test sets it.</summary>
    public long LastBeat
    {
        get => _lastBeat ?? _time.GetTimestamp();
        set => _lastBeat = value;
    }

    public long LastRehook { get; set; }

    public bool StopThrows { get; set; }

    public Task StartEntered => _startEntered.Task;

    /// <summary>The next starts wait for <see cref="ReleaseStart"/> (or their token).</summary>
    public void HoldStart() => _holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseStart()
    {
        var hold = _holdStart;
        _holdStart = null;
        hold?.TrySetResult();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        Starts++;
        _startEntered.TrySetResult();
        if (_holdStart is { } hold) await hold.Task.WaitAsync(ct);
        if (_startFailures > 0)
        {
            _startFailures--;
            throw new InvalidOperationException("The input monitor failed to start.");
        }
    }

    public Task StopAsync()
    {
        Stops++;
        Filter.SetSuppression(false, 0);
        return StopThrows ? Task.FromException(new InvalidOperationException("The input monitor failed to stop.")) : Task.CompletedTask;
    }

    public void RequestRehook() => Rehooks++;

    public ValueTask DisposeAsync() => new(StopAsync());
}

internal sealed class FakeIndicator : IControlIndicator
{
    private readonly TaskCompletionSource _showStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _hold;
    private bool _fail;
    private bool _failRender;
    private bool _never;

    public Task ShowStarted => _showStarted.Task;

    public List<long> Shows { get; } = [];

    public List<long> Hides { get; } = [];

    public bool Healthy { get; set; } = true;

    public bool Disposed { get; private set; }

    public void HoldShow() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void CompleteShow() => _hold!.TrySetResult();

    /// <summary>A dispatcher failure: the show throws and the indicator turns unhealthy.</summary>
    public void FailShow() => _fail = true;

    /// <summary>A render failure (preflight F19): the next show throws and the indicator stays healthy.</summary>
    public void FailRender() => _failRender = true;

    public void NeverCompleteShow() => _never = true;

    public async Task ShowAsync(long generation, CancellationToken ct)
    {
        Shows.Add(generation);
        _showStarted.TrySetResult();
        if (_hold is { } hold) await hold.Task.WaitAsync(ct);
        if (_never) await Task.Delay(Timeout.Infinite, ct);
        if (_fail)
        {
            Healthy = false;
            throw new InvalidOperationException("AI control indicator failed");
        }
        if (_failRender)
        {
            _failRender = false;
            throw new DesktopMcp.Desktop.Errors.DesktopException("Indicator render failed.");
        }
    }

    public void Hide(long generation) => Hides.Add(generation);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Healthy = false;
        return ValueTask.CompletedTask;
    }
}
