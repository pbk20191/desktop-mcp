using System.Text;
using System.Text.Json;
using DesktopMcp.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace DesktopMcp.Server.Http;

/// <summary>
/// The OAuth routes over an <see cref="OAuthServer"/> (spec §8 OAuth; Decisions 12; upstream <c>__main__.py:837-851</c>,
/// <c>oauth.py:113-266</c>). Mapped only when a client id and secret are configured.
/// </summary>
internal static class OAuthEndpoints
{
    /// <summary>
    /// The most body bytes the token endpoint reads. A token request is a few hundred bytes; a longer body is
    /// 400 <c>{"error":"invalid_request"}</c> without being buffered.
    /// </summary>
    internal const int MaxBodyBytes = 16 * 1024;

    private const string Json = "application/json";

    private static readonly string[] GetOrHead = [HttpMethods.Get, HttpMethods.Head];

    /// <summary>
    /// Maps <c>GET|HEAD /.well-known/oauth-authorization-server</c>, <c>POST /oauth/register</c>,
    /// <c>GET|HEAD /oauth/authorize</c> and <c>POST /oauth/token</c>; <paramref name="issuer"/> names the server for the request (Decisions 12).
    /// </summary>
    internal static void Map(IEndpointRouteBuilder app, OAuthServer oauth, Func<HttpContext, string> issuer)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(oauth);
        ArgumentNullException.ThrowIfNull(issuer);

        // GET routes answer HEAD too, as Starlette's do.
        app.MapMethods("/.well-known/oauth-authorization-server", GetOrHead, context =>
        {
            // On a wildcard bind the issuer comes from the request's Host header, so no cache may keep one answer.
            context.Response.Headers.CacheControl = "no-store";
            return WriteAsync(context, new OAuthResult(StatusCodes.Status200OK, oauth.Metadata(issuer(context)), null));
        });
        app.MapPost("/oauth/register", context => WriteAsync(context, oauth.Register()));
        app.MapMethods("/oauth/authorize", GetOrHead, context => WriteAsync(context, oauth.Authorize(Query(context.Request.Query))));
        app.MapPost("/oauth/token", async context =>
        {
            var fields = await ReadFieldsAsync(context.Request).ConfigureAwait(false);
            var result = fields is null
                ? new OAuthResult(StatusCodes.Status400BadRequest, SecurityJson.Error("invalid_request"), null)
                : oauth.Token(fields);
            // RFC 6749 §5.1: token responses (and their errors, §5.2) are never cached.
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            await WriteAsync(context, result).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// The token request's fields (<c>oauth.py:201-215</c>, Decisions 12): a content type containing
    /// <c>application/x-www-form-urlencoded</c> is read as a form; one containing <c>application/json</c> as a JSON
    /// object, string values as they are and other values as their raw JSON text; anything else gives no fields. For a
    /// repeated name the last value wins, as Starlette's <c>dict(form)</c> and Python's <c>json</c>. Null when the JSON
    /// is not one object or the body exceeds <see cref="MaxBodyBytes"/>.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, string>?> ReadFieldsAsync(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var contentType = request.ContentType ?? "";
        var form = contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        if (!form && !contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var body = await ReadBodyAsync(request).ConfigureAwait(false);
        if (body is null) return null;
        return form ? FormFields(body) : JsonFields(body);
    }

    /// <summary>The body, or null when it is longer than <see cref="MaxBodyBytes"/> (declared or read).</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentLength > MaxBodyBytes) return null;
        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(length), request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        return length > MaxBodyBytes ? null : buffer[..length];
    }

    private static Dictionary<string, string> FormFields(byte[] body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        // '+' is a space and %XX is UTF-8, as application/x-www-form-urlencoded and Python's parse_qsl.
        var parsed = QueryHelpers.ParseNullableQuery(Encoding.UTF8.GetString(body));
        if (parsed is null) return fields;
        foreach (var (name, values) in parsed) fields[name] = Last(values);
        return fields;
    }

    private static Dictionary<string, string>? JsonFields(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                fields[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
            return fields;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The query as Starlette's <c>query_params.get</c> sees it: the last value of each name.</summary>
    private static Dictionary<string, string> Query(IQueryCollection query)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in query) fields[name] = Last(values);
        return fields;
    }

    private static string Last(StringValues values) => values.Count == 0 ? "" : values[values.Count - 1] ?? "";

    /// <summary>Writes <paramref name="result"/>: a 302 with its Location and no body, or its JSON as the guards' errors are.</summary>
    private static Task WriteAsync(HttpContext context, OAuthResult result)
    {
        var response = context.Response;
        response.StatusCode = result.Status;
        if (result.Location is { } location)
        {
            response.Headers.Location = location;
            response.ContentLength = 0;
            return Task.CompletedTask;
        }
        response.ContentType = Json;
        response.Headers.XContentTypeOptions = "nosniff";
        return response.WriteAsync(result.Json ?? "");
    }
}
