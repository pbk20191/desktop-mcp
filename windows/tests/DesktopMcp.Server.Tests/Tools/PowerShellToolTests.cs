namespace DesktopMcp.Server.Tests;

public class PowerShellToolTests
{
    [Fact]
    public async Task ReturnsTheResponseAndStatusCode()
    {
        var text = ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("PowerShell"), """{"command": "Write-Output ok", "timeout": "20"}"""));
        Assert.Equal("Response: ok\r\n\nStatus Code: 0", text);
    }

    [Fact]
    public async Task ATimeoutIsAResultNotAnError()
    {
        var text = ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("PowerShell"), """{"command": "Start-Sleep -Seconds 30", "timeout": 1}"""));
        Assert.Equal("Response: Command execution timed out\nStatus Code: 1", text);
    }

    [Fact]
    public async Task AMissingCommandIsAnError()
    {
        var result = await ToolCalls.CallAsync(ToolCalls.Create("PowerShell"), "{}");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'PowerShell': Missing required argument: command", ToolCalls.Text(result));
    }
}
