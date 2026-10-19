using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using Windows.UI;

namespace DesktopMcp.Desktop.Imaging;

/// <summary>Scales, annotates and PNG-encodes screenshots with Win2D. Call <see cref="AppRuntime.EnsureInitialized"/> first.</summary>
public sealed class Win2DRenderer : IImageRenderer
{
    private const float Dpi = 96;
    private const float StrokeWidth = 2;
    private const float LabelPadX = 2;
    private const float LabelPadY = 1;

    /// <summary>Box colours; annotation i uses Palette[i % Palette.Count].</summary>
    public static IReadOnlyList<(byte R, byte G, byte B)> Palette { get; } =
    [
        (230, 25, 75), (60, 180, 75), (0, 130, 200), (245, 130, 48), (145, 30, 180), (70, 240, 240),
        (240, 50, 230), (210, 245, 60), (0, 128, 128), (170, 110, 40), (128, 0, 0), (0, 0, 128),
    ];

    public async Task<byte[]> RenderPngAsync(CapturedImage image, double scale, IReadOnlyList<Annotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(annotations);
        if (!(scale > 0 && scale <= 1))
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be in (0, 1].");

        var outWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
        var outHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
        var device = CanvasDevice.GetSharedDevice();

        using var bitmap = CanvasBitmap.CreateFromBytes(
            device, image.Bgra, image.Width, image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, Dpi, CanvasAlphaMode.Premultiplied);
        using var target = new CanvasRenderTarget(device, outWidth, outHeight, Dpi);
        using (var session = target.CreateDrawingSession())
        {
            session.DrawImage(bitmap, new Rect(0, 0, outWidth, outHeight), new Rect(0, 0, image.Width, image.Height), 1f, CanvasImageInterpolation.HighQualityCubic);
            if (annotations.Count > 0)
                DrawAnnotations(session, annotations, scale);
        }

        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        var png = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(png.AsBuffer(), (uint)png.Length, InputStreamOptions.None);
        return png;
    }

    private static void DrawAnnotations(CanvasDrawingSession session, IReadOnlyList<Annotation> annotations, double scale)
    {
        session.Antialiasing = CanvasAntialiasing.Aliased;
        session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        using var format = new CanvasTextFormat { FontFamily = "Arial", FontSize = 12 };
        var white = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        for (var i = 0; i < annotations.Count; i++)
        {
            var (r, g, b) = Palette[i % Palette.Count];
            var colour = Color.FromArgb(0xFF, r, g, b);
            var box = annotations[i].Box;
            var left = (float)Math.Round(box.Left * scale);
            var top = (float)Math.Round(box.Top * scale);
            var right = (float)Math.Round(box.Right * scale);
            var bottom = (float)Math.Round(box.Bottom * scale);

            // The stroke is centred on the path: inset by half its width so it covers the box's first and last pixels.
            const float inset = StrokeWidth / 2;
            session.DrawRectangle(left + inset, top + inset, right - left - StrokeWidth, bottom - top - StrokeWidth, colour, StrokeWidth);

            using var layout = new CanvasTextLayout(session, annotations[i].Label, format, float.MaxValue, float.MaxValue);
            var labelWidth = (float)Math.Ceiling(layout.LayoutBounds.Width) + 2 * LabelPadX;
            var labelHeight = (float)Math.Ceiling(layout.LayoutBounds.Height) + 2 * LabelPadY;
            var labelLeft = right - labelWidth;
            var labelTop = top - labelHeight >= 0 ? top - labelHeight : bottom;
            session.FillRectangle(labelLeft, labelTop, labelWidth, labelHeight, colour);
            session.DrawTextLayout(layout, labelLeft + LabelPadX, labelTop + LabelPadY, white);
        }
    }
}
