using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Server.Tests;

public class TypeToolTests
{
    [Fact]
    public async Task MatchingReadBackKeepsUpstreamsText()
    {
        var desktop = new FakeDesktop { ReadBack = "hi there" };
        Assert.Equal("Typed hi there at (5,6).", ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "hi there", "loc": [5, 6]}""")));
    }

    [Fact]
    public async Task AMismatchAppendsOneWarningLine()
    {
        var desktop = new FakeDesktop { ReadBack = "hi thre" };
        Assert.Equal("Typed hi there at (5,6).\nWarning: the focused element's value does not contain the typed text; it reads 'hi thre'.",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "hi there", "loc": [5, 6]}""")));
    }

    [Fact]
    public async Task PressEnterSkipsTheReadBack()
    {
        var desktop = new FakeDesktop { ReadBack = "" };
        ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "q", "loc": [5, 6], "press_enter": "true"}"""));
        Assert.Equal(0, desktop.ReadBackCalls);
    }

    [Fact]
    public async Task EmptyTextSkipsTheReadBack()
    {
        var desktop = new FakeDesktop { ReadBack = "left over" };
        Assert.Equal("Typed  at (5,6).",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "", "loc": [5, 6], "clear": true}""")));
        Assert.Equal(0, desktop.ReadBackCalls);
        Assert.Equal(["Type (5,6) clear "], desktop.Input.Calls);
    }

    [Fact]
    public async Task TheForegroundIsCheckedAfterTheClickAndBeforeTheKeys()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(5, 5) }, ForegroundFails = "Could not bring Notepad to the foreground; nothing was typed." };
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "x", "label": 0}""");
        Assert.Equal("Error calling tool 'Type': Could not bring Notepad to the foreground; nothing was typed.", ToolCalls.Text(result));
        Assert.Equal(["Type (5,5) click-only"], desktop.Input.Calls); // the fake stops at the after-click hook
        Assert.Equal(0, desktop.ReadBackCalls);
    }

    [Fact]
    public async Task TheForegroundCheckGetsTheTargetsWindow()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(5, 5) } };
        ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "x", "label": 0}"""));
        Assert.Equal(["1 Window"], desktop.ForegroundChecks);
    }

    [Fact]
    public async Task ALabelReportsTheLocatedPoint()
    {
        var desktop = new FakeDesktop { Labels = { [2] = new ScreenPoint(70, 80) } };
        Assert.Equal("Typed abc at (70,80).",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), """{"text": "abc", "loc": [1, 1], "label": 2, "clear": "true"}""")));
        Assert.Equal(["Type (70,80) clear abc"], desktop.Input.Calls);
    }

    [Fact]
    public async Task TargetsAreResolvedInsideTheLock()
    {
        var desktop = new FakeDesktop { BlockFirstCall = true };
        var services = desktop.Services();
        var first = ToolCalls.CallAsync(ToolCalls.Create("Click", services), """{"loc": [1, 1]}""");
        await desktop.FirstCallStarted;
        var second = ToolCalls.CallAsync(ToolCalls.Create("Type", services), """{"text": "x", "loc": [2, 2]}""");
        await Task.Delay(100);
        Assert.Equal(["Point (1,1)"], desktop.Targets.Calls);
        desktop.ReleaseFirstCall();
        await Task.WhenAll(first, second);
        Assert.Equal(["Point (1,1)", "Point (2,2)"], desktop.Targets.Calls);
    }

    [Theory]
    [InlineData("""{"text": "x", "loc": [1, 2], "clear": "yes"}""", "clear must be true or false")]
    [InlineData("""{"text": "x", "loc": [1, 2], "press_enter": 1}""", "press_enter must be true or false")]
    [InlineData("""{"text": "x", "loc": [1, 2], "caret_position": "middle"}""", "caret_position must be one of: start, idle, end")]
    [InlineData("""{"loc": [1, 2]}""", "Missing required argument: text")]
    [InlineData("""{"text": "x"}""", "Either loc or label must be provided.")]
    [InlineData("""{"text": "x", "loc": [1]}""", "Location must be a list of exactly 2 integers [x, y]")]
    [InlineData("""{"text": "x", "label": 9}""", "Failed to find element with label 9: Label 9 out of range")]
    public async Task TypeErrors(string json, string message)
    {
        var desktop = new FakeDesktop();
        Assert.Equal($"Error calling tool 'Type': {message}", ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("Type", desktop.Services()), json)));
        Assert.Empty(desktop.Input.Calls);
    }
}
