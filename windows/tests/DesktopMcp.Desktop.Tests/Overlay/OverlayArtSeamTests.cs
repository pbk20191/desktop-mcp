using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;
using DesktopMcp.Desktop.Overlay;

namespace DesktopMcp.Desktop.Tests.Overlay;

// Default run: the Win2D drawing, the gate and the clock are fakes; nothing touches the runtime or a device.
public class OverlayArtSeamTests
{
    private static readonly (byte R, byte G, byte B) Blue = OverlayLayout.IndicatorBlue;
    private static readonly OverlayBitmap One = new(1, 1, new byte[4]);

    private static OverlayArt Art(Action ensure, GpuGate gate, Func<LayerArt, Task<OverlayBitmap>>? render = null, TimeProvider? time = null) =>
        new(ensure, gate, render ?? (_ => Task.FromResult(One))) { Time = time ?? TimeProvider.System };

    [Fact]
    public async Task EdgesAndTheRingNeverInitialiseTheRuntime()
    {
        var calls = 0;
        var art = Art(() => { calls++; throw new InvalidOperationException("no runtime"); }, new GpuGate());
        foreach (var kind in new[] { ArtKind.EdgeTop, ArtKind.EdgeBottom, ArtKind.EdgeLeft, ArtKind.EdgeRight })
            Assert.Equal(60 * 60 * 4, (await art.RenderAsync(new LayerArt(kind, 60, 60, Blue, 56), default)).Pixels.Length);
        Assert.Equal(88 * 88 * 4, (await art.RenderAsync(new LayerArt(ArtKind.CursorRing, 88, 88, Blue), default)).Pixels.Length);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("Notice")]
    [InlineData("NoticeGlow")]
    [InlineData("FlashEdge")]
    public async Task Win2DKindsInitialiseTheRuntimeAndRenderUnderTheGate(string kindName)
    {
        var calls = 0;
        var art = Art(() => calls++, new GpuGate());
        Assert.Same(One, await art.RenderAsync(new LayerArt(Enum.Parse<ArtKind>(kindName), 10, 10, Blue), default));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("FlashEdge", "Capture flash failed: ")]
    [InlineData("Notice", "Control indicator failed: ")]
    public async Task AGateErrorNamesTheOverlay(string kindName, string prefix)
    {
        var gate = new GpuGate();
        using var held = await gate.EnterAsync("Screen capture", TimeSpan.FromSeconds(5), default);
        var art = new OverlayArt(() => { }, gate, _ => Task.FromResult(One)) { GateWait = TimeSpan.FromMilliseconds(20) };
        var error = await Assert.ThrowsAsync<DesktopException>(() => art.RenderAsync(new LayerArt(Enum.Parse<ArtKind>(kindName), 10, 10, Blue), default));
        Assert.StartsWith(prefix, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledCallerStopsWaitingForTheGateAndRendersNothing()
    {
        var gate = new GpuGate();
        var rendered = 0;
        using var held = await gate.EnterAsync("Screen capture", TimeSpan.FromSeconds(5), default);
        var art = Art(() => { }, gate, _ => { rendered++; return Task.FromResult(One); });
        using var cts = new CancellationTokenSource();
        var call = art.RenderAsync(new LayerArt(ArtKind.Notice, 10, 10, Blue), cts.Token);
        Assert.False(call.IsCompleted);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, rendered);
    }

    [Fact]
    public async Task ARenderPastItsTimeoutKeepsTheSlotUntilTheStalledWorkEnds()
    {
        var gate = new GpuGate();
        var time = new ManualTime();
        var started = new TaskCompletionSource();
        var stalled = new TaskCompletionSource<OverlayBitmap>();
        var art = Art(() => { }, gate, _ => { started.SetResult(); return stalled.Task; }, time);
        var call = art.RenderAsync(new LayerArt(ArtKind.Notice, 10, 10, Blue), default);
        await started.Task;
        for (var i = 0; i < 200 && !call.IsCompleted; i++)
        {
            time.Now += TimeSpan.FromSeconds(11);
            await Task.Delay(5);
        }
        var error = await Assert.ThrowsAsync<DesktopException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("did not finish within 10 s", error.Message);
        await Assert.ThrowsAsync<DesktopException>(() => gate.EnterAsync("Next", TimeSpan.FromMilliseconds(50), default));
        stalled.SetResult(One);
        await Task.Delay(50);
        (await gate.EnterAsync("Next", TimeSpan.FromSeconds(5), default)).Dispose();
    }
}
