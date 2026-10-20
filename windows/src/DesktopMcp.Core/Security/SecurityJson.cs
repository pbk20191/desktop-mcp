using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopMcp.Core.Security;

public sealed record ErrorBody(string Error);

public sealed record OAuthErrorBody(string Error, string ErrorDescription);

public sealed record HealthBody(string Status);

/// <summary>RFC 8414 authorization server metadata (oauth.py:113-123).</summary>
public sealed record OAuthMetadata(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string[] ResponseTypesSupported,
    string[] GrantTypesSupported,
    string[] CodeChallengeMethodsSupported,
    string[] TokenEndpointAuthMethodsSupported);

/// <summary>The token endpoint's success body (oauth.py:254-258).</summary>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);

[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(OAuthErrorBody))]
[JsonSerializable(typeof(HealthBody))]
[JsonSerializable(typeof(OAuthMetadata))]
[JsonSerializable(typeof(TokenResponse))]
internal partial class SecurityJsonContext : JsonSerializerContext;

/// <summary>Compact snake_case JSON bodies for the HTTP guards, matching Python's json.dumps without ASCII escaping of ' < >.</summary>
public static class SecurityJson
{
    private static readonly SecurityJsonContext Context = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    });

    public static string Error(string error) => JsonSerializer.Serialize(new ErrorBody(error), Context.ErrorBody);

    public static string OAuthError(string error, string description) =>
        JsonSerializer.Serialize(new OAuthErrorBody(error, description), Context.OAuthErrorBody);

    public static string Metadata(string issuer) =>
        JsonSerializer.Serialize(
            new OAuthMetadata(
                issuer,
                $"{issuer}/oauth/authorize",
                $"{issuer}/oauth/token",
                ["code"],
                ["authorization_code"],
                ["S256"],
                ["client_secret_post"]),
            Context.OAuthMetadata);

    public static string Token(string accessToken) =>
        JsonSerializer.Serialize(new TokenResponse(accessToken, "Bearer", OAuthServer.TokenLifetimeSeconds), Context.TokenResponse);

    public static string Health => JsonSerializer.Serialize(new HealthBody("ok"), Context.HealthBody);
}
