namespace DesktopMcp.Desktop.Imaging;

/// <summary>
/// The geometry of the annotations (upstream desktop/service.py:1385-1486), kept apart from Win2D so it is unit-tested:
/// output size, box clamping and scaling, label placement and label text colour.
/// </summary>
internal static class AnnotationLayout
{
    /// <summary>Space between a label and its box, as upstream (label_y = top - label_height - 2).</summary>
    public const int LabelGap = 2;

    /// <summary>upstream desktop/service.py:258-262: int(width * scale), at least one pixel.</summary>
    public static (int Width, int Height) OutputSize(int width, int height, double scale) =>
        (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));

    /// <summary>
    /// Clamps a box to the captured image (upstream desktop/service.py:1441-1453) and scales it into the output image.
    /// Null when nothing of it is on the image.
    /// </summary>
    public static PixelRect? ScaleBox(PixelRect box, int imageWidth, int imageHeight, double scale, int outWidth, int outHeight)
    {
        var left = Math.Clamp(box.Left, 0, imageWidth);
        var top = Math.Clamp(box.Top, 0, imageHeight);
        var right = Math.Clamp(box.Right, 0, imageWidth);
        var bottom = Math.Clamp(box.Bottom, 0, imageHeight);
        if (right <= left || bottom <= top) return null;
        var scaledLeft = Math.Clamp((int)Math.Round(left * scale), 0, outWidth - 1);
        var scaledTop = Math.Clamp((int)Math.Round(top * scale), 0, outHeight - 1);
        var scaledRight = Math.Clamp((int)Math.Round(right * scale), scaledLeft + 1, outWidth);
        var scaledBottom = Math.Clamp((int)Math.Round(bottom * scale), scaledTop + 1, outHeight);
        return new PixelRect(scaledLeft, scaledTop, scaledRight, scaledBottom);
    }

    /// <summary>
    /// upstream desktop/service.py:1457-1463 and 1412-1415: right-aligned above the box, below it when there is no room
    /// above, then kept inside the image.
    /// </summary>
    public static PixelRect PlaceLabel(PixelRect box, int labelWidth, int labelHeight, int outWidth, int outHeight)
    {
        var x = box.Right - labelWidth;
        var y = box.Top - labelHeight - LabelGap;
        if (y < 0) y = box.Bottom + LabelGap;
        x = Math.Clamp(x, 0, Math.Max(0, outWidth - labelWidth));
        y = Math.Clamp(y, 0, Math.Max(0, outHeight - labelHeight));
        return new PixelRect(x, y, x + labelWidth, y + labelHeight);
    }

    /// <summary>Dark label text on light palette colours (yellow-green, cyan), white text on the others.</summary>
    public static bool UseDarkText((byte R, byte G, byte B) colour) => 0.299 * colour.R + 0.587 * colour.G + 0.114 * colour.B > 150;

    /// <summary>upstream desktop/service.py:1430-1435: line i of n at size * i // n; counts above the size are capped.</summary>
    public static IReadOnlyList<int> GridPositions(int count, int size)
    {
        count = Math.Min(count, size);
        var positions = new List<int>();
        for (var i = 1; i < count; i++) positions.Add((int)((long)size * i / count));
        return positions;
    }
}
