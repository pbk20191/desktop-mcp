using System.Runtime.InteropServices.WindowsRuntime;
using DesktopMcp.Desktop.Errors;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using Windows.UI;

namespace DesktopMcp.Desktop.Imaging;

/// <summary>
/// Scales, annotates and PNG-encodes screenshots with Win2D (spec §6). The entry point initialises the Windows App SDK
/// runtime itself, which also installs the static Win2D activation (P0 follow-up). The Win2D work runs on the thread pool
/// under <see cref="Timeout"/> and is retried once on a new shared device when the device is lost (<see cref="DeviceLostRetry"/>).
/// </summary>
public sealed class Win2DRenderer : IImageRenderer
{
    private const float Dpi = 96;
    private const float StrokeWidth = 2;
    private const int LabelPadding = 2;
    private const float CursorRadius = 15;
    private const string Operation = "Image rendering";

    /// <summary>Box colours; annotation i uses Palette[i % Palette.Count].</summary>
    public static IReadOnlyList<(byte R, byte G, byte B)> Palette { get; } =
    [
        (230, 25, 75), (60, 180, 75), (0, 130, 200), (245, 130, 48), (145, 30, 180), (70, 240, 240),
        (240, 50, 230), (210, 245, 60), (0, 128, 128), (170, 110, 40), (128, 0, 0), (0, 0, 128),
    ];

    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color Black = Color.FromArgb(0xFF, 0, 0, 0);
    private static readonly Color Red = Color.FromArgb(0xFF, 0xFF, 0, 0);
    private static readonly Color Grid = Color.FromArgb(128, 200, 200, 200);

    private readonly Action _ensureRuntime;
    private readonly Func<CapturedImage, RenderOptions, Task<byte[]>> _render;

    public Win2DRenderer() : this(AppRuntime.EnsureInitialized)
    {
    }

    internal Win2DRenderer(Action ensureRuntime) : this(ensureRuntime, RenderOnSharedDeviceAsync)
    {
    }

    /// <summary>Tests replace the Win2D work (everything after the runtime is initialised) with a fake.</summary>
    internal Win2DRenderer(Action ensureRuntime, Func<CapturedImage, RenderOptions, Task<byte[]>> render)
    {
        _ensureRuntime = ensureRuntime;
        _render = render;
    }

    /// <summary>
    /// How long the Win2D work (drawing and PNG encoding, both attempts) may take. Under NativeAOT a WinRT call can block
    /// instead of failing; past the deadline the work is abandoned on its thread-pool thread, not stopped.
    /// </summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<byte[]> RenderPngAsync(CapturedImage image, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        if (!(options.Scale > 0 && options.Scale <= 1))
            throw new ArgumentOutOfRangeException(nameof(options), options.Scale, "Scale must be in (0, 1].");
        if (image.Width <= 0 || image.Height <= 0 || image.Bgra.Length != (long)image.Width * image.Height * 4)
            throw new ArgumentException($"A {image.Width}x{image.Height} image needs {(long)image.Width * image.Height * 4} bytes of BGRA, not {image.Bgra.Length}.", nameof(image));
        Task<byte[]>? work = null;
        try
        {
            _ensureRuntime();
            // Task.Run: the synchronous Win2D calls (device, bitmap, drawing) are bounded too, not only the awaited ones.
            work = Task.Run(() => _render(image, options));
            return await work.WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (TimeoutException) when (work is { IsCompleted: false })
        {
            throw new DesktopException($"{Operation} failed: Win2D did not finish within {Timeout.TotalSeconds:0.###} s.");
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate(Operation, e);
        }
    }

    private static Task<byte[]> RenderOnSharedDeviceAsync(CapturedImage image, RenderOptions options) =>
        DeviceLostRetry.RunAsync(device => RenderAsync(device, image, options));

    private static async Task<byte[]> RenderAsync(CanvasDevice device, CapturedImage image, RenderOptions options)
    {
        var (outWidth, outHeight) = AnnotationLayout.OutputSize(image.Width, image.Height, options.Scale);
        using var bitmap = CanvasBitmap.CreateFromBytes(
            device, image.Bgra, image.Width, image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, Dpi, CanvasAlphaMode.Premultiplied);
        using var target = new CanvasRenderTarget(device, outWidth, outHeight, Dpi);
        using (var session = target.CreateDrawingSession())
        {
            session.DrawImage(bitmap, new Rect(0, 0, outWidth, outHeight), new Rect(0, 0, image.Width, image.Height), 1f, CanvasImageInterpolation.HighQualityCubic);
            session.Antialiasing = CanvasAntialiasing.Aliased;
            session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            using var format = new CanvasTextFormat { FontFamily = "Arial", FontSize = 12 };
            if (options.Grid is { } grid) DrawGrid(session, grid, outWidth, outHeight);
            for (var i = 0; i < options.Annotations.Count; i++)
                DrawAnnotation(session, format, options.Annotations[i], Palette[i % Palette.Count], image, options.Scale, outWidth, outHeight);
            if (options.Cursor is { } cursor) DrawCursor(session, format, cursor, options.Scale, outWidth, outHeight);
        }

        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        var size = (uint)stream.Size;
        var buffer = await stream.ReadAsync(new Windows.Storage.Streams.Buffer(size), size, InputStreamOptions.None);
        return buffer.ToArray();
    }

    private static void DrawGrid(CanvasDrawingSession session, GridLines grid, int width, int height)
    {
        foreach (var x in AnnotationLayout.GridPositions(grid.Columns, width)) session.DrawLine(x + 0.5f, 0, x + 0.5f, height, Grid, 1);
        foreach (var y in AnnotationLayout.GridPositions(grid.Rows, height)) session.DrawLine(0, y + 0.5f, width, y + 0.5f, Grid, 1);
    }

    private static void DrawAnnotation(CanvasDrawingSession session, CanvasTextFormat format, Annotation annotation,
        (byte R, byte G, byte B) palette, CapturedImage image, double scale, int outWidth, int outHeight)
    {
        if (AnnotationLayout.ScaleBox(annotation.Box, image.Width, image.Height, scale, outWidth, outHeight) is not { } box) return;
        var colour = Color.FromArgb(0xFF, palette.R, palette.G, palette.B);
        if (box.Width >= 2 * StrokeWidth && box.Height >= 2 * StrokeWidth)
        {
            // The stroke is centred on the path: inset by half its width so it covers the box's first and last pixels.
            const float inset = StrokeWidth / 2;
            session.DrawRectangle(box.Left + inset, box.Top + inset, box.Width - StrokeWidth, box.Height - StrokeWidth, colour, StrokeWidth);
        }
        else
        {
            session.FillRectangle(box.Left, box.Top, box.Width, box.Height, colour);
        }
        using var label = new CanvasTextLayout(session, annotation.Label, format, float.MaxValue, float.MaxValue);
        var (width, height) = LabelSize(label);
        var place = AnnotationLayout.PlaceLabel(box, width, height, outWidth, outHeight);
        DrawLabel(session, label, place, colour, AnnotationLayout.UseDarkText(palette) ? Black : White);
    }

    // upstream desktop/service.py:1470-1484: a red ring with a cross and a "CURSOR" label, at a fixed size in the output.
    private static void DrawCursor(CanvasDrawingSession session, CanvasTextFormat format, PixelPoint cursor, double scale, int outWidth, int outHeight)
    {
        var x = (float)Math.Round(cursor.X * scale);
        var y = (float)Math.Round(cursor.Y * scale);
        session.DrawEllipse(x, y, CursorRadius, CursorRadius, Red, 3);
        session.DrawLine(x - CursorRadius, y, x + CursorRadius, y, Red, 2);
        session.DrawLine(x, y - CursorRadius, x, y + CursorRadius, Red, 2);
        using var label = new CanvasTextLayout(session, "CURSOR", format, float.MaxValue, float.MaxValue);
        var (width, height) = LabelSize(label);
        var left = Math.Clamp((int)(x + CursorRadius), 0, Math.Max(0, outWidth - width));
        var top = Math.Clamp((int)(y - CursorRadius), 0, Math.Max(0, outHeight - height));
        DrawLabel(session, label, new PixelRect(left, top, left + width, top + height), Red, White);
    }

    // upstream desktop/service.py:1408-1420: the text bounds plus 2 px on each side.
    private static (int Width, int Height) LabelSize(CanvasTextLayout layout) =>
        ((int)Math.Ceiling(layout.LayoutBounds.Width) + 2 * LabelPadding, (int)Math.Ceiling(layout.LayoutBounds.Height) + 2 * LabelPadding);

    private static void DrawLabel(CanvasDrawingSession session, CanvasTextLayout layout, PixelRect place, Color background, Color foreground)
    {
        session.FillRectangle(place.Left, place.Top, place.Width, place.Height, background);
        session.DrawTextLayout(layout, place.Left + LabelPadding, place.Top + LabelPadding, foreground);
    }
}
