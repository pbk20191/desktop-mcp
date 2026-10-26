namespace DesktopMcp.Server.Tests;

public class ScrapeToolTests
{
    private static Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(string json) => ToolCalls.CallAsync(ToolCalls.Create("Scrape"), json);

    [Theory]
    [InlineData("""{"url": "https://example.com", "use_dom": true}""")]
    [InlineData("""{"url": "https://example.com", "use_dom": "TRUE"}""")]
    public async Task UseDomReturnsUpstreamsNoDomMessage(string json) =>
        Assert.Equal("No DOM information found. Please open https://example.com in browser first.", ToolCalls.OkText(await CallAsync(json)));

    [Theory]
    [InlineData("""{"url": "http://127.0.0.1:9/"}""")]
    [InlineData("""{"url": "http://127.0.0.1:9/", "use_dom": "yes", "use_sampling": false}""")]
    public async Task ABlockedAddressIsAnErrorResult(string json)
    {
        var result = await CallAsync(json);
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Scrape': Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1", ToolCalls.Text(result));
    }

    [Fact]
    public async Task ASchemeErrorIsAnErrorResult()
    {
        var result = await CallAsync("""{"url": "file:///C:/Windows/win.ini"}""");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Scrape': URL scheme 'file' is not allowed; use http or https.", ToolCalls.Text(result));
    }

    [Fact]
    public async Task UrlIsRequired()
    {
        var result = await CallAsync("{}");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Scrape': Missing required argument: url", ToolCalls.Text(result));
    }
}
