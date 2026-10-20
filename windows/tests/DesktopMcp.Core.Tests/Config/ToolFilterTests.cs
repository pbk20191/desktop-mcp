using DesktopMcp.Core.Config;

namespace DesktopMcp.Core.Tests;

public class ToolFilterTests
{
    private static readonly string[] Registered = ["ControlStatus", "App", "PowerShell", "Wait", "Registry"];

    [Fact]
    public void NoFilterKeepsEverything() => Assert.Equal(Registered, ToolFilter.Apply(Registered, [], []));

    [Fact]
    public void ExplicitToolsKeepOnlyThoseAndControlStatusInRegistryOrder() =>
        Assert.Equal(["ControlStatus", "Wait", "Registry"], ToolFilter.Apply(Registered, ["Registry", "Wait", "Nope"], []));

    [Fact]
    public void ExcludeNeverRemovesControlStatus() =>
        Assert.Equal(["ControlStatus", "App", "Wait", "Registry"], ToolFilter.Apply(Registered, [], ["PowerShell", "ControlStatus"]));

    [Fact]
    public void ExplicitToolsOverrideExclude() =>
        Assert.Equal(["ControlStatus", "PowerShell"], ToolFilter.Apply(Registered, ["PowerShell"], ["PowerShell"]));

    [Fact]
    public void NamesAreCaseSensitive() => Assert.Equal(["ControlStatus"], ToolFilter.Apply(Registered, ["wait"], []));
}
