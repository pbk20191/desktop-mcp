using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Tests;

public class GpuGateTests
{
    [Fact]
    public async Task ASecondCallerWaitsForTheFirst()
    {
        var gate = new GpuGate();
        var first = await gate.EnterAsync("Render", TimeSpan.FromSeconds(5), default);
        var second = gate.EnterAsync("Render", TimeSpan.FromSeconds(5), default);
        Assert.False(second.IsCompleted);
        first.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task AnAbandonedCallMakesTheNextFailAtOnce()
    {
        var gate = new GpuGate();
        var running = new TaskCompletionSource();
        (await gate.EnterAsync("Screen capture", TimeSpan.FromSeconds(5), default)).AbandonUntil(running.Task);
        var error = await Assert.ThrowsAsync<DesktopException>(() => gate.EnterAsync("Image rendering", TimeSpan.FromSeconds(5), default));
        Assert.Equal("Image rendering failed: an earlier graphics call is still running after its deadline; try again later.", error.Message);
        running.SetResult();
        (await gate.EnterAsync("Image rendering", TimeSpan.FromSeconds(5), default)).Dispose();
    }

    [Fact]
    public async Task WaitingPastTheLimitFails()
    {
        var gate = new GpuGate();
        using var held = await gate.EnterAsync("Render", TimeSpan.FromSeconds(5), default);
        Assert.Equal("Render failed: the graphics device stayed busy for 0.05 s.",
            (await Assert.ThrowsAsync<DesktopException>(() => gate.EnterAsync("Render", TimeSpan.FromMilliseconds(50), default))).Message);
    }

    [Fact]
    public async Task ACancelledWaitLeavesTheSlotUntouched()
    {
        var gate = new GpuGate();
        var held = await gate.EnterAsync("Render", TimeSpan.FromSeconds(5), default);
        using var cancel = new CancellationTokenSource();
        var waiting = gate.EnterAsync("Render", TimeSpan.FromSeconds(5), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();
        (await gate.EnterAsync("Render", TimeSpan.FromSeconds(1), default)).Dispose();
    }

    [Fact]
    public async Task DisposingAnAbandonedLeaseDoesNotFreeTheSlot()
    {
        var gate = new GpuGate();
        var running = new TaskCompletionSource();
        var lease = await gate.EnterAsync("Render", TimeSpan.FromSeconds(5), default);
        lease.AbandonUntil(running.Task);
        lease.Dispose();
        await Assert.ThrowsAsync<DesktopException>(() => gate.EnterAsync("Render", TimeSpan.FromMilliseconds(50), default));
        running.SetResult();
        (await gate.EnterAsync("Render", TimeSpan.FromSeconds(5), default)).Dispose();
    }

    [Fact]
    public async Task WaitersAlreadyQueuedFailAtOnceWhenTheHolderIsAbandoned()
    {
        var gate = new GpuGate();
        var running = new TaskCompletionSource();
        var holder = await gate.EnterAsync("Screen capture", TimeSpan.FromSeconds(5), default);
        var waiting = gate.EnterAsync("Image rendering", TimeSpan.FromSeconds(30), default);
        Assert.False(waiting.IsCompleted);
        holder.AbandonUntil(running.Task);
        var error = await Assert.ThrowsAsync<DesktopException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Image rendering failed: an earlier graphics call is still running after its deadline; try again later.", error.Message);
        running.SetResult();
        await Task.Yield();
        (await gate.EnterAsync("Image rendering", TimeSpan.FromSeconds(5), default)).Dispose();
    }
}
