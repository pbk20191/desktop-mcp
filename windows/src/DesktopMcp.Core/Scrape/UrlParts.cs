using System.Globalization;
using System.Net.Sockets;
using DesktopMcp.Core.Net;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Scrape;

/// <summary>
/// The parts of a URL that validate_url reads, split the way Python 3.14's urllib.parse.urlsplit splits them, with its
/// ValueError messages as FormatException messages. Known differences: a bracketed host is checked with .NET's IPv6
/// parser, which also accepts an empty scope ("[fe80::1%]") that Python rejects, and hostname lowercasing uses .NET's
/// simple case mapping rather than Python's full one.
/// </summary>
public sealed record UrlParts(string Scheme, string Netloc, string? Hostname, string? Username, string? Password, string? PortText)
{
    private const string InvalidIpv6 = "Invalid IPv6 URL";

    public static UrlParts Parse(string url)
    {
        // urlsplit strips C0 controls and spaces at the start only and removes tabs and newlines anywhere.
        var text = url.TrimStart(LeadingCharacters).Replace("\t", "").Replace("\r", "").Replace("\n", "");
        var scheme = "";
        var colon = text.IndexOf(':');
        if (colon > 0 && char.IsAsciiLetter(text[0]) && text[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            scheme = text[..colon].ToLowerInvariant();
            text = text[(colon + 1)..];
        }

        var netloc = "";
        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            var end = text.IndexOfAny(['/', '?', '#'], 2);
            netloc = end < 0 ? text[2..] : text[2..end];
            var open = netloc.Contains('[');
            if (open != netloc.Contains(']')) throw new FormatException(InvalidIpv6);
            if (open) CheckBracketedNetloc(netloc);
        }
        CheckNetloc(netloc);

        var at = netloc.LastIndexOf('@');
        string? username = null, password = null;
        if (at >= 0)
        {
            var userinfo = netloc[..at];
            var separator = userinfo.IndexOf(':');
            username = separator < 0 ? userinfo : userinfo[..separator];
            password = separator < 0 ? null : userinfo[(separator + 1)..];
        }
        var (hostname, port) = HostInfo(netloc[(at + 1)..]);
        return new UrlParts(scheme, netloc, Lower(hostname), username, password, port.Length == 0 ? null : port);
    }

    /// <summary>Python's SplitResult.port.</summary>
    public int? ReadPort()
    {
        if (PortText is null) return null;
        if (!PortText.All(char.IsAsciiDigit)) throw new FormatException($"Port could not be cast to integer value as {PyText.Repr(PortText)}");
        if (!int.TryParse(PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
            throw new FormatException("Port out of range 0-65535");
        return port;
    }

    /// <summary>NetlocResultMixinStr._hostinfo: the host is inside the brackets, or before the first colon.</summary>
    private static (string Hostname, string Port) HostInfo(string hostinfo)
    {
        var open = hostinfo.IndexOf('[');
        if (open < 0)
        {
            var colon = hostinfo.IndexOf(':');
            return colon < 0 ? (hostinfo, "") : (hostinfo[..colon], hostinfo[(colon + 1)..]);
        }
        var bracketed = hostinfo[(open + 1)..];
        var close = bracketed.IndexOf(']');
        if (close < 0) return (bracketed, "");
        var rest = bracketed[(close + 1)..];
        var portColon = rest.IndexOf(':');
        return (bracketed[..close], portColon < 0 ? "" : rest[(portColon + 1)..]);
    }

    /// <summary>The hostname property: lowercased except for an IPv6 zone after '%'; empty is None.</summary>
    private static string? Lower(string hostname)
    {
        if (hostname.Length == 0) return null;
        var percent = hostname.IndexOf('%');
        return percent < 0 ? hostname.ToLowerInvariant() : hostname[..percent].ToLowerInvariant() + hostname[percent..];
    }

    /// <summary>_check_bracketed_netloc: nothing before '[', only ":port" after ']', and a valid bracketed host.</summary>
    private static void CheckBracketedNetloc(string netloc)
    {
        var hostinfo = netloc[(netloc.LastIndexOf('@') + 1)..];
        var open = hostinfo.IndexOf('[');
        string hostname;
        if (open >= 0)
        {
            if (open > 0) throw new FormatException(InvalidIpv6);
            var close = hostinfo.IndexOf(']');
            hostname = close < 0 ? hostinfo[1..] : hostinfo[1..close];
            var after = close < 0 ? "" : hostinfo[(close + 1)..];
            if (after.Length > 0 && after[0] != ':') throw new FormatException(InvalidIpv6);
        }
        else
        {
            var colon = hostinfo.IndexOf(':');
            hostname = colon < 0 ? hostinfo : hostinfo[..colon];
        }
        CheckBracketedHost(hostname);
    }

    /// <summary>_check_bracketed_host: an IPvFuture literal or an IPv6 address.</summary>
    private static void CheckBracketedHost(string hostname)
    {
        if (hostname.StartsWith('v') || hostname.StartsWith('V'))
        {
            // \A[vV][a-fA-F0-9]+\..+\Z
            var dot = hostname.IndexOf('.');
            var valid = dot > 1 && dot < hostname.Length - 1 && hostname[1..dot].All(char.IsAsciiHexDigit) && !hostname[(dot + 1)..].Contains('\n');
            if (!valid) throw new FormatException("IPvFuture address is invalid");
            return;
        }
        if (!IpAddressText.TryParse(hostname, out var address))
            throw new FormatException($"{PyText.Repr(hostname)} does not appear to be an IPv4 or IPv6 address");
        if (address.AddressFamily == AddressFamily.InterNetwork) throw new FormatException("An IPv4 address cannot be in brackets");
    }

    /// <summary>
    /// _checknetloc: a netloc must not contain characters whose NFKC form holds '/', '?', '#', '@' or ':'. The process
    /// runs with invariant globalization, where string.Normalize does nothing, so the characters are listed: every one
    /// in Unicode 15.1 (all in the BMP), taken from CPython's unicodedata.
    /// </summary>
    private static void CheckNetloc(string netloc)
    {
        if (netloc.AsSpan().IndexOfAny(NfkcDelimiters) >= 0)
            throw new FormatException($"netloc '{netloc}' contains invalid characters under NFKC normalization");
    }

    private const string NfkcDelimiters =
        "⁇⁈⁉℀℁℅℆⩴︓︖﹕﹖﹟﹫＃／：？＠";

    private static readonly char[] LeadingCharacters = [.. Enumerable.Range(0, 0x21).Select(i => (char)i)];
}
