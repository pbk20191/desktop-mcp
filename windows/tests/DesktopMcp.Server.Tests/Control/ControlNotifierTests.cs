using DesktopMcp.Core.Control;
using DesktopMcp.Server.Control;
using Microsoft.Extensions.Logging;

namespace DesktopMcp.Server.Tests;

public class ControlNotifierTests
{
    [Theory]
    [InlineData(ControlState.Ready, ControlState.User, "USER_CONTROL")]
    [InlineData(ControlState.Ai, ControlState.User, "USER_CONTROL")]
    [InlineData(ControlState.User, ControlState.Ready, "AI_CONTROL_AVAILABLE")]
    [InlineData(ControlState.Ai, ControlState.Ready, null)]
    [InlineData(ControlState.Ready, ControlState.Ai, null)]
    [InlineData(ControlState.Ai, ControlState.Unavailable, null)]
    [InlineData(ControlState.Unavailable, ControlState.User, null)]
    public void UpstreamsEvents(ControlState previous, ControlState current, string? expected) =>
        Assert.Equal(expected, ControlNotifier.EventFor(previous, current));

    [Fact]
    public async Task ATakeoverReachesEveryLiveSessionWithUpstreamsShape()
    {
        var rig = new NotifierRig(ttl: null);
        var a = rig.Session("a"); var b = rig.Session("b");
        rig.Control.RaiseStateChanged(new(ControlState.Ai, 2, 0, true, "relaxed", 10));
        rig.Control.RaiseStateChanged(new(ControlState.User, 3, 10, true, "relaxed", 10));
        await rig.Drained();
        var message = Assert.Single(a.Sent);
        Assert.Equal(("notice", "desktop-mcp.control"), (message.Level, message.Logger));
        Assert.Equal("""{"event":"USER_CONTROL","status":{"state":"user","generation":3,"wait_seconds":10.0,"enabled":true,"policy":"relaxed","idle_timeout_seconds":10.0}}""",
            message.Data.GetRawText());
        Assert.Single(b.Sent);
    }

    [Fact]
    public async Task TheReturnIsAnnouncedAsAiControlAvailable()
    {
        var rig = new NotifierRig(ttl: null);
        var a = rig.Session("a");
        rig.Control.RaiseStateChanged(new(ControlState.User, 2, 10, true, "relaxed", 10));
        rig.Control.RaiseStateChanged(new(ControlState.Ready, 3, 0, true, "relaxed", 10));
        await rig.Drained();
        Assert.Equal(["USER_CONTROL", "AI_CONTROL_AVAILABLE"], a.Sent.Select(m => m.Data.GetProperty("event").GetString()));
    }

    [Fact]
    public async Task TwoQuickChangesReachASessionInOrder()
    {
        var rig = new NotifierRig(ttl: null);
        var gated = new GatedSession();
        rig.Sessions.Remember("a", gated);
        var probe = rig.Session("b");
        rig.Control.RaiseStateChanged(new(ControlState.User, 2, 10, true, "relaxed", 10));
        rig.Control.RaiseStateChanged(new(ControlState.Ready, 3, 0, true, "relaxed", 10));
        await gated.FirstEntered.WaitAsync(TimeSpan.FromSeconds(10));
        // The probe has both messages, so the consumer has handled both changes; the gated session's second send must
        // still wait for its first.
        using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (probe.Sent.Count < 2) await Task.Delay(10, bound.Token);
        Assert.Equal(1, gated.Entered);
        gated.Release();
        await rig.Drained();
        Assert.Equal(["USER_CONTROL", "AI_CONTROL_AVAILABLE"], gated.Completed.Select(m => m.Data.GetProperty("event").GetString()));
    }

    [Fact]
    public async Task AFailingSessionDoesNotStopTheOthers()
    {
        var rig = new NotifierRig(ttl: null);
        rig.Session("a", fail: true);
        var b = rig.Session("b");
        rig.Control.RaiseStateChanged(new(ControlState.User, 2, 10, true, "relaxed", 10));
        rig.Control.RaiseStateChanged(new(ControlState.Ready, 3, 0, true, "relaxed", 10));
        await rig.Drained();
        Assert.Equal(["USER_CONTROL", "AI_CONTROL_AVAILABLE"], b.Sent.Select(m => m.Data.GetProperty("event").GetString()));
        Assert.Equal("b", Assert.Single(rig.Sessions.Live()).Key);
    }

    [Fact]
    public async Task OlderGenerationsAreDropped()
    {
        var rig = new NotifierRig(ttl: null);
        var a = rig.Session("a");
        rig.Control.RaiseStateChanged(new(ControlState.User, 5, 10, true, "relaxed", 10));
        rig.Control.RaiseStateChanged(new(ControlState.Ready, 4, 0, true, "relaxed", 10));
        await rig.Drained();
        Assert.Single(a.Sent);
    }

    [Fact]
    public async Task AFailedSendForgetsTheSession()
    {
        var rig = new NotifierRig(ttl: null);
        rig.Session("a", fail: true);
        rig.Control.RaiseStateChanged(new(ControlState.User, 2, 10, true, "relaxed", 10));
        await rig.Drained();
        Assert.Empty(rig.Sessions.Live());
        Assert.Contains(rig.Log.Entries, entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task ExpiredAndStatelessSessionsGetNothing()
    {
        var rig = new NotifierRig(ttl: TimeSpan.FromSeconds(60));
        var a = rig.Session("a");
        rig.Advance(61);
        rig.Control.RaiseStateChanged(new(ControlState.User, 2, 10, true, "relaxed", 10));
        await rig.Drained();
        Assert.Empty(a.Sent);
        var stateless = new SessionRegistry(TimeProvider.System, TimeSpan.Zero);
        stateless.Remember("s", new RecordingSession());
        Assert.Empty(stateless.Live());
    }

    [Fact]
    public void RememberingRefreshesTheLastSeenTime()
    {
        var time = new ManualTime();
        var sessions = new SessionRegistry(time, TimeSpan.FromSeconds(60));
        var first = new RecordingSession();
        sessions.Remember("a", first);
        time.Advance(TimeSpan.FromSeconds(50));
        var second = new RecordingSession();
        sessions.Remember("a", second);
        time.Advance(TimeSpan.FromSeconds(50));
        var live = Assert.Single(sessions.Live());
        Assert.Equal("a", live.Key);
        Assert.Same(second, live.Session);
        sessions.Forget("a");
        Assert.Empty(sessions.Live());
    }

    [Fact]
    public void RememberingDropsExpiredSessionsWithoutANotification()
    {
        // Final review I2: a server whose control state never changes must not keep every session it ever served.
        var time = new ManualTime();
        var sessions = new SessionRegistry(time, TimeSpan.FromSeconds(60));
        var a = new RecordingSession();
        sessions.Remember("a", a);
        time.Advance(TimeSpan.FromSeconds(61));
        sessions.Remember("b", new RecordingSession());
        Assert.Equal(1, sessions.Count);
        Assert.Empty(a.Sent);
        Assert.Equal("b", Assert.Single(sessions.Live()).Key);
    }

    [Fact]
    public void RememberingKeepsSessionsWithinTheTtl()
    {
        var time = new ManualTime();
        var sessions = new SessionRegistry(time, TimeSpan.FromSeconds(60));
        sessions.Remember("a", new RecordingSession());
        time.Advance(TimeSpan.FromSeconds(60));
        sessions.Remember("b", new RecordingSession());
        Assert.Equal(2, sessions.Count);
        var forever = new SessionRegistry(time, null);
        forever.Remember("a", new RecordingSession());
        time.Advance(TimeSpan.FromDays(1));
        forever.Remember("b", new RecordingSession());
        Assert.Equal(2, forever.Count);
    }

    [Fact]
    public async Task NothingIsSentAfterDisposal()
    {
        var rig = new NotifierRig(ttl: null);
        var a = rig.Session("a");
        await rig.Drained();
        rig.Control.RaiseStateChanged(new(ControlState.User, 2, 10, true, "relaxed", 10));
        Assert.Empty(a.Sent);
    }
}

internal sealed class NotifierRig
{
    private readonly ManualTime _time = new();

    public NotifierRig(TimeSpan? ttl)
    {
        Sessions = new SessionRegistry(_time, ttl);
        Notifier = new ControlNotifier(Control, Sessions, Log);
        Notifier.Start();
    }

    public FakeControl Control { get; } = new();

    public SessionRegistry Sessions { get; }

    public RecordingLogger Log { get; } = new();

    public ControlNotifier Notifier { get; }

    public RecordingSession Session(string key, bool fail = false)
    {
        var session = new RecordingSession { Fail = fail };
        Sessions.Remember(key, session);
        return session;
    }

    public void Advance(double seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    /// <summary>Handles everything raised so far and waits for the sends (disposal drains the queue).</summary>
    public Task Drained() => Notifier.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
}

internal sealed class RecordingSession : IControlSession
{
    private readonly Lock _lock = new();
    private readonly List<ControlLogMessage> _sent = [];

    public bool Fail { get; init; }

    public IReadOnlyList<ControlLogMessage> Sent
    {
        get
        {
            lock (_lock) return [.. _sent];
        }
    }

    public ValueTask NotifyAsync(ControlLogMessage message, CancellationToken ct)
    {
        if (Fail) throw new IOException("The client went away.");
        lock (_lock) _sent.Add(message);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A session whose first send waits for <see cref="Release"/>; messages are recorded when their send completes.</summary>
internal sealed class GatedSession : IControlSession
{
    private readonly Lock _lock = new();
    private readonly List<ControlLogMessage> _completed = [];
    private readonly TaskCompletionSource _firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entered;

    public Task FirstEntered => _firstEntered.Task;

    public int Entered => Volatile.Read(ref _entered);

    public IReadOnlyList<ControlLogMessage> Completed
    {
        get
        {
            lock (_lock) return [.. _completed];
        }
    }

    public void Release() => _release.TrySetResult();

    public async ValueTask NotifyAsync(ControlLogMessage message, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _entered) == 1)
        {
            _firstEntered.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }
        lock (_lock) _completed.Add(message);
    }
}

internal sealed class RecordingLogger : ILogger
{
    private readonly Lock _lock = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_lock) return [.. _entries];
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock) _entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>A clock that moves only when <see cref="Advance"/> is called.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
