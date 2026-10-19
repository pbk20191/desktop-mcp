using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tests.Uia;

public class UiaWorkerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private sealed class Session
    {
        public int ThreadId { get; } = Environment.CurrentManagedThreadId;
    }

    [Fact]
    public async Task JobsRunOnOneMtaThreadThatOwnsTheSession()
    {
        using var worker = new UiaWorker<Session>(() => new Session());
        var first = await worker.RunAsync((s, _) => (s.ThreadId, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()), Generous, default);
        var second = await worker.RunAsync((s, _) => s.ThreadId, Generous, default);
        Assert.Equal(first.Item1, first.Item2);
        Assert.Equal(ApartmentState.MTA, first.Item3);
        Assert.Equal(first.Item1, second);
        Assert.NotEqual(Environment.CurrentManagedThreadId, second);
    }

    [Fact]
    public async Task JobExceptionsReachTheCaller()
    {
        using var worker = new UiaWorker<Session>(() => new Session());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync<int>((_, _) => throw new InvalidOperationException("boom"), Generous, default));
        Assert.Equal("boom", error.Message);
        // A job's own TimeoutException is the job's failure, not the deadline: the worker is kept.
        await Assert.ThrowsAsync<TimeoutException>(() => worker.RunAsync<int>((_, _) => throw new TimeoutException(), Generous, default));
        Assert.Equal(0, worker.StuckWorkers);
        Assert.Equal(1, await worker.RunAsync((_, _) => 1, Generous, default));
    }

    [Fact]
    public async Task AFailedSessionIsCreatedAgainByTheNextJob()
    {
        var attempts = 0;
        using var worker = new UiaWorker<Session>(() => ++attempts == 1 ? throw new InvalidOperationException("no client") : new Session());
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync((s, _) => s.ThreadId, Generous, default));
        Assert.True(await worker.RunAsync((s, _) => s.ThreadId == Environment.CurrentManagedThreadId, Generous, default));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AHungJobTimesOutAndTheNextJobGetsAFreshThreadAndSession()
    {
        using var release = new ManualResetEventSlim();
        var sessions = 0;
        using var worker = new UiaWorker<Session>(() => { Interlocked.Increment(ref sessions); return new Session(); });
        var hungThread = await worker.RunAsync((s, _) => s.ThreadId, Generous, default);

        var error = await Assert.ThrowsAsync<UiaTimeoutException>(() => worker.RunAsync((_, _) => release.Wait(Generous), TimeSpan.FromMilliseconds(100), default));
        Assert.Equal("UI Automation did not answer within 0.1 s.", error.Message);
        Assert.True(ComErrors.IsTimeout(error));
        Assert.Equal(1, worker.StuckWorkers);

        var next = await worker.RunAsync((s, _) => s.ThreadId, Generous, default);
        Assert.NotEqual(hungThread, next);
        Assert.Equal(2, sessions);

        release.Set();
        await WaitUntil(() => worker.StuckWorkers == 0);
    }

    [Fact]
    public async Task TheJobTokenIsCancelledAtTheDeadlineSoAnAbandonedJobCanStop()
    {
        using var worker = new UiaWorker<Session>(() => new Session());
        Assert.False(await worker.RunAsync((_, token) => token.IsCancellationRequested, Generous, default));

        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<UiaTimeoutException>(() => worker.RunAsync((_, token) =>
        {
            // A walk checks its token at each step; this one waits for it.
            observed.TrySetResult(token.WaitHandle.WaitOne(Generous));
            return 0;
        }, TimeSpan.FromMilliseconds(50), default));
        Assert.True(await observed.Task.WaitAsync(Generous));
        await WaitUntil(() => worker.StuckWorkers == 0);
    }

    [Fact]
    public async Task TooManyStuckThreadsFailFast()
    {
        using var release = new ManualResetEventSlim();
        using var worker = new UiaWorker<Session>(() => new Session());
        for (var i = 0; i < UiaWorker<Session>.MaxStuckWorkers; i++)
            await Assert.ThrowsAsync<UiaTimeoutException>(() => worker.RunAsync((_, _) => release.Wait(Generous), TimeSpan.FromMilliseconds(50), default));

        var ran = false;
        var error = await Assert.ThrowsAsync<UiaUnavailableException>(() => worker.RunAsync((_, _) => ran = true, Generous, default));
        Assert.Equal("UI Automation is not responding: 4 calls are still blocked in other applications.", error.Message);
        Assert.False(ran);

        release.Set();
        await WaitUntil(() => worker.StuckWorkers == 0);
        Assert.True(await worker.RunAsync((_, _) => true, Generous, default));
    }

    [Fact]
    public async Task CancellationAbandonsTheWorkerLikeATimeoutButIsNoTimeout()
    {
        // Preflight F5: the next job must not queue behind a cancelled (maybe hung) job and miss its own deadline.
        using var release = new ManualResetEventSlim();
        using var worker = new UiaWorker<Session>(() => new Session());
        var hungThread = await worker.RunAsync((s, _) => s.ThreadId, Generous, default);
        var jobToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync((_, token) =>
        {
            jobToken.TrySetResult(token);
            return release.Wait(Generous);
        }, Generous, cancel.Token));
        // Callers block a window and its process only on timeouts; a cancellation blocks nothing.
        Assert.False(ComErrors.IsTimeout(error));
        Assert.True((await jobToken.Task.WaitAsync(Generous)).IsCancellationRequested);
        Assert.Equal(1, worker.StuckWorkers);

        var next = await worker.RunAsync((s, _) => s.ThreadId, Generous, default);
        Assert.NotEqual(hungThread, next);

        release.Set();
        await WaitUntil(() => worker.StuckWorkers == 0);
    }

    [Fact]
    public async Task CancellationBeforeTheJobStartsAbandonsNothing()
    {
        using var release = new ManualResetEventSlim();
        using var worker = new UiaWorker<Session>(() => new Session());
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = worker.RunAsync((s, _) =>
        {
            started.TrySetResult(s.ThreadId);
            release.Wait(Generous);
            return s.ThreadId;
        }, Generous, default);
        var thread = await started.Task.WaitAsync(Generous);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync((_, _) => ran = true, Generous, cancel.Token));
        Assert.Equal(0, worker.StuckWorkers);

        release.Set();
        Assert.Equal(thread, await running.WaitAsync(Generous));
        Assert.Equal(thread, await worker.RunAsync((s, _) => s.ThreadId, Generous, default));
        Assert.False(ran);
    }

    [Fact]
    public async Task AJobThatAllocatedMuchIsFollowedByACollection()
    {
        // COM objects from the generated interfaces are released by their finalizers (see UiaWorker), so a job that
        // created many of them is followed by a collection on the worker thread.
        var collections = new List<int>();
        var collected = new SemaphoreSlim(0);
        using var worker = new UiaWorker<Session>(() => new Session(), collectAfterBytes: 1 << 20, collect: () =>
        {
            lock (collections) collections.Add(Environment.CurrentManagedThreadId);
            collected.Release();
        });

        var thread = await worker.RunAsync((s, _) => s.ThreadId, Generous, default);
        await worker.RunAsync((_, _) => new byte[2 << 20].Length, Generous, default);
        Assert.True(await collected.WaitAsync(Generous));
        await worker.RunAsync((_, _) => 1, Generous, default);
        lock (collections) Assert.Equal([thread], collections);
    }

    [Fact]
    public async Task DisposeRefusesNewJobs()
    {
        var worker = new UiaWorker<Session>(() => new Session());
        Assert.Equal(1, await worker.RunAsync((_, _) => 1, Generous, default));
        worker.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.RunAsync((_, _) => 2, Generous, default));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}

public class HangBlockListTests
{
    [Fact]
    public void BlocksTheWindowAndItsProcessForTheDuration()
    {
        var time = new ManualTime();
        var blocks = new HangBlockList(time, TimeSpan.FromSeconds(90));
        blocks.Block(window: 0x1234, processId: 42);

        Assert.True(blocks.IsBlocked(0x1234, 0));
        Assert.True(blocks.IsBlocked(0x9999, 42));
        Assert.False(blocks.IsBlocked(0x9999, 7));

        time.Now += TimeSpan.FromSeconds(89);
        Assert.True(blocks.IsBlocked(0x1234, 42));
        time.Now += TimeSpan.FromSeconds(2);
        Assert.False(blocks.IsBlocked(0x1234, 42));
    }

    [Fact]
    public void AnUnknownProcessBlocksOnlyTheWindow()
    {
        var blocks = new HangBlockList(new ManualTime(), HangBlockList.DefaultDuration);
        blocks.Block(window: 0x1234, processId: 0);
        Assert.True(blocks.IsBlocked(0x1234, 0));
        Assert.False(blocks.IsBlocked(0x9999, 0));
        Assert.Equal(TimeSpan.FromSeconds(90), HangBlockList.DefaultDuration);
    }
}
