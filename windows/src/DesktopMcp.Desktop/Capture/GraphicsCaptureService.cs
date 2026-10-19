using DesktopMcp.Desktop.Imaging;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Windowing;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace DesktopMcp.Desktop.Capture;

/// <summary>
/// Screen capture through Windows.Graphics.Capture (one frame per call). Frames are read through Win2D's
/// shared device, so call <see cref="AppRuntime.EnsureInitialized"/> first.
/// </summary>
public sealed class GraphicsCaptureService : IScreenCapture
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);

    public static bool IsSupported { get; } = GraphicsCaptureSession.IsSupported();

    /// <summary>Whether the last capture could turn the yellow capture border off (null before the first capture).</summary>
    public static bool? LastBorderSuppressed { get; private set; }

    public async Task<CapturedImage> CapturePrimaryMonitorAsync(CancellationToken cancellationToken)
    {
        var item = CreateItemForPrimaryMonitor();
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

    private static GraphicsCaptureItem CreateItemForPrimaryMonitor()
    {
        var displayId = DisplayArea.Primary.DisplayId;
        return GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId { Value = displayId.Value })
            ?? throw new InvalidOperationException($"Windows.Graphics.Capture could not create an item for the primary display (id {displayId.Value}).");
    }
}
