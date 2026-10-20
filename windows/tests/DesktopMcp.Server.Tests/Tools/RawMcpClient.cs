using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Hosting;
using DesktopMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopMcp.Server.Tests;

/// <summary>
/// A JSON-RPC client written by hand over the in-memory stdio pipes (as <see cref="StdioServerTests"/>), so tests see
/// and answer the server's own requests (<c>sampling/createMessage</c>) as raw JSON, without the SDK's client, whose
/// sampling API is obsolete (MCP9005). It pins <c>protocolVersion</c> 2025-06-18: from 2026-07-28 there is no
/// <c>initialize</c> and the SDK sends server requests another way (MRTR).
/// </summary>
internal sealed class RawMcpClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(1);
    private static readonly ServePlan Plan = new()
    {
        Transport = "stdio", Host = "localhost", Port = 8000, Control = new ControlSettings(false, "relaxed", 10.0, 500, 500),
    };

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _serverRequests = new();
    private readonly List<JsonObject> _samplingRequests = [];
    private Task<int> _serving = Task.FromResult(0);
    private Task _reading = Task.CompletedTask;
    private long _nextId;

    private RawMcpClient() { }

    /// <summary>Answers <c>sampling/createMessage</c> with a <c>result</c>, or <c>null</c> for a JSON-RPC error -32603.</summary>
    public Func<JsonObject, JsonNode?>? OnSampling { get; set; }

    /// <summary>
    /// As <see cref="OnSampling"/>, asynchronously; the token is cancelled when the server cancels the request
    /// (<c>notifications/cancelled</c>) or the client is disposed. Takes precedence over <see cref="OnSampling"/>.
    /// </summary>
    public Func<JsonObject, CancellationToken, Task<JsonNode?>>? OnSamplingAsync { get; set; }

    /// <summary>The <c>params</c> of every <c>sampling/createMessage</c> request the server sent, in order.</summary>
    public IReadOnlyList<JsonObject> SamplingRequests
    {
        get { lock (_samplingRequests) return _samplingRequests.ToArray(); }
    }

    /// <summary>Serves <paramref name="services"/> over in-memory stdio and initializes, declaring sampling when asked.</summary>
    public static async Task<RawMcpClient> ServeAsync(ToolServices services, bool sampling)
    {
        var client = new RawMcpClient();
        client._serving = StdioServer.RunAsync(Plan, services,
            builder => builder.WithStreamServerTransport(client._clientToServer.Reader.AsStream(), client._serverToClient.Writer.AsStream()),
            client._stop.Token);
        client._reading = Task.Run(client.ReadLoopAsync);
        try
        {
            await client.InitializeAsync(sampling);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        return client;
    }

    /// <summary><c>initialize</c> with <c>{"sampling":{}}</c> or <c>{}</c> as the capabilities, then <c>notifications/initialized</c>.</summary>
    public async Task InitializeAsync(bool sampling)
    {
        var response = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = sampling ? new JsonObject { ["sampling"] = new JsonObject() } : new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "raw-test-client", ["version"] = "1.0" },
        }, _stop.Token);
        Assert.True(response.ContainsKey("result"), response.ToJsonString());
        await NotifyAsync("notifications/initialized", null);
    }

    /// <summary>The <c>tools/call</c> response message (with <c>result</c> or <c>error</c>).</summary>
    public Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken) =>
        StartToolCall(name, arguments).Response.WaitAsync(ResponseTimeout, cancellationToken);

    /// <summary>The single text block of a <c>tools/call</c> result that must not be an error.</summary>
    public async Task<string> CallToolTextAsync(string name, JsonObject arguments)
    {
        var response = await CallToolAsync(name, arguments, _stop.Token);
        Assert.True(response["result"] is JsonObject, response.ToJsonString());
        var result = response["result"]!.AsObject();
        var block = Assert.Single(result["content"]!.AsArray())!;
        Assert.Equal("text", block["type"]!.GetValue<string>());
        var text = block["text"]!.GetValue<string>();
        Assert.False(result["isError"]?.GetValue<bool>() == true, text);
        return text;
    }

    /// <summary>Sends <c>tools/call</c> without waiting for its response.</summary>
    public PendingCall StartToolCall(string name, JsonObject arguments)
    {
        var id = Interlocked.Increment(ref _nextId);
        var response = SendRequestAsync(id, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() });
        return new PendingCall(id, response);
    }

    /// <summary><c>notifications/cancelled</c> for the client's request <paramref name="requestId"/>.</summary>
    public Task SendCancelledAsync(long requestId) =>
        NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = requestId, ["reason"] = "test cancelled the call" });

    /// <summary>"ok" when the server answers <c>ping</c> with a result object.</summary>
    public async Task<string> PingAsync()
    {
        var response = await RequestAsync("ping", null, _stop.Token);
        return response["result"] is JsonObject ? "ok" : response.ToJsonString();
    }

    /// <summary>
    /// The cancelled <paramref name="call"/> gets no response within <paramref name="within"/> (the MCP rule for a
    /// cancelled request) or an error response; a result fails, and so does a server that no longer answers pings. The
    /// call must also have stopped waiting: by the end of <paramref name="within"/> the server has cancelled every
    /// sampling request still unanswered (<c>notifications/cancelled</c>), so none is outstanding. That check polls until
    /// the end of the window (and at least <see cref="CleanupGrace"/>), since a handler leaves the outstanding set only
    /// after its cancellation has run.
    /// </summary>
    public async Task AssertNoResponseOrCancelledWithinAsync(PendingCall call, TimeSpan within)
    {
        ArgumentNullException.ThrowIfNull(call);
        var deadline = DateTime.UtcNow + (within > CleanupGrace ? within : CleanupGrace);
        var finished = await Task.WhenAny(call.Response, Task.Delay(within, _stop.Token));
        if (finished == call.Response)
        {
            var response = await call.Response;
            Assert.True(response.ContainsKey("error"), $"A cancelled call returned a result: {response.ToJsonString()}");
        }
        while (!_serverRequests.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20, _stop.Token);
        Assert.True(_serverRequests.IsEmpty, "The server still waits for a sampling request after the call was cancelled.");
        Assert.Equal("ok", await PingAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var request in _serverRequests.Values) await CancelAsync(request);
        try
        {
            await _serving.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException) { }
        await _clientToServer.Writer.CompleteAsync();
        await _serverToClient.Writer.CompleteAsync();
        try
        {
            await _reading.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException) { }
        foreach (var pending in _pending.Values) pending.TrySetCanceled();
        _stop.Dispose();
        _writeLock.Dispose();
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken) =>
        await SendRequestAsync(Interlocked.Increment(ref _nextId), method, parameters).WaitAsync(ResponseTimeout, cancellationToken);

    private Task<JsonObject> SendRequestAsync(long id, string method, JsonObject? parameters)
    {
        var response = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[IdKey(id)] = response;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return WriteThenAwaitAsync(message, response.Task);
    }

    private async Task<JsonObject> WriteThenAwaitAsync(JsonObject message, Task<JsonObject> response)
    {
        await WriteAsync(message);
        return await response;
    }

    private Task NotifyAsync(string method, JsonObject? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return WriteAsync(message);
    }

    private async Task WriteAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await _writeLock.WaitAsync(_stop.Token);
        try
        {
            await _clientToServer.Writer.WriteAsync(bytes, _stop.Token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string IdKey(JsonNode? id) => id?.ToJsonString() ?? "null";

    private static string IdKey(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task ReadLoopAsync()
    {
        using var reader = new StreamReader(_serverToClient.Reader.AsStream(), Encoding.UTF8);
        try
        {
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (line.Length == 0) continue;
                if (JsonNode.Parse(line) is not JsonObject message) continue;
                var method = message["method"]?.GetValue<string>();
                var hasId = message.ContainsKey("id");
                if (method is null && hasId)
                {
                    if (_pending.TryRemove(IdKey(message["id"]), out var pending)) pending.TrySetResult(message);
                }
                else if (method is not null && hasId)
                {
                    var id = message["id"]!.DeepClone();
                    // Registered before the next line is read, so a notifications/cancelled for it always finds it.
                    var cancellation = method == McpSampling.CreateMessageMethod ? CancellationTokenSource.CreateLinkedTokenSource(_stop.Token) : null;
                    if (cancellation is not null) _serverRequests[IdKey(id)] = cancellation;
                    _ = Task.Run(() => AnswerAsync(method, id, message["params"]?.DeepClone() as JsonObject ?? [], cancellation));
                }
                else if (method == "notifications/cancelled"
                    && _serverRequests.TryGetValue(IdKey(message["params"]?["requestId"]), out var cancelled))
                {
                    await CancelAsync(cancelled);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    // The handler may have finished (and disposed its source) meanwhile.
    private static async Task CancelAsync(CancellationTokenSource source)
    {
        try
        {
            await source.CancelAsync();
        }
        catch (ObjectDisposedException) { }
    }

    // A request from the server: sampling goes to the handlers, ping is answered, anything else is method-not-found.
    private async Task AnswerAsync(string method, JsonNode id, JsonObject parameters, CancellationTokenSource? cancellation)
    {
        JsonObject response;
        if (method == "ping")
        {
            response = Result(id, new JsonObject());
        }
        else if (cancellation is not null)
        {
            using var owned = cancellation;
            lock (_samplingRequests) _samplingRequests.Add((JsonObject)parameters.DeepClone());
            JsonNode? result;
            try
            {
                result = OnSamplingAsync is { } handler
                    ? await handler(parameters, cancellation.Token)
                    : OnSampling?.Invoke(parameters);
            }
            catch (OperationCanceledException)
            {
                return;   // the server cancelled the request: no response (the MCP rule)
            }
            finally
            {
                _serverRequests.TryRemove(IdKey(id), out _);
            }
            response = result is null ? Error(id, -32603, "sampling failed") : Result(id, result);
        }
        else
        {
            response = Error(id, -32601, $"Method not found: {method}");
        }
        try
        {
            await WriteAsync(response);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    private static JsonObject Error(JsonNode id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    /// <summary>A <c>tools/call</c> sent by <see cref="StartToolCall"/>.</summary>
    public sealed record PendingCall(long RequestId, Task<JsonObject> Response);
}
