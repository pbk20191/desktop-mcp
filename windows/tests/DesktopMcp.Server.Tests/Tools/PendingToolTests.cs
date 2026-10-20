using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public class PendingToolTests
{
    [Fact]
    public async Task PendingToolReturnsAnErrorResult()
    {
        var result = await ToolCalls.CallAsync(new PendingTool(ToolSpecCatalog.LoadEmbedded().Get("Click")), "{}");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Click': not available in this build yet", ToolCalls.Text(result));
    }
}
