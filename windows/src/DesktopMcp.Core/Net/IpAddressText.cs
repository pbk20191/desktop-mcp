using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DesktopMcp.Core.Net;

/// <summary>
/// Python's <c>ipaddress.ip_address</c> parsing: IPv4 as exactly four decimal octets without leading zeros, IPv6
/// without brackets. .NET's IPAddress.TryParse alone is more lenient ("1.2.3" and "010.0.0.1" parse there).
/// </summary>
public static class IpAddressText
{
    public static bool TryParse(string text, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        if (text.Contains(':'))
        {
            if (text.Contains('[') || text.Contains(']') || text.Contains('/')) return false;
            return IPAddress.TryParse(text, out address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0')) return false;
            var value = int.Parse(part, CultureInfo.InvariantCulture);
            if (value > 255) return false;
            bytes[i] = (byte)value;
        }
        address = new IPAddress(bytes);
        return true;
    }

    /// <summary>upstream is_loopback_host (infrastructure/auth.py:82-89).</summary>
    public static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }
}
