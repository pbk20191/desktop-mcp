using System.Globalization;
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Desktop.Overlay;

/// <summary>A rendered overlay layer: premultiplied BGRA, top-down, <c>Pixels.Length == Width * Height * 4</c>.</summary>
public sealed record OverlayBitmap(int Width, int Height, byte[] Pixels);

internal enum ArtKind { EdgeTop, EdgeBottom, EdgeLeft, EdgeRight, NoticeGlow, Notice, CursorRing, FlashEdge }

/// <summary>
/// What to draw into one layer's bitmap. <paramref name="Extent"/>: the feather depth of an edge, the glow pad of a
/// <see cref="ArtKind.NoticeGlow"/>, the monitor width of a <see cref="ArtKind.Notice"/> (it picks the font size).
/// <paramref name="Source"/>: for a <see cref="ArtKind.FlashEdge"/>, the flashed rectangle in the bitmap's own
/// coordinates (it may reach outside the bitmap), so the strips of one rectangle tile without seams.
/// </summary>
internal sealed record LayerArt(ArtKind Kind, int Width, int Height, (byte R, byte G, byte B) Color, int Extent = 0, bool Inward = true,
    ScreenRect? Source = null);

internal sealed record LayerSpec(string Name, ScreenRect Bounds, LayerArt Art, bool Breathes);

/// <summary>The pure geometry and formulas of the AI-control indicator and the capture flash (spec §7, Decisions 26-27).</summary>
internal static class OverlayLayout
{
    public const int Border = 56;
    public const int CursorSize = 88;
    public const int NoticeGlowPad = 32;
    public const double BreathPeriodSeconds = 3.0;
    public const int BreathMinAlpha = 140;
    public static readonly (byte R, byte G, byte B) IndicatorBlue = (45, 145, 255);
    public static readonly (byte R, byte G, byte B) FlashColor = (0xFF, 0x45, 0x00);
    public static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(3500);
    public static readonly TimeSpan FlashFrame = TimeSpan.FromMilliseconds(30);
    public const int FlashBand = 8;
    public const int FlashBlur = 14;
    public const int FlashDepth = 50;
    public const int FlashInset = 6;

    private const double FlashMinVisible = 0.04;
    private const int FlashSteps = 32;

    public const string NoticeTitle = "AI is controlling this computer";
    public const string NoticeMouseHint = "Move the mouse a significant distance";
    public const string NoticeShortcut = "Ctrl + Alt + Shift + Backspace";
    public const string NoticeHint = "or press " + NoticeShortcut + " to take over";

    /// <summary>control_overlay.py:137-194: per monitor the four edges, then the notice glow and the notice.</summary>
    public static IReadOnlyList<LayerSpec> Indicator(IReadOnlyList<ScreenRect> monitors, Func<int, PixelSize?> noticeSize)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(noticeSize);
        if (monitors.Count == 0) throw new InvalidOperationException("no display available for AI control indicator");
        var color = IndicatorBlue;
        var layers = new List<LayerSpec>();
        for (var index = 0; index < monitors.Count; index++)
        {
            var (left, top, right, bottom) = (monitors[index].Left, monitors[index].Top, monitors[index].Right, monitors[index].Bottom);
            int width = right - left, height = bottom - top;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("invalid display geometry for AI control indicator");
            var border = Math.Min(Border, Math.Min(width / 2, height / 2));
            if (border <= 0) throw new InvalidOperationException("display too small for AI control indicator");
            // The horizontal edges carry the corner glow; the vertical ones sit between them.
            (int X, int Y, int W, int H, string Side, ArtKind Kind)[] strips =
            [
                (left, top, width, border, "top", ArtKind.EdgeTop),
                (left, bottom - border, width, border, "bottom", ArtKind.EdgeBottom),
                (left, top + border, border, height - 2 * border, "left", ArtKind.EdgeLeft),
                (right - border, top + border, border, height - 2 * border, "right", ArtKind.EdgeRight),
            ];
            foreach (var (x, y, w, h, side, kind) in strips)
            {
                if (w <= 0 || h <= 0) continue;
                var extent = Math.Min(Border, kind is ArtKind.EdgeTop or ArtKind.EdgeBottom ? h : w);
                layers.Add(new LayerSpec($"{index}_{side}", new ScreenRect(x, y, x + w, y + h), new LayerArt(kind, w, h, color, extent), true));
            }

            if (noticeSize(width) is not { } notice) continue;
            var pad = Math.Min(NoticeGlowPad, (width - notice.Width) / 2);
            if (height <= border + notice.Height + 2 * pad) continue;
            var noticeX = left + (width - notice.Width) / 2;
            var noticeY = top + border + pad;
            var glow = new ScreenRect(noticeX - pad, noticeY - pad, noticeX + notice.Width + pad, noticeY + notice.Height + pad);
            layers.Add(new LayerSpec($"{index}_notice_glow", glow, new LayerArt(ArtKind.NoticeGlow, glow.Width, glow.Height, color, pad), true));
            layers.Add(new LayerSpec($"{index}_notice", new ScreenRect(noticeX, noticeY, noticeX + notice.Width, noticeY + notice.Height),
                new LayerArt(ArtKind.Notice, notice.Width, notice.Height, color, width), false));
        }
        return layers;
    }

    /// <summary>The 88 px aura centred on <paramref name="cursor"/> (control_overlay.py:196-210).</summary>
    public static LayerSpec CursorRing(ScreenPoint cursor)
    {
        var left = cursor.X - CursorSize / 2;
        var top = cursor.Y - CursorSize / 2;
        return new LayerSpec("cursor", new ScreenRect(left, top, left + CursorSize, top + CursorSize),
            new LayerArt(ArtKind.CursorRing, CursorSize, CursorSize, IndicatorBlue), true);
    }

    /// <summary>
    /// Decisions 27: a region gets four outward strips around it; no region gets four inward strips (inset
    /// <see cref="FlashInset"/>) on each captured monitor. Strips are clipped to <paramref name="virtualScreen"/>.
    /// </summary>
    public static IReadOnlyList<LayerSpec> Flash(ScreenRect? region, IReadOnlyList<ScreenRect> capturedMonitors, ScreenRect virtualScreen)
    {
        ArgumentNullException.ThrowIfNull(capturedMonitors);
        var layers = new List<LayerSpec>();
        if (region is { } r)
        {
            AddFlashStrips(layers, "region", r, outward: true, virtualScreen);
            return layers;
        }
        for (var i = 0; i < capturedMonitors.Count; i++)
        {
            var m = capturedMonitors[i];
            var inset = new ScreenRect(m.Left + FlashInset, m.Top + FlashInset, m.Right - FlashInset, m.Bottom - FlashInset);
            if (inset.Width <= 0 || inset.Height <= 0) continue;
            AddFlashStrips(layers, i.ToString(CultureInfo.InvariantCulture), inset, outward: false, virtualScreen);
        }
        return layers;
    }

    private static void AddFlashStrips(List<LayerSpec> layers, string prefix, ScreenRect rect, bool outward, ScreenRect virtualScreen)
    {
        var (l, t, r, b) = (rect.Left, rect.Top, rect.Right, rect.Bottom);
        var d = outward ? FlashDepth : Math.Min(FlashDepth, Math.Min(rect.Width / 2, rect.Height / 2));
        (string Name, ScreenRect Strip)[] strips = outward
            ?
            [
                ("top", new ScreenRect(l - d, t - d, r + d, t)),
                ("bottom", new ScreenRect(l - d, b, r + d, b + d)),
                ("left", new ScreenRect(l - d, t, l, b)),
                ("right", new ScreenRect(r, t, r + d, b)),
            ]
            :
            [
                ("top", new ScreenRect(l, t, r, t + d)),
                ("bottom", new ScreenRect(l, b - d, r, b)),
                ("left", new ScreenRect(l, t + d, l + d, b - d)),
                ("right", new ScreenRect(r - d, t + d, r, b - d)),
            ];
        foreach (var (name, strip) in strips)
        {
            if (ScreenRect.Clip(strip, virtualScreen) is not { } clipped) continue;
            var source = new ScreenRect(l - clipped.Left, t - clipped.Top, r - clipped.Left, b - clipped.Top);
            layers.Add(new LayerSpec($"flash_{prefix}_{name}", clipped,
                new LayerArt(ArtKind.FlashEdge, clipped.Width, clipped.Height, FlashColor, 0, !outward, source), false));
        }
    }

    /// <summary>control_overlay_art.py:28-32: peak at the screen edge, fading to transparency inward.</summary>
    public static int GlowAlpha(int depth, int extent)
    {
        if (depth >= extent - 1) return 0;
        return (int)Math.Round(240 * Math.Pow(1 - (double)depth / (extent - 1), 1.4));
    }

    /// <summary>control_overlay_art.py:22-25: bright at start, cycling between <see cref="BreathMinAlpha"/> and 255.</summary>
    public static byte BreathOpacity(double elapsedSeconds)
    {
        var strength = (1 + Math.Cos(2 * Math.PI * elapsedSeconds / BreathPeriodSeconds)) / 2;
        return (byte)Math.Round(BreathMinAlpha + (255 - BreathMinAlpha) * strength);
    }

    /// <summary>flash_overlay.py:472-479; <paramref name="t"/> is the elapsed fraction of the flash (0..1).</summary>
    public static double FlashIntensity(double t, bool fullScreen)
    {
        if (fullScreen) return 1.0 - Math.Abs(2 * t - 1);
        if (t < 0.15) return t / 0.15;
        if (t < 0.65) return 1.0;
        return Math.Max(0.0, 1.0 - (t - 0.65) / 0.35);
    }

    /// <summary>The layer opacity for <see cref="FlashIntensity"/> quantised to 32 steps; null (hidden) below 0.04.</summary>
    public static byte? FlashOpacity(double t, bool fullScreen)
    {
        var intensity = FlashIntensity(t, fullScreen);
        if (intensity < FlashMinVisible) return null;
        var step = Math.Clamp((int)Math.Round(intensity * FlashSteps), 0, FlashSteps);
        return (byte)Math.Round(step * 255.0 / FlashSteps);
    }
}
