namespace DesktopMcp.Server.Tests;

public class ProcessToolTests
{
    private static Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(string json) => ToolCalls.CallAsync(ToolCalls.Create("Process"), json);

    [Fact]
    public async Task ListsWithALimitGivenAsAString()
    {
        // The PID column is as wide as the largest PID shown, so only the header's tail is fixed.
        var lines = ToolCalls.OkText(await CallAsync("""{"mode": "list", "limit": "1"}""")).Split('\n');
        Assert.Equal("Processes (1 shown):", lines[0]);
        Assert.Matches("^ *PID  Name +CPU%  +Memory$", lines[1]);
        Assert.Equal(4, lines.Length);
    }

    [Theory]
    [InlineData("""{"mode": "kill"}""", "Error: Provide either pid or name parameter for kill mode.")]
    [InlineData("""{"mode": "kill", "pid": 2147483644, "force": "yes"}""", "No process with PID 2147483644 found.")]
    [InlineData("""{"mode": "stop"}""", "Error: mode must be either \"list\" or \"kill\".")]
    public async Task UpstreamMessages(string json, string expected) => Assert.Equal(expected, ToolCalls.OkText(await CallAsync(json)));

    [Fact]
    public async Task ANegativePidIsAnError()
    {
        var result = await CallAsync("""{"mode": "kill", "pid": -1}""");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Process': pid must be a positive integer (got -1)", ToolCalls.Text(result));
    }
}
