using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Server.Tests;

public class MultiToolTests
{
    [Fact]
    public async Task LocsComeBeforeLabels()
    {
        var desktop = new FakeDesktop { Labels = { [3] = new ScreenPoint(30, 30) } };
        Assert.Equal("Multi-selected elements at:\n(1,2)\n(30,30)",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("MultiSelect", desktop.Services()), """{"locs": [[1, 2]], "labels": "[3]", "press_ctrl": "false"}""")));
        Assert.Equal(["MultiSelect ctrl=False 2"], desktop.Input.Calls);
    }

    [Fact]
    public async Task OutOfRangeLabelsFailBeforeAnyInput()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(1, 1) } };
        Assert.Equal("Error calling tool 'MultiSelect': Failed to resolve labels [0, 9]: Label 9 out of range",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("MultiSelect", desktop.Services()), """{"labels": [0, 9]}""")));
        Assert.Empty(desktop.Input.Calls);
    }

    [Fact]
    public async Task AStaleLabelMidwayNamesTheProgress()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(1, 1), [1] = new ScreenPoint(2, 2) }, StaleLabels = { 1 } };
        Assert.Equal("Error calling tool 'MultiSelect': Label 1 is stale: button \"OK\" is no longer in Notepad. Call Snapshot again to get current labels. Stopped after 1 of 2 items.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("MultiSelect", desktop.Services()), """{"labels": [0, 1]}""")));
    }

    [Fact]
    public async Task MultiEditTypesEachItemWithClear()
    {
        var desktop = new FakeDesktop { Labels = { [2] = new ScreenPoint(9, 9) }, ReadBackEchoesTypedText = true };
        Assert.Equal("Multi-edited elements at: (1,2) with text 'a', (9,9) with text 'b'",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("MultiEdit", desktop.Services()), """{"locs": [[1, 2, "a"]], "labels": [[2, "b"]]}""")));
        Assert.Equal(["Type (1,2) clear a", "Type (9,9) clear b"], desktop.Input.Calls);
    }

    [Fact]
    public async Task MultiEditWarnsPerMismatchingItem()
    {
        var desktop = new FakeDesktop { ReadBack = "zz" };
        Assert.Equal("Multi-edited elements at: (1,2) with text 'a'\nWarning: the focused element's value at (1,2) does not contain the typed text; it reads 'zz'.",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("MultiEdit", desktop.Services()), """{"locs": [[1, 2, "a"]]}""")));
    }

    [Fact]
    public async Task MultiEditWarningsFollowTheTextInItemOrder()
    {
        var desktop = new FakeDesktop { ReadBack = "zz", Labels = { [0] = new ScreenPoint(7, 8) } };
        Assert.Equal("Multi-edited elements at: (1,2) with text 'a', (7,8) with text 'b'\n"
            + "Warning: the focused element's value at (1,2) does not contain the typed text; it reads 'zz'.\n"
            + "Warning: the focused element's value at (7,8) does not contain the typed text; it reads 'zz'.",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("MultiEdit", desktop.Services()), """{"locs": [[1, 2, "a"]], "labels": [[0, "b"]]}""")));
    }

    [Fact]
    public async Task MultiEditChecksEachItemsWindowAndUsesIdleCaretWithoutEnter()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(7, 8) } };
        ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("MultiEdit", desktop.Services()), """{"locs": [[1, 2, "a"]], "labels": [[0, "b"]]}"""));
        Assert.Equal(["2 Window", "1 Window"], desktop.ForegroundChecks);
        Assert.Equal(["Idle False", "Idle False"], desktop.TypeOptions);
    }

    [Fact]
    public async Task MultiEditStaleLabelMidwayNamesTheProgress()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(7, 8) }, StaleLabels = { 0 } };
        Assert.Equal("Error calling tool 'MultiEdit': Label 0 is stale: button \"OK\" is no longer in Notepad. Call Snapshot again to get current labels. Stopped after 1 of 2 items.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("MultiEdit", desktop.Services()), """{"locs": [[1, 2, "a"]], "labels": [[0, "b"]]}""")));
        Assert.Equal(["Type (1,2) clear a"], desktop.Input.Calls);
    }

    [Fact]
    public async Task AFailureOnTheFirstItemHasNoProgressSuffix()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(7, 8) }, StaleLabels = { 0 } };
        Assert.Equal("Error calling tool 'MultiSelect': Label 0 is stale: button \"OK\" is no longer in Notepad. Call Snapshot again to get current labels.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("MultiSelect", desktop.Services()), """{"labels": [0]}""")));
        Assert.Equal("Error calling tool 'MultiEdit': Label 0 is stale: button \"OK\" is no longer in Notepad. Call Snapshot again to get current labels.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("MultiEdit", desktop.Services()), """{"labels": [[0, "b"]]}""")));
    }

    [Fact]
    public async Task APointErrorMidwayNamesTheProgress()
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(7, 8) }, PointErrorAt = new ScreenPoint(5, 5), PointError = "Point (5,5) is outside the screen." };
        Assert.Equal("Error calling tool 'MultiSelect': Point (5,5) is outside the screen. Stopped after 1 of 2 items.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("MultiSelect", desktop.Services()), """{"locs": [[1, 1], [5, 5]]}""")));
    }

    [Fact]
    public async Task MultiSelectPressesCtrlByDefault()
    {
        var desktop = new FakeDesktop();
        Assert.Equal("Multi-selected elements at:\n(1,1)",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("MultiSelect", desktop.Services()), """{"locs": [[1, 1]]}""")));
        Assert.Equal(["MultiSelect ctrl=True 1"], desktop.Input.Calls);
    }

    [Fact]
    public async Task TargetsAreResolvedInsideTheLock()
    {
        var desktop = new FakeDesktop { BlockFirstCall = true, Labels = { [0] = new ScreenPoint(3, 3) } };
        var services = desktop.Services();
        var first = ToolCalls.CallAsync(ToolCalls.Create("Click", services), """{"loc": [1, 1]}""");
        await desktop.FirstCallStarted;
        var select = ToolCalls.CallAsync(ToolCalls.Create("MultiSelect", services), """{"locs": [[2, 2]], "labels": [0]}""");
        var edit = ToolCalls.CallAsync(ToolCalls.Create("MultiEdit", services), """{"locs": [[4, 4, "x"]]}""");
        await Task.Delay(100);
        Assert.Equal(["Point (1,1)"], desktop.Targets.Calls);
        desktop.ReleaseFirstCall();
        await Task.WhenAll(first, select, edit);
        var calls = desktop.Targets.Calls;
        Assert.Equal("Point (1,1)", calls[0]);
        Assert.Equal(["Label 0", "Point (2,2)", "Point (4,4)"], calls.Skip(1).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("MultiSelect", """{}""", "Either locs or labels must be provided.")]
    [InlineData("MultiSelect", """{"locs": [[1, 2]], "press_ctrl": "maybe"}""", "press_ctrl must be true or false")]
    [InlineData("MultiSelect", """{"labels": [9], "press_ctrl": "maybe"}""", "Failed to resolve labels [9]: Label 9 out of range")]
    [InlineData("MultiSelect", """{"locs": [[1]]}""", "Each loc must be a list of exactly 2 integers [x, y]")]
    [InlineData("MultiSelect", """{"labels": ["a"]}""", "labels must be a list of integers")]
    [InlineData("MultiEdit", """{}""", "Either locs or labels must be provided.")]
    [InlineData("MultiEdit", """{"labels": [[0, "a"], [9, "b"]]}""", "Failed to process labels: Label 9 out of range")]
    [InlineData("MultiEdit", """{"locs": [[1, 2]]}""", "Each loc item must be [x, y, text]. Invalid: [1, 2]")]
    [InlineData("MultiEdit", """{"labels": [[0]]}""", "Each label item must be [label, text]. Invalid: [0]")]
    public async Task MultiErrors(string tool, string json, string message)
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(1, 1) } };
        Assert.Equal($"Error calling tool '{tool}': {message}", ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create(tool, desktop.Services()), json)));
        Assert.Empty(desktop.Input.Calls);
        Assert.Empty(desktop.Targets.Calls);
    }
}
