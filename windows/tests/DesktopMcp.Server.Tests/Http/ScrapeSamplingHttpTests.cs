using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Scrape;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Client;

namespace DesktopMcp.Server.Tests.Http;

/// <summary>
/// Scrape's sampling over HTTP as raw JSON-RPC (P5 plan, Decisions 5): a stateful session gets the
/// <c>sampling/createMessage</c> on the tool call's own POST stream; stateless HTTP never sends one; legacy SSE sends it on
/// the <c>GET /sse</c> stream and takes the answer through <c>POST /message</c>.
/// </summary>
[Collection(HttpHostCollection.Name)]
public class ScrapeSamplingHttpTests
{
    private const string Url = "https://example.com/page";
    private const string ProtocolVersion = "2025-06-18";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed class FakeScraper : IWebScraper
    {
        public Task<string> FetchMarkdownAsync(string url, CancellationToken ct) => Task.FromResult("# Title\nbody");
    }

    private static ToolServices Services() => ToolServices.CreateDefault() with { Scrape = new FakeScraper() };

    [Fact]
    public async Task StatefulHttpSendsSamplingOnTheCallsStream()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" }, Services());
        using var timeout = new CancellationTokenSource(Timeout);
        var session = await InitializeAsync(rig, timeout.Token);
        Assert.NotNull(session);

        using var call = Post(rig, session, ToolCall());
        using var response = await rig.Http.SendAsync(call, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sampled = 0;
        JsonObject? result = null;
        await foreach (var message in MessagesAsync(response, timeout.Token))
        {
            if (message["method"]?.GetValue<string>() == McpSampling.CreateMessageMethod)
            {
                sampled++;
                Assert.Equal(ScrapeTool.SystemPrompt, message["params"]!["systemPrompt"]!.GetValue<string>());
                // The client answers the server's request with a POST of its own (Streamable HTTP).
                using var answer = Post(rig, session, SamplingAnswer(message));
                using var accepted = await rig.Http.SendAsync(answer, timeout.Token);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }
            else if (message["id"]?.ToJsonString() == "2")
            {
                result = message;
                break;
            }
        }
        Assert.Equal(1, sampled);
        Assert.Equal($"URL: {Url}\nContent:\nSummary", Text(result));
    }

    [Fact]
    public async Task StatelessHttpNeverSendsSampling()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", StatelessHttp = true }, Services());
        using var timeout = new CancellationTokenSource(Timeout);
        Assert.Null(await InitializeAsync(rig, timeout.Token));

        using var call = Post(rig, null, ToolCall());
        using var response = await rig.Http.SendAsync(call, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var messages = new List<JsonObject>();
        await foreach (var message in MessagesAsync(response, timeout.Token)) messages.Add(message);
        Assert.DoesNotContain(messages, m => m["method"]?.GetValue<string>() == McpSampling.CreateMessageMethod);
        Assert.Equal($"URL: {Url}\nContent:\n# Title\nbody", Text(Assert.Single(messages, m => m["id"]?.ToJsonString() == "2")));
    }

    [Fact]
    public async Task APendingSamplingRequestDoesNotBlockAnotherSessionsCall()
    {
        // Final review I1 (Decisions 22): with control disabled (the rig's default), Scrape runs without the desktop lock.
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" }, Services());
        using var timeout = new CancellationTokenSource(Timeout);
        var session = await InitializeAsync(rig, timeout.Token);

        using var call = Post(rig, session, ToolCall());
        using var response = await rig.Http.SendAsync(call, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await using var messages = MessagesAsync(response, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await messages.MoveNextAsync());
        var request = messages.Current;
        Assert.Equal(McpSampling.CreateMessageMethod, request["method"]?.GetValue<string>());

        // The sampling request is unanswered: another session's call still completes.
        await using (var other = await rig.ConnectAsync(HttpTransportMode.StreamableHttp))
        {
            var wait = await other.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 }, cancellationToken: timeout.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
            Assert.False(wait.IsError ?? false);
        }

        using var answer = Post(rig, session, SamplingAnswer(request));
        using var accepted = await rig.Http.SendAsync(answer, timeout.Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        JsonObject? result = null;
        while (result is null && await messages.MoveNextAsync())
            if (messages.Current["id"]?.ToJsonString() == "2") result = messages.Current;
        Assert.Equal($"URL: {Url}\nContent:\nSummary", Text(result));
    }

    [Fact]
    public async Task LegacySseSendsSamplingOnTheSseStream()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse" }, Services());
        using var timeout = new CancellationTokenSource(Timeout);
        using var open = new HttpRequestMessage(HttpMethod.Get, new Uri(rig.Base, "/sse"));
        open.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var stream = await rig.Http.SendAsync(open, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        await using var events = SseEventsAsync(stream, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("endpoint", events.Current.Event);
        var messageUri = new Uri(rig.Base, events.Current.Data);

        async Task<JsonObject> NextMessageAsync()
        {
            while (await events.MoveNextAsync())
                if (events.Current.Event == "message" && JsonNode.Parse(events.Current.Data) is JsonObject message) return message;
            throw new InvalidOperationException("GET /sse ended.");
        }

        async Task PostAsync(JsonObject message)
        {
            using var content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
            using var posted = await rig.Http.PostAsync(messageUri, content, timeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        }

        await PostAsync(InitializeMessage());
        Assert.True((await NextMessageAsync())["result"] is JsonObject);
        await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });

        // The request goes out on GET /sse, and the answer comes back through POST /message.
        await PostAsync(ToolCall());
        var request = await NextMessageAsync();
        Assert.Equal(McpSampling.CreateMessageMethod, request["method"]?.GetValue<string>());
        Assert.Equal(ScrapeTool.SystemPrompt, request["params"]!["systemPrompt"]!.GetValue<string>());
        await PostAsync(SamplingAnswer(request));
        JsonObject? result = null;
        while (result is null)
        {
            var message = await NextMessageAsync();
            if (message["id"]?.ToJsonString() == "2") result = message;
        }
        Assert.Equal($"URL: {Url}\nContent:\nSummary", Text(result));
    }

    private static JsonObject SamplingAnswer(JsonObject request) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = request["id"]!.DeepClone(),
        ["result"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Summary" },
            ["model"] = "m",
        },
    };

    /// <summary>The events of an SSE stream as (event name, data); the name defaults to <c>message</c>.</summary>
    private static async IAsyncEnumerable<(string Event, string Data)> SseEventsAsync(HttpResponseMessage response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        string? name = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan("data:".Length).TrimStart(' '));
            }
            else if (line.Length == 0)
            {
                if (data.Length > 0) yield return (name ?? "message", data.ToString());
                name = null;
                data.Clear();
            }
        }
    }

    private static JsonObject ToolCall() => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 2,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = "Scrape", ["arguments"] = new JsonObject { ["url"] = Url } },
    };

    /// <summary><c>initialize</c> declaring sampling, then <c>notifications/initialized</c>; returns the session id, if any.</summary>
    private static async Task<string?> InitializeAsync(HttpRig rig, CancellationToken ct)
    {
        using var request = Post(rig, null, InitializeMessage());
        using var response = await rig.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.Single() : null;
        var initialized = false;
        await foreach (var message in MessagesAsync(response, ct)) initialized |= message["result"] is JsonObject;
        Assert.True(initialized);

        using var notification = Post(rig, session, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        using var accepted = await rig.Http.SendAsync(notification, ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        return session;
    }

    /// <summary><c>initialize</c> declaring sampling.</summary>
    private static JsonObject InitializeMessage() => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject { ["sampling"] = new JsonObject() },
            ["clientInfo"] = new JsonObject { ["name"] = "raw-http-client", ["version"] = "1.0" },
        },
    };

    private static HttpRequestMessage Post(HttpRig rig, string? session, JsonObject message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, rig.Endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
        return request;
    }

    /// <summary>The JSON-RPC messages of a response: one JSON body, or each SSE event's <c>data</c>.</summary>
    private static async IAsyncEnumerable<JsonObject> MessagesAsync(HttpResponseMessage response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            yield return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
            yield break;
        }
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan("data:".Length).TrimStart(' '));
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                if (JsonNode.Parse(data.ToString()) is JsonObject message) yield return message;
                data.Clear();
            }
        }
    }

    private static string Text(JsonObject? response)
    {
        Assert.NotNull(response);
        Assert.True(response["result"] is JsonObject, response.ToJsonString());
        var block = Assert.Single(response["result"]!["content"]!.AsArray())!;
        return block["text"]!.GetValue<string>();
    }
}
