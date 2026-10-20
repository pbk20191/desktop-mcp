using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Input;

public enum MouseButton { Left, Right, Middle }

/// <summary>Intermediate pointer points for moves and drags, like upstream desktop/service.py:930-986.</summary>
public static class PointerPath
{
    private const int MaxMoveSteps = 80;
    private const int MaxDragSteps = 200;

    /// <summary>
    /// service.py:981-984: steps = max(1, min(80, max(|dx|, |dy|) // 20)); each point is cx + (x - cx) * step // steps
    /// with Python's floor division.
    /// </summary>
    public static IReadOnlyList<ScreenPoint> MoveSteps(ScreenPoint from, ScreenPoint to)
    {
        var steps = MoveStepCount(from, to);
        var points = new ScreenPoint[steps];
        for (var step = 1; step <= steps; step++)
        {
            points[step - 1] = new ScreenPoint(
                (int)(from.X + FloorDiv((long)(to.X - from.X) * step, steps)),
                (int)(from.Y + FloorDiv((long)(to.Y - from.Y) * step, steps)));
        }
        return points;
    }

    /// <summary>
    /// service.py:958-971: with a duration, steps = max(1, min(200, ceil(d / 0.01))) and the interval is d / steps; without
    /// one the move step count and 10 ms. Each point is from + round(offset), half to even.
    /// </summary>
    public static (IReadOnlyList<ScreenPoint> Points, TimeSpan Interval) DragSteps(ScreenPoint from, ScreenPoint to, double? duration)
    {
        int steps;
        TimeSpan interval;
        if (duration is { } seconds)
        {
            steps = (int)Math.Max(1, Math.Min(MaxDragSteps, Math.Ceiling(seconds / 0.01)));
            interval = TimeSpan.FromSeconds(seconds / steps);
        }
        else
        {
            steps = MoveStepCount(from, to);
            interval = TimeSpan.FromMilliseconds(10);
        }
        var points = new ScreenPoint[steps];
        for (var step = 1; step <= steps; step++)
        {
            points[step - 1] = new ScreenPoint(
                from.X + (int)PyText.RoundToInteger((double)(to.X - from.X) * step / steps),
                from.Y + (int)PyText.RoundToInteger((double)(to.Y - from.Y) * step / steps));
        }
        return (points, interval);
    }

    private static int MoveStepCount(ScreenPoint from, ScreenPoint to)
    {
        var distance = Math.Max(Math.Abs((long)to.X - from.X), Math.Abs((long)to.Y - from.Y));
        return (int)Math.Max(1, Math.Min(MaxMoveSteps, FloorDiv(distance, 20)));
    }

    private static long FloorDiv(long a, long b)
    {
        var quotient = a / b;
        return a % b != 0 && (a < 0) != (b < 0) ? quotient - 1 : quotient;
    }
}

/// <summary>Absolute mouse coordinates (uia/core.py:234-236) and the bounds check (desktop/service.py:712-720).</summary>
public static class MouseCoordinates
{
    /// <summary>round((x - left) * 65535 / max(width - 1, 1)), half to even, over the virtual desktop.</summary>
    public static (int X, int Y) Normalize(ScreenPoint point, ScreenRect virtualScreen) => (
        (int)PyText.RoundToInteger((double)(point.X - virtualScreen.Left) * 65535 / Math.Max(virtualScreen.Width - 1, 1)),
        (int)PyText.RoundToInteger((double)(point.Y - virtualScreen.Top) * 65535 / Math.Max(virtualScreen.Height - 1, 1)));

    /// <summary>The inverse of <see cref="Normalize"/>: left + round(x * (width - 1) / 65535), half to even.</summary>
    public static ScreenPoint Denormalize((int X, int Y) normalized, ScreenRect virtualScreen) => new(
        virtualScreen.Left + (int)PyText.RoundToInteger((double)normalized.X * Math.Max(virtualScreen.Width - 1, 1) / 65535),
        virtualScreen.Top + (int)PyText.RoundToInteger((double)normalized.Y * Math.Max(virtualScreen.Height - 1, 1) / 65535));

    /// <summary>Upstream's out-of-bounds text, or null when the point is on the desktop.</summary>
    public static string? OutsideMessage(ScreenPoint point, ScreenRect virtualScreen)
    {
        var right = virtualScreen.Left + virtualScreen.Width - 1;
        var bottom = virtualScreen.Top + virtualScreen.Height - 1;
        if (virtualScreen.Left <= point.X && point.X <= right && virtualScreen.Top <= point.Y && point.Y <= bottom) return null;
        return $"Coordinates ({point.X},{point.Y}) are outside the desktop bounds x={virtualScreen.Left}..{right}, y={virtualScreen.Top}..{bottom}";
    }
}
