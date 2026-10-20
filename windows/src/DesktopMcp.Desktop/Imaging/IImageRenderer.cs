namespace DesktopMcp.Desktop.Imaging;

/// <summary>A point in image pixels of the captured (unscaled) image.</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>Reference grid: <c>Columns</c> by <c>Rows</c> cells (upstream grid_lines, desktop/service.py:1427-1435).</summary>
public readonly record struct GridLines(int Columns, int Rows);

/// <summary>
/// What to draw over a capture. Annotation boxes and the cursor are in captured-image pixels; the renderer scales them
/// with the image (Decisions 12).
/// </summary>
public sealed record RenderOptions(double Scale, IReadOnlyList<Annotation> Annotations, PixelPoint? Cursor = null, GridLines? Grid = null)
{
    public static RenderOptions Plain(double scale) => new(scale, []);
}

public interface IImageRenderer
{
    /// <summary>
    /// Scales the image by <see cref="RenderOptions.Scale"/> (0 &lt; scale ≤ 1, output size truncated like PIL's resize),
    /// draws the grid, the annotations and the cursor marker, and encodes PNG. Initialises the Windows App SDK runtime.
    /// A cancelled token ends the wait for the shared graphics slot, and a render whose caller gave up never starts.
    /// </summary>
    Task<byte[]> RenderPngAsync(CapturedImage image, RenderOptions options, CancellationToken cancellationToken = default);
}
