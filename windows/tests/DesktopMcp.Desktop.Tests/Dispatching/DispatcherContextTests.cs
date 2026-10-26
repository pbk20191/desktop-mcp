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
    public void ARejectedPostIsDroppedReportedAndDoesNotThrow()
    {
        var reported = new List<Exception>();
        var ran = 0;
        new DispatcherQueueSynchronizationContext(_ => false, () => false, reported.Add).Post(_ => ran++, null);
        Assert.Equal(0, ran);
        Assert.IsType<ObjectDisposedException>(Assert.Single(reported));
        new DispatcherQueueSynchronizationContext(_ => false, () => false).Post(_ => { }, null);
        new DispatcherQueueSynchronizationContext(_ => throw new COMException("closed"), () => false, reported.Add).Post(_ => { }, null);
        Assert.Equal("closed", reported[1].Message);
        new DispatcherQueueSynchronizationContext(_ => false, () => false, _ => throw new Exception("sink failed")).Post(_ => { }, null);
    }

    [Fact]
    public void APostedCallbackThatThrowsIsReportedNotThrownIntoTheQueue()
    {
        var queued = new List<Action>();
        var reported = new List<Exception>();
        var context = new DispatcherQueueSynchronizationContext(work => { queued.Add(work); return true; }, () => false, reported.Add);
        context.Post(_ => throw new InvalidOperationException("async void"), null);
        queued.Single()();
        Assert.Equal("async void", Assert.Single(reported).Message);
    }

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
    public async Task AnAlreadyCancelledCallerStartsNothing()
    {
        var starts = 0;
        var host = new DispatcherHost(() => { starts++; return new FakeDispatcher(); });
        var cancelled = host.GetAsync(new CancellationToken(canceled: true));
        Assert.True(cancelled.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(0, starts);
        Assert.False(host.IsStarted);
    }

    [Fact]
    public async Task TheHostReportsTheDispatchersUnhandledExceptionsToItsSink()
    {
        var dispatcher = new FakeDispatcher();
        var host = new DispatcherHost(() => dispatcher);
        var reported = new List<Exception>();
        await host.GetAsync(default);
        host.Report = reported.Add;  // a sink set after the start still receives the reports
        using var timer = await dispatcher.StartTimerAsync(TimeSpan.FromMilliseconds(30), () => throw new InvalidOperationException("tick"));
        dispatcher.TickAll();
        Assert.Equal("tick", Assert.Single(reported).Message);
        host.Report = _ => throw new Exception("sink failed");
        dispatcher.TickAll();
        timer.Dispose();
        dispatcher.TickAll();
        Assert.Single(reported);
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
