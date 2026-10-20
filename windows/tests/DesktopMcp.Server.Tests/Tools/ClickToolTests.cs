using DesktopMcp.Core.Snapshots;
using DesktopMcp.Server.Tools;

using DesktopMcp.Server.Control;

namespace DesktopMcp.Server.Tests;

public class ClickToolTests
{
    [Theory]
    [InlineData("""{"loc": [5, 6]}""", "Single left clicked at (5,6).")]
    [InlineData("""{"loc": "[5, 6]", "button": "right", "clicks": 2}""", "Double right clicked at (5,6).")]
    [InlineData("""{"loc": [5, 6], "clicks": 0}""", "Hover left clicked at (5,6).")]
    [InlineData("""{"loc": [5, 6], "clicks": 3}""", "Clicked left 3 times at (5,6).")]
    public async Task ClickTexts(string json, string text) =>
        Assert.Equal(text, ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Click", new FakeDesktop().Services()), json)));

    [Fact]
    public async Task ALabelWinsOverLocAndReportsTheLocatedPoint()
    {
        var desktop = new FakeDesktop { Labels = { [4] = new ScreenPoint(70, 80) } };
        var text = ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Click", desktop.Services()), """{"loc": [1, 1], "label": 4}"""));
        Assert.Equal("Single left clicked at (70,80).", text);
        Assert.Equal(["Click (70,80) Left 1"], desktop.Input.Calls);
    }

    [Theory]
    [InlineData("{}", "Either loc or label must be provided.")]
    [InlineData("""{"loc": [1]}""", "Location must be a list of exactly 2 integers [x, y]")]
    [InlineData("""{"loc": [1, 2], "clicks": -1}""", "clicks must be 0 or more")]
    [InlineData("""{"loc": [1, 2], "button": "top"}""", "button must be one of: left, right, middle")]
    [InlineData("""{"label": 9}""", "Failed to find element with label 9: Label 9 out of range")]
    public async Task ClickErrorsAreToolErrorsAndSendNothing(string json, string message)
    {
        var desktop = new FakeDesktop();
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Click", desktop.Services()), json);
        Assert.True(result.IsError);
        Assert.Equal($"Error calling tool 'Click': {message}", ToolCalls.Text(result));
        Assert.Empty(desktop.Input.Calls);
    }

    [Fact]
    public async Task ARejectedPointSendsNothing()
    {
        var desktop = new FakeDesktop { PointError = "outside" };
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Click", desktop.Services()), """{"loc": [1, 2]}""");
        Assert.Equal("Error calling tool 'Click': outside", ToolCalls.Text(result));
        Assert.Empty(desktop.Input.Calls);
    }

    [Fact]
    public async Task TargetsAreResolvedInsideTheLock()
    {
        var desktop = new FakeDesktop { BlockFirstCall = true, Labels = { [1] = new ScreenPoint(3, 3) } };
        var services = desktop.Services();
        var gate = new ControlGate(services.Control, services.Time);
        var first = ToolCalls.GatedCallAsync(gate, ToolCalls.Create("Click", services), """{"loc": [1, 1]}""");
        await desktop.FirstCallStarted;
        var second = ToolCalls.GatedCallAsync(gate, ToolCalls.Create("Click", services), """{"label": 1}""");
        await Task.Delay(100);
        Assert.Equal(["Point (1,1)"], desktop.Targets.Calls);
        desktop.ReleaseFirstCall();
        await Task.WhenAll(first, second);
        Assert.Equal(["Point (1,1)", "Label 1"], desktop.Targets.Calls);
    }
}
