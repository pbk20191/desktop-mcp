using System.Text.Json.Nodes;
using DesktopMcp.Core.Scrape;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

/// <summary>Scrape's <c>use_sampling</c> (P5 plan, Decisions 5) against a raw JSON-RPC client over in-memory stdio.</summary>
public class ScrapeSamplingTests
{
    private const string Url = "https://example.com/page";
    private sealed class FakeScraper : IWebScraper
    {
        public Task<string> FetchMarkdownAsync(string url, CancellationToken ct) => Task.FromResult("# Title\nbody");
    }

    private static JsonObject TextResult(string text) =>
        new() { ["role"] = "assistant", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text }, ["model"] = "m" };

    [Fact]
    public async Task TheSummaryReplacesTheRawContent()
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: true);
        rig.OnSampling = _ => TextResult("Summary");
        var text = await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url, ["query"] = "prices" });
        Assert.Equal($"URL: {Url}\nContent:\nSummary", text);
        var request = Assert.Single(rig.SamplingRequests);
        Assert.Equal(ScrapeTool.SystemPrompt + " Focus specifically on: prices.", request["systemPrompt"]!.GetValue<string>());
        Assert.Equal(2048, request["maxTokens"]!.GetValue<int>());
        var message = Assert.Single(request["messages"]!.AsArray())!;
        Assert.Equal("user", message["role"]!.GetValue<string>());
        Assert.Equal($"Raw scraped content from {Url}:\n\n# Title\nbody", message["content"]!["text"]!.GetValue<string>());
        Assert.False(request.ContainsKey("temperature") || request.ContainsKey("modelPreferences") || request.ContainsKey("includeContext"));
    }

    [Fact]
    public async Task WithoutAQueryThePromptHasNoFocus()
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: true);
        rig.OnSampling = _ => TextResult("S");
        await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url });
        Assert.Equal(ScrapeTool.SystemPrompt, rig.SamplingRequests.Single()["systemPrompt"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, "true")]     // client cannot sample: no request at all
    [InlineData(true, "false")]     // use_sampling false
    [InlineData(true, "no")]        // loose boolean: anything but true/"true" is false
    public async Task RawContentWithoutASamplingRequest(bool clientSamples, string useSampling)
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: clientSamples);
        rig.OnSampling = _ => TextResult("never");
        var text = await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url, ["use_sampling"] = useSampling });
        Assert.Equal($"URL: {Url}\nContent:\n# Title\nbody", text);
        Assert.Empty(rig.SamplingRequests);
    }

    public static TheoryData<JsonObject?> FailedResults => new()
    {
        null,                                                                                   // JSON-RPC error
        new JsonObject { ["role"] = "assistant", ["content"] = new JsonObject { ["type"] = "image", ["data"] = "AA==", ["mimeType"] = "image/png" }, ["model"] = "m" },
        new JsonObject { ["role"] = "assistant", ["model"] = "m" },
    };

    [Theory]
    [MemberData(nameof(FailedResults))]
    public async Task AFailedSamplingFallsBackToTheRawContent(JsonObject? result)
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: true);
        rig.OnSampling = _ => result?.DeepClone();
        Assert.Equal($"URL: {Url}\nContent:\n# Title\nbody", await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url }));
    }

    [Fact]
    public async Task AnArrayWithOneTextBlockIsAccepted()
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: true);
        rig.OnSampling = _ => new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Arr" }), ["model"] = "m" };
        Assert.Equal($"URL: {Url}\nContent:\nArr", await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url }));
    }

    [Fact]
    public async Task SamplingIsSkippedWhenDisabled()
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper(), SamplingEnabled = false }, sampling: true);
        rig.OnSampling = _ => TextResult("never");
        await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url });
        Assert.Empty(rig.SamplingRequests);
    }

    [Fact]
    public async Task ACancelledCallStopsWaitingForSampling()
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: true);
        var asked = new TaskCompletionSource();
        rig.OnSamplingAsync = async (_, ct) => { asked.SetResult(); await Task.Delay(Timeout.Infinite, ct); return null; };   // never answers
        var call = rig.StartToolCall("Scrape", new JsonObject { ["url"] = Url });
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.SendCancelledAsync(call.RequestId);
        await rig.AssertNoResponseOrCancelledWithinAsync(call, TimeSpan.FromSeconds(5));   // the call ends; the server stays responsive
        Assert.Equal("ok", await rig.PingAsync());
    }
}
