using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DesktopMcp.Core.Net;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Config;

/// <summary>An IPv4 or IPv6 network for the IP allowlist (enforced by the HTTP pipeline in P5).</summary>
public sealed record IpNetwork(IPAddress Network, int PrefixLength)
{
    public override string ToString() => $"{Network}/{PrefixLength}";

    /// <summary>upstream parse_ip_allowlist (infrastructure/security.py:210-236): blank entries are skipped, host bits
    /// are cleared, and every bad entry is reported at once.</summary>
    public static IReadOnlyList<IpNetwork> ParseAllowlist(IEnumerable<string> entries)
    {
        var networks = new List<IpNetwork>();
        var errors = new List<string>();
        foreach (var entry in entries)
        {
            var value = entry.Trim();
            if (value.Length == 0) continue;
            if (TryParse(value, out var network, out var error)) networks.Add(network);
            else errors.Add($"{PyText.Repr(entry)}: {error}");
        }
        if (errors.Count > 0) throw new FormatException("Invalid IP allowlist entries: " + string.Join("; ", errors));
        return networks;
    }

    private static bool TryParse(string value, [NotNullWhen(true)] out IpNetwork? network, [NotNullWhen(false)] out string? error)
    {
        network = null;
        error = null;
        var slash = value.IndexOf('/');
        if (slash < 0)
        {
            if (IpAddressText.TryParse(value, out var address))
            {
                network = new IpNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
                return true;
            }
            error = $"{PyText.Repr(value)} does not appear to be an IPv4 or IPv6 address";
            return false;
        }
        if (IpAddressText.TryParse(value[..slash], out var baseAddress))
        {
            var max = baseAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefixText = value[(slash + 1)..];
            if (prefixText.Length is > 0 and <= 3 && prefixText.All(char.IsAsciiDigit))
            {
                var prefix = int.Parse(prefixText, CultureInfo.InvariantCulture);
                if (prefix <= max)
                {
                    network = new IpNetwork(Mask(baseAddress, prefix), prefix);
                    return true;
                }
            }
        }
        error = $"{PyText.Repr(value)} does not appear to be an IPv4 or IPv6 network";
        return false;
    }

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
        return new IPAddress(bytes);
    }
}
