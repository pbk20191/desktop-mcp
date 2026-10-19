using System.Diagnostics;
using System.Text.Json;

namespace DesktopMcp.Server.Tests;

public class WaitToolTests
{
    [Fact]
    public async Task WaitsAndReportsTheDuration()
    {
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Wait"), """{"duration": 0}""");
        Assert.Equal("Waited for 0 seconds.", ToolCalls.OkText(result));
        Assert.Equal("Waited for 0 seconds.", Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("result").GetString());
    }

    [Fact]
    public async Task AcceptsAnIntegerString() =>
        Assert.Equal("Waited for 1 seconds.", ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Wait"), """{"duration": "1"}""")));

    [Theory]
    [InlineData("""{"duration": -1}""", "Error calling tool 'Wait': sleep length must be non-negative")]
    [InlineData("""{"duration": 99999999999}""", "Error calling tool 'Wait': sleep length is too large")]
    [InlineData("""{"duration": 1.5}""", "Error calling tool 'Wait': duration must be an integer")]
    [InlineData("{}", "Error calling tool 'Wait': Missing required argument: duration")]
    public async Task InvalidDurationsAreErrorResults(string arguments, string expected)
    {
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Wait"), arguments);
        Assert.True(result.IsError);
        Assert.Equal(expected, ToolCalls.Text(result));
    }

    // SpecTool rethrows OperationCanceledException when the SDK's token is cancelled (P1 follow-up).
    [Fact]
    public async Task CancellationPropagatesThroughSpecTool()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToolCalls.CallAsync(ToolCalls.Create("Wait"), """{"duration": 30}""", cancellation.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"cancellation took {clock.Elapsed}");
    }
}
