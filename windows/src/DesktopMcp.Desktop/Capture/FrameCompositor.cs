using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Capture;

/// <summary>
/// Places monitor frames into one image of a virtual-desktop rectangle (spec §6: a full virtual-desktop capture
/// composites the monitors it covers). Pixels no frame covers are opaque black, as mss and PIL leave them upstream.
/// The capture uses <see cref="Blank"/> and <see cref="Place"/> directly, one frame at a time, so it never holds more
/// than the output and one frame; <see cref="Compose"/> is the same rule over a list.
/// </summary>
internal static class FrameCompositor
{
    public static CapturedImage Compose(ScreenRect area, IReadOnlyList<(ScreenRect Monitor, CapturedImage Frame)> frames)
    {
        if (frames is [var (monitor, frame)] && Covers(area, monitor, frame))
            return frame;
        var output = Blank(area);
        foreach (var (bounds, image) in frames)
            Place(output, area, bounds, image);
        return output;
    }

    /// <summary>True when <paramref name="frame"/>, placed at <paramref name="origin"/>, is exactly <paramref name="area"/>.</summary>
    public static bool Covers(ScreenRect area, ScreenRect origin, CapturedImage frame) =>
        origin.Left == area.Left && origin.Top == area.Top && frame.Width == area.Width && frame.Height == area.Height;

    /// <summary>An opaque black image of <paramref name="area"/>'s size.</summary>
    public static CapturedImage Blank(ScreenRect area)
    {
        var pixels = new byte[checked(area.Width * area.Height * 4)];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;
        return new CapturedImage(area.Width, area.Height, pixels);
    }

    /// <summary>
    /// Copies the part of <paramref name="frame"/> (its top-left at <paramref name="origin"/>'s top-left) that lies inside
    /// <paramref name="area"/> into <paramref name="output"/>, an image of <paramref name="area"/>.
    /// </summary>
    public static void Place(CapturedImage output, ScreenRect area, ScreenRect origin, CapturedImage frame)
    {
        // The frame may be smaller than the monitor rectangle (a mode change between the two reads): copy what overlaps.
        var source = new ScreenRect(origin.Left, origin.Top, origin.Left + frame.Width, origin.Top + frame.Height);
        if (ScreenRect.Clip(source, area) is not { } overlap) return;
        var rowBytes = overlap.Width * 4;
        for (var y = overlap.Top; y < overlap.Bottom; y++)
        {
            var from = ((y - source.Top) * frame.Width + (overlap.Left - source.Left)) * 4;
            var to = ((y - area.Top) * output.Width + (overlap.Left - area.Left)) * 4;
            Buffer.BlockCopy(frame.Bgra, from, output.Bgra, to, rowBytes);
        }
    }
}
