using System.Net.Http.Headers;
using System.Text;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Http;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Client;

namespace DesktopMcp.Server.Tests.Http;

/// <summary>
/// The production <see cref="HttpServer"/> on real Kestrel, bound to 127.0.0.1 (or the loopback address the arguments
/// name) on an ephemeral port (Decisions 14; controller safety rules), with clients for it.
/// </summary>
public sealed class HttpRig : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stop;
    private readonly Task<int> _serving;

    private HttpRig(CancellationTokenSource stop, Task<int> serving, Uri endpoint, string announced)
    {
        _stop = stop;
        _serving = serving;
        Endpoint = endpoint;
        Base = new Uri(endpoint, "/");
        Announced = announced;
        Http = CreateClient();
    }

    /// <summary>The announced MCP endpoint, such as <c>http://127.0.0.1:53110/mcp</c>.</summary>
    public Uri Endpoint { get; }

    /// <summary>The server root, such as <c>http://127.0.0.1:53110/</c>.</summary>
    public Uri Base { get; }

    /// <summary>A client without default headers (no auth).</summary>
    public HttpClient Http { get; }

    /// <summary>The announcement line the server wrote (Decisions 8), without its line break.</summary>
    public string Announced { get; }

    /// <summary>
    /// The plan <paramref name="arguments"/> resolve to over <paramref name="config"/> or the defaults (never
    /// ~/.desktop-mcp/config.toml). Host and port are taken as given.
    /// </summary>
    public static ServePlan Plan(ServeArguments arguments, DesktopMcpConfig? config = null) => ServePlan.Resolve(arguments, config ?? new());

    /// <summary>A client that never uses a proxy: the server is on loopback and nothing here may leave the machine.</summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout };

    /// <summary>
    /// Serves <paramref name="arguments"/> on 127.0.0.1 (or 127.0.0.1/::1 when the arguments name it) on port 0 with
    /// <paramref name="services"/> (default: <see cref="ToolServices.CreateDefault"/>, control disabled).
    /// </summary>
    public static async Task<HttpRig> StartAsync(ServeArguments arguments, ToolServices? services = null, DesktopMcpConfig? config = null)
    {
        var plan = Plan(arguments, config) with { Host = arguments.Host is "127.0.0.1" or "::1" ? arguments.Host : "127.0.0.1", Port = 0 };
        var announce = new StringWriter();
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new CancellationTokenSource();
        var serving = Task.Run(() => HttpServer.RunAsync(plan, services ?? ToolServices.CreateDefault(), stop.Token, uri => listening.TrySetResult(uri), announce));
        var first = await Task.WhenAny(listening.Task, serving).WaitAsync(Timeout);
        if (first != listening.Task)
        {
            stop.Dispose();
            throw new InvalidOperationException($"The server exited with {await serving} before listening: {announce}");
        }
        return new HttpRig(stop, serving, await listening.Task, announce.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>An SDK client for <see cref="Endpoint"/> over <paramref name="mode"/>.</summary>
    public async Task<McpClient> ConnectAsync(HttpTransportMode mode, IDictionary<string, string>? headers = null, McpClientOptions? options = null)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            TransportMode = mode,
            AdditionalHeaders = headers,
        }, Http, loggerFactory: null, ownsHttpClient: false);
        using var timeout = new CancellationTokenSource(Timeout);
        return await McpClient.CreateAsync(transport, options, cancellationToken: timeout.Token);
    }

    /// <summary>A raw <c>initialize</c> POST to <paramref name="endpoint"/>.</summary>
    public static HttpRequestMessage InitializeRequest(Uri endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"rig","version":"1"}}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    /// <summary>Stops the server and asserts that it exited with 0.</summary>
    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _stop.CancelAsync();
        var exitCode = await _serving.WaitAsync(Timeout);
        _stop.Dispose();
        Assert.Equal(0, exitCode);
    }
}
