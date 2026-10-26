using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Overlay;

namespace DesktopMcp.Desktop.Tests.Overlay;

// Win2D on an off-screen target: needs the runtime and a GPU device, shows nothing.
[Trait("Category", "Desktop")]
public class OverlayArtTests
{
    private static readonly (byte R, byte G, byte B) Blue = OverlayLayout.IndicatorBlue;

    private static int Alpha(OverlayBitmap b, int x, int y) => b.Pixels[(y * b.Width + x) * 4 + 3];

    [Fact]
    public async Task EdgeBitmapsFadeInwardsFromTheScreenEdge()
    {
        var bitmap = await OverlayArt.Shared.RenderAsync(new LayerArt(ArtKind.EdgeTop, 400, 56, Blue, 56), CancellationToken.None);
        Assert.Equal(400 * 56 * 4, bitmap.Pixels.Length);
        var i = (0 * 400 + 200) * 4;
        Assert.Equal(240, bitmap.Pixels[i + 3]);
        Assert.InRange(bitmap.Pixels[i], 239, 241);   // premultiplied blue: 255 * 240 / 255
        Assert.Equal(0, Alpha(bitmap, 200, 55));
    }

    [Fact]
    public async Task TheNoticeFitsAndHasTheBadge()
    {
        var size = OverlayArt.Shared.NoticeSize(1920);
        Assert.NotNull(size);
        Assert.True(size.Value.Width < 1888);
        var bitmap = await OverlayArt.Shared.RenderAsync(new LayerArt(ArtKind.Notice, size.Value.Width, size.Value.Height, Blue, 1920, true), CancellationToken.None);
        Assert.Equal(size.Value.Width, bitmap.Width);
        // The badge sits on the last text line, in the bottom third of the panel.
        var found = false;
        for (var y = bitmap.Height * 2 / 3; y < bitmap.Height && !found; y++)
            for (var x = 0; x < bitmap.Width && !found; x++)
            {
                var i = (y * bitmap.Width + x) * 4;
                found = bitmap.Pixels[i] == 0 && bitmap.Pixels[i + 1] == 0 && bitmap.Pixels[i + 2] == 0 && bitmap.Pixels[i + 3] == 255;
            }
        Assert.True(found, "no opaque black pixel in the badge's row band");
        Assert.Null(OverlayArt.Shared.NoticeSize(220));
    }

    [Fact]
    public async Task TheCursorRingIsClearInTheCentreAndFadesOut()
    {
        var bitmap = await OverlayArt.Shared.RenderAsync(OverlayLayout.CursorRing(new ScreenPoint(100, 100)).Art, CancellationToken.None);
        Assert.Equal(0, Alpha(bitmap, 44, 44));
        Assert.Equal(0, Alpha(bitmap, 0, 0));
        Assert.Equal(0, Alpha(bitmap, 87, 87));
        Assert.Contains(Enumerable.Range(0, 88 * 88), p => bitmap.Pixels[p * 4 + 3] > 100);
    }

    [Fact]
    public async Task AFlashEdgeIsOrange()
    {
        var art = OverlayLayout.Flash(new ScreenRect(100, 100, 300, 200), [new ScreenRect(0, 0, 1920, 1080)], new ScreenRect(0, 0, 1920, 1080))[0].Art;
        var bitmap = await OverlayArt.Shared.RenderAsync(art, CancellationToken.None);
        var best = Enumerable.Range(0, bitmap.Width * bitmap.Height).MaxBy(p => bitmap.Pixels[p * 4 + 3]);
        var a = bitmap.Pixels[best * 4 + 3];
        Assert.True(a > 200);
        int Un(int channel) => (int)Math.Round(bitmap.Pixels[best * 4 + channel] * 255.0 / a);
        Assert.InRange(Un(2), 0xFF - 8, 0xFF);
        Assert.InRange(Un(1), 0x45 - 8, 0x45 + 8);
        Assert.InRange(Un(0), 0, 8);
    }

    [Fact]
    public async Task TheNoticeGlowIsClearInsideThePanelAndSoftOutside()
    {
        var art = new LayerArt(ArtKind.NoticeGlow, 664, 184, Blue, 32);
        var bitmap = await OverlayArt.Shared.RenderAsync(art, CancellationToken.None);
        Assert.Equal(0, Alpha(bitmap, 332, 92));
        Assert.Contains(Enumerable.Range(0, 664 * 184), p => bitmap.Pixels[p * 4 + 3] > 100);
    }

    [Fact]
    public async Task AnInwardMonitorFlashHasItsBandAtTheOuterRowsOfTheTopStrip()
    {
        var screen = new ScreenRect(0, 0, 1920, 1080);
        var top = OverlayLayout.Flash(null, [screen], screen)[0].Art;
        var bitmap = await OverlayArt.Shared.RenderAsync(top, CancellationToken.None);
        var x = bitmap.Width / 2;
        for (var y = 0; y < 8; y++) Assert.Equal(255, Alpha(bitmap, x, y));   // the sharp 8 px band, rows 0-7
        Assert.True(Alpha(bitmap, x, 8) < 255);
        Assert.True(Alpha(bitmap, x, 40) < Alpha(bitmap, x, 10));   // the blur fades inwards
    }
}
