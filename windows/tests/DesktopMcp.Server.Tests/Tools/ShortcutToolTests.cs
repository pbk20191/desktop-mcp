namespace DesktopMcp.Server.Tests;

public class ShortcutToolTests
{
    [Fact]
    public async Task PressedTextWithoutStructuredContent()
    {
        var desktop = new FakeDesktop();
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Shortcut", desktop.Services()), """{"shortcut": "ctrl+c"}""");
        Assert.Equal("Pressed ctrl+c.", ToolCalls.OkText(result));
        Assert.Null(result.StructuredContent);
        Assert.Equal(["Keys 4 10ms 500ms"], desktop.Input.Calls);
        Assert.Equal(1, desktop.Targets.ForegroundInputChecks);
    }

    [Fact]
    public async Task AParseErrorSendsNothing()
    {
        var desktop = new FakeDesktop();
        Assert.Equal("Error calling tool 'Shortcut': Invalid shortcut 'ctrl+': empty key name",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("Shortcut", desktop.Services()), """{"shortcut": "ctrl+"}""")));
        Assert.Empty(desktop.Input.Calls);
        Assert.Equal(0, desktop.Targets.ForegroundInputChecks);
    }

    [Fact]
    public async Task AMissingShortcutIsAnArgumentError()
    {
        var desktop = new FakeDesktop();
        Assert.Equal("Error calling tool 'Shortcut': Missing required argument: shortcut",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("Shortcut", desktop.Services()), "{}")));
        Assert.Empty(desktop.Input.Calls);
    }

    [Fact]
    public async Task ABlockedForegroundSendsNothing()
    {
        var desktop = new FakeDesktop { ForegroundInputError = "elevated" };
        Assert.Equal("Error calling tool 'Shortcut': elevated",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("Shortcut", desktop.Services()), """{"shortcut": "ctrl+c"}""")));
        Assert.Empty(desktop.Input.Calls);
    }

    [Fact]
    public async Task TheForegroundIsCheckedInsideTheLock()
    {
        var desktop = new FakeDesktop { BlockFirstCall = true };
        var services = desktop.Services();
        var first = ToolCalls.CallAsync(ToolCalls.Create("Click", services), """{"loc": [1, 1]}""");
        await desktop.FirstCallStarted;
        var second = ToolCalls.CallAsync(ToolCalls.Create("Shortcut", services), """{"shortcut": "ctrl+c"}""");
        await Task.Delay(100);
        Assert.Equal(0, desktop.Targets.ForegroundInputChecks);
        desktop.ReleaseFirstCall();
        await Task.WhenAll(first, second);
        Assert.Equal(1, desktop.Targets.ForegroundInputChecks);
    }
}
