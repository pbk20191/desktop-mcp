using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Server.Tests;

/// <summary>
/// Every input tool checks the desktop session (P2 Decisions 18) inside the input lock before it resolves a target, so a
/// locked screen or a secure desktop gives the session text, not the elevated advice, and nothing is resolved or sent.
/// </summary>
public class ActSessionTests
{
    private const string Locked = "The desktop is not available: the screen is locked or a secure desktop (such as a UAC prompt) is showing.";

    [Theory]
    [InlineData("Click", """{"label": 0}""")]
    [InlineData("Click", """{"loc": [1, 2]}""")]
    [InlineData("Type", """{"loc": [1, 2], "text": "a"}""")]
    [InlineData("Scroll", """{"loc": [1, 2]}""")]
    [InlineData("Move", """{"loc": [1, 2]}""")]
    [InlineData("Move", """{"loc": [1, 2], "drag": true}""")]
    [InlineData("Shortcut", """{"shortcut": "ctrl+c"}""")]
    [InlineData("MultiSelect", """{"labels": [0]}""")]
    [InlineData("MultiEdit", """{"locs": [[1, 2, "a"]]}""")]
    [InlineData("App", """{"mode": "switch", "name": "notepad"}""")]
    [InlineData("App", """{"mode": "resize"}""")]
    [InlineData("App", """{"name": "notepad"}""")]
    [InlineData("App", """{"mode": "launch_executable", "executable": "a.exe"}""")]
    public async Task ALockedScreenGivesTheSessionTextAndResolvesAndSendsNothing(string tool, string json)
    {
        var desktop = new FakeDesktop { Labels = { [0] = new ScreenPoint(1, 2) }, SessionError = Locked };
        var result = await ToolCalls.CallAsync(ToolCalls.Create(tool, desktop.Services()), json);
        Assert.True(result.IsError);
        Assert.Equal($"Error calling tool '{tool}': {Locked}", ToolCalls.Text(result));
        Assert.Equal(1, desktop.Targets.SessionChecks);
        Assert.Empty(desktop.Targets.Calls);
        Assert.Equal(0, desktop.Targets.ForegroundInputChecks);
        Assert.Empty(desktop.Input.Calls);
        Assert.Empty(desktop.AppCalls);
    }

    [Theory]
    [InlineData("Click", """{"loc": [1, 2]}""")]
    [InlineData("App", """{"mode": "resize"}""")]
    public async Task AnAvailableDesktopIsCheckedOncePerCall(string tool, string json)
    {
        var desktop = new FakeDesktop();
        ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create(tool, desktop.Services()), json));
        Assert.Equal(1, desktop.Targets.SessionChecks);
    }
}
