using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using DesktopMcp.Core.Net;

namespace DesktopMcp.Core.Security;

/// <summary>An OAuth endpoint's answer: a JSON body, or a 302 with <see cref="Location"/> and no body.</summary>
public sealed record OAuthResult(int Status, string? Json, string? Location);

/// <summary>
/// The in-memory OAuth 2.0 authorization server with PKCE (upstream <c>infrastructure/oauth.py:24-288</c>, P5 Decisions 12):
/// one pre-provisioned client, no dynamic registration, S256 only, loopback redirect URIs only.
/// Lifetimes are measured on <paramref name="time"/>'s monotonic timestamps.
/// </summary>
/// <param name="token">Makes a random token from <c>n</c> bytes; defaults to Python's <c>secrets.token_urlsafe(n)</c>.</param>
public sealed class OAuthServer(string clientId, string clientSecret, TimeProvider time, Func<int, string>? token = null)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(3600);
    public const int MaxPendingCodes = 1000;

    private const int CodeBytes = 32;    // oauth.py:180
    private const int TokenBytes = 48;   // oauth.py:248

    private readonly Func<int, string> _token = token ?? RandomToken;
    private readonly Lock _lock = new();
    private readonly OrderedDictionary<string, PendingCode> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _tokens = new(StringComparer.Ordinal);   // token → expiry timestamp

    private sealed record PendingCode(string ClientId, string RedirectUri, string Challenge, long ExpiresAt);

    public string Metadata(string issuer) => SecurityJson.Metadata(issuer);

    public OAuthResult Register() => new(403, SecurityJson.OAuthError("registration_disabled",
        "Dynamic OAuth client registration is disabled. Configure --oauth-client-id and --oauth-client-secret on the server."), null);

    public OAuthResult Authorize(IReadOnlyDictionary<string, string> query)
    {
        var requestClientId = query.GetValueOrDefault("client_id", "");
        var redirectUri = query.GetValueOrDefault("redirect_uri", "");
        var responseType = query.GetValueOrDefault("response_type", "");
        var state = query.GetValueOrDefault("state", "");
        var challenge = query.GetValueOrDefault("code_challenge", "");
        var method = query.GetValueOrDefault("code_challenge_method", "S256");

        if (responseType != "code") return Json(400, SecurityJson.Error("unsupported_response_type"));
        if (challenge.Length == 0) return InvalidRequest("code_challenge required (PKCE)");
        if (method != "S256") return InvalidRequest("code_challenge_method must be S256");
        if (!IsLoopbackRedirectUri(redirectUri)) return InvalidRequest("redirect_uri must be a loopback http(s) URI");
        if (clientId.Length == 0 || clientSecret.Length == 0)
            return Json(500, SecurityJson.OAuthError("server_error", "OAuth requires a configured client ID and secret"));
        if (requestClientId != clientId) return Json(400, SecurityJson.Error("invalid_client"));

        var code = _token(CodeBytes);
        lock (_lock)
        {
            var now = time.GetTimestamp();
            PurgeExpired(now);
            while (_codes.Count >= MaxPendingCodes) _codes.RemoveAt(0);
            _codes[code] = new PendingCode(requestClientId, redirectUri, challenge, now + Ticks(CodeLifetime));
        }

        var location = redirectUri + (redirectUri.Contains('?') ? "&" : "?") + "code=" + Quote(code);
        if (state.Length > 0) location += "&state=" + Quote(state);
        return new OAuthResult(302, null, location);
    }

    public OAuthResult Token(IReadOnlyDictionary<string, string> fields)
    {
        if (fields.GetValueOrDefault("grant_type", "") != "authorization_code")
            return Json(400, SecurityJson.Error("unsupported_grant_type"));

        var code = fields.GetValueOrDefault("code", "");
        var verifier = fields.GetValueOrDefault("code_verifier", "");
        var requestClientId = fields.GetValueOrDefault("client_id", "");
        var redirectUri = fields.GetValueOrDefault("redirect_uri", "");

        lock (_lock)
        {
            var now = time.GetTimestamp();
            if (!_codes.TryGetValue(code, out var pending)) return InvalidGrant("unknown code");
            if (pending.ClientId != requestClientId) return InvalidGrant("client_id mismatch");
            if (pending.RedirectUri != redirectUri) return InvalidGrant("redirect_uri mismatch");
            if (now > pending.ExpiresAt)
            {
                _codes.Remove(code);
                return InvalidGrant("code expired");
            }

            // One-time use; a failed PKCE check burns the code too (Decisions 12, RFC 7636 §4.6).
            _codes.Remove(code);
            if (!HttpGuards.FixedTimeEquals(S256(verifier), pending.Challenge)) return InvalidGrant("PKCE verification failed");
        }

        var provided = fields.GetValueOrDefault("client_secret", "");
        if (clientSecret.Length == 0 || provided.Length == 0 || !HttpGuards.FixedTimeEquals(provided, clientSecret))
            return new OAuthResult(401, SecurityJson.Error("invalid_client"), null);

        var accessToken = _token(TokenBytes);
        lock (_lock)
        {
            var now = time.GetTimestamp();
            PurgeExpired(now);
            _tokens[accessToken] = now + Ticks(TokenLifetime);
        }

        return new OAuthResult(200, SecurityJson.Token(accessToken, (int)TokenLifetime.TotalSeconds), null);
    }

    /// <summary>Whether <paramref name="token"/> is a live access token. Never throws: the pipeline calls it for every Bearer token.</summary>
    public bool ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (_lock)
        {
            if (!_tokens.TryGetValue(token, out var expiresAt)) return false;
            if (time.GetTimestamp() <= expiresAt) return true;
            _tokens.Remove(token);
            return false;
        }
    }

    /// <summary>Python's <c>urllib.parse.quote(value)</c> (safe "/"): UTF-8 bytes, unreserved characters and '/' kept, others %XX.</summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~' or (byte)'/')
                builder.Append((char)b);
            else
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>oauth.py:81-91: http(s), a host, no fragment, and a loopback host. A control character, space or
    /// non-ASCII character is refused on the raw string (Decisions 12), so the Location built from it is always a valid header.</summary>
    private static bool IsLoopbackRedirectUri(string redirectUri)
    {
        foreach (var c in redirectUri)
            if (c <= ' ' || c >= '\u007f') return false;
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        // Any '#' is refused: the code appended after it would land in the fragment.
        if (uri.Host.Length == 0 || redirectUri.Contains('#')) return false;
        var host = uri.Host;
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
        return IpAddressText.IsLoopbackHost(host);
    }

    private static string S256(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    private static string RandomToken(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * time.TimestampFrequency);

    /// <summary>Drops expired codes and tokens (Decisions 12); the caller holds the lock.</summary>
    private void PurgeExpired(long now)
    {
        for (var i = _codes.Count - 1; i >= 0; i--)
            if (now > _codes.GetAt(i).Value.ExpiresAt) _codes.RemoveAt(i);
        foreach (var (key, expiresAt) in _tokens)
            if (now > expiresAt) _tokens.Remove(key);
    }

    private static OAuthResult Json(int status, string json) => new(status, json, null);

    private static OAuthResult InvalidRequest(string description) => Json(400, SecurityJson.OAuthError("invalid_request", description));

    private static OAuthResult InvalidGrant(string description) => Json(400, SecurityJson.OAuthError("invalid_grant", description));
}
