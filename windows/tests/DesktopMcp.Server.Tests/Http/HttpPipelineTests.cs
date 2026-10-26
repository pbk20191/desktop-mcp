using System.Net;
using System.Net.Sockets;
using System.Text;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Client;
using Xunit.Abstractions;

namespace DesktopMcp.Server.Tests.Http;

/// <summary>The HTTP pipeline on real Kestrel (spec §8 HTTP pipeline; Decisions 9, 10, 13, 15, 16, 17).</summary>
[Collection(HttpHostCollection.Name)]
public class HttpPipelineTests(ITestOutputHelper output)
{
    private static ServeArguments Streamable(string? authKey = null, string? allowlist = null, string? cors = null) =>
        new() { Transport = "streamable-http", AuthKey = authKey, IpAllowlist = allowlist, CorsOrigins = cors };

    [Fact]
    public async Task HealthIsOkWithoutAuth()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "k", allowlist: "10.0.0.0/8"));
        using var response = await rig.Http.GetAsync(new Uri(rig.Base, "/health"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthAnswersHead()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "k", allowlist: "10.0.0.0/8"));
        using var response = await rig.SendAsync(HttpMethod.Head, "/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        using var post = await rig.SendAsync(HttpMethod.Post, "/health");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task HealthIsServedForSseToo()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse" });
        using var response = await rig.Http.GetAsync(new Uri(rig.Base, "/health"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthStillChecksTheHostAndOrigin()
    {
        await using var rig = await HttpRig.StartAsync(Streamable());
        using var host = await rig.SendAsync(HttpMethod.Get, "/health", ("Host", "attacker.example"));
        Assert.Equal(HttpStatusCode.BadRequest, host.StatusCode);
        using var origin = await rig.SendAsync(HttpMethod.Get, "/health", ("Origin", "https://attacker.example"));
        Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
    }

    [Fact]
    public async Task EveryOptionsRequestIs200AndOnlyListedOriginsGetCorsHeaders()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "k", cors: "https://client.example.com"));
        using var listed = await rig.SendAsync(HttpMethod.Options, "/mcp", ("Origin", "https://client.example.com"));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal("https://client.example.com", listed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("GET, POST, OPTIONS", listed.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("content-type, authorization, mcp-session-id", listed.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.Equal("Origin", listed.Headers.Vary.Single());
        using var other = await rig.SendAsync(HttpMethod.Options, "/mcp", ("Origin", "https://attacker.example"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(0, other.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task ARebindingHostIsRejectedBeforeTheEndpoint()
    {
        await using var rig = await HttpRig.StartAsync(Streamable());
        using var response = await rig.SendInitializeAsync(("Host", "attacker.example"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("Invalid host header", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ACrossSiteOriginIsRejectedEvenWithoutAuth()
    {
        await using var rig = await HttpRig.StartAsync(Streamable());
        using var response = await rig.SendInitializeAsync(("Origin", "https://attacker.example"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("""{"error":"Forbidden: origin 'https://attacker.example' is not allowed"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnEchoedOriginIsServedAsJsonThatIsNotSniffed()
    {
        await using var rig = await HttpRig.StartAsync(Streamable());
        using var response = await rig.SendInitializeAsync(("Origin", "https://a.example/<script>"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("""{"error":"Forbidden: origin 'https://a.example/<script>' is not allowed"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ALoopbackOriginPassesAndAListedOriginGetsCorsHeaders()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(cors: "https://client.example.com"));
        using var loopback = await rig.SendInitializeAsync(("Origin", "http://localhost:6274"));
        Assert.Equal(HttpStatusCode.OK, loopback.StatusCode);
        Assert.False(loopback.Headers.Contains("Access-Control-Allow-Origin"));
        using var listed = await rig.SendInitializeAsync(("Origin", "https://client.example.com"));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal("https://client.example.com", listed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("Origin", listed.Headers.Vary.Single());
        Assert.False(listed.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task AnUnlistedClientIs403()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(allowlist: "10.0.0.0/8"));
        using var response = await rig.SendInitializeAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("""{"error":"Forbidden: 127.0.0.1 is not in the IP allowlist"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null, """{"error":"Missing or invalid Authorization header. Expected: Bearer <token>"}""")]
    [InlineData("bearer secret", """{"error":"Missing or invalid Authorization header. Expected: Bearer <token>"}""")]
    [InlineData("Bearer wrong", """{"error":"Invalid authentication token"}""")]
    [InlineData("Bearer  secret", """{"error":"Invalid authentication token"}""")]
    public async Task TheAuthKeyIsRequired(string? authorization, string body)
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "secret"));
        using var response = authorization is null ? await rig.SendInitializeAsync() : await rig.SendInitializeAsync(("Authorization", authorization));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheRightKeyReachesTheEndpoint()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "secret"));
        using var response = await rig.SendInitializeAsync(("Authorization", "Bearer secret"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var client = await rig.ConnectAsync(HttpTransportMode.StreamableHttp, new Dictionary<string, string> { ["Authorization"] = "Bearer secret" });
        Assert.Equal(21, (await client.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task TheHostCheckRunsBeforeTheAllowlistAndAuth()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "secret", allowlist: "10.0.0.0/8"));
        using var host = await rig.SendInitializeAsync(("Host", "attacker.example"));
        Assert.Equal(HttpStatusCode.BadRequest, host.StatusCode);
        using var allowlist = await rig.SendInitializeAsync();
        Assert.Equal(HttpStatusCode.Forbidden, allowlist.StatusCode);   // allowlist before auth
    }

    [Fact]
    public async Task TheGuardsCoverLegacySse()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse" });
        var message = await rig.ReadSseEndpointEventAsync();   // a live session's /message?sessionId=...
        foreach (var (method, path) in new[] { (HttpMethod.Get, "/sse"), (HttpMethod.Post, message) })
        {
            using var host = await rig.SendAsync(method, path, ("Host", "attacker.example"));
            Assert.Equal(HttpStatusCode.BadRequest, host.StatusCode);
            Assert.Equal("Invalid host header", await host.Content.ReadAsStringAsync());
            using var origin = await rig.SendAsync(method, path, ("Origin", "https://attacker.example"));
            Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
            Assert.Equal("""{"error":"Forbidden: origin 'https://attacker.example' is not allowed"}""", await origin.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task LegacySseNeedsTheAuthKey()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse", AuthKey = "secret" });
        foreach (var (method, path) in new[] { (HttpMethod.Get, "/sse"), (HttpMethod.Post, "/message?sessionId=x") })
        {
            using var response = await rig.SendAsync(method, path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
            Assert.Equal("""{"error":"Missing or invalid Authorization header. Expected: Bearer <token>"}""", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task AnIPv6LoopbackClientPassesTheChecks()
    {
        if (!HasIPv6Loopback())
        {
            output.WriteLine("Skipped: this machine has no IPv6 loopback ([::1]).");
            return;
        }
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", Host = "::1", IpAllowlist = "::1" });
        using var response = await rig.SendInitializeAsync(("Host", $"[::1]:{rig.Base.Port}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Whether a socket can bind [::1] on an ephemeral port.</summary>
    private static bool HasIPv6Loopback()
    {
        if (!Socket.OSSupportsIPv6) return false;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>The auth step with an OAuth token validator (Decisions 13), on the bare pipeline without a server.</summary>
public class HttpPipelineAuthTests
{
    private const string Reached = "reached";

    [Theory]
    [InlineData(null, 401)]
    [InlineData("Bearer wrong", 401)]
    [InlineData("bearer tok1", 401)]
    [InlineData("Bearer tok1", 200)]
    public async Task OAuthOnlyAcceptsAValidTokenAndAnswersUnauthorized(string? authorization, int status)
    {
        var (code, headers, body) = await RunAsync(authKey: null, authorization);
        Assert.Equal(status, code);
        if (status == 200)
        {
            Assert.Equal(Reached, body);
            return;
        }
        Assert.Equal("Bearer", headers.WWWAuthenticate.ToString());
        Assert.Equal("application/json", headers.ContentType.ToString());
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
        Assert.Equal("""{"error":"Unauthorized"}""", body);
    }

    [Theory]
    [InlineData("Bearer secret", 200)]
    [InlineData("Bearer tok1", 200)]
    [InlineData("Bearer tok2", 401)]
    public async Task AnOAuthTokenIsTheAuthKeysFallback(string authorization, int status)
    {
        var (code, _, body) = await RunAsync(authKey: "secret", authorization);
        Assert.Equal(status, code);
        Assert.Equal(status == 200 ? Reached : """{"error":"Invalid authentication token"}""", body);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/oauth/token")]
    public async Task PublicPathsNeedNoToken(string path)
    {
        var (code, _, body) = await RunAsync(authKey: null, authorization: null, path);
        Assert.Equal(200, code);
        Assert.Equal(Reached, body);
    }

    private static async Task<(int Status, IHeaderDictionary Headers, string Body)> RunAsync(
        string? authKey, string? authorization, string path = "/mcp")
    {
        var plan = HttpRig.Plan(new ServeArguments { Transport = "streamable-http", AuthKey = authKey });
        var app = new ApplicationBuilder(new EmptyServices());
        HttpPipeline.Use(app, plan, token => token == "tok1");
        app.Run(context => context.Response.WriteAsync(Reached));
        var pipeline = app.Build();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.Host = new HostString("127.0.0.1", 8000);
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        using var body = new MemoryStream();
        context.Response.Body = body;
        await pipeline(context);
        return (context.Response.StatusCode, context.Response.Headers, Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
