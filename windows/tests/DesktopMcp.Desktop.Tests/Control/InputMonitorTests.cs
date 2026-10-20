using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Tests.Dispatching;

namespace DesktopMcp.Desktop.Tests.Control;

/// <summary>
/// The input monitor's logic through its seams (default run): a <see cref="FakeDispatcher"/> that runs work inline and
/// records the timers, and a fake Win32 layer that installs nothing. The real hooks are in <see cref="InputMonitorDesktopTests"/>.
/// </summary>
[Collection(InputMonitorCollection.Name)]
public class InputMonitorTests
{
    private const uint WmKeyDown = 0x100;
    private const uint WmKeyUp = 0x101;
    private const uint WmSysKeyDown = 0x104;
    private const uint WmLButtonDown = 0x201;
    private const uint KeyInjected = 0x10;

    private long _now = 1_000;
    private readonly FakeDispatcher _dispatcher = new();
    private readonly FakeMonitorNative _native = new();

    private PhysicalInputFilter NewFilter() => new(() => _now, TimeSpan.TicksPerSecond, ForeignInjectedPolicy.Physical, static () => { });

    private readonly System.Collections.Concurrent.ConcurrentQueue<Exception> _reports = new();

    private InputMonitor NewMonitor(PhysicalInputFilter? filter = null, IDesktopDispatcher? dispatcher = null, TimeSpan? startLimit = null,
        TimeSpan? stopLimit = null, DispatcherHost? host = null) =>
        new(host ?? new DispatcherHost(() => dispatcher ?? _dispatcher) { Report = _reports.Enqueue }, filter ?? NewFilter(), () => _now, _native,
            startLimit ?? TimeSpan.FromSeconds(3), stopLimit ?? TimeSpan.FromSeconds(2));

    private async Task<Exception> ReportedAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_reports.TryPeek(out var report)) return report;
            await Task.Delay(10);
        }
        throw new TimeoutException("Nothing was reported.");
    }

    private FakeDispatcher.FakeTimer Timer(TimeSpan interval) => Assert.Single(_dispatcher.Timers, t => t.Interval == interval);

    private FakeDispatcher.FakeTimer Tick => Timer(TimeSpan.FromMilliseconds(100));

    private FakeDispatcher.FakeTimer Rehook => Timer(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task StartCreatesTheSinkInstallsBothHooksAndStartsTheTimers()
    {
        await using var monitor = NewMonitor();
        await monitor.StartAsync(default);

        Assert.Equal(["sink", "install k1 m2"], _native.Calls);
        Assert.Equal([1, 2], _native.Installed.Order());
        Assert.Equal(2, _dispatcher.Timers.Count);
        _ = Tick;
        _ = Rehook;
        Assert.Equal(_now, monitor.LastBeat);
        Assert.Equal(_now, monitor.LastRehook);
    }

    [Fact]
    public async Task OnlyOneMonitorRunsAtATime()
    {
        var first = NewMonitor();
        await first.StartAsync(default);
        await using var second = NewMonitor();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(default));
        Assert.Equal("Another input monitor is running.", error.Message);

        await first.StopAsync();
        await second.StartAsync(default); // the slot is free once the first one's hooks are gone
        await second.StopAsync();
    }

    [Fact]
    public async Task AFailedStartUndoesItsWorkAndFreesTheSlot()
    {
        _native.InstallFailures = 1;
        var monitor = NewMonitor();
        await Assert.ThrowsAnyAsync<Exception>(() => monitor.StartAsync(default));
        Assert.Equal(["sink", "install failed", "destroy sink"], _native.Calls);
        Assert.Empty(_native.Installed);
        Assert.All(_dispatcher.Timers, t => Assert.True(t.Stopped));

        await monitor.StartAsync(default); // a retry (the coordinator's, every 5 s) may use the same instance
        Assert.Equal(2, _native.Installed.Count);
        await monitor.StopAsync();
    }

    [Fact]
    public async Task ASinkFailureInstallsNoHook()
    {
        _native.SinkFails = true;
        var monitor = NewMonitor();
        await Assert.ThrowsAnyAsync<Exception>(() => monitor.StartAsync(default));
        Assert.Equal(["sink failed"], _native.Calls);
        Assert.Empty(_native.Installed);

        _native.SinkFails = false;
        await monitor.StartAsync(default);
        await monitor.StopAsync();
    }

    [Fact]
    public async Task AStartThatDoesNotFinishInTimeThrowsAndInstallsNothingLater()
    {
        var stalled = new StalledDispatcher();
        var monitor = NewMonitor(dispatcher: stalled, startLimit: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => monitor.StartAsync(default));
        Assert.Empty(_native.Calls);

        // The dispatcher wakes up: the queued start is stale (the failed start's undo moved the generation on), so it
        // installs nothing, and the queued undo frees the slot.
        stalled.RunAll();
        Assert.Empty(_native.Calls);
        Assert.Empty(_native.Installed);
        await using var next = NewMonitor();
        await next.StartAsync(default);
        await next.StopAsync();
    }

    [Fact]
    public async Task StopTurnsSuppressionOffUnhooksAndDestroysTheSink()
    {
        var filter = NewFilter();
        var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);
        filter.SetSuppression(true, _now + 10_000_000);

        await monitor.StopAsync();

        Assert.False(filter.Suppressing);
        Assert.Equal(["sink", "install k1 m2", "unhook k1", "unhook m2", "destroy sink"], _native.Calls);
        Assert.Empty(_native.Installed);
        Assert.All(_dispatcher.Timers, t => Assert.True(t.Stopped));
        await monitor.StopAsync(); // idempotent
        await monitor.DisposeAsync();
        Assert.Equal(5, _native.Calls.Count);
    }

    [Fact]
    public async Task StopBeforeStartDoesNothing()
    {
        await using var monitor = NewMonitor();
        await monitor.StopAsync();
        Assert.Empty(_native.Calls);
    }

    [Fact]
    public async Task TheReinstallInstallsTheNewHooksBeforeRemovingTheOldOnes()
    {
        await using var monitor = NewMonitor();
        await monitor.StartAsync(default);
        _now += 5 * TimeSpan.TicksPerSecond;

        Rehook.Tick();

        Assert.Equal(["sink", "install k1 m2", "install k3 m4", "unhook k1", "unhook m2"], _native.Calls);
        Assert.Equal([3, 4], _native.Installed.Order());
        Assert.Equal(_now, monitor.LastRehook);
    }

    [Fact]
    public async Task AFailedReinstallKeepsTheOldHooksFailsOpenAndRetriesOnTheNextTick()
    {
        var filter = NewFilter();
        await using var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);
        var started = monitor.LastRehook;
        filter.SetSuppression(true, _now + 10_000_000);
        _native.InstallFailures = 1;
        _now += 5 * TimeSpan.TicksPerSecond;

        Rehook.Tick();

        Assert.Equal([1, 2], _native.Installed.Order());
        Assert.True(filter.FailOpenRequested);
        Assert.False(filter.Suppressing);
        Assert.Equal(started, monitor.LastRehook);

        _now += TimeSpan.TicksPerSecond / 10;
        Tick.Tick();
        Assert.Equal([3, 4], _native.Installed.Order());
        Assert.Equal(_now, monitor.LastRehook);

        Tick.Tick(); // the retry flag was cleared
        Assert.Equal([3, 4], _native.Installed.Order());
    }

    [Fact]
    public async Task ARequestedRehookRunsOnTheNextTick()
    {
        await using var monitor = NewMonitor();
        await monitor.StartAsync(default);
        _now += 1;

        monitor.RequestRehook();
        Assert.Equal([1, 2], _native.Installed.Order()); // nothing happens off the dispatcher
        Tick.Tick();

        Assert.Equal([3, 4], _native.Installed.Order());
        Assert.Equal(_now, monitor.LastRehook);
    }

    [Fact]
    public async Task TheTickSamplesMissedReleasesAndBeats()
    {
        var filter = NewFilter();
        await using var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);
        filter.PublishState(ControlState.Ready, 0, 0);
        Assert.Equal(HookVerdict.Pass, filter.OnKey(0x41, true, 0, 0)); // a key whose up Windows never delivered
        Assert.True(filter.Held);

        for (var i = 0; i < 5; i++)
        {
            _now += TimeSpan.TicksPerSecond / 10;
            Tick.Tick();
            Assert.Equal(_now, monitor.LastBeat);
        }

        Assert.False(filter.Held);
        Assert.Contains(0x41, _native.KeyStateQueries);
    }

    [Fact]
    public async Task AKeyStateFailureFailsOpenAndStillBeats()
    {
        var filter = NewFilter();
        await using var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);
        filter.PublishState(ControlState.Ready, 0, 0);
        filter.OnKey(0x41, true, 0, 0);
        _native.KeyStateThrows = true;
        var reported = new List<Exception>();
        _dispatcher.UnhandledException += reported.Add;
        _now += TimeSpan.TicksPerSecond;

        Tick.Tick();

        Assert.True(filter.FailOpenRequested);
        Assert.Equal(_now, monitor.LastBeat);
        Assert.Empty(reported); // handled by the tick, not left to the dispatcher's report
    }

    [Fact]
    public async Task KeysPassToTheNextHookAndTheirDeliveryIsRecorded()
    {
        var filter = NewFilter();
        await using var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);

        Assert.Equal(0, monitor.Keyboard(0, (nint)WmKeyDown, 77, 0x41, 0, 0));
        Assert.Equal([(0, (nint)WmKeyDown, (nint)77)], _native.NextHookCalls);
        Assert.True(filter.KeyDelivered(0x41));

        _native.NextResult = 1; // a later hook swallowed it: not delivered
        monitor.Keyboard(0, (nint)WmSysKeyDown, 0, 0x42, 0, 0);
        Assert.False(filter.KeyDelivered(0x42));

        _native.NextResult = 0;
        monitor.Keyboard(0, (nint)WmKeyUp, 0, 0x41, 0, 0);
        Assert.False(filter.KeyDelivered(0x41));
    }

    [Fact]
    public async Task ASwallowedEventReturnsOneWithoutCallingTheNextHook()
    {
        var filter = NewFilter();
        await using var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);
        filter.SetSuppression(true, _now + 10_000_000);

        Assert.Equal(1, monitor.Keyboard(0, (nint)WmKeyDown, 0, 0x41, KeyInjected, 0)); // foreign injected counts as physical
        Assert.Equal(1, monitor.Mouse(0, (nint)WmLButtonDown, 0, 10, 10, 0, 0, 0));
        Assert.Empty(_native.NextHookCalls);
        Assert.False(filter.KeyDelivered(0x41));
        Assert.False(filter.ButtonDelivered(1));
    }

    [Fact]
    public async Task MouseButtonsPassToTheNextHookAndTheirDeliveryIsRecorded()
    {
        var filter = NewFilter();
        await using var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);

        Assert.Equal(0, monitor.Mouse(0, (nint)WmLButtonDown, 5, 10, 10, 0, 0, 0));
        Assert.True(filter.ButtonDelivered(1));
        Assert.Single(_native.NextHookCalls);
    }

    [Fact]
    public async Task ANegativeCodeGoesStraightToTheNextHook()
    {
        var filter = NewFilter();
        await using var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);
        filter.SetSuppression(true, _now + 10_000_000);
        _native.NextResult = 9;

        Assert.Equal(9, monitor.Keyboard(-1, (nint)WmKeyDown, 3, 0x41, 0, 0));
        Assert.Equal(9, monitor.Mouse(-1, (nint)WmLButtonDown, 3, 0, 0, 0, 0, 0));
        Assert.Equal(2, _native.NextHookCalls.Count);
        Assert.False(filter.Events.TryRead(out _));
        Assert.False(filter.Held);
    }

    [Fact]
    public async Task AFailingNextHookFailsOpenAndNeverThrows()
    {
        var filter = NewFilter();
        await using var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);
        _native.NextHookThrows = true;

        Assert.Equal(0, monitor.Keyboard(0, (nint)WmKeyDown, 0, 0x41, 0, 0));
        Assert.Equal(0, monitor.Mouse(0, (nint)WmLButtonDown, 0, 0, 0, 0, 0, 0));
        Assert.True(filter.FailOpenRequested);
    }

    [Fact]
    public async Task AFailedNextHookCallIsRetriedSoTheChainStillRuns()
    {
        var filter = NewFilter();
        await using var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);
        _native.NextResult = 5; // a later hook's verdict, which must reach Windows
        _native.NextHookFailures = 1;

        Assert.Equal(5, monitor.Keyboard(0, (nint)WmKeyDown, 0, 0x41, 0, 0));
        _native.NextHookFailures = 1;
        Assert.Equal(5, monitor.Mouse(0, (nint)WmLButtonDown, 0, 0, 0, 0, 0, 0));
        Assert.Equal(4, _native.NextHookCalls.Count);
        Assert.True(filter.FailOpenRequested);
    }

    [Fact]
    public async Task AStopWhileTheDispatcherIsStartingInstallsNothing()
    {
        using var release = new ManualResetEventSlim();
        var host = new DispatcherHost(() => { release.Wait(); return _dispatcher; });
        var monitor = NewMonitor(host: host);

        var start = monitor.StartAsync(default);
        await monitor.StopAsync();
        await using (var other = NewMonitor()) // the slot is free at once
        {
            await other.StartAsync(default);
            await other.StopAsync();
        }
        _native.Calls.Clear();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Empty(_native.Calls);
        Assert.Empty(_native.Installed);
        Assert.All(_dispatcher.Timers, t => Assert.True(t.Stopped));
        await using var next = NewMonitor();
        await next.StartAsync(default);
    }

    [Fact]
    public async Task AStaleStartOfTheSameInstanceInstallsNothing()
    {
        using var release = new ManualResetEventSlim();
        var host = new DispatcherHost(() => { release.Wait(); return _dispatcher; });
        var monitor = NewMonitor(host: host);

        var first = monitor.StartAsync(default);   // waiting for the dispatcher
        await monitor.StopAsync();                 // frees the slot
        var second = monitor.StartAsync(default);  // the same instance wins the slot again
        release.Set();

        await second;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(["sink", "install k1 m2"], _native.Calls);
        Assert.Equal(2, _dispatcher.Timers.Count);
        await monitor.StopAsync();
        Assert.Empty(_native.Installed);
    }

    [Fact]
    public async Task StopGivesUpOnAStalledDispatcherAfterItsBound()
    {
        var filter = NewFilter();
        var stalled = new StalledDispatcher { Stalled = false };
        var monitor = NewMonitor(filter, stalled, stopLimit: TimeSpan.FromMilliseconds(100));
        await monitor.StartAsync(default);
        filter.SetSuppression(true, _now + 10_000_000);
        stalled.Stalled = true;

        await monitor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(filter.Suppressing);
        Assert.IsType<TimeoutException>(await ReportedAsync());
        stalled.RunAll(); // the dispatcher wakes: the teardown runs then
        Assert.Empty(_native.Installed);
    }

    [Fact]
    public async Task AFailedUndoOfATimedOutStartIsReported()
    {
        // The hooks and the sink are in; the timers never start, so the start times out and its undo (unawaited) fails.
        var stalled = new StalledDispatcher { Stalled = false, StallTimers = true };
        var monitor = NewMonitor(dispatcher: stalled, startLimit: TimeSpan.FromMilliseconds(100));
        _native.DestroySinkThrows = true;

        await Assert.ThrowsAsync<TimeoutException>(() => monitor.StartAsync(default));

        Assert.Empty(_native.Installed);
        Assert.Equal("destroy sink", Assert.IsType<InvalidOperationException>(await ReportedAsync()).Message);
    }

    [Fact]
    public async Task ARetryWhileTheTimedOutStartIsBeingUndoneSaysSo()
    {
        var stalled = new StalledDispatcher();
        var monitor = NewMonitor(dispatcher: stalled, startLimit: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => monitor.StartAsync(default));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.StartAsync(default));
        Assert.Equal("The previous run of this input monitor is still being torn down on the dispatcher; try again later.", error.Message);

        stalled.RunAll();
        stalled.Stalled = false;
        await monitor.StartAsync(default);
        await monitor.StopAsync();
    }

    [Fact]
    public async Task RawInputIsCountedAndOnlyRelativeDeviceMotionReachesTheFilter()
    {
        var filter = NewFilter();
        await using var monitor = NewMonitor(filter);
        await monitor.StartAsync(default);

        monitor.OnRawInput(0, 0, 0, 1, 0);          // the null device (injected): counted, not motion
        monitor.OnRawInput(0, 7, 0x01, 100, 100);   // absolute
        monitor.OnRawInput(1, 7, 0, 3, 4);          // a keyboard report
        monitor.OnRawInput(0, 7, 0, 3, 4);

        Assert.Equal(4, monitor.RawMessages);
        Assert.True(filter.Events.TryRead(out var motion));
        Assert.Equal(PhysicalEventKind.RawMotion, motion.Kind);
        Assert.Equal((7L, 3, 4), (motion.Device, motion.Dx, motion.Dy));
        Assert.False(filter.Events.TryRead(out _));
    }

    [Fact]
    public async Task TicksAfterStopDoNothing()
    {
        var monitor = NewMonitor();
        await monitor.StartAsync(default);
        var tick = Tick;
        var rehook = Rehook;
        await monitor.StopAsync();
        var calls = _native.Calls.Count;
        _now += TimeSpan.TicksPerSecond;

        monitor.RequestRehook();
        tick.Tick();
        rehook.Tick();

        Assert.Equal(calls, _native.Calls.Count);
    }

    /// <summary>A dispatcher that queues work until the test runs it, as a stalled dispatcher thread would.</summary>
    private sealed class StalledDispatcher : IDesktopDispatcher
    {
        private readonly Queue<Action> _work = new();

        /// <summary>While false, work runs inline as it is queued.</summary>
        public bool Stalled { get; set; } = true;

        /// <summary>Timers never finish starting.</summary>
        public bool StallTimers { get; set; }

        public bool HasThreadAccess => false;
        public int ManagedThreadId => -1;
        public event Action<Exception>? UnhandledException { add { } remove { } }

        public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_work)
                _work.Enqueue(() =>
                {
                    try { done.SetResult(func()); }
                    catch (Exception e) { done.SetException(e); }
                });
            if (!Stalled) RunAll();
            return done.Task;
        }

        public Task InvokeAsync(Func<Task> func) => InvokeAsync<Task>(func).Unwrap();

        public Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick) => StallTimers
            ? new TaskCompletionSource<IDisposable>().Task
            : InvokeAsync<IDisposable>(() => new FakeDispatcher.FakeTimer(interval, tick));

        public void RunAll()
        {
            while (true)
            {
                Action next;
                lock (_work)
                {
                    if (_work.Count == 0) return;
                    next = _work.Dequeue();
                }
                next();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>The monitor's hook procedures route to one static instance, so its tests never run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InputMonitorCollection
{
    public const string Name = "InputMonitor";
}

/// <summary>Win32 for the monitor without Win32: hooks are numbers, every call is logged.</summary>
internal sealed class FakeMonitorNative : IInputMonitorNative
{
    private nint _next = 1;
    private readonly Dictionary<nint, char> _kinds = [];

    public List<string> Calls { get; } = [];
    public HashSet<nint> Installed { get; } = [];
    public List<(int Code, nint WParam, nint LParam)> NextHookCalls { get; } = [];
    public List<int> KeyStateQueries { get; } = [];
    public int InstallFailures { get; set; }
    public bool SinkFails { get; set; }
    public bool NextHookThrows { get; set; }
    public int NextHookFailures { get; set; }
    public bool DestroySinkThrows { get; set; }
    public bool KeyStateThrows { get; set; }
    public nint NextResult { get; set; }

    public void CreateSink()
    {
        if (SinkFails)
        {
            Calls.Add("sink failed");
            throw new InvalidOperationException("RegisterRawInputDevices failed (5).");
        }
        Calls.Add("sink");
    }

    public void DestroySink()
    {
        Calls.Add("destroy sink");
        if (DestroySinkThrows) throw new InvalidOperationException("destroy sink");
    }

    public (nint Keyboard, nint Mouse) InstallHooks()
    {
        if (InstallFailures > 0)
        {
            InstallFailures--;
            Calls.Add("install failed");
            throw new InvalidOperationException("SetWindowsHookEx failed (8).");
        }
        var keyboard = _next++;
        var mouse = _next++;
        _kinds[keyboard] = 'k';
        _kinds[mouse] = 'm';
        Installed.Add(keyboard);
        Installed.Add(mouse);
        Calls.Add($"install k{keyboard} m{mouse}");
        return (keyboard, mouse);
    }

    public void Unhook(nint hook)
    {
        Installed.Remove(hook);
        Calls.Add($"unhook {_kinds[hook]}{hook}");
    }

    public nint CallNextHook(int code, nint wParam, nint lParam)
    {
        NextHookCalls.Add((code, wParam, lParam));
        if (NextHookThrows) throw new InvalidOperationException("next hook");
        if (NextHookFailures > 0)
        {
            NextHookFailures--;
            throw new InvalidOperationException("next hook");
        }
        return NextResult;
    }

    public short GetAsyncKeyState(int vk)
    {
        KeyStateQueries.Add(vk);
        if (KeyStateThrows) throw new InvalidOperationException("GetAsyncKeyState");
        return 0;
    }
}
