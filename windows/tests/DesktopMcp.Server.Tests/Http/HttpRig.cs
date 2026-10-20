using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Control;
using DesktopMcp.Server.Http;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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

    /// <summary>True while the server's <c>RunAsync</c> has not completed.</summary>
    public bool HostIsRunning => !_serving.IsCompleted;

    /// <summary>
    /// The plan <paramref name="arguments"/> resolve to over <paramref name="config"/> or the defaults (never
    /// ~/.desktop-mcp/config.toml). Host and port are taken as given.
    /// </summary>
    public static ServePlan Plan(ServeArguments arguments, DesktopMcpConfig? config = null) => ServePlan.Resolve(arguments, config ?? new());

    /// <summary>
    /// A client that never uses a proxy: the server is on loopback and nothing here may leave the machine. Without
    /// <paramref name="followRedirects"/> a 302 is returned as is.
    /// </summary>
    public static HttpClient CreateClient(bool followRedirects = true) =>
        new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = followRedirects }) { Timeout = Timeout };

    /// <summary>
    /// A client for the HTTPS server that trusts only the certificate whose thumbprint is <paramref name="thumbprint"/>
    /// (no other certificate, and nothing about the process-wide validation, changes). With <paramref name="version"/>,
    /// every request uses exactly that HTTP version.
    /// </summary>
    public static HttpClient PinnedClient(string thumbprint, Version? version = null)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(certificate.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase);
        var client = new HttpClient(handler) { Timeout = Timeout };
        if (version is not null)
        {
            client.DefaultRequestVersion = version;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        return client;
    }

    /// <summary>The redirect URI <see cref="OAuthTokenAsync"/> registers; nothing ever connects to it.</summary>
    public const string OAuthRedirectUri = "http://127.0.0.1:9/cb";

    /// <summary>
    /// The OAuth code flow for client <c>client</c> with secret <c>s3cret</c>: <c>GET /oauth/authorize</c> without following
    /// the 302, the <c>code</c> from its <c>Location</c>, then <c>POST /oauth/token</c> as a form or as JSON. Returns
    /// <c>access_token</c>.
    /// </summary>
    public async Task<string> OAuthTokenAsync(string challenge, string verifier, bool form)
    {
        using var noRedirects = CreateClient(followRedirects: false);
        var authorize = new Uri(Base, "/oauth/authorize?response_type=code&client_id=client&redirect_uri="
            + Uri.EscapeDataString(OAuthRedirectUri) + "&code_challenge=" + Uri.EscapeDataString(challenge) + "&code_challenge_method=S256");
        using var redirect = await noRedirects.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        var location = redirect.Headers.Location!.OriginalString;
        Assert.StartsWith(OAuthRedirectUri + "?code=", location, StringComparison.Ordinal);
        var code = Uri.UnescapeDataString(location[(OAuthRedirectUri + "?code=").Length..]);

        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["client_id"] = "client",
            ["client_secret"] = "s3cret",
            ["redirect_uri"] = OAuthRedirectUri,
        };
        using HttpContent content = form
            ? new FormUrlEncodedContent(fields)
            : new StringContent(JsonSerializer.Serialize(fields, RigJson.Default.DictionaryStringString), Encoding.UTF8, "application/json");
        using var response = await noRedirects.PostAsync(new Uri(Base, "/oauth/token"), content);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

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
        Task first;
        try
        {
            first = await Task.WhenAny(listening.Task, serving).WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            await StopAsync(stop, serving);
            throw;
        }
        if (first != listening.Task)
        {
            stop.Dispose();
            throw new InvalidOperationException($"The server exited with {await serving} before listening: {announce}");
        }
        return new HttpRig(stop, serving, await listening.Task, announce.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// An SDK client for <see cref="Endpoint"/> over <paramref name="mode"/>, through <paramref name="http"/> (default
    /// <see cref="Http"/>). The transport owns <paramref name="http"/> when it is given.
    /// </summary>
    public async Task<McpClient> ConnectAsync(HttpTransportMode mode, IDictionary<string, string>? headers = null, McpClientOptions? options = null,
        HttpClient? http = null)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            TransportMode = mode,
            AdditionalHeaders = headers,
        }, http ?? Http, loggerFactory: null, ownsHttpClient: http is not null);
        using var timeout = new CancellationTokenSource(Timeout);
        return await McpClient.CreateAsync(transport, options, cancellationToken: timeout.Token);
    }

    /// <summary>
    /// Opens <c>GET /sse</c> and returns the <c>data</c> of its first <c>endpoint</c> event (5 s bound); the stream is
    /// closed afterwards.
    /// </summary>
    public async Task<string> ReadSseEndpointEventAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, "/sse"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        string? eventName = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Length == 0) eventName = null;
            else if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal) && eventName == "endpoint") return line["data:".Length..].Trim();
        }
        throw new InvalidOperationException("GET /sse ended without an endpoint event.");
    }

    /// <summary>
    /// Client options whose <c>notifications/message</c> handler completes <paramref name="received"/> with the
    /// notification's params: the first message, or the first whose <c>data.event</c> is <paramref name="eventName"/>.
    /// </summary>
    public static McpClientOptions OnLogMessage(TaskCompletionSource<JsonElement> received, string? eventName = null) =>
        OnLogMessage(message =>
        {
            if (eventName is null || EventOf(message) == eventName) received.TrySetResult(message);
        });

    /// <summary>Client options that pass the params of every <c>notifications/message</c> to <paramref name="heard"/>.</summary>
    public static McpClientOptions OnLogMessage(Action<JsonElement> heard) => new()
    {
        Handlers = new McpClientHandlers
        {
            NotificationHandlers =
            [
                new(McpLogging.MessageMethod, (notification, _) =>
                {
                    if (notification.Params is { } parameters)
                    {
                        using var document = JsonDocument.Parse(parameters.ToJsonString());
                        heard(document.RootElement.Clone());
                    }
                    return ValueTask.CompletedTask;
                }),
            ],
        },
    };

    /// <summary>The <c>data.event</c> of a <c>notifications/message</c>'s params, or null.</summary>
    public static string? EventOf(JsonElement message) =>
        message.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("event", out var name)
            ? name.GetString()
            : null;

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

    /// <summary>
    /// Sends <paramref name="method"/> to <paramref name="path"/> under <see cref="Base"/> with <paramref name="headers"/>
    /// added without validation (a <c>Host</c> header replaces the default one).
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, params (string Name, string Value)[] headers) =>
        SendWithHeadersAsync(new HttpRequestMessage(method, new Uri(Base, path)), headers);

    /// <summary>A raw <c>initialize</c> POST to <see cref="Endpoint"/> with <paramref name="headers"/> added without validation.</summary>
    public Task<HttpResponseMessage> SendInitializeAsync(params (string Name, string Value)[] headers) =>
        SendWithHeadersAsync(InitializeRequest(Endpoint), headers);

    /// <summary>Stops the server and asserts that it exited with 0.</summary>
    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        Assert.Equal(0, await StopAsync(_stop, _serving));
    }

    private async Task<HttpResponseMessage> SendWithHeadersAsync(HttpRequestMessage request, (string Name, string Value)[] headers)
    {
        using (request)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.Remove(name);
                Assert.True(request.Headers.TryAddWithoutValidation(name, value), $"could not set {name}");
            }
            return await Http.SendAsync(request);
        }
    }

    /// <summary>Cancels the server and waits for its exit code; the token source is disposed even when that times out.</summary>
    private static async Task<int> StopAsync(CancellationTokenSource stop, Task<int> serving)
    {
        try
        {
            await stop.CancelAsync();
            return await serving.WaitAsync(Timeout);
        }
        finally
        {
            stop.Dispose();
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class RigJson : JsonSerializerContext;
