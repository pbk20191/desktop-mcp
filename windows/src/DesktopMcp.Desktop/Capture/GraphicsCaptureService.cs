using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace DesktopMcp.Desktop.Capture;

/// <summary>
/// Screen capture through Windows.Graphics.Capture, one display item per monitor, composited for areas that span
/// monitors (spec §6). <see cref="CaptureAsync"/> initialises the Windows App SDK runtime itself (P2 follow-up), reads
/// frames through Win2D's shared device (retried once on a lost device, <see cref="DeviceLostRetry"/>) and bounds every
/// WinRT step: the borderless-access request by <see cref="AccessTimeout"/>, and each monitor (item, frame pool, session
/// start, frame and pixel read) by <see cref="MonitorTimeout"/>, inside which the frame itself has <see cref="FrameTimeout"/>.
/// Under NativeAOT a WinRT call can block instead of failing; past a deadline the work is abandoned, not stopped.
/// Monitors are captured one at a time and each is cropped to the area before its pixels are copied out, so memory peaks
/// at the output image plus one cropped frame.
/// </summary>
public sealed class GraphicsCaptureService : IScreenCapture
{
    private const string Operation = "Screen capture";

    /// <summary>Captures the part of one monitor inside an area: the placed origin and pixels, or null when nothing overlaps.</summary>
    internal delegate Task<(ScreenRect Origin, CapturedImage Frame)?> MonitorCapture(ScreenRect monitor, ScreenRect area, CancellationToken cancellationToken);

    private readonly Action _ensureRuntime;
    private readonly Func<Task<string>> _requestAccess;
    private readonly MonitorCapture _captureMonitor;
    // Once per process, like BorderlessAccess. A request that times out keeps running on the thread pool and is not
    // repeated; a capture made while it is still pending (or after it was refused) may show the border.
    private static int _accessRequested;

    public GraphicsCaptureService() : this(AppRuntime.EnsureInitialized)
    {
    }

    /// <summary>Tests replace the runtime initialisation, the access request and the per-monitor capture with fakes.</summary>
    internal GraphicsCaptureService(Action ensureRuntime, Func<Task<string>>? requestAccess = null, MonitorCapture? captureMonitor = null)
    {
        _ensureRuntime = ensureRuntime;
        _requestAccess = requestAccess ?? RequestBorderlessAccessAsync;
        _captureMonitor = captureMonitor ?? CaptureMonitorAsync;
    }

    /// <summary>How long one monitor may take to deliver its first frame after the session starts.</summary>
    internal TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long the one-time borderless access request may take.</summary>
    internal TimeSpan AccessTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long one monitor may take in all: creating its item, frame pool and session, starting the session, the frame
    /// wait and the pixel read, on both device attempts.
    /// </summary>
    internal TimeSpan MonitorTimeout { get; init; } = TimeSpan.FromSeconds(6);

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <summary>Diagnostics: whether the last capture could turn the capture border off (null before the first).</summary>
    internal static bool? LastBorderSuppressed { get; private set; }

    /// <summary>Tests: forget that the borderless access was requested, so the next capture requests it again.</summary>
    internal static void ResetAccessRequest()
    {
        Volatile.Write(ref _accessRequested, 0);
        BorderlessAccess = null;
    }

    /// <summary>Diagnostics: the result of GraphicsCaptureAccess.RequestAccessAsync(Borderless), or why it failed.</summary>
    internal static string? BorderlessAccess { get; private set; }

    public async Task<CapturedImage> CaptureAsync(ScreenRect area, IReadOnlyList<ScreenRect> monitors, CancellationToken cancellationToken)
    {
        if (area.Width <= 0 || area.Height <= 0) throw new ArgumentException($"The capture area {area.ToXyxy()} is empty.", nameof(area));
        ArgumentNullException.ThrowIfNull(monitors);
        try
        {
            _ensureRuntime();
            await RequestBorderlessAccessOnceAsync().ConfigureAwait(false);
            var overlapping = monitors.Where(m => ScreenRect.Clip(m, area) is not null).ToArray();
            CapturedImage? output = null;
            foreach (var monitor in overlapping)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await CaptureMonitorBoundedAsync(monitor, area, cancellationToken).ConfigureAwait(false) is not { } part) continue;
                var (origin, frame) = part;
                if (overlapping.Length == 1 && FrameCompositor.Covers(area, origin, frame)) return frame;
                output ??= FrameCompositor.Blank(area);
                FrameCompositor.Place(output, area, origin, frame);
            }
            return output ?? FrameCompositor.Blank(area);
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate(Operation, e);
        }
    }

    // Task.Run: the synchronous WinRT calls (item, pool, session, StartCapture, pixel read) are bounded too.
    private async Task<(ScreenRect Origin, CapturedImage Frame)?> CaptureMonitorBoundedAsync(ScreenRect monitor, ScreenRect area, CancellationToken cancellationToken)
    {
        var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = Task.Run(() => _captureMonitor(monitor, area, abandon.Token), CancellationToken.None);
        // CancelAsync, not Cancel: Cancel runs the token's callbacks on this thread, and an abandoned job's continuation
        // (FrameArrived -=, session and pool Dispose: unbounded WinRT calls) would then run here before the error is thrown.
        Task? cancelling = null;
        try
        {
            return await work.WaitAsync(MonitorTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            cancelling = abandon.CancelAsync();
            throw new DesktopException(
                $"{Operation} failed: Windows.Graphics.Capture did not deliver the monitor {monitor.ToXyxy()} within {MonitorTimeout.TotalSeconds:0.###} s.");
        }
        catch (OperationCanceledException) when (!work.IsCompleted)
        {
            cancelling = abandon.CancelAsync();
            throw;
        }
        finally
        {
            // An abandoned capture still holds the token; release it once the work and the cancellation end (the work disposes
            // its own frame and session), and observe the failure so it does not surface as an unobserved task exception.
            if (work.IsCompleted && cancelling is null) abandon.Dispose();
            else _ = Task.WhenAll(work, cancelling ?? Task.CompletedTask).ContinueWith(static (task, state) =>
            {
                _ = task.Exception;
                ((CancellationTokenSource)state!).Dispose();
            }, abandon, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private Task<(ScreenRect Origin, CapturedImage Frame)?> CaptureMonitorAsync(ScreenRect monitor, ScreenRect area, CancellationToken cancellationToken)
    {
        var item = CreateItem(monitor);
        return DeviceLostRetry.RunAsync(device => CaptureFrameAsync(item, monitor, area, device, cancellationToken));
    }

    private async Task<(ScreenRect Origin, CapturedImage Frame)?> CaptureFrameAsync(
        GraphicsCaptureItem item, ScreenRect monitor, ScreenRect area, CanvasDevice device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        LastBorderSuppressed = TrySuppressBorder(session);

        var first = new FirstFrame<Direct3D11CaptureFrame>();
        TypedEventHandler<Direct3D11CaptureFramePool, object> onFrameArrived = (sender, _) => first.Offer(sender.TryGetNextFrame);
        pool.FrameArrived += onFrameArrived;
        try
        {
            session.StartCapture();
            Direct3D11CaptureFrame frame;
            try
            {
                frame = await first.WaitAsync(FrameTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new DesktopException(
                    $"{Operation} failed: no frame arrived from the monitor {monitor.ToXyxy()} within {FrameTimeout.TotalSeconds:0.###} s.");
            }
            using (frame)
            // The frame pool uses the device on its own thread (the device is D3D multithread protected, which the Desktop
            // test TheSharedDeviceIsMultithreadProtected pins); Win2D's lock keeps our own D2D use of it serial.
            using (device.Lock())
            {
                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
                var size = bitmap.SizeInPixels;
                if (Crop(monitor, (int)size.Width, (int)size.Height, area) is not { } crop) return null;
                var bytes = Pack(bitmap.GetPixelBytes(crop.Left - monitor.Left, crop.Top - monitor.Top, crop.Width, crop.Height), crop.Width, crop.Height);
                return (crop, new CapturedImage(crop.Width, crop.Height, MakeOpaque(bytes)));
            }
        }
        finally
        {
            pool.FrameArrived -= onFrameArrived;
        }
    }

    /// <summary>
    /// The part of a frame (its top-left at the monitor's) to read: inside the frame, the monitor and the area, or null.
    /// The frame may differ from the monitor rectangle after a mode change between the two reads: a smaller frame is read
    /// as far as it goes, and a larger one is cut at the monitor so it cannot overwrite a neighbouring monitor.
    /// </summary>
    internal static ScreenRect? Crop(ScreenRect monitor, int frameWidth, int frameHeight, ScreenRect area)
    {
        var shown = new ScreenRect(monitor.Left, monitor.Top, monitor.Left + frameWidth, monitor.Top + frameHeight);
        return ScreenRect.Clip(shown, monitor) is { } onMonitor ? ScreenRect.Clip(onMonitor, area) : null;
    }

    /// <summary>Sets every alpha byte to 255: screenshots are opaque RGB upstream, so a translucent frame must not make a translucent PNG.</summary>
    internal static byte[] MakeOpaque(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 0xFF;
        return bgra;
    }

    /// <summary>Returns rows packed at Width * 4 bytes, repacking when the source rows are padded.</summary>
    internal static byte[] Pack(byte[] pixels, int width, int height)
    {
        var rowBytes = width * 4;
        if (pixels.Length == rowBytes * height)
            return pixels;
        if (height == 0 || pixels.Length % height != 0 || pixels.Length / height < rowBytes)
            throw new DesktopException($"{Operation} failed: unexpected frame buffer of {pixels.Length} bytes for {width}x{height}.");
        var stride = pixels.Length / height;
        var packed = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * stride, packed, y * rowBytes, rowBytes);
        return packed;
    }

    // spec §6: requested once, where available; unpackaged processes may be refused, which only keeps the border.
    private async Task RequestBorderlessAccessOnceAsync()
    {
        if (Interlocked.Exchange(ref _accessRequested, 1) != 0) return;
        try
        {
            // Task.Run: the synchronous part of the WinRT call is bounded too.
            BorderlessAccess = await Task.Run(_requestAccess).WaitAsync(AccessTimeout).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            BorderlessAccess = $"{e.GetType().Name} 0x{e.HResult:X8}";
        }
    }

    private static async Task<string> RequestBorderlessAccessAsync() =>
        (await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)).ToString();

    private static bool TrySuppressBorder(GraphicsCaptureSession session)
    {
        try
        {
            session.IsBorderRequired = false;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Unpackaged apps may lack the graphicsCaptureWithoutBorder capability; the border then stays.
            return false;
        }
    }

    // spec §6: the capture item comes from the monitor's display id.
    private static GraphicsCaptureItem CreateItem(ScreenRect bounds)
    {
        var rect = new RECT { left = bounds.Left, top = bounds.Top, right = bounds.Right, bottom = bounds.Bottom };
        var monitor = PInvoke.MonitorFromRect(rect, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
        if (monitor.IsNull) throw new DesktopException($"{Operation} failed: no monitor at {bounds.ToXyxy()}.");
        var displayId = Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor((nint)monitor);
        return GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId { Value = displayId.Value })
            ?? throw new DesktopException($"{Operation} failed: Windows.Graphics.Capture could not open display {displayId.Value}.");
    }
}
