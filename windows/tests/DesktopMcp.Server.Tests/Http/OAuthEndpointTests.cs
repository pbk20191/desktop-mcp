using System.Net;
using System.Text;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Http;
using Microsoft.AspNetCore.Http;

namespace DesktopMcp.Server.Tests.Http;

/// <summary>The OAuth endpoints and their tokens in the auth step, on real Kestrel (spec §8 OAuth; Decisions 12, 13).</summary>
[Collection(HttpHostCollection.Name)]
public class OAuthEndpointTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private static ServeArguments WithOAuth(string? authKey = null) =>
        new() { Transport = "streamable-http", OAuthClientId = "client", OAuthClientSecret = "s3cret", AuthKey = authKey };

    [Fact]
    public async Task MetadataNamesTheBoundPort()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        var json = await rig.Http.GetStringAsync(new Uri(rig.Base, "/.well-known/oauth-authorization-server"));
        Assert.Contains($"\"issuer\":\"http://127.0.0.1:{rig.Base.Port}\"", json);
    }

    [Fact]
    public async Task TheCodeFlowYieldsATokenThatOpensTheEndpoint()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        var token = await rig.OAuthTokenAsync(Challenge, Verifier, form: true);      // authorize (302) then POST /oauth/token
        Assert.Equal(HttpStatusCode.OK, (await rig.SendInitializeAsync(("Authorization", $"Bearer {token}"))).StatusCode);
        var denied = await rig.SendInitializeAsync(("Authorization", "Bearer forged"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("""{"error":"Unauthorized"}""", await denied.Content.ReadAsStringAsync());
        Assert.Equal("Bearer", denied.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task TheTokenEndpointAcceptsJson()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        Assert.NotEmpty(await rig.OAuthTokenAsync(Challenge, Verifier, form: false));
    }

    [Theory]
    [InlineData("application/json", "{not json", """{"error":"invalid_request"}""")]
    [InlineData("application/json", "[1]", """{"error":"invalid_request"}""")]
    [InlineData("text/plain", "grant_type=authorization_code", """{"error":"unsupported_grant_type"}""")]
    public async Task BadTokenBodiesAre400(string contentType, string body, string json)
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        using var response = await rig.Http.PostAsync(new Uri(rig.Base, "/oauth/token"), new StringContent(body, Encoding.UTF8, contentType));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(json, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnOAuthTokenAlsoPassesTheAuthKeyStep()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth(authKey: "key"));
        var token = await rig.OAuthTokenAsync(Challenge, Verifier, form: true);
        Assert.Equal(HttpStatusCode.OK, (await rig.SendInitializeAsync(("Authorization", $"Bearer {token}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.SendInitializeAsync(("Authorization", "Bearer key"))).StatusCode);
    }

    [Fact]
    public async Task WithoutOAuthTheRoutesDoNotExist()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.GetAsync(new Uri(rig.Base, "/.well-known/oauth-authorization-server"))).StatusCode);
    }

    [Theory]
    [InlineData("0.0.0.0", 8000, "127.0.0.1:5555", "http://127.0.0.1:5555")]
    [InlineData("::", 8000, "[::1]:5555", "http://[::1]:5555")]
    [InlineData("localhost", 0, "ignored", "http://localhost:41000")]
    [InlineData("::1", 41000, "ignored", "http://[::1]:41000")]
    public void TheIssuerFollowsDecisions12(string host, int port, string hostHeader, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = HostString.FromUriComponent(hostHeader);
        var plan = HttpRig.Plan(new ServeArguments { Transport = "streamable-http", Host = host, Port = port, AllowInsecureRemote = true });
        Assert.Equal(expected, HttpServer.Issuer(plan, boundPort: 41000, context));
    }

    // Beyond the brief: response headers, the remaining routes, the body cap and the field parsing.

    [Fact]
    public async Task TheTokenResponseIsNeverCached()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        using var noRedirects = HttpRig.CreateClient(followRedirects: false);
        using var authorize = await noRedirects.GetAsync(new Uri(rig.Base,
            "/oauth/authorize?response_type=code&client_id=client&redirect_uri=http://127.0.0.1:9/cb&code_challenge=" + Challenge));
        Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);
        Assert.Empty(await authorize.Content.ReadAsByteArrayAsync());
        var code = Uri.UnescapeDataString(authorize.Headers.Location!.OriginalString["http://127.0.0.1:9/cb?code=".Length..]);

        using var response = await rig.Http.PostAsync(new Uri(rig.Base, "/oauth/token"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = Verifier,
            ["client_id"] = "client",
            ["client_secret"] = "s3cret",
            ["redirect_uri"] = "http://127.0.0.1:9/cb",
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("no-cache", response.Headers.GetValues("Pragma").Single());
        Assert.Matches("""^\{"access_token":"[A-Za-z0-9_-]{64}","token_type":"Bearer","expires_in":3600\}$""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RegistrationIsDisabled()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        using var response = await rig.Http.PostAsync(new Uri(rig.Base, "/oauth/register"), new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("""{"error":"registration_disabled","error_description":"Dynamic OAuth""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuthorizeErrorsAreJson()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        using var response = await rig.Http.GetAsync(new Uri(rig.Base, "/oauth/authorize?response_type=token"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("""{"error":"unsupported_response_type"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/json")]
    public async Task AnOversizedTokenBodyIsRefused(string contentType)
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        var body = new string('a', OAuthEndpoints.MaxBodyBytes + 1);
        using var response = await rig.Http.PostAsync(new Uri(rig.Base, "/oauth/token"), new StringContent(body, Encoding.UTF8, contentType));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"error":"invalid_request"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ABodyWithoutLengthIsCappedToo()
    {
        var request = Request("application/json", new string(' ', OAuthEndpoints.MaxBodyBytes) + "{}", chunked: true);
        Assert.Null(await OAuthEndpoints.ReadFieldsAsync(request));
        var fits = Request("application/json", new string(' ', OAuthEndpoints.MaxBodyBytes - 2) + "{}", chunked: true);
        Assert.Empty((await OAuthEndpoints.ReadFieldsAsync(fits))!);
    }

    [Fact]
    public async Task FormFieldsAreDecodedAndTheLastValueWins()
    {
        var fields = await OAuthEndpoints.ReadFieldsAsync(Request("application/x-www-form-urlencoded; charset=utf-8", "a=1&b=x+y%2Bz&a=2&c="));
        Assert.Equal(new Dictionary<string, string> { ["a"] = "2", ["b"] = "x y+z", ["c"] = "" }, fields);
    }

    [Fact]
    public async Task JsonValuesThatAreNotStringsAreTheirRawText()
    {
        var fields = await OAuthEndpoints.ReadFieldsAsync(Request("application/json", """{"s":"té","n":1.50,"b":true,"z":null,"o":{"k":[1]},"s":"last"}"""));
        Assert.Equal(new Dictionary<string, string> { ["s"] = "last", ["n"] = "1.50", ["b"] = "true", ["z"] = "null", ["o"] = """{"k":[1]}""" }, fields);
    }

    [Theory]
    [InlineData("application/json", "")]
    [InlineData("application/json", "\"text\"")]
    [InlineData("application/json", "{\"a\":1} {}")]
    public async Task JsonThatIsNotOneObjectIsNull(string contentType, string body) =>
        Assert.Null(await OAuthEndpoints.ReadFieldsAsync(Request(contentType, body)));

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("multipart/form-data; boundary=x")]
    public async Task OtherContentTypesGiveNoFields(string? contentType) =>
        Assert.Empty((await OAuthEndpoints.ReadFieldsAsync(Request(contentType, "grant_type=authorization_code")))!);

    private static HttpRequest Request(string? contentType, string body, bool chunked = false)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Method = "POST";
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(bytes);
        if (!chunked) context.Request.ContentLength = bytes.Length;
        return context.Request;
    }
}
