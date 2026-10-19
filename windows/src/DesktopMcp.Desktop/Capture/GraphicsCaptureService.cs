using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Imaging;
using DesktopMcp.Desktop.Interop;
using Microsoft.Graphics.Canvas;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace DesktopMcp.Desktop.Capture;

/// <summary>
/// Screen capture through Windows.Graphics.Capture (one frame per call). Frames are read through Win2D's
/// shared device, so call <see cref="AppRuntime.EnsureInitialized"/> first.
/// </summary>
public sealed class GraphicsCaptureService : IScreenCapture
{
    private const string CaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private const string CaptureSessionClass = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);

    public static bool IsSupported { get; } = GraphicsCaptureSession.IsSupported();

    /// <summary>How the last capture item was created: "DisplayId" or "MonitorInterop" (diagnostics for the P0 probe).</summary>
    public static string? LastItemCreationPath { get; private set; }

    /// <summary>Whether the last capture could turn the yellow capture border off (null before the first capture).</summary>
    public static bool? LastBorderSuppressed { get; private set; }

    public async Task<CapturedImage> CapturePrimaryMonitorAsync(CancellationToken cancellationToken)
    {
        var monitor = Native.MonitorFromPoint(default, Native.MonitorDefaultToPrimary);
        var item = CreateItemForMonitor(monitor);
        var device = CanvasDevice.GetSharedDevice();

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        LastBorderSuppressed = TrySuppressBorder(session);

        var firstFrame = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (sender, _) =>
        {
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame is not null && !firstFrame.TrySetResult(frame)) frame.Dispose();
            }
            catch (Exception e)
            {
                firstFrame.TrySetException(e);
            }
        };
        session.StartCapture();

        using var frame = await firstFrame.Task.WaitAsync(FrameTimeout, cancellationToken).ConfigureAwait(false);
        using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
        var width = (int)bitmap.SizeInPixels.Width;
        var height = (int)bitmap.SizeInPixels.Height;
        return new CapturedImage(width, height, Pack(bitmap.GetPixelBytes(), width, height));
    }

    /// <summary>Returns rows packed at Width * 4 bytes, repacking when the source rows are padded.</summary>
    private static byte[] Pack(byte[] pixels, int width, int height)
    {
        var rowBytes = width * 4;
        if (pixels.Length == rowBytes * height)
            return pixels;
        if (height == 0 || pixels.Length % height != 0 || pixels.Length / height < rowBytes)
            throw new InvalidOperationException($"Unexpected frame buffer of {pixels.Length} bytes for {width}x{height}.");
        var stride = pixels.Length / height;
        var packed = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * stride, packed, y * rowBytes, rowBytes);
        return packed;
    }

    private static bool TrySuppressBorder(GraphicsCaptureSession session)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
            || !ApiInformation.IsPropertyPresent(CaptureSessionClass, nameof(GraphicsCaptureSession.IsBorderRequired)))
            return false;
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

    private static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
            && ApiInformation.IsMethodPresent(CaptureItemClass, nameof(GraphicsCaptureItem.TryCreateFromDisplayId)))
        {
            var displayId = Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor(monitor);
            var item = GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId { Value = displayId.Value });
            if (item is not null)
            {
                LastItemCreationPath = "DisplayId";
                return item;
            }
        }
        LastItemCreationPath = "MonitorInterop";
        return CreateItemThroughInterop(monitor);
    }

    private static unsafe GraphicsCaptureItem CreateItemThroughInterop(nint monitor)
    {
        Marshal.ThrowExceptionForHR(Native.WindowsCreateString(CaptureItemClass, (uint)CaptureItemClass.Length, out var hstring));
        nint interop = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(hstring, IidGraphicsCaptureItemInterop, out interop));
            // IGraphicsCaptureItemInterop: IUnknown (3 slots), CreateForWindow (3), CreateForMonitor (4).
            var createForMonitor = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(void***)interop)[4];
            var iid = IidGraphicsCaptureItem;
            Marshal.ThrowExceptionForHR(createForMonitor(interop, monitor, &iid, &item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (interop != 0) Marshal.Release(interop);
            Native.WindowsDeleteString(hstring);
        }
    }
}
