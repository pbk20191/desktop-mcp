using System.Net;
using System.Security.Cryptography;
using System.Text;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Net;

namespace DesktopMcp.Core.Security;

/// <summary>Pure request guards for the HTTP pipeline (spec §8 steps 2-5); the pipeline supplies the request facts.</summary>
public static class HttpGuards
{
    /// <summary>Paths served without authentication, in upstream's order (auth.py:14-20).</summary>
    public static IReadOnlyList<string> PublicPaths { get; } =
    [
        "/health",
        "/oauth/token",
        "/.well-known/oauth-authorization-server",
        "/.well-known/oauth-protected-resource",
        "/.well-known/openid-configuration",
    ];

    public static bool IsPublicPath(string path) => PublicPaths.Contains(path, StringComparer.Ordinal);

    /// <summary>DNS-rebinding guard. <paramref name="hostPart"/> is the Host header's host without port
    /// (<c>HostString.Host</c>); the configured list is compared case-insensitively with IPv6 brackets ignored.</summary>
    public static bool IsTrustedHost(string? hostPart, IReadOnlyList<string>? allowedHosts)
    {
        if (allowedHosts is null) return true;
        if (string.IsNullOrEmpty(hostPart)) return false;
        var host = TrimBrackets(hostPart);
        return allowedHosts.Any(allowed => TrimBrackets(allowed).Equals(host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>No Origin, a listed origin, or an http(s) loopback origin is allowed.</summary>
    public static bool IsAllowedOrigin(string? origin, IReadOnlyList<string> corsOrigins)
    {
        if (origin is null) return true;
        if (corsOrigins.Contains(origin, StringComparer.Ordinal)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        return IpAddressText.IsLoopbackHost(TrimBrackets(uri.Host));
    }

    /// <summary>Null when the client is allowed (or the list is empty), else the 403 error text.</summary>
    public static string? AllowlistRejection(IPAddress? client, IReadOnlyList<IpNetwork> allowlist)
    {
        if (allowlist.Count == 0) return null;
        if (client is null) return "Forbidden: missing client address";
        if (client.IsIPv4MappedToIPv6) client = client.MapToIPv4();
        return allowlist.Any(network => network.Contains(client))
            ? null
            : $"Forbidden: {client} is not in the IP allowlist";
    }

    public static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    private static string TrimBrackets(string host) => host.Trim('[', ']');
}
