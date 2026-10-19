using DesktopMcp.Desktop.Dispatching;

namespace DesktopMcp.Desktop.Tests;

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
    public async Task InvokeAfterDisposeThrows()
    {
        var dispatcher = DispatcherQueueThread.Start();
        await dispatcher.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => { }));
    }
}
