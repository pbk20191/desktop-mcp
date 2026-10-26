using System.Net;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Net;

namespace DesktopMcp.Server.Http;

/// <summary>What one Kestrel listener binds (Decisions 7).</summary>
internal abstract record ListenTarget
{
    private ListenTarget() { }

    /// <summary>Kestrel's localhost: 127.0.0.1 and ::1 (127.0.0.1 alone on port 0, which Kestrel cannot bind on localhost).</summary>
    internal sealed record Localhost : ListenTarget;

    /// <summary>One address; <see cref="IPAddress.IPv6Any"/> binds dual-mode.</summary>
    internal sealed record Address(IPAddress Ip) : ListenTarget;

    /// <summary>
    /// <c>localhost</c> (any case) is <see cref="Localhost"/>; an IP literal (brackets stripped) is that address, the
    /// empty string is <c>::</c>; any other name is resolved once with <paramref name="dns"/> and binds every address.
    /// </summary>
    /// <exception cref="ConfigException">The name resolves to no address.</exception>
    public static IReadOnlyList<ListenTarget> Resolve(string host, Func<string, IPAddress[]> dns)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(dns);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return [new Localhost()];
        if (host.Length == 0) return [new Address(IPAddress.IPv6Any)];
        var bare = host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        if (IpAddressText.TryParse(bare, out var ip)) return [new Address(ip)];
        var resolved = dns(host);
        if (resolved.Length == 0) throw new ConfigException($"Could not resolve server.host '{host}'.");
        return [.. resolved.Select(address => new Address(address))];
    }
}
