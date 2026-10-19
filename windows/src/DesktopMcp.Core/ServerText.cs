using System.Reflection;

namespace DesktopMcp.Core;

/// <summary>Identity the server reports in <c>initialize</c>.</summary>
public static class ServerText
{
    public const string Name = "desktop-mcp";

    // Upstream's instructions without the leading and trailing newline (see spec §10).
    public const string Instructions =
        "Windows MCP server provides tools to interact directly with the Windows desktop,\n" +
        "thus enabling to operate the desktop on the user's behalf.";

    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(ServerText).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
