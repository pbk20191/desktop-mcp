using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Capture;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;
using Microsoft.Graphics.Canvas;

namespace DesktopMcp.Desktop.Tests;

public class FrameCompositorTests
{
    private static CapturedImage Solid(int width, int height, byte value) =>
        new(width, height, Enumerable.Repeat(value, width * height * 4).ToArray());

    [Fact]
    public void ASingleFrameCoveringTheAreaIsReturnedAsIs()
    {
        var frame = Solid(4, 2, 7);
        Assert.Same(frame, FrameCompositor.Compose(new ScreenRect(0, 0, 4, 2), [(new ScreenRect(0, 0, 4, 2), frame)]));
    }

    [Fact]
    public void FramesArePlacedAtTheirOffsetAndGapsAreOpaqueBlack()
    {
        // Two 2x2 monitors side by side at x = -2 and x = 2; the area spans both and the 2-pixel gap between them.
        var image = FrameCompositor.Compose(new ScreenRect(-2, 0, 4, 2),
            [(new ScreenRect(-2, 0, 0, 2), Solid(2, 2, 10)), (new ScreenRect(2, 0, 4, 2), Solid(2, 2, 20))]);
        Assert.Equal((6, 2), (image.Width, image.Height));
        byte[] Pixel(int x, int y) => image.Bgra.AsSpan((y * 6 + x) * 4, 4).ToArray();
        Assert.Equal([10, 10, 10, 10], Pixel(1, 1));
        Assert.Equal([0, 0, 0, 255], Pixel(2, 0));
        Assert.Equal([20, 20, 20, 20], Pixel(5, 0));
    }

    [Fact]
    public void ARegionCropsTheFrame()
    {
        var frame = new CapturedImage(3, 1, [1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3]);
        var image = FrameCompositor.Compose(new ScreenRect(11, 0, 13, 1), [(new ScreenRect(10, 0, 13, 1), frame)]);
        Assert.Equal([2, 2, 2, 2, 3, 3, 3, 3], image.Bgra);
    }
}

public class FirstFrameTests
{
    private sealed class Frame : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task TheFirstFrameIsReturnedAndLaterOnesAreDisposed()
    {
        var first = new FirstFrame<Frame>();
        var (a, b) = (new Frame(), new Frame());
        first.Offer(() => a);
        first.Offer(() => b);
        Assert.Same(a, await first.WaitAsync(TimeSpan.FromSeconds(5), default));
        Assert.False(a.Disposed);
        Assert.True(b.Disposed);
    }

    [Fact]
    public async Task AFrameThatArrivesAfterTheTimeoutIsDisposed()
    {
        var first = new FirstFrame<Frame>();
        await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromMilliseconds(10), default));
        var late = new Frame();
        first.Offer(() => late);
        Assert.True(late.Disposed);
    }

    [Fact]
    public async Task AFrameThatArrivesAfterCancellationIsDisposed()
    {
        var first = new FirstFrame<Frame>();
        using var cancel = new CancellationTokenSource();
        var wait = first.WaitAsync(TimeSpan.FromSeconds(30), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        var late = new Frame();
        first.Offer(() => late);
        Assert.True(late.Disposed);
    }

    [Fact]
    public async Task AFailingReadFailsTheWaitAndANullFrameIsIgnored()
    {
        var first = new FirstFrame<Frame>();
        first.Offer(() => null);
        first.Offer(() => throw new InvalidOperationException("read failed"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(5), default));
        Assert.Equal("read failed", error.Message);
    }
}

public class GraphicsCaptureServiceTests
{
    private static CapturedImage Solid(int width, int height, byte value) =>
        new(width, height, Enumerable.Repeat(value, width * height * 4).ToArray());

    private static Task<string> Allowed() => Task.FromResult("Allowed");

    /// <summary>A fake monitor capture: returns the part of the monitor inside the area, filled with the monitor's Left.</summary>
    private static Task<(ScreenRect, CapturedImage)?> CropFake(ScreenRect monitor, ScreenRect area, CancellationToken _) =>
        Task.FromResult<(ScreenRect, CapturedImage)?>(ScreenRect.Clip(monitor, area) is { } part
            ? (part, Solid(part.Width, part.Height, (byte)monitor.Left)) : null);

    [Fact]
    public void PackRemovesRowPadding()
    {
        // 2x2 pixels in rows padded to 12 bytes.
        byte[] padded = [1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 3, 3, 3, 3, 4, 4, 4, 4, 0, 0, 0, 0];
        Assert.Equal([1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4], GraphicsCaptureService.Pack(padded, 2, 2));
        var packed = new byte[16];
        Assert.Same(packed, GraphicsCaptureService.Pack(packed, 2, 2));
        Assert.Throws<DesktopException>(() => GraphicsCaptureService.Pack(new byte[10], 2, 2));
    }

    [Fact]
    public async Task TheRuntimeIsInitialisedFirstAndItsInstallHintIsNotWrapped()
    {
        var service = new GraphicsCaptureService(() => throw new InvalidOperationException("install the runtime"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CaptureAsync(new ScreenRect(0, 0, 10, 10), [new ScreenRect(0, 0, 10, 10)], default));
        Assert.Equal("install the runtime", error.Message);
    }

    [Fact]
    public async Task AnEmptyAreaIsRejected() =>
        await Assert.ThrowsAsync<ArgumentException>(() => new GraphicsCaptureService(() => { }).CaptureAsync(new ScreenRect(5, 5, 5, 9), [], default));

    [Fact]
    public async Task OnlyOverlappingMonitorsAreCapturedEachCroppedToTheArea()
    {
        var requests = new List<(ScreenRect, ScreenRect)>();
        var service = new GraphicsCaptureService(() => { }, Allowed, (monitor, area, token) =>
        {
            requests.Add((monitor, area));
            return CropFake(monitor, area, token);
        });
        var area = new ScreenRect(8, 0, 12, 1);
        var image = await service.CaptureAsync(area, [new ScreenRect(0, 0, 10, 1), new ScreenRect(10, 0, 20, 1), new ScreenRect(30, 0, 40, 1)], default);

        Assert.Equal([(new ScreenRect(0, 0, 10, 1), area), (new ScreenRect(10, 0, 20, 1), area)], requests);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 10, 10, 10, 10, 10, 10, 10, 10], image.Bgra);
    }

    [Fact]
    public async Task AFrameThatCoversTheWholeAreaIsReturnedWithoutACopy()
    {
        var frame = Solid(4, 2, 9);
        var service = new GraphicsCaptureService(() => { }, Allowed,
            (_, area, _) => Task.FromResult<(ScreenRect, CapturedImage)?>((area, frame)));
        Assert.Same(frame, await service.CaptureAsync(new ScreenRect(1, 1, 5, 3), [new ScreenRect(0, 0, 10, 10)], default));
    }

    [Fact]
    public async Task AnAreaNoMonitorCoversIsBlack()
    {
        var service = new GraphicsCaptureService(() => { }, Allowed, CropFake);
        var image = await service.CaptureAsync(new ScreenRect(0, 0, 1, 1), [new ScreenRect(5, 5, 6, 6)], default);
        Assert.Equal([0, 0, 0, 255], image.Bgra);
    }

    [Fact]
    public async Task AMonitorCaptureThatHangsFailsAtItsDeadlineAndIsCancelled()
    {
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new GraphicsCaptureService(() => { }, Allowed, (_, _, token) =>
        {
            token.Register(() => abandoned.TrySetResult());
            return new TaskCompletionSource<(ScreenRect, CapturedImage)?>().Task;
        })
        { MonitorTimeout = TimeSpan.FromMilliseconds(50) };

        var error = await Assert.ThrowsAsync<DesktopException>(() =>
            service.CaptureAsync(new ScreenRect(0, 0, 1, 1), [new ScreenRect(0, 0, 1, 1)], default));
        Assert.Equal("Screen capture failed: Windows.Graphics.Capture did not deliver the monitor (0,0,1,1) within 0.05 s.", error.Message);
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ATimedOutMonitorCaptureHoldsTheGateUntilItEnds()
    {
        var finish = new TaskCompletionSource<(ScreenRect, CapturedImage)?>();
        var first = true;
        var service = new GraphicsCaptureService(() => { }, Allowed, (_, _, _) =>
            Interlocked.Exchange(ref first, false) ? finish.Task : Task.FromResult<(ScreenRect, CapturedImage)?>(null))
        { MonitorTimeout = TimeSpan.FromMilliseconds(50) };
        var area = new ScreenRect(0, 0, 1, 1);
        await Assert.ThrowsAsync<DesktopException>(() => service.CaptureAsync(area, [area], default));
        var error = await Assert.ThrowsAsync<DesktopException>(() => service.CaptureAsync(area, [area], default));
        Assert.Equal("Screen capture failed: an earlier graphics call is still running after its deadline; try again later.", error.Message);
        finish.SetResult(null);
        Assert.NotNull(await service.CaptureAsync(area, [area], default));
    }

    [Fact]
    public async Task AnAbandonedCaptureCleansUpOffTheCallersThread()
    {
        // The abandoned job's cleanup (here a token callback standing in for FrameArrived -= and the session's Dispose)
        // blocks; the caller must still get its error at the deadline, not after the cleanup.
        using var release = new ManualResetEventSlim();
        var service = new GraphicsCaptureService(() => { }, Allowed, (_, _, token) =>
        {
            token.Register(() => release.Wait(TimeSpan.FromSeconds(10)));
            return new TaskCompletionSource<(ScreenRect, CapturedImage)?>().Task;
        })
        { MonitorTimeout = TimeSpan.FromMilliseconds(50) };
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<DesktopException>(() =>
                service.CaptureAsync(new ScreenRect(0, 0, 1, 1), [new ScreenRect(0, 0, 1, 1)], default));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the capture failed after {clock.Elapsed}");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void AFrameIsCroppedToItsMonitorAndTheArea()
    {
        var monitor = new ScreenRect(0, 0, 100, 50);
        // A frame larger than its monitor (a mode change between the reads) must not spill onto the neighbour at x = 100.
        Assert.Equal(new ScreenRect(90, 0, 100, 50), GraphicsCaptureService.Crop(monitor, 200, 80, new ScreenRect(90, 0, 300, 100)));
        // A smaller frame is read as far as it goes.
        Assert.Equal(new ScreenRect(10, 10, 60, 40), GraphicsCaptureService.Crop(monitor, 60, 40, new ScreenRect(10, 10, 300, 300)));
        Assert.Null(GraphicsCaptureService.Crop(monitor, 200, 80, new ScreenRect(100, 0, 300, 100)));
    }

    [Fact]
    public void FramesAreMadeOpaque()
    {
        byte[] pixels = [1, 2, 3, 0, 4, 5, 6, 128];
        Assert.Same(pixels, GraphicsCaptureService.MakeOpaque(pixels));
        Assert.Equal([1, 2, 3, 255, 4, 5, 6, 255], pixels);
    }

    [Fact]
    public async Task ACallThatBlocksSynchronouslyIsBoundedToo()
    {
        using var release = new ManualResetEventSlim();
        var service = new GraphicsCaptureService(() => { }, Allowed, (_, _, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return Task.FromResult<(ScreenRect, CapturedImage)?>(null);
        })
        { MonitorTimeout = TimeSpan.FromMilliseconds(50) };
        try
        {
            await Assert.ThrowsAsync<DesktopException>(() =>
                service.CaptureAsync(new ScreenRect(0, 0, 1, 1), [new ScreenRect(0, 0, 1, 1)], default).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task CancellationIsNotWrappedAndReachesTheMonitorCapture()
    {
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var service = new GraphicsCaptureService(() => { }, Allowed, (_, _, token) =>
        {
            token.Register(() => abandoned.TrySetResult());
            cancel.Cancel();
            return new TaskCompletionSource<(ScreenRect, CapturedImage)?>().Task;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CaptureAsync(new ScreenRect(0, 0, 1, 1), [new ScreenRect(0, 0, 1, 1)], cancel.Token));
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AComFailureBecomesADesktopException()
    {
        var service = new GraphicsCaptureService(() => { }, Allowed,
            (_, _, _) => throw new COMException("boom", unchecked((int)0x887A0005)));
        var error = await Assert.ThrowsAsync<DesktopException>(() =>
            service.CaptureAsync(new ScreenRect(0, 0, 1, 1), [new ScreenRect(0, 0, 1, 1)], default));
        Assert.Equal("Screen capture failed: the graphics device was lost (HRESULT 0x887A0005)", error.Message);
    }

    [Fact]
    public async Task TheBorderlessAccessRequestIsMadeOncePerProcessAndBoundedWhenItHangs()
    {
        GraphicsCaptureService.ResetAccessRequest();
        var requests = 0;
        var service = new GraphicsCaptureService(() => { }, () =>
        {
            requests++;
            return new TaskCompletionSource<string>().Task;
        }, CropFake)
        { AccessTimeout = TimeSpan.FromMilliseconds(50) };

        var area = new ScreenRect(0, 0, 1, 1);
        await service.CaptureAsync(area, [area], default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("TimeoutException", GraphicsCaptureService.BorderlessAccess);
        await service.CaptureAsync(area, [area], default).WaitAsync(TimeSpan.FromSeconds(5));
        // Once per process: another instance does not ask again.
        await new GraphicsCaptureService(() => { }, () =>
        {
            requests++;
            return Allowed();
        }, CropFake).CaptureAsync(area, [area], default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, requests);
    }
}

[Trait("Category", "Desktop")]
public class GraphicsCaptureServiceDesktopTests
{
    public GraphicsCaptureServiceDesktopTests() => TestDpi.EnsurePerMonitorV2();

    [Fact]
    public async Task CapturesThePrimaryMonitor()
    {
        Assert.True(GraphicsCaptureService.IsSupported);
        var primary = new Win32Displays().GetDisplays().Single(d => d.Primary).Bounds;
        var image = await new GraphicsCaptureService().CaptureAsync(primary, [primary], CancellationToken.None);

        Assert.Equal((primary.Width, primary.Height), (image.Width, image.Height));
        Assert.Equal(image.Width * image.Height * 4, image.Bgra.Length);
        var nonBlack = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4 * 97)
            if (image.Bgra[i] != 0 || image.Bgra[i + 1] != 0 || image.Bgra[i + 2] != 0) nonBlack++;
        Assert.True(nonBlack > 100, "the capture is entirely black");
    }

    [Fact]
    public async Task CapturesTheWholeVirtualScreenAndARegion()
    {
        var displays = new Win32Displays();
        var monitors = displays.GetDisplays().Select(d => d.Bounds).ToArray();
        var screen = displays.GetVirtualScreen();
        var service = new GraphicsCaptureService();

        var whole = await service.CaptureAsync(screen, monitors, CancellationToken.None);
        Assert.Equal((screen.Width, screen.Height), (whole.Width, whole.Height));

        var region = new ScreenRect(screen.Left + 10, screen.Top + 20, screen.Left + 210, screen.Top + 120);
        var part = await service.CaptureAsync(region, monitors, CancellationToken.None);
        Assert.Equal((200, 100), (part.Width, part.Height));
        Assert.NotNull(GraphicsCaptureService.BorderlessAccess);
    }

    /// <summary>
    /// P0 follow-up "confirm multithread protection on the shared device": the frame pool (CreateFreeThreaded) uses the
    /// D3D device on its own thread while Win2D reads the frame on ours, so the device must serialise its immediate context.
    /// </summary>
    [Fact]
    public unsafe void TheSharedDeviceIsMultithreadProtected()
    {
        AppRuntime.EnsureInitialized();
        var device = CanvasDevice.GetSharedDevice();
        var inspectable = ((WinRT.IWinRTObject)device).NativeObject.ThisPtr;
        var accessIid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"); // IDirect3DDxgiInterfaceAccess
        var deviceIid = new Guid("DB6F6DDB-AC77-4E88-8253-819DF9BBF140"); // ID3D11Device
        var multithreadIid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0"); // ID3D10Multithread
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in accessIid, out var access));
        nint d3d = 0, multithread = 0;
        try
        {
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)access)[3];
            Marshal.ThrowExceptionForHR(getInterface(access, &deviceIid, &d3d));
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in multithreadIid, out multithread));
            var getProtected = (delegate* unmanaged[Stdcall]<nint, int>)(*(void***)multithread)[6];
            Assert.NotEqual(0, getProtected(multithread));
        }
        finally
        {
            if (multithread != 0) Marshal.Release(multithread);
            if (d3d != 0) Marshal.Release(d3d);
            Marshal.Release(access);
        }
    }
}
