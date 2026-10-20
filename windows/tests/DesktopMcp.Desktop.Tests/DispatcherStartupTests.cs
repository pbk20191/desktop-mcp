using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Tests;

public class DispatcherStartupTests
{
    [Fact]
    public void ReturnsTheIdOfTheThreadThatRanTheWork()
    {
        var shutdowns = 0;
        var id = DispatcherQueueThread.WaitForThreadId(work => { work(); return true; }, () => { shutdowns++; return Task.CompletedTask; },
            TimeSpan.FromSeconds(5));
        Assert.Equal((Environment.CurrentManagedThreadId, 0), (id, shutdowns));
    }

    [Fact]
    public void ARejectedWorkItemShutsTheQueueDown()
    {
        var shutdowns = 0;
        var error = Assert.Throws<DesktopException>(() => DispatcherQueueThread.WaitForThreadId(
            _ => false, () => { shutdowns++; return Task.CompletedTask; }, TimeSpan.FromSeconds(5)));
        Assert.Equal(("The new DispatcherQueue rejected work.", 1), (error.Message, shutdowns));
    }

    [Fact]
    public void AThreadThatNeverRunsTimesOutAndShutsTheQueueDown()
    {
        var shutdowns = 0;
        var error = Assert.Throws<DesktopException>(() => DispatcherQueueThread.WaitForThreadId(
            _ => true, () => { shutdowns++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(("The dispatcher thread did not start within 0.1 s.", 1), (error.Message, shutdowns));
    }

    [Fact]
    public void AFailingShutdownDoesNotHideTheStartupFailure() =>
        Assert.Equal("The new DispatcherQueue rejected work.",
            Assert.Throws<DesktopException>(() => DispatcherQueueThread.WaitForThreadId(
                _ => false, () => Task.FromException(new InvalidOperationException("dead")), TimeSpan.FromSeconds(1))).Message);

    [Fact]
    public void AShutdownThatThrowsSynchronouslyDoesNotHideTheStartupFailure() =>
        Assert.Equal("The new DispatcherQueue rejected work.",
            Assert.Throws<DesktopException>(() => DispatcherQueueThread.WaitForThreadId(
                _ => false, () => throw new InvalidOperationException("dead"), TimeSpan.FromSeconds(1))).Message);
}
