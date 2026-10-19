using DesktopMcp.Desktop.Displays;
using Microsoft.UI.Windowing;

namespace DesktopMcp.Desktop.Tests.Displays;

public class Win32DisplaysTests
{
    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(144, 1.5)]
    [InlineData(120, 1.25)]
    [InlineData(100, 1.041667)]
    public void ScaleIsTheDpiOver96RoundedToSixDigits(int dpi, double scale)
    {
        var (effective, factor) = Win32Displays.DpiMetadata(dpi);
        Assert.Equal((dpi, scale), (effective, factor));
    }
}

[Trait("Category", "Desktop")]
public class Win32DisplaysDesktopTests
{
    public Win32DisplaysDesktopTests() => TestDpi.EnsurePerMonitorV2();

    [Fact]
    public void ListsThePrimaryDisplayInsideTheVirtualScreen()
    {
        var provider = new Win32Displays();
        var displays = provider.GetDisplays();
        var screen = provider.GetVirtualScreen();
        var primary = Assert.Single(displays, d => d.Primary);
        Assert.Equal(displays.Select(d => d.Index).Order(), displays.Select(d => d.Index));
        Assert.All(displays, d => Assert.Equal(d.Bounds, d.Bounds.Intersect(screen)));
        Assert.StartsWith(@"\\.\DISPLAY", primary.DeviceName);
        Assert.NotNull(primary.EffectiveDpi);
    }

    /// <summary>
    /// The DisplayArea evaluation (P0 follow-up, Decisions 15): the Windows App SDK reports the same bounds and work areas
    /// as GetMonitorInfo for a PerMonitorV2 process.
    /// </summary>
    [Fact]
    public void DisplayAreaAgreesWithGetMonitorInfo()
    {
        AppRuntime.EnsureInitialized();
        var win32 = new Win32Displays().GetDisplays();
        var areas = DisplayArea.FindAll();
        Assert.Equal(win32.Count, areas.Count);
        for (var i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            var match = Assert.Single(win32, d => d.Bounds.Left == area.OuterBounds.X && d.Bounds.Top == area.OuterBounds.Y);
            Assert.Equal((match.Bounds.Width, match.Bounds.Height), (area.OuterBounds.Width, area.OuterBounds.Height));
            Assert.Equal(match.WorkArea, new Core.Snapshots.ScreenRect(area.WorkArea.X, area.WorkArea.Y,
                area.WorkArea.X + area.WorkArea.Width, area.WorkArea.Y + area.WorkArea.Height));
            Assert.Equal(match.Primary, area.IsPrimary);
        }
    }
}
