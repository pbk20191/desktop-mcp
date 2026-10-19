namespace DesktopMcp.Server.Tests;

public class PendingToolTests
{
    [Fact]
    public async Task PendingToolReturnsAnErrorResult()
    {
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Click"), "{}");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Click': not available in this build yet", ToolCalls.Text(result));
    }
}
