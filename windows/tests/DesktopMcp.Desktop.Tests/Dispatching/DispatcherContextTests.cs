using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Tests.Dispatching;

public class DispatcherContextTests
{
    [Fact]
    public void PostEnqueuesAndSendNeedsThreadAccess()
    {
        var queued = new List<Action>();
        var context = new DispatcherQueueSynchronizationContext(work => { queued.Add(work); return true; }, () => false);
        var ran = 0;
        context.Post(_ => ran++, null);
        Assert.Equal(0, ran);
        queued.Single()();
        Assert.Equal(1, ran);
        Assert.Throws<NotSupportedException>(() => context.Send(_ => { }, null));
    }

    [Fact]
    public void SendRunsInlineWithThreadAccessAndCopyIsTheSameContext()
    {
        var context = new DispatcherQueueSynchronizationContext(_ => throw new InvalidOperationException("no enqueue"), () => true);
        var ran = 0;
        context.Send(_ => ran++, null);
        Assert.Equal(1, ran);
        Assert.Same(context, context.CreateCopy());
    }

    [Fact]
    public void ARejectedPostThrows() =>
        Assert.Throws<ObjectDisposedException>(() =>
            new DispatcherQueueSynchronizationContext(_ => false, () => false).Post(_ => { }, null));

    [Fact]
    public void ATickExceptionIsReportedNotThrown()
    {
        Exception? reported = null;
        TimerTicks.Guard(() => throw new InvalidOperationException("tick"), e => reported = e);
        Assert.Equal("tick", reported?.Message);
        TimerTicks.Guard(() => throw new InvalidOperationException("again"), _ => throw new Exception("report failed"));
    }

    [Fact]
    public async Task HostStartsOnceForConcurrentCallersAndRetriesAFailedStart()
    {
        var starts = 0;
        var host = new DispatcherHost(() => ++starts == 1 ? throw new InvalidOperationException("no runtime") : new FakeDispatcher());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetAsync(default));
        var both = await Task.WhenAll(host.GetAsync(default), host.GetAsync(default));
        Assert.Same(both[0], both[1]);
        Assert.Equal(2, starts);
        await host.ShutdownAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.GetAsync(default));
    }

    [Fact]
    public async Task HostStartsNothingUntilAskedAndShutdownIsIdempotent()
    {
        var starts = 0;
        var dispatcher = new FakeDispatcher();
        var host = new DispatcherHost(() => { starts++; return dispatcher; });
        Assert.False(host.IsStarted);
        await host.ShutdownAsync();
        Assert.Equal(0, starts);

        var started = new DispatcherHost(() => dispatcher);
        await started.GetAsync(default);
        Assert.True(started.IsStarted);
        await started.ShutdownAsync();
        await started.ShutdownAsync();
        Assert.Equal(1, dispatcher.Disposals);
        Assert.False(started.IsStarted);
    }

    [Fact]
    public async Task ACancelledCallerDoesNotCancelTheSharedStart()
    {
        using var release = new ManualResetEventSlim();
        var dispatcher = new FakeDispatcher();
        var host = new DispatcherHost(() => { release.Wait(TimeSpan.FromSeconds(5)); return dispatcher; });
        using var cancel = new CancellationTokenSource();
        var cancelled = host.GetAsync(cancel.Token);
        var waiting = host.GetAsync(default);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.Set();
        Assert.Same(dispatcher, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AShutdownDuringTheStartDisposesTheLateDispatcher()
    {
        using var release = new ManualResetEventSlim();
        var dispatcher = new FakeDispatcher();
        var host = new DispatcherHost(() => { release.Wait(TimeSpan.FromSeconds(5)); return dispatcher; });
        var starting = host.GetAsync(default);
        var shutdown = host.ShutdownAsync().AsTask();
        release.Set();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => starting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, dispatcher.Disposals);
    }

    [Fact]
    public void WaitForThreadIdShutsDownWhenTheEnqueueThrows()
    {
        var shutdown = false;
        var error = Assert.Throws<DesktopException>(() => DispatcherQueueThread.WaitForThreadId(
            _ => throw new COMException("closed", unchecked((int)0x80000013)), () => { shutdown = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(1)));
        Assert.True(shutdown);
        Assert.Equal("The new DispatcherQueue rejected work: closed", error.Message);
    }
}

/// <summary>
/// An <see cref="IDesktopDispatcher"/> that runs work inline on the caller's thread and records timers instead of
/// starting them: a test drives a timer by invoking its recorded tick (<see cref="Timers"/>, <see cref="TickAll"/>).
/// </summary>
internal sealed class FakeDispatcher : IDesktopDispatcher
{
    private readonly List<FakeTimer> _timers = [];

    public bool HasThreadAccess => true;
    public int ManagedThreadId => Environment.CurrentManagedThreadId;
    public int Disposals { get; private set; }
    public IReadOnlyList<FakeTimer> Timers => _timers;

    public event Action<Exception>? UnhandledException;

    public Task InvokeAsync(Action action)
    {
        try { action(); return Task.CompletedTask; }
        catch (Exception e) { return Task.FromException(e); }
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        try { return Task.FromResult(func()); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }

    public Task InvokeAsync(Func<Task> func)
    {
        try { return func(); }
        catch (Exception e) { return Task.FromException(e); }
    }

    public Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick)
    {
        var timer = new FakeTimer(interval, () => TimerTicks.Guard(tick, e => UnhandledException?.Invoke(e)));
        _timers.Add(timer);
        return Task.FromResult<IDisposable>(timer);
    }

    /// <summary>Runs one tick of every running timer.</summary>
    public void TickAll()
    {
        foreach (var timer in _timers.ToArray())
            if (!timer.Stopped) timer.Tick();
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }

    internal sealed class FakeTimer(TimeSpan interval, Action tick) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;
        public bool Stopped { get; private set; }
        public void Tick() => tick();
        public void Dispose() => Stopped = true;
    }
}
