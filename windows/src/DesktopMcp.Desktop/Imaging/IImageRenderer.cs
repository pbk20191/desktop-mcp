namespace DesktopMcp.Desktop.Imaging;

public interface IImageRenderer
{
    /// <summary>Scales the image by <paramref name="scale"/> (0 &lt; scale ≤ 1), draws the annotations and encodes PNG.</summary>
    Task<byte[]> RenderPngAsync(CapturedImage image, double scale, IReadOnlyList<Annotation> annotations);
}
