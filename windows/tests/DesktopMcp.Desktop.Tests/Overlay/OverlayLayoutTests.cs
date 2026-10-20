using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Overlay;

namespace DesktopMcp.Desktop.Tests.Overlay;

public class OverlayLayoutTests
{
    private static PixelSize? Notice(int width) => width - 32 >= 200 ? new PixelSize(600, 120) : null;

    [Fact]
    public void EachMonitorGetsFourEdgesAndTheNotice()
    {
        var layers = OverlayLayout.Indicator([new ScreenRect(0, 0, 1920, 1080)], Notice);
        Assert.Equal(["0_top", "0_bottom", "0_left", "0_right", "0_notice_glow", "0_notice"], layers.Select(l => l.Name));
        Assert.Equal(new ScreenRect(0, 0, 1920, 56), layers[0].Bounds);
        Assert.Equal(new ScreenRect(0, 56, 56, 1024), layers[2].Bounds);
        Assert.Equal(new ScreenRect(628, 56, 1292, 240), layers[4].Bounds);   // 600x120 notice centred below the border, 32 px glow pad
        Assert.False(layers[5].Breathes);
    }

    [Fact]
    public void IndicatorLayoutCoversMonitorsLeftOfThePrimary()
    {
        var layers = OverlayLayout.Indicator([new ScreenRect(-1280, -200, 0, 824), new ScreenRect(0, 0, 2560, 1600)], Notice);
        Assert.Equal(new ScreenRect(-1280, -200, 0, -144), layers.Single(l => l.Name == "0_top").Bounds);
        Assert.Equal(new ScreenRect(-56, -144, 0, 768), layers.Single(l => l.Name == "0_right").Bounds);
    }

    [Fact]
    public void ANarrowMonitorHasNoNotice() =>
        Assert.DoesNotContain(OverlayLayout.Indicator([new ScreenRect(0, 0, 220, 600)], Notice), l => l.Name.Contains("notice"));

    [Fact]
    public void NoMonitorIsAnError() =>
        Assert.Equal("no display available for AI control indicator",
            Assert.Throws<InvalidOperationException>(() => OverlayLayout.Indicator([], Notice)).Message);

    [Fact]
    public void BadGeometryIsAnError()
    {
        Assert.Equal("invalid display geometry for AI control indicator",
            Assert.Throws<InvalidOperationException>(() => OverlayLayout.Indicator([new ScreenRect(0, 0, 0, 100)], Notice)).Message);
        Assert.Equal("display too small for AI control indicator",
            Assert.Throws<InvalidOperationException>(() => OverlayLayout.Indicator([new ScreenRect(0, 0, 1, 100)], Notice)).Message);
    }

    [Fact]
    public void UpstreamsFormulas()
    {
        Assert.Equal(240, OverlayLayout.GlowAlpha(0, 56));
        Assert.Equal(0, OverlayLayout.GlowAlpha(55, 56));
        Assert.Equal(255, OverlayLayout.BreathOpacity(0));
        Assert.Equal(140, OverlayLayout.BreathOpacity(1.5));
        Assert.Equal(0.5, OverlayLayout.FlashIntensity(0.25, fullScreen: true));
        Assert.Equal(1.0, OverlayLayout.FlashIntensity(0.5, fullScreen: false));
        Assert.Null(OverlayLayout.FlashOpacity(0.01, fullScreen: true));
        Assert.Equal((byte)255, OverlayLayout.FlashOpacity(0.5, fullScreen: true));
    }

    [Fact]
    public void ARegionFlashSurroundsTheRegionAndAFullFlashSitsInsideEachMonitor()
    {
        var screen = new ScreenRect(0, 0, 1920, 1080);
        var region = OverlayLayout.Flash(new ScreenRect(100, 100, 300, 200), [screen], screen);
        Assert.Equal(new ScreenRect(50, 50, 350, 100), region[0].Bounds);   // the top band, 50 px above the region
        var full = OverlayLayout.Flash(null, [screen], screen);
        Assert.Equal(new ScreenRect(6, 6, 1914, 56), full[0].Bounds);
        Assert.All(full, l => Assert.True(l.Art.Inward));
    }

    [Fact]
    public void FlashStripsAreClippedToTheVirtualScreen()
    {
        var screen = new ScreenRect(0, 0, 1920, 1080);
        var strips = OverlayLayout.Flash(new ScreenRect(0, 0, 300, 200), [screen], screen);
        Assert.DoesNotContain(strips, l => l.Name.EndsWith("_top", StringComparison.Ordinal));   // wholly above the screen
        Assert.All(strips, l => Assert.Equal(l.Bounds, ScreenRect.Clip(l.Bounds, screen)));
    }

    [Fact]
    public void TheCursorRingIsCentredOnTheCursor() =>
        Assert.Equal(new ScreenRect(56, 56, 144, 144), OverlayLayout.CursorRing(new ScreenPoint(100, 100)).Bounds);
}
