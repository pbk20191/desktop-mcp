using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopMcp.Core.Security;

public sealed record ErrorBody(string Error);

public sealed record OAuthErrorBody(string Error, string ErrorDescription);

public sealed record HealthBody(string Status);

[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(OAuthErrorBody))]
[JsonSerializable(typeof(HealthBody))]
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

    public static string Health => JsonSerializer.Serialize(new HealthBody("ok"), Context.HealthBody);
}
