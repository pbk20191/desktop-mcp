using System.Text.Json;
using DesktopMcp.Core.Control;

namespace DesktopMcp.Core.Tests;

public class ControlPolicyTests
{
    [Theory]
    [InlineData("relaxed", "Click", null, true)]
    [InlineData("relaxed", "PowerShell", null, false)]
    [InlineData("balanced", "PowerShell", null, true)]
    [InlineData("balanced", "Snapshot", null, false)]
    [InlineData("strict", "Snapshot", null, true)]
    [InlineData("strict", "ControlStatus", null, false)]
    [InlineData("relaxed", "App", "launch", false)]
    [InlineData("balanced", "App", "launch_executable", true)]
    [InlineData("relaxed", "App", "switch", true)]
    [InlineData("relaxed", "Process", "kill", false)]
    [InlineData("balanced", "Process", "kill", true)]
    [InlineData("balanced", "Clipboard", "get", false)]
    [InlineData("relaxed", "Process", null, true)]
    [InlineData("relaxed", "App", "fly", true)]
    [InlineData("relaxed", "SomeNewTool", null, true)]
    public void UpstreamsPolicyTable(string policy, string tool, string? mode, bool gated) =>
        Assert.Equal(gated, ControlPolicy.RequiresControl(policy, tool, mode));

    [Fact]
    public void ModeDefaultsFollowUpstream()
    {
        static IDictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.Equal("launch", ControlPolicy.ModeOf("App", null));
        Assert.Equal("launch", ControlPolicy.ModeOf("App", Args("{}")));
        Assert.Null(ControlPolicy.ModeOf("App", Args("""{"mode": null}""")));
        Assert.Null(ControlPolicy.ModeOf("Process", Args("{}")));
        Assert.Null(ControlPolicy.ModeOf("Process", Args("""{"mode": 3}""")));
        Assert.Equal("kill", ControlPolicy.ModeOf("Process", Args("""{"mode": "kill"}""")));
    }

    [Fact]
    public void AnUnknownPolicyIsAnError() =>
        Assert.Equal("Unknown control policy: loose", Assert.Throws<ArgumentException>(() => ControlPolicy.RequiresControl("loose", "Click", null)).Message);
}
