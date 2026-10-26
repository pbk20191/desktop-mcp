using Microsoft.Win32;

namespace DesktopMcp.Server.Tests;

public sealed class RegistryToolTests : IDisposable
{
    private const string Parent = @"Software\desktop-mcp-tests";
    private readonly string _subKey = Parent + @"\" + Guid.NewGuid().ToString("N");

    /// <summary>Deletes the throwaway key, and its parent once no other test is using it.</summary>
    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(Parent);
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException) { }
    }

    private static Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(string json) => ToolCalls.CallAsync(ToolCalls.Create("Registry"), json);

    [Fact]
    public async Task SetThenGetThroughTheTool()
    {
        var path = (@"HKCU:\" + _subKey).Replace(@"\", @"\\");
        Assert.EndsWith("\"n\" set to \"7\" (type: DWord).", ToolCalls.OkText(await CallAsync($$"""{"mode": "set", "path": "{{path}}", "name": "n", "value": "7", "type": "DWord"}""")));
        Assert.EndsWith("\"n\" = 7", ToolCalls.OkText(await CallAsync($$"""{"mode": "get", "path": "{{path}}", "name": "n"}""")));
    }

    [Theory]
    [InlineData("""{"mode": "get", "path": "HKCU:\\Software"}""", "Error: name parameter is required for get mode.")]
    [InlineData("""{"mode": "set", "path": "HKCU:\\Software", "value": "x"}""", "Error: name parameter is required for set mode.")]
    [InlineData("""{"mode": "set", "path": "HKCU:\\Software", "name": "x"}""", "Error: value parameter is required for set mode.")]
    [InlineData("""{"mode": "rename", "path": "HKCU:\\Software"}""", "Error: mode must be \"get\", \"set\", \"delete\", or \"list\".")]
    public async Task MissingParametersUseUpstreamMessages(string json, string expected) =>
        Assert.Equal(expected, ToolCalls.OkText(await CallAsync(json)));
}
