using DesktopMcp.Desktop.Dispatching;

namespace DesktopMcp.Desktop.Tests;

[Trait("Category", "Desktop")]
public class DispatcherQueueThreadTests
{
    [Fact]
    public async Task InvokeRunsOnTheDedicatedThread()
    {
        await using var dispatcher = DispatcherQueueThread.Start();
        var (threadId, hasAccess) = await dispatcher.InvokeAsync(() => (Environment.CurrentManagedThreadId, dispatcher.HasThreadAccess));
        Assert.Equal(dispatcher.ManagedThreadId, threadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, threadId);
        Assert.True(hasAccess);
        Assert.False(dispatcher.HasThreadAccess);
    }

    [Fact]
    public async Task InvokePropagatesExceptions()
    {
        await using var dispatcher = DispatcherQueueThread.Start();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task TimerTicksOnTheDispatcherUntilDisposed()
    {
        await using var dispatcher = DispatcherQueueThread.Start();
        var ticks = 0;
        var wrongThread = false;
        var timer = await dispatcher.StartTimerAsync(TimeSpan.FromMilliseconds(20), () =>
        {
            Interlocked.Increment(ref ticks);
            if (!dispatcher.HasThreadAccess) wrongThread = true;
        });
        await Task.Delay(300);
        timer.Dispose();
        var afterDispose = Volatile.Read(ref ticks);
        await Task.Delay(100);
        Assert.True(afterDispose >= 3, $"only {afterDispose} ticks");
        Assert.InRange(Volatile.Read(ref ticks), afterDispose, afterDispose + 1);
        Assert.False(wrongThread);
    }

    [Fact]
    public async Task AwaitInsideInvokeAsyncResumesOnTheDispatcher()
    {
        await using var dispatcher = DispatcherQueueThread.Start();
        var observed = new List<bool>();
        await dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(10);
            observed.Add(dispatcher.HasThreadAccess);
            await Task.Delay(10);
            observed.Add(dispatcher.HasThreadAccess);
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([true, true], observed);
    }

    [Fact]
    public async Task AThrowingTickIsReportedAndTheTimerKeepsTicking()
    {
        await using var dispatcher = DispatcherQueueThread.Start();
        var ticks = 0;
        var reported = new List<(string Message, bool OnDispatcher)>();
        dispatcher.UnhandledException += e => { lock (reported) reported.Add((e.Message, dispatcher.HasThreadAccess)); };
        using (await dispatcher.StartTimerAsync(TimeSpan.FromMilliseconds(20), () =>
        {
            if (Interlocked.Increment(ref ticks) == 1) throw new InvalidOperationException("first tick");
        }))
        {
            await Task.Delay(300);
        }
        lock (reported) Assert.Equal([("first tick", true)], reported);
        Assert.True(Volatile.Read(ref ticks) >= 3, $"only {Volatile.Read(ref ticks)} ticks");
    }

    [Fact]
    public async Task DisposeResumesOffTheDispatcherThread()
    {
        // Task.Run: no synchronization context (xUnit's would move the continuation off the dispatcher thread whatever
        // DisposeAsync does), so the caller resumes wherever DisposeAsync's own await resumed.
        var (dispatcherThread, resumedOn) = await Task.Run(async () =>
        {
            var dispatcher = DispatcherQueueThread.Start();
            // The dispatcher stays busy until the caller's continuation is registered, so the shutdown completes on the
            // dispatcher thread and the caller resumes from there (not synchronously on its own thread).
            using var running = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var busy = dispatcher.InvokeAsync(() => { running.Set(); release.Wait(TimeSpan.FromSeconds(5)); });
            Assert.True(running.Wait(TimeSpan.FromSeconds(5)), "the dispatcher did not run the blocking work item");
            var disposing = DisposeAndReportThread(dispatcher);
            release.Set();
            var resumedOn = await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            await busy.WaitAsync(TimeSpan.FromSeconds(5));
            return (dispatcher.ManagedThreadId, resumedOn);
        }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(dispatcherThread, resumedOn);

        static async Task<int> DisposeAndReportThread(DispatcherQueueThread dispatcher)
        {
            await dispatcher.DisposeAsync();
            return Environment.CurrentManagedThreadId;
        }
    }

    [Fact]
    public async Task InvokeAfterDisposeThrows()
    {
        var dispatcher = DispatcherQueueThread.Start();
        await dispatcher.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => { }));
    }
}
