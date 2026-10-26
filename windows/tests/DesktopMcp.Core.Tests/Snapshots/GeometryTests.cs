using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class GeometryTests
{
    [Fact]
    public void CenterFloorsLikePython()
    {
        Assert.Equal(new ScreenPoint(15, 25), new ScreenRect(10, 20, 21, 31).Center);
        Assert.Equal(new ScreenPoint(-2, -2), new ScreenRect(0, 0, -3, -3).Center);
    }

    [Fact]
    public void TextFormsMatchUpstream()
    {
        Assert.Equal("(1,2,30,40)", new ScreenRect(1, 2, 30, 40).ToXyxy());
        Assert.Equal("(5,6)", new ScreenPoint(5, 6).ToText());
        Assert.Equal("(1920,1080)", new PixelSize(1920, 1080).ToText());
    }

    [Fact]
    public void ClipReturnsTheOverlapOrNull()
    {
        var region = new ScreenRect(0, 0, 100, 100);
        Assert.Equal(new ScreenRect(50, 50, 100, 100), ScreenRect.Clip(new ScreenRect(50, 50, 150, 150), region));
        Assert.Null(ScreenRect.Clip(new ScreenRect(100, 0, 150, 50), region));
    }

    [Fact]
    public void ContainsExcludesTheRightAndBottomEdges()
    {
        var rect = new ScreenRect(0, 0, 10, 10);
        Assert.True(rect.Contains(new ScreenPoint(9, 9)));
        Assert.False(rect.Contains(new ScreenPoint(10, 5)));
    }
}
