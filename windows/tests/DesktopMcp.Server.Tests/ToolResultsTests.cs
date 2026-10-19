using System.Text.Json;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tests;

public class ToolResultsTests
{
    [Fact]
    public void TextCarriesContentAndStructuredResult()
    {
        var result = ToolResults.Text("hello");
        Assert.Equal("hello", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("hello", structured.GetProperty("result").GetString());
        Assert.Single(structured.EnumerateObject());
    }
}
