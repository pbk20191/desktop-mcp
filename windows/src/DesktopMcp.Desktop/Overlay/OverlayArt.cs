using System.Collections.Concurrent;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace DesktopMcp.Desktop.Overlay;

/// <summary>The bitmaps of the indicator and the flash; a seam so the indicator and the flash can run on fakes.</summary>
internal interface IOverlayArt
{
    /// <summary>The size of the notice panel for a monitor <paramref name="screenWidth"/> pixels wide, or null when it does not fit.</summary>
    PixelSize? NoticeSize(int screenWidth);

    /// <summary>
    /// Renders one layer. The wait for the shared graphics slot is bounded by <paramref name="ct"/> (the caller's own
    /// deadline, not the indicator's show bound), and a render timeout is an error that does not fault the caller's state.
    /// </summary>
    Task<OverlayBitmap> RenderAsync(LayerArt art, CancellationToken ct);
}

/// <summary>
/// Ports control_overlay_art.py and the glow of flash_overlay.py (spec §7). Edges and the cursor aura are plain pixel
/// math; the notice (text), its glow and the flash band use Win2D on the thread pool under <see cref="GpuGate"/>. The
/// output is premultiplied BGRA, ready for UpdateLayeredWindow.
/// </summary>
internal sealed class OverlayArt : IOverlayArt
{
    private const float Dpi = 96;
    private const string Operation = "Control indicator";

    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);

    private readonly Action _ensureRuntime;
    private readonly GpuGate _gate;
    private readonly ConcurrentDictionary<int, NoticeMetrics?> _notices = new();

    public static OverlayArt Shared { get; } = new();

    public OverlayArt() : this(AppRuntime.EnsureInitialized, GpuGate.Shared)
    {
    }

    internal OverlayArt(Action ensureRuntime, GpuGate gate)
    {
        _ensureRuntime = ensureRuntime;
        _gate = gate;
    }

    /// <summary>How long a caller without its own deadline waits for the graphics slot.</summary>
    internal TimeSpan GateWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the Win2D drawing of one layer may take; past it the work is abandoned on its thread, not stopped.</summary>
    internal TimeSpan RenderTimeout { get; init; } = TimeSpan.FromSeconds(10);

    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// control_overlay_art.py:96-137: measured with Win2D text layouts (cached per width). Not under the gate: it only
    /// measures text on the shared device, and its caller may not wait.
    /// </summary>
    public PixelSize? NoticeSize(int screenWidth)
    {
        if (!_notices.TryGetValue(screenWidth, out var metrics))
        {
            _ensureRuntime();
            metrics = _notices.GetOrAdd(screenWidth, static width => Measure(CanvasDevice.GetSharedDevice(), width));
        }
        return metrics is null ? null : new PixelSize(metrics.Width, metrics.Height);
    }

    public async Task<OverlayBitmap> RenderAsync(LayerArt art, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(art);
        if (art.Width <= 0 || art.Height <= 0) throw new ArgumentException($"A {art.Width}x{art.Height} layer has no pixels.", nameof(art));
        ct.ThrowIfCancellationRequested();
        if (art.Kind is ArtKind.EdgeTop or ArtKind.EdgeBottom or ArtKind.EdgeLeft or ArtKind.EdgeRight or ArtKind.CursorRing)
            return await Task.Run(() => art.Kind == ArtKind.CursorRing ? RenderCursor(art) : RenderEdge(art), ct).ConfigureAwait(false);

        try
        {
            _ensureRuntime();
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate(Operation, e);
        }

        using var lease = await _gate.EnterAsync(Operation, GateWait, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        Task<OverlayBitmap>? work = null;
        try
        {
            work = Task.Run(() => DeviceLostRetry.RunAsync(device => Task.FromResult(RenderOnDevice(device, art))));
            return await work.WaitAsync(RenderTimeout, Time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException e) when (work is not null && work.Exception?.InnerException != e)
        {
            if (!work.IsCompleted)
            {
                lease.AbandonUntil(work);
                throw new DesktopException($"{Operation} failed: Win2D did not finish within {RenderTimeout.TotalSeconds:0.###} s.");
            }
        }
        catch (OperationCanceledException) when (work is not null && ct.IsCancellationRequested && !work.IsCompleted)
        {
            lease.AbandonUntil(work);
            throw;
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate(Operation, e);
        }

        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate(Operation, e);
        }
    }

    private OverlayBitmap RenderOnDevice(CanvasDevice device, LayerArt art) => art.Kind switch
    {
        ArtKind.Notice => RenderNotice(device, art),
        ArtKind.NoticeGlow => RenderNoticeGlow(device, art),
        ArtKind.FlashEdge => RenderFlashEdge(device, art),
        _ => throw new ArgumentOutOfRangeException(nameof(art), art.Kind, "This layer is not drawn with Win2D."),
    };

    // ---- pixel math (control_overlay_art.py:35-76) ----

    private static byte Premultiply(byte channel, int alpha) => (byte)((channel * alpha + 127) / 255);

    private static void SetPixel(byte[] pixels, int offset, (byte R, byte G, byte B) color, int alpha)
    {
        pixels[offset] = Premultiply(color.B, alpha);
        pixels[offset + 1] = Premultiply(color.G, alpha);
        pixels[offset + 2] = Premultiply(color.R, alpha);
        pixels[offset + 3] = (byte)alpha;
    }

    // Feathered edges; the horizontal ones join the corners at one glow strength (nearest screen edge as the depth).
    private static OverlayBitmap RenderEdge(LayerArt art)
    {
        int width = art.Width, height = art.Height;
        var horizontal = art.Kind is ArtKind.EdgeTop or ArtKind.EdgeBottom;
        var extent = art.Extent > 0 ? art.Extent : Math.Min(OverlayLayout.Border, horizontal ? height : width);
        var glow = new int[extent];
        for (var d = 0; d < extent; d++) glow[d] = OverlayLayout.GlowAlpha(d, extent);
        var corner = Math.Min(extent, width / 2);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int depth, alpha;
                if (horizontal)
                {
                    depth = art.Kind == ArtKind.EdgeTop ? y : height - 1 - y;
                    if (depth >= extent) continue;
                    alpha = glow[depth];
                    if (x < corner) alpha = glow[Math.Min(depth, x)];
                    else if (x >= width - corner) alpha = glow[Math.Min(depth, width - 1 - x)];
                }
                else
                {
                    depth = art.Kind == ArtKind.EdgeLeft ? x : width - 1 - x;
                    if (depth >= extent) continue;
                    alpha = glow[depth];
                }
                if (alpha > 0) SetPixel(pixels, (y * width + x) * 4, art.Color, alpha);
            }
        }
        return new OverlayBitmap(width, height, pixels);
    }

    // A broad aura with a clear centre and no drawn contour.
    private static OverlayBitmap RenderCursor(LayerArt art)
    {
        int width = art.Width, height = art.Height;
        var halfX = (width - 1) / 2.0;
        var halfY = (height - 1) / 2.0;
        var half = Math.Min(halfX, halfY);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var radius = Math.Sqrt((x - halfX) * (x - halfX) + (y - halfY) * (y - halfY));
                var clearCentre = 1 - Math.Exp(-0.5 * Math.Pow(Math.Max(0.0, radius - 1) / 5, 2));
                var outerGlow = Math.Exp(-0.5 * Math.Pow(radius / 20, 2));
                var edgeFade = Math.Min(1.0, Math.Max(0.0, (half - radius) / 8));
                var alpha = (int)Math.Round(230 * clearCentre * outerGlow * edgeFade);
                if (alpha > 0) SetPixel(pixels, (y * width + x) * 4, art.Color, alpha);
            }
        }
        return new OverlayBitmap(width, height, pixels);
    }

    // ---- the notice (control_overlay_art.py:79-177) ----

    private sealed record NoticeMetrics(int HintSize, int Width, int Height, int Pad, int TitleW, int TitleH, int MouseW, int MouseH,
        int HintW, int HintH, int PrefixW, int ShortcutW, int SuffixW);

    private const int NoticeTop = 14;
    private const int NoticeGap = 6;
    private const int NoticeBottom = 16;

    private static CanvasTextFormat Format(string family, int size) => new()
    {
        FontFamily = family,
        FontSize = size,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    private static (int Width, int Height) TextSize(CanvasDevice device, string text, CanvasTextFormat format)
    {
        using var layout = new CanvasTextLayout(device, text, format, 4096, 1024);
        return ((int)Math.Ceiling(layout.LayoutBounds.Width), (int)Math.Ceiling(layout.LayoutBounds.Height));
    }

    // Prefer presentation-sized text, then shrink only when a monitor is narrow.
    private static NoticeMetrics? Measure(CanvasDevice device, int screenWidth)
    {
        var available = screenWidth - 32;
        if (available < 200) return null;
        var pad = screenWidth >= 640 ? 32 : 20;
        for (var hintSize = 24; hintSize >= 8; hintSize--)
        {
            using var titleFormat = Format("Segoe UI Semibold", hintSize + 8);
            using var hintFormat = Format("Segoe UI", hintSize);
            var (titleW, titleH) = TextSize(device, OverlayLayout.NoticeTitle, titleFormat);
            var (mouseW, mouseH) = TextSize(device, OverlayLayout.NoticeMouseHint, hintFormat);
            var (_, hintH) = TextSize(device, OverlayLayout.NoticeHint, hintFormat);
            // Bars keep trailing and leading spaces in the measured width.
            var (barW, _) = TextSize(device, "|", hintFormat);
            var prefix = OverlayLayout.NoticeHint[..OverlayLayout.NoticeHint.IndexOf(OverlayLayout.NoticeShortcut, StringComparison.Ordinal)];
            var suffix = OverlayLayout.NoticeHint[(prefix.Length + OverlayLayout.NoticeShortcut.Length)..];
            var prefixW = TextSize(device, prefix + "|", hintFormat).Width - barW;
            var shortcutW = TextSize(device, OverlayLayout.NoticeShortcut, hintFormat).Width;
            var suffixW = TextSize(device, "|" + suffix, hintFormat).Width - barW;
            var hintW = prefixW + shortcutW + suffixW + 16;
            var width = Math.Max(titleW, Math.Max(hintW, mouseW)) + 2 * pad;
            if (width > available) continue;
            var height = NoticeTop + titleH + NoticeGap + mouseH + NoticeGap + hintH + NoticeBottom;
            return new NoticeMetrics(hintSize, width, height, pad, titleW, titleH, mouseW, mouseH, hintW, hintH, prefixW, shortcutW, suffixW);
        }
        return null;
    }

    // The rounded panel (radius 22, colour (8, 42, 88) at 80 %) fading through its outer 8 px.
    private static byte[] PanelPixels(int width, int height)
    {
        var fade = new double[9];
        for (var step = 0; step <= 8; step++) fade[step] = Math.Round(255 * Math.Pow(step / 8.0, 2) * (3 - 2 * step / 8.0)) / 255.0;
        var radius = Math.Min(22.0, Math.Min(width, height) / 2.0);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var edge = Math.Min(fade[Math.Min(8, Math.Min(x, width - 1 - x))], fade[Math.Min(8, Math.Min(y, height - 1 - y))]);
                var qx = Math.Abs(x + 0.5 - width / 2.0) - (width / 2.0 - radius);
                var qy = Math.Abs(y + 0.5 - height / 2.0) - (height / 2.0 - radius);
                var distance = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                var coverage = Math.Clamp(0.5 - distance, 0, 1);
                var alpha = (int)Math.Round(255 * 0.8 * edge * coverage);
                if (alpha > 0) SetPixel(pixels, (y * width + x) * 4, (8, 42, 88), alpha);
            }
        }
        return pixels;
    }

    private OverlayBitmap RenderNotice(CanvasDevice device, LayerArt art)
    {
        var m = _notices.GetOrAdd(art.Extent, static (width, d) => Measure(d, width), device)
            ?? throw new InvalidOperationException($"The notice does not fit a {art.Extent} px wide monitor.");
        using var target = new CanvasRenderTarget(device, m.Width, m.Height, Dpi);
        using var panel = CanvasBitmap.CreateFromBytes(device, PanelPixels(m.Width, m.Height), m.Width, m.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, Dpi, CanvasAlphaMode.Premultiplied);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Clear);
            session.DrawImage(panel);
            session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            using var titleFormat = Format("Segoe UI Semibold", m.HintSize + 8);
            using var hintFormat = Format("Segoe UI", m.HintSize);
            var mouseY = NoticeTop + m.TitleH + NoticeGap;
            var hintY = mouseY + m.MouseH + NoticeGap;
            var soft = Color.FromArgb(255, 225, 240, 255);
            session.DrawText(OverlayLayout.NoticeTitle, (m.Width - m.TitleW) / 2f, NoticeTop, Color.FromArgb(255, 248, 251, 255), titleFormat);
            session.DrawText(OverlayLayout.NoticeMouseHint, (m.Width - m.MouseW) / 2f, mouseY, soft, hintFormat);

            var hintX = (m.Width - m.HintW) / 2;
            var badgeLeft = hintX + m.PrefixW;
            // A solid black key label separates the takeover shortcut from its sentence.
            session.FillRoundedRectangle(badgeLeft, hintY - 2, m.ShortcutW + 15, m.HintH + 4, 6, 6, Color.FromArgb(255, 0, 0, 0));
            var prefix = OverlayLayout.NoticeHint[..OverlayLayout.NoticeHint.IndexOf(OverlayLayout.NoticeShortcut, StringComparison.Ordinal)];
            session.DrawText(prefix, hintX, hintY, soft, hintFormat);
            session.DrawText(OverlayLayout.NoticeShortcut, badgeLeft + 8, hintY, White, hintFormat);
            session.DrawText(OverlayLayout.NoticeHint[(prefix.Length + OverlayLayout.NoticeShortcut.Length)..], badgeLeft + m.ShortcutW + 16, hintY, soft, hintFormat);
        }
        return new OverlayBitmap(m.Width, m.Height, target.GetPixelBytes());
    }

    // control_overlay_art.py:180-194: the halo outside the panel's solid core, feathered, peaking at alpha 211.
    private static OverlayBitmap RenderNoticeGlow(CanvasDevice device, LayerArt art)
    {
        int width = art.Width, height = art.Height, pad = art.Extent;
        using var shape = new CanvasRenderTarget(device, width, height, Dpi);
        using (var session = shape.CreateDrawingSession())
        {
            session.Clear(Clear);
            // The inset contour meets the panel where its 8 px fade begins.
            session.FillRoundedRectangle(pad + 8, pad + 8, Math.Max(0, width - 2 * pad - 16), Math.Max(0, height - 2 * pad - 16), 14, 14, White);
        }
        var shapeBytes = shape.GetPixelBytes();
        var blurredBytes = Blur(device, shape, width, height, Math.Min(15f, pad / 2f));
        var ring = new byte[width * height * 4];
        for (var i = 3; i < ring.Length; i += 4)
        {
            var outside = (byte)Math.Max(0, blurredBytes[i] - shapeBytes[i]);
            ring[i] = ring[i - 1] = ring[i - 2] = ring[i - 3] = outside;
        }
        using var ringBitmap = CanvasBitmap.CreateFromBytes(device, ring, width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, Dpi, CanvasAlphaMode.Premultiplied);
        // Feather the inner cutoff as well, so the halo cannot end as a bright line.
        var feathered = Blur(device, ringBitmap, width, height, 3f);
        var peak = 0;
        for (var i = 3; i < feathered.Length; i += 4) peak = Math.Max(peak, feathered[i]);
        var pixels = new byte[width * height * 4];
        if (peak > 0)
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = (int)Math.Round(feathered[i + 3] * 211.0 / peak);
                if (alpha > 0) SetPixel(pixels, i, art.Color, alpha);
            }
        }
        return new OverlayBitmap(width, height, pixels);
    }

    private static byte[] Blur(CanvasDevice device, ICanvasImage source, int width, int height, float amount)
    {
        using var output = new CanvasRenderTarget(device, width, height, Dpi);
        using (var session = output.CreateDrawingSession())
        {
            session.Clear(Clear);
            using var effect = new GaussianBlurEffect { Source = source, BlurAmount = amount, BorderMode = EffectBorderMode.Hard };
            session.DrawImage(effect);
        }
        return output.GetPixelBytes();
    }

    // flash_overlay.py:310-346: an 8 px band blurred (radius 14) under the same sharp band. The band is drawn for the
    // whole flashed rectangle (Source), padded so the blur of a strip sees its neighbours, then cut to the strip.
    private static OverlayBitmap RenderFlashEdge(CanvasDevice device, LayerArt art)
    {
        var source = art.Source ?? new ScreenRect(0, 0, art.Width, art.Height);
        var pad = 3 * OverlayLayout.FlashBlur;
        int paddedWidth = art.Width + 2 * pad, paddedHeight = art.Height + 2 * pad;
        var colour = Color.FromArgb(255, art.Color.R, art.Color.G, art.Color.B);
        using var sharp = new CanvasRenderTarget(device, paddedWidth, paddedHeight, Dpi);
        using (var session = sharp.CreateDrawingSession())
        {
            session.Clear(Clear);
            session.Antialiasing = CanvasAntialiasing.Aliased;
            // The stroke is centred on its path: move the path half a band out (outward flash) or in (inward flash).
            var shift = (art.Inward ? -1 : 1) * OverlayLayout.FlashBand / 2f;
            var w = source.Width + 2 * shift;
            var h = source.Height + 2 * shift;
            if (w > 0 && h > 0)
                session.DrawRectangle(source.Left + pad - shift, source.Top + pad - shift, w, h, colour, OverlayLayout.FlashBand);
        }
        using var output = new CanvasRenderTarget(device, art.Width, art.Height, Dpi);
        using (var session = output.CreateDrawingSession())
        {
            session.Clear(Clear);
            using var blur = new GaussianBlurEffect { Source = sharp, BlurAmount = OverlayLayout.FlashBlur, BorderMode = EffectBorderMode.Hard };
            session.DrawImage(blur, -pad, -pad);
            session.DrawImage(sharp, -pad, -pad);
        }
        return new OverlayBitmap(art.Width, art.Height, output.GetPixelBytes());
    }
}
