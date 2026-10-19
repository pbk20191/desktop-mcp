using DesktopMcp.Core.FileSystem;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public sealed class FileSystemToolTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dmcp-fs-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(string json) =>
        ToolCalls.CallAsync(ToolCalls.Create("FileSystem", ToolServices.CreateDefault() with { FileSystem = new FileSystemService(_root, () => true) }), json);

    [Fact]
    public async Task RelativePathsResolveFromTheDefaultDirectory()
    {
        var written = ToolCalls.OkText(await CallAsync("""{"mode": "write", "path": "notes.txt", "content": "hi"}"""));
        Assert.StartsWith("Written to ", written);
        Assert.EndsWith(@"\notes.txt (2 bytes)", written);
        Assert.EndsWith("\nhi", ToolCalls.OkText(await CallAsync("""{"mode": "read", "path": "notes.txt"}""")));
    }

    [Fact]
    public async Task BooleanStringsAreStrict()
    {
        Assert.StartsWith("Directory ", ToolCalls.OkText(await CallAsync("""{"mode": "list", "path": ".", "recursive": "TRUE"}""")));
        var result = await CallAsync("""{"mode": "list", "path": ".", "recursive": "yes"}""");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'FileSystem': recursive must be true or false", ToolCalls.Text(result));
    }

    [Theory]
    [InlineData("""{"mode": "write", "path": "a.txt"}""", "Error: content parameter is required for write mode.")]
    [InlineData("""{"mode": "copy", "path": "a.txt"}""", "Error: destination parameter is required for copy mode.")]
    [InlineData("""{"mode": "move", "path": "a.txt"}""", "Error: destination parameter is required for move mode.")]
    [InlineData("""{"mode": "search", "path": "."}""", "Error: pattern parameter is required for search mode.")]
    [InlineData("""{"mode": "zip", "path": "."}""", "Error: Unknown mode \"zip\". Use: read, write, copy, move, delete, list, search, info.")]
    public async Task MissingParametersUseUpstreamMessages(string json, string expected) =>
        Assert.Equal(expected, ToolCalls.OkText(await CallAsync(json)));
}
