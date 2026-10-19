using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class RegionFilterTests
{
    private static readonly ScreenRect Region = new(0, 0, 100, 100);

    [Fact]
    public void WindowsAreClippedOrDropped()
    {
        var inside = new WindowInfo("A", 0, WindowStatus.Normal, new ScreenRect(50, 50, 150, 150), 1, 10, false);
        var outside = inside with { Name = "B", Bounds = new ScreenRect(200, 200, 300, 300) };
        var filtered = RegionFilter.Windows([inside, outside], Region);
        Assert.Equal(new ScreenRect(50, 50, 100, 100), Assert.Single(filtered).Bounds);
    }

    [Fact]
    public void ACursorOutsideTheRegionIsDropped()
    {
        Assert.Null(RegionFilter.Cursor(new ScreenPoint(100, 5), Region));
        Assert.Equal(new ScreenPoint(5, 5), RegionFilter.Cursor(new ScreenPoint(5, 5), Region));
    }

    [Fact]
    public void ElementsAreClippedAndRecentred()
    {
        var element = new UiElement(7, "OK", "button", "App", new ScreenRect(80, 80, 120, 120), new ScreenPoint(100, 100), UiMetadata.Empty);
        var gone = element with { Id = 8, Box = new ScreenRect(150, 150, 160, 160) };
        var tree = new TreeSnapshot([element, gone], [], null, false, 500);
        var filtered = RegionFilter.Tree(tree, Region);
        var kept = Assert.Single(filtered.Interactive);
        Assert.Equal((new ScreenRect(80, 80, 100, 100), new ScreenPoint(90, 90)), (kept.Box, kept.Center));
    }

    [Fact]
    public void TheSemanticTreeKeepsOnlyBranchesWithVisibleElements()
    {
        var root = new SemanticNode(SemanticKind.Desktop, "Desktop", "Desktop");
        var keptWindow = new SemanticNode(SemanticKind.Window, "Window", "Kept");
        keptWindow.Children.Add(new SemanticNode(SemanticKind.Interactive, "button", "OK") { Box = new ScreenRect(10, 10, 20, 20), ElementId = 1 });
        var emptyWindow = new SemanticNode(SemanticKind.Window, "Window", "Gone");
        emptyWindow.Children.Add(new SemanticNode(SemanticKind.Interactive, "button", "Far") { Box = new ScreenRect(500, 500, 510, 510), ElementId = 2 });
        root.Children.AddRange([keptWindow, emptyWindow]);

        var filtered = RegionFilter.Tree(new TreeSnapshot([], [], root, false, 500), Region).Root!;

        Assert.Equal(Region, filtered.Box);
        var window = Assert.Single(filtered.Children);
        Assert.Equal("Kept", window.Name);
        Assert.Equal(new ScreenPoint(15, 15), Assert.Single(window.Children).Center);
        Assert.Equal(2, root.Children.Count);
    }
}
