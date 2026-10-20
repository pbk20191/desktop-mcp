using System.Text.Json;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public class DisplayInventoryToolTests
{
    private sealed class FakeDisplays(params DisplayInfo[] displays) : IDisplayProvider
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => displays;
        public ScreenRect GetVirtualScreen() => new(0, 0, 1, 1);
    }

    private const string Expected =
        """[{"index":0,"device":"\\\\.\\DISPLAY1","primary":true,"bounds":{"left":0,"top":0,"right":2560,"bottom":1600,"width":2560,"height":1600},"work_area":{"left":0,"top":0,"right":2560,"bottom":1520,"width":2560,"height":1520},"resolution":"2560x1600","orientation":"landscape","effective_dpi":144,"scale":1.5},{"index":1,"device":"DISPLAY1","primary":false,"bounds":{"left":-1080,"top":0,"right":0,"bottom":1920,"width":1080,"height":1920},"work_area":null,"resolution":"1080x1920","orientation":"portrait","effective_dpi":96,"scale":1.0}]""";

    [Fact]
    public async Task ReturnsTheDisplaysAsJsonTextAndStructuredContent()
    {
        var services = ToolServices.CreateDefault() with
        {
            Displays = new FakeDisplays(
                new DisplayInfo(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1600), true, new ScreenRect(0, 0, 2560, 1520), 144, 1.5, "landscape"),
                new DisplayInfo(1, "DISPLAY1", new ScreenRect(-1080, 0, 0, 1920), false, null, 96, 1.0, "portrait")),
        };
        var result = await ToolCalls.CallAsync(ToolCalls.Create("DisplayInventory", services), "{}");

        Assert.Equal(Expected, ToolCalls.OkText(result));
        var list = result.StructuredContent!.Value.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(1.0, list[1].GetProperty("scale").GetDouble());
    }
}
