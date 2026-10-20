using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Tests;

public class AnnotationLayoutTests
{
    [Theory]
    [InlineData(2560, 1600, 0.675, 1728, 1080)]
    [InlineData(1000, 3, 0.1, 100, 1)]
    public void OutputSizeTruncatesLikePil(int width, int height, double scale, int outWidth, int outHeight) =>
        Assert.Equal((outWidth, outHeight), AnnotationLayout.OutputSize(width, height, scale));

    [Fact]
    public void BoxesAreClampedToTheImageThenScaled()
    {
        Assert.Equal(new PixelRect(0, 5, 50, 25), AnnotationLayout.ScaleBox(new PixelRect(-20, 10, 100, 50), 200, 100, 0.5, 100, 50));
        Assert.Null(AnnotationLayout.ScaleBox(new PixelRect(250, 10, 300, 50), 200, 100, 0.5, 100, 50));
        // A box that scales to nothing keeps one pixel.
        Assert.Equal(new PixelRect(10, 10, 11, 11), AnnotationLayout.ScaleBox(new PixelRect(100, 100, 101, 101), 200, 200, 0.1, 20, 20));
    }

    [Fact]
    public void LabelsSitAboveTheBoxRightAlignedOrBelowWhenThereIsNoRoom()
    {
        Assert.Equal(new PixelRect(80, 22, 100, 38), AnnotationLayout.PlaceLabel(new PixelRect(40, 40, 100, 80), 20, 16, 400, 300));
        Assert.Equal(new PixelRect(80, 82, 100, 98), AnnotationLayout.PlaceLabel(new PixelRect(40, 5, 100, 80), 20, 16, 400, 300));
        Assert.Equal(new PixelRect(0, 22, 20, 38), AnnotationLayout.PlaceLabel(new PixelRect(0, 40, 10, 80), 20, 16, 400, 300));
        Assert.Equal(new PixelRect(380, 284, 400, 300), AnnotationLayout.PlaceLabel(new PixelRect(390, 5, 400, 299), 20, 16, 400, 300));
    }

    [Fact]
    public void LightPaletteColoursGetDarkText()
    {
        Assert.True(AnnotationLayout.UseDarkText((210, 245, 60)));
        Assert.True(AnnotationLayout.UseDarkText((70, 240, 240)));
        Assert.False(AnnotationLayout.UseDarkText((230, 25, 75)));
        Assert.False(AnnotationLayout.UseDarkText((0, 0, 128)));
    }

    [Fact]
    public void GridLinesSplitTheImageEvenly()
    {
        Assert.Equal([33, 66], AnnotationLayout.GridPositions(3, 100));
        Assert.Empty(AnnotationLayout.GridPositions(1, 100));
        Assert.Equal(4, AnnotationLayout.GridPositions(int.MaxValue, 5).Count);
    }
}

public class Win2DRendererValidationTests
{
    [Fact]
    public async Task ABufferOfTheWrongLengthIsRejectedBeforeAnyWin2DCall()
    {
        var renderer = new Win2DRenderer(() => throw new InvalidOperationException("must not initialise"), new GpuGate());
        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderPngAsync(new CapturedImage(10, 10, new byte[399]), RenderOptions.Plain(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => renderer.RenderPngAsync(new CapturedImage(1, 1, new byte[4]), RenderOptions.Plain(1.5)));
    }
}

public class Win2DRendererBoundsTests
{
    private static readonly CapturedImage Pixel = new(1, 1, new byte[4]);

    [Fact]
    public async Task TheRuntimeIsInitialisedBeforeTheRender()
    {
        var calls = new List<string>();
        var renderer = new Win2DRenderer(() => calls.Add("runtime"), new GpuGate(), (_, _) =>
        {
            calls.Add("render");
            return Task.FromResult(new byte[] { 1 });
        });
        Assert.Equal([1], await renderer.RenderPngAsync(Pixel, RenderOptions.Plain(1)));
        Assert.Equal(["runtime", "render"], calls);
    }

    [Fact]
    public async Task ARenderThatBlocksItsThreadIsAbandonedAtTheDeadline()
    {
        using var release = new ManualResetEventSlim();
        var renderer = new Win2DRenderer(() => { }, new GpuGate(), (_, _) =>
        {
            release.Wait(); // a WinRT call that blocks instead of failing (NativeAOT)
            return Task.FromResult(Array.Empty<byte>());
        }) { Timeout = TimeSpan.FromMilliseconds(100) };
        try
        {
            var error = await Assert.ThrowsAsync<DesktopException>(() => renderer.RenderPngAsync(Pixel, RenderOptions.Plain(1)));
            Assert.Equal("Image rendering failed: Win2D did not finish within 0.1 s.", error.Message);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ATimedOutRenderHoldsTheGateUntilItEnds()
    {
        var finish = new TaskCompletionSource<byte[]>();
        var first = true;
        var renderer = new Win2DRenderer(() => { }, new GpuGate(), (_, _) =>
        {
            if (!Interlocked.Exchange(ref first, false)) return Task.FromResult(new byte[] { 7 });
            return finish.Task;
        }) { Timeout = TimeSpan.FromMilliseconds(100) };
        await Assert.ThrowsAsync<DesktopException>(() => renderer.RenderPngAsync(Pixel, RenderOptions.Plain(1)));
        var error = await Assert.ThrowsAsync<DesktopException>(() => renderer.RenderPngAsync(Pixel, RenderOptions.Plain(1)));
        Assert.Equal("Image rendering failed: an earlier graphics call is still running after its deadline; try again later.", error.Message);
        finish.SetResult([1]);
        await Task.Yield();
        Assert.Equal([7], await renderer.RenderPngAsync(Pixel, RenderOptions.Plain(1)));
    }

    [Fact]
    public async Task TheRenderBudgetIncludesTheWaitAtTheGate()
    {
        var gate = new GpuGate();
        var renderer = new Win2DRenderer(() => { }, gate, (_, _) => new TaskCompletionSource<byte[]>().Task)
        { Timeout = TimeSpan.FromMilliseconds(600) };
        var held = await gate.EnterAsync("Other", TimeSpan.FromSeconds(5), default);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var call = Assert.ThrowsAsync<DesktopException>(() => renderer.RenderPngAsync(Pixel, RenderOptions.Plain(1)));
        await Task.Delay(400);
        held.Dispose();
        await call;
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(900), $"the render took {clock.Elapsed}");
    }

    [Fact]
    public async Task ACancelledCallersRenderNeverTakesTheSlotOrStartsWork()
    {
        var gate = new GpuGate();
        var started = false;
        var renderer = new Win2DRenderer(() => { }, gate, (_, _) =>
        {
            started = true;
            return Task.FromResult(new byte[] { 1 });
        });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderPngAsync(Pixel, RenderOptions.Plain(1), cancelled.Token));
        Assert.False(started);
        (await gate.EnterAsync("Next", TimeSpan.FromMilliseconds(50), default)).Dispose();

        // A caller that gives up while waiting at the gate leaves without rendering either.
        using var cancel = new CancellationTokenSource();
        var held = await gate.EnterAsync("Other", TimeSpan.FromSeconds(5), default);
        var waiting = renderer.RenderPngAsync(Pixel, RenderOptions.Plain(1), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();
        Assert.False(started);
    }

    /// <summary>
    /// Fires each deadline timer as soon as it is created, after <paramref name="beforeFire"/>, then runs
    /// <paramref name="afterFire"/>: the deadline passes first and the work completes just after it.
    /// </summary>
    private sealed class ExpireAtOnce(Action beforeFire, Action afterFire) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            beforeFire();
            callback(state);
            afterFire();
            return System.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    [Fact]
    public async Task ARenderThatFinishesJustAfterTheDeadlineReturnsItsResult()
    {
        using var started = new ManualResetEventSlim();
        var finished = new TaskCompletionSource<byte[]>();
        var renderer = new Win2DRenderer(() => { }, new GpuGate(), (_, _) =>
        {
            started.Set();
            return finished.Task;
        })
        {
            Time = new ExpireAtOnce(
                () => Assert.True(started.Wait(TimeSpan.FromSeconds(10))),
                () =>
                {
                    finished.SetResult([7]);
                    Thread.Sleep(200); // lets Task.Run's proxy see the finished render
                }),
        };
        Assert.Equal([7], await renderer.RenderPngAsync(Pixel, RenderOptions.Plain(1)));
    }

    [Fact]
    public void AShortPngReadIsAnError()
    {
        var error = Assert.Throws<DesktopException>(() => Win2DRenderer.CheckRead(new byte[3], 5));
        Assert.Equal("Image rendering failed: the PNG stream returned 3 of 5 bytes.", error.Message);
        Assert.Equal([1, 2], Win2DRenderer.CheckRead([1, 2], 2));
    }

    [Fact]
    public async Task ALostDeviceIsReportedInFixedEnglish()
    {
        var renderer = new Win2DRenderer(() => { }, new GpuGate(), (_, _) => throw new COMException("lost", unchecked((int)0x887A0005)));
        var error = await Assert.ThrowsAsync<DesktopException>(() => renderer.RenderPngAsync(Pixel, RenderOptions.Plain(1)));
        Assert.Equal("Image rendering failed: the graphics device was lost (HRESULT 0x887A0005)", error.Message);
    }
}

public class DeviceLostRetryTests
{
    private sealed class FakeDevice
    {
        public bool Lost { get; set; }
    }

    private static readonly int DeviceRemoved = unchecked((int)0x887A0005);

    private static bool IsLost(FakeDevice device, Exception error) => device.Lost && error.HResult == DeviceRemoved;

    [Fact]
    public async Task ALostDeviceIsReplacedAndTheWorkRunsOnceMore()
    {
        var devices = new List<FakeDevice>();
        var result = await DeviceLostRetry.RunAsync(
            () => { devices.Add(new FakeDevice()); return devices[^1]; },
            IsLost,
            device =>
            {
                if (devices.Count == 1)
                {
                    device.Lost = true;
                    throw new COMException("removed", DeviceRemoved);
                }
                return Task.FromResult(devices.IndexOf(device));
            });
        Assert.Equal(1, result);
        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public async Task OtherFailuresAreNotRetried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<COMException>(() => DeviceLostRetry.RunAsync(
            () => new FakeDevice(),
            IsLost,
            device =>
            {
                attempts++;
                // The device-lost code from a device that is not lost (Win2D's IsDeviceLost says no).
                return Task.FromException<int>(new COMException("removed", DeviceRemoved));
            }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ASecondLossIsThrown()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<COMException>(() => DeviceLostRetry.RunAsync(
            () => new FakeDevice(),
            IsLost,
            device =>
            {
                attempts++;
                device.Lost = true;
                return Task.FromException<int>(new COMException("removed", DeviceRemoved));
            }));
        Assert.Equal(2, attempts);
    }
}

[Trait("Category", "Desktop")]
public class Win2DRendererTests
{
    private static readonly (byte R, byte G, byte B, byte A) Gray = (0x80, 0x80, 0x80, 0xFF);

    [Fact]
    public async Task EncodesAPngOfTheSameSize()
    {
        var png = await new Win2DRenderer().RenderPngAsync(Solid(120, 90), RenderOptions.Plain(1.0));
        var decoded = await PngTestDecoder.DecodeAsync(png);
        Assert.Equal((120, 90), (decoded.Width, decoded.Height));
        Assert.Equal(Gray, decoded.Pixel(60, 45));
    }

    [Fact]
    public async Task ScalesDownWithTruncatedSize()
    {
        var png = await new Win2DRenderer().RenderPngAsync(Solid(201, 101), RenderOptions.Plain(0.5));
        var decoded = await PngTestDecoder.DecodeAsync(png);
        Assert.Equal((100, 50), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task TheBoxCoversItsFirstAndLastPixelsAndNothingBeyond()
    {
        var annotations = new[] { new Annotation(new PixelRect(40, 50, 140, 120), "0") };
        var decoded = await PngTestDecoder.DecodeAsync(await new Win2DRenderer().RenderPngAsync(Solid(200, 150), new RenderOptions(1.0, annotations)));
        var colour = Opaque(Win2DRenderer.Palette[0]);
        Assert.Equal(colour, decoded.Pixel(40, 90));
        Assert.Equal(colour, decoded.Pixel(139, 90));
        Assert.Equal(Gray, decoded.Pixel(140, 90));
        Assert.Equal(colour, decoded.Pixel(90, 119));
        Assert.Equal(Gray, decoded.Pixel(90, 120));
        Assert.Equal(Gray, decoded.Pixel(90, 90));
    }

    [Fact]
    public async Task TheLabelIsRightAlignedJustAboveTheBox()
    {
        var annotations = new[] { new Annotation(new PixelRect(40, 60, 140, 120), "7") };
        var decoded = await PngTestDecoder.DecodeAsync(await new Win2DRenderer().RenderPngAsync(Solid(200, 150), new RenderOptions(1.0, annotations)));
        var colour = Opaque(Win2DRenderer.Palette[0]);
        Assert.Equal(colour, decoded.Pixel(139, 57)); // the label's bottom-right corner, 2 px above the box
        Assert.Equal(Gray, decoded.Pixel(139, 58));
    }

    [Fact]
    public async Task BoxesScaleWithTheImage()
    {
        var annotations = new[] { new Annotation(new PixelRect(80, 100, 280, 240), "0") };
        var decoded = await PngTestDecoder.DecodeAsync(await new Win2DRenderer().RenderPngAsync(Solid(400, 300), new RenderOptions(0.5, annotations)));
        Assert.Equal(Opaque(Win2DRenderer.Palette[0]), decoded.Pixel(40, 85));
    }

    [Fact]
    public async Task DrawsTheCursorMarkerAndTheGrid()
    {
        var options = new RenderOptions(1.0, [], new PixelPoint(100, 100), new GridLines(2, 2));
        var decoded = await PngTestDecoder.DecodeAsync(await new Win2DRenderer().RenderPngAsync(Solid(300, 200), options));
        var red = decoded.Pixel(100, 92); // on the vertical bar of the cross
        Assert.True(red.R > 200 && red.G < 60 && red.B < 60, $"not red: {red}");
        var grid = decoded.Pixel(150, 20); // the vertical grid line at 300 * 1 // 2
        Assert.NotEqual(Gray, grid);
    }

    private static CapturedImage Solid(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = Gray.B;
            bgra[i + 1] = Gray.G;
            bgra[i + 2] = Gray.R;
            bgra[i + 3] = Gray.A;
        }
        return new CapturedImage(width, height, bgra);
    }

    private static (byte R, byte G, byte B, byte A) Opaque((byte R, byte G, byte B) c) => (c.R, c.G, c.B, 0xFF);
}
