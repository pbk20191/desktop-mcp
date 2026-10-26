using DesktopMcp.Core.Security;

namespace DesktopMcp.Core.Tests.Security;

public class OAuthServerTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";   // RFC 7636 appendix B
    private readonly StepClock _time = new();                                        // Core.Tests' fake TimeProvider (P4): timestamps only
    private int _next;
    private OAuthServer Server() => new("client", "s3cret", _time, n => $"tok{++_next}");

    private static Dictionary<string, string> Query(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);
    private static Dictionary<string, string> GoodAuthorize(string redirect = "http://127.0.0.1:33418/callback", string state = "") =>
        Query(("response_type", "code"), ("client_id", "client"), ("redirect_uri", redirect), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", state));

    [Fact]
    public void MetadataIsUpstreams() =>
        Assert.Equal("""{"issuer":"http://localhost:8000","authorization_endpoint":"http://localhost:8000/oauth/authorize","token_endpoint":"http://localhost:8000/oauth/token","response_types_supported":["code"],"grant_types_supported":["authorization_code"],"code_challenge_methods_supported":["S256"],"token_endpoint_auth_methods_supported":["client_secret_post"]}""",
            Server().Metadata("http://localhost:8000"));

    [Fact]
    public void RegistrationIsDisabled()
    {
        var result = Server().Register();
        Assert.Equal(403, result.Status);
        Assert.Equal("""{"error":"registration_disabled","error_description":"Dynamic OAuth client registration is disabled. Configure --oauth-client-id and --oauth-client-secret on the server."}""", result.Json);
    }

    [Theory]
    [InlineData("response_type", "token", 400, """{"error":"unsupported_response_type"}""")]
    [InlineData("code_challenge", "", 400, """{"error":"invalid_request","error_description":"code_challenge required (PKCE)"}""")]
    [InlineData("code_challenge_method", "plain", 400, """{"error":"invalid_request","error_description":"code_challenge_method must be S256"}""")]
    [InlineData("redirect_uri", "https://attacker.example/cb", 400, """{"error":"invalid_request","error_description":"redirect_uri must be a loopback http(s) URI"}""")]
    [InlineData("redirect_uri", "http://localhost/cb#frag", 400, """{"error":"invalid_request","error_description":"redirect_uri must be a loopback http(s) URI"}""")]
    [InlineData("redirect_uri", "myapp://localhost/cb", 400, """{"error":"invalid_request","error_description":"redirect_uri must be a loopback http(s) URI"}""")]
    [InlineData("client_id", "other", 400, """{"error":"invalid_client"}""")]
    public void AuthorizeRejectsInUpstreamsOrder(string key, string value, int status, string json)
    {
        var query = GoodAuthorize();
        query[key] = value;
        Assert.Equal(new OAuthResult(status, json, null), Server().Authorize(query));
    }

    // Redirect tricks: each host a browser would visit is not loopback, or the string could not be a valid Location (Decisions 12).
    [Theory]
    [InlineData("http://localhost@attacker.example/cb")]
    [InlineData("http://attacker.example\\@localhost/cb")]
    [InlineData("http://attacker.example\\localhost/cb")]
    [InlineData("http:/\\attacker.example/cb")]
    [InlineData("http://127.0.0.1.attacker.example/cb")]
    [InlineData("http://localhost/cb#")]
    [InlineData("http://localhost/a\r\nX-Injected: y")]
    [InlineData("http://localhost/a\nb")]
    [InlineData("http://localhost/a\tb")]
    [InlineData("http://localhost/a b")]
    [InlineData("http://localhost/é")]
    [InlineData("http://localhost/\u007f")]
    public void AuthorizeRefusesRedirectTricks(string redirect) =>
        Assert.Equal(new OAuthResult(400, """{"error":"invalid_request","error_description":"redirect_uri must be a loopback http(s) URI"}""", null),
            Server().Authorize(GoodAuthorize(redirect)));

    [Theory]
    [InlineData("client", "")]
    [InlineData("", "s3cret")]
    public void AnUnconfiguredClientIsAServerError(string id, string secret) =>
        Assert.Equal(new OAuthResult(500, """{"error":"server_error","error_description":"OAuth requires a configured client ID and secret"}""", null),
            new OAuthServer(id, secret, _time, n => $"tok{++_next}").Authorize(GoodAuthorize()));

    [Fact]
    public void AMissingMethodMeansS256()
    {
        var query = GoodAuthorize();
        query.Remove("code_challenge_method");
        Assert.Equal(302, Server().Authorize(query).Status);
    }

    [Theory]
    [InlineData("http://127.0.0.1:33418/callback", "", "http://127.0.0.1:33418/callback?code=tok1")]
    [InlineData("http://localhost/cb?x=1", "a b/c", "http://localhost/cb?x=1&code=tok1&state=a%20b/c")]
    [InlineData("https://[::1]:5000/cb", "s", "https://[::1]:5000/cb?code=tok1&state=s")]
    public void AuthorizeRedirectsWithTheCode(string redirect, string state, string location) =>
        Assert.Equal(new OAuthResult(302, null, location), Server().Authorize(GoodAuthorize(redirect, state)));

    private (OAuthServer Server, Dictionary<string, string> Fields) Issued()
    {
        var server = Server();
        server.Authorize(GoodAuthorize());
        return (server, Query(("grant_type", "authorization_code"), ("code", "tok1"), ("client_id", "client"),
            ("redirect_uri", "http://127.0.0.1:33418/callback"), ("code_verifier", Verifier), ("client_secret", "s3cret")));
    }

    [Fact]
    public void TheCodeBuysOneToken()
    {
        var (server, fields) = Issued();
        var result = server.Token(fields);
        Assert.Equal(new OAuthResult(200, """{"access_token":"tok2","token_type":"Bearer","expires_in":3600}""", null), result);
        Assert.True(server.ValidateToken("tok2"));
        Assert.Equal("""{"error":"invalid_grant","error_description":"unknown code"}""", server.Token(fields).Json);   // single use
    }

    [Theory]
    [InlineData("grant_type", "password", 400, """{"error":"unsupported_grant_type"}""")]
    [InlineData("code", "nope", 400, """{"error":"invalid_grant","error_description":"unknown code"}""")]
    [InlineData("client_id", "other", 400, """{"error":"invalid_grant","error_description":"client_id mismatch"}""")]
    [InlineData("redirect_uri", "http://127.0.0.1:1/x", 400, """{"error":"invalid_grant","error_description":"redirect_uri mismatch"}""")]
    [InlineData("code_verifier", "wrong", 400, """{"error":"invalid_grant","error_description":"PKCE verification failed"}""")]
    [InlineData("client_secret", "bad", 401, """{"error":"invalid_client"}""")]
    [InlineData("client_secret", "", 401, """{"error":"invalid_client"}""")]
    public void TokenRejectsInUpstreamsOrder(string key, string value, int status, string json)
    {
        var (server, fields) = Issued();
        fields[key] = value;
        Assert.Equal(new OAuthResult(status, json, null), server.Token(fields));
    }

    [Theory]
    [InlineData("code_verifier", "wrong", true)]      // burned (Decisions 12)
    [InlineData("client_secret", "bad", true)]        // consumed before the secret check (upstream)
    [InlineData("client_id", "other", false)]
    [InlineData("redirect_uri", "http://127.0.0.1:1/x", false)]
    public void WhichFailuresBurnTheCode(string key, string value, bool burned)
    {
        var (server, fields) = Issued();
        var good = new Dictionary<string, string>(fields);
        fields[key] = value;
        server.Token(fields);
        Assert.Equal(burned ? 400 : 200, server.Token(good).Status);
    }

    [Fact]
    public void ACodeExpiresAfter300Seconds()
    {
        var (server, fields) = Issued();
        _time.Advance(301);
        Assert.Equal("""{"error":"invalid_grant","error_description":"code expired"}""", server.Token(fields).Json);
        Assert.Equal("""{"error":"invalid_grant","error_description":"unknown code"}""", server.Token(fields).Json);   // removed
    }

    [Fact]
    public void ATokenExpiresAfter3600Seconds()
    {
        var (server, fields) = Issued();
        server.Token(fields);
        _time.Advance(3601);
        Assert.False(server.ValidateToken("tok2"));
        Assert.False(server.ValidateToken(""));
    }

    [Fact]
    public void AtMostAThousandCodesWait()
    {
        var server = Server();
        for (var i = 0; i < 1001; i++) server.Authorize(GoodAuthorize());
        var fields = Query(("grant_type", "authorization_code"), ("code", "tok1"), ("client_id", "client"),
            ("redirect_uri", "http://127.0.0.1:33418/callback"), ("code_verifier", Verifier), ("client_secret", "s3cret"));
        Assert.Equal("""{"error":"invalid_grant","error_description":"unknown code"}""", server.Token(fields).Json);
    }

    // Carried from the Task 4 review: the pipeline calls ValidateToken for every Bearer token, so no input may throw.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("tok2 ")]
    [InlineData("TOK2")]
    [InlineData("\u0001\u007f%00\r\n\t")]
    [InlineData("한글 토큰")]
    public void ValidateTokenNeverThrows(string token)
    {
        var (server, fields) = Issued();
        server.Token(fields);
        Assert.False(server.ValidateToken(token));
        Assert.False(server.ValidateToken(new string('x', 1_000_000)));
        Assert.False(server.ValidateToken("tok2" + '\ud800'));
        Assert.False(server.ValidateToken(null!));
        Assert.True(server.ValidateToken("tok2"));
    }

    [Theory]
    [InlineData("abcXYZ019_.-~/", "abcXYZ019_.-~/")]
    [InlineData("a b&c=d?e#f+g", "a%20b%26c%3Dd%3Fe%23f%2Bg")]
    [InlineData("é한", "%C3%A9%ED%95%9C")]
    [InlineData("", "")]
    public void QuoteIsPythonsUrllibQuote(string value, string expected) => Assert.Equal(expected, OAuthServer.Quote(value));

    [Fact]
    public void TheDefaultTokensAreRandomUrlSafeBase64()
    {
        var server = new OAuthServer("client", "s3cret", _time);
        var location = server.Authorize(GoodAuthorize()).Location!;
        var code = location[(location.IndexOf("code=", StringComparison.Ordinal) + 5)..];
        Assert.Equal(43, code.Length);                                                  // token_urlsafe(32)
        Assert.Matches("^[A-Za-z0-9_-]+$", code);
        var token = server.Token(Query(("grant_type", "authorization_code"), ("code", code), ("client_id", "client"),
            ("redirect_uri", "http://127.0.0.1:33418/callback"), ("code_verifier", Verifier), ("client_secret", "s3cret")));
        Assert.Equal(200, token.Status);
        Assert.Matches("^\\{\"access_token\":\"[A-Za-z0-9_-]{64}\",", token.Json!);   // token_urlsafe(48)
    }

    [Fact]
    public void MissingFieldsAreEmpty()
    {
        var server = Server();
        Assert.Equal(new OAuthResult(400, """{"error":"unsupported_response_type"}""", null), server.Authorize(Query()));
        Assert.Equal(new OAuthResult(400, """{"error":"unsupported_grant_type"}""", null), server.Token(Query()));
        Assert.Equal("""{"error":"invalid_grant","error_description":"unknown code"}""",
            server.Token(Query(("grant_type", "authorization_code"))).Json);
    }
}
