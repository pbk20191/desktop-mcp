using DesktopMcp.Core;

namespace DesktopMcp.Core.Tests;

public class ServerTextTests
{
    [Fact]
    public void NameIsDesktopMcp() => Assert.Equal("desktop-mcp", ServerText.Name);

    [Fact]
    public void InstructionsAreUpstreamTextWithoutOuterNewlines()
    {
        Assert.Equal(
            "Windows MCP server provides tools to interact directly with the Windows desktop,\n" +
            "thus enabling to operate the desktop on the user's behalf.",
            ServerText.Instructions);
    }

    [Fact]
    public void VersionHasNoBuildMetadata()
    {
        Assert.Equal("0.1.0", ServerText.Version);
    }
}
