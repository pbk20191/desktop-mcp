using System.Text.Json;
using DesktopMcp.Core.Control;

namespace DesktopMcp.Server.Tests;

public class ControlStatusToolTests
{
    [Fact]
    public async Task ReturnsTheStatusAsTextAndStructuredContent()
    {
        var status = new ControlStatus(ControlState.Ready, 3, 0, true, "relaxed", 10);
        var control = new FakeControl { Current = status };
        var result = await ToolCalls.CallAsync(ToolCalls.Create("ControlStatus", ToolServicesWith(control)), "{}");
        Assert.Equal(ControlJson.Status(status), ToolCalls.OkText(result));
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.False(structured.TryGetProperty("result", out _));
        Assert.Equal("ready", structured.GetProperty("state").GetString());
        Assert.Equal(3, structured.GetProperty("generation").GetInt64());
        Assert.Equal(10.0, structured.GetProperty("idle_timeout_seconds").GetDouble());
    }

    [Fact]
    public async Task ReadsTheStatusAtEachCall()
    {
        var control = new FakeControl { Current = new(ControlState.Ready, 1, 0, true, "relaxed", 10) };
        var tool = ToolCalls.Create("ControlStatus", ToolServicesWith(control));
        await ToolCalls.CallAsync(tool, "{}");
        control.Current = new(ControlState.User, 2, 4.5, true, "strict", 10);
        Assert.Equal("""{"state":"user","generation":2,"wait_seconds":4.5,"enabled":true,"policy":"strict","idle_timeout_seconds":10.0}""",
            ToolCalls.OkText(await ToolCalls.CallAsync(tool, "{}")));
    }

    private static DesktopMcp.Server.Tools.ToolServices ToolServicesWith(FakeControl control) =>
        DesktopMcp.Server.Tools.ToolServices.CreateDefault() with { Control = control };
}
