using System.Text;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public class ToolSpecCatalogTests
{
    private const string Sample = """
        {"tools":[
          {"name":"Wait","title":"Wait","description":"Waits.","inputSchema":{"type":"object","properties":{"duration":{"type":"integer"}},"required":["duration"]},
           "annotations":{"title":"Wait","readOnlyHint":true}},
          {"name":"Click","title":"Click","description":"Clicks.","inputSchema":{"type":"object","properties":{}}}
        ]}
        """;

    [Fact]
    public void ParseKeepsSpecOrder()
    {
        var catalog = ToolSpecCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Sample)));
        Assert.Equal(["Wait", "Click"], catalog.Names);
    }

    [Fact]
    public void GetReturnsTheProtocolTool()
    {
        var catalog = ToolSpecCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Sample)));
        var wait = catalog.Get("Wait");
        Assert.Equal("Wait", wait.Name);
        Assert.Equal("Waits.", wait.Description);
        Assert.True(wait.Annotations?.ReadOnlyHint);
        Assert.Equal("integer", wait.InputSchema.GetProperty("properties").GetProperty("duration").GetProperty("type").GetString());
    }

    [Fact]
    public void GetUnknownToolThrows()
    {
        var catalog = ToolSpecCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Sample)));
        Assert.Throws<KeyNotFoundException>(() => catalog.Get("Nope"));
    }

    [Fact]
    public void EmbeddedCatalogHasTheTwentyOneTools()
    {
        Assert.Equal(21, ToolSpecCatalog.LoadEmbedded().Names.Count);
    }

    [Fact]
    public void DuplicateToolNamesAreRejected()
    {
        const string duplicated = """{"tools":[{"name":"Wait","inputSchema":{"type":"object"}},{"name":"Wait","inputSchema":{"type":"object"}}]}""";
        var error = Assert.Throws<InvalidDataException>(() => ToolSpecCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(duplicated))));
        Assert.Equal("The tool contract lists 'Wait' more than once.", error.Message);
    }
}
