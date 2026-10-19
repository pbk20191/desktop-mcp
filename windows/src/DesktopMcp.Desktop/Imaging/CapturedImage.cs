namespace DesktopMcp.Desktop.Imaging;

/// <summary>A captured frame: 32-bit BGRA pixels, rows packed (stride = Width * 4).</summary>
public sealed record CapturedImage(int Width, int Height, byte[] Bgra);

/// <summary>A rectangle in image pixels; Right and Bottom are exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>A numbered box drawn over a screenshot.</summary>
public sealed record Annotation(PixelRect Box, string Label);
