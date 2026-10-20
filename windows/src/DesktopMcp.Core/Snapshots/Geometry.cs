namespace DesktopMcp.Core.Snapshots;

/// <summary>A point in physical virtual-desktop pixels.</summary>
public readonly record struct ScreenPoint(int X, int Y)
{
    /// <summary>upstream tree/views.py:253-254 Center.to_string: <c>(x,y)</c>.</summary>
    public string ToText() => $"({X},{Y})";
}

/// <summary>A size in pixels.</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    /// <summary>upstream desktop/views.py:53-54 Size.to_string: <c>(w,h)</c>.</summary>
    public string ToText() => $"({Width},{Height})";
}

/// <summary>
/// A rectangle in physical virtual-desktop pixels with exclusive Right and Bottom, like upstream's uia Rect
/// (uia/core.py:2187-2218) and BoundingBox (tree/views.py:212-245).
/// </summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>upstream uia/core.py:2205-2206 isempty.</summary>
    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>upstream tree/views.py:232-233 get_center, with Python's floor division.</summary>
    public ScreenPoint Center => new(Left + (Width >> 1), Top + (Height >> 1));

    /// <summary>upstream uia/core.py:2211-2218: the result is inverted (negative size) when the rectangles are disjoint.</summary>
    public ScreenRect Intersect(ScreenRect other) => new(
        Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));

    /// <summary>upstream desktop/service.py:1502-1504 _point_in_region.</summary>
    public bool Contains(ScreenPoint point) => Left <= point.X && point.X < Right && Top <= point.Y && point.Y < Bottom;

    /// <summary>upstream tree/views.py:238-240 xyxy_to_string: <c>(left,top,right,bottom)</c>.</summary>
    public string ToXyxy() => $"({Left},{Top},{Right},{Bottom})";

    /// <summary>upstream desktop/service.py:1507-1525 _clip_bounding_box_to_region: null when nothing overlaps.</summary>
    public static ScreenRect? Clip(ScreenRect box, ScreenRect region)
    {
        var clipped = box.Intersect(region);
        return clipped.Right <= clipped.Left || clipped.Bottom <= clipped.Top ? null : clipped;
    }
}
