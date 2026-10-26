using System.Net;
using System.Net.Sockets;
using DesktopMcp.Core.Config;

namespace DesktopMcp.Core.Scrape;

/// <summary>
/// upstream _is_private_target and _is_fake_ip_dns_answer (infrastructure/security.py:19-38) with Python 3.14's
/// ipaddress network tables. An IPv4-mapped IPv6 address is judged as its IPv4 address, as Python 3.13+ does for every
/// property; a scope ID plays no part.
/// </summary>
public static class AddressPolicy
{
    private static readonly IpNetwork[] V4Private = Networks(
        "0.0.0.0/8", "10.0.0.0/8", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.0.170/31",
        "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "240.0.0.0/4", "255.255.255.255/32");
    private static readonly IpNetwork[] V4PrivateExceptions = Networks("192.0.0.9/32", "192.0.0.10/32");
    // is_loopback, is_link_local, is_multicast, is_reserved, is_unspecified
    private static readonly IpNetwork[] V4Other = Networks("127.0.0.0/8", "169.254.0.0/16", "224.0.0.0/4", "240.0.0.0/4", "0.0.0.0/32");
    private static readonly IpNetwork[] V6Private = Networks(
        "::1/128", "::/128", "64:ff9b:1::/48", "100::/64", "2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20", "fc00::/7", "fe80::/10");
    private static readonly IpNetwork[] V6PrivateExceptions = Networks(
        "2001:1::1/128", "2001:1::2/128", "2001:3::/32", "2001:4:112::/48", "2001:20::/28", "2001:30::/28");
    // is_loopback, is_link_local, is_multicast, is_reserved (the rest of the list), is_unspecified
    private static readonly IpNetwork[] V6Other = Networks(
        "::1/128", "fe80::/10", "ff00::/8", "::/8", "100::/8", "200::/7", "400::/6", "800::/5", "1000::/4", "4000::/3", "6000::/3",
        "8000::/3", "a000::/3", "c000::/3", "e000::/4", "f000::/5", "f800::/6", "fe00::/9", "::/128");
    // Stricter than upstream on purpose: CGNAT (cloud metadata such as 100.100.100.200, tailnet peers) and deprecated site-local.
    private static readonly IpNetwork[] Stricter = Networks("100.64.0.0/10", "fec0::/10");
    private static readonly IpNetwork[] FakeIpRanges = Networks("198.18.0.0/15", "2001:2::/48");

    /// <summary>Python's is_private, is_loopback, is_link_local, is_multicast, is_reserved or is_unspecified, plus the Stricter ranges.</summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (In(Stricter, address)) return true;
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => (In(V4Private, address) && !In(V4PrivateExceptions, address)) || In(V4Other, address),
            AddressFamily.InterNetworkV6 => (In(V6Private, address) && !In(V6PrivateExceptions, address)) || In(V6Other, address),
            _ => true,
        };
    }

    /// <summary>Python's <c>ip in network</c>: an IPv6 address is never in an IPv4 network, so no mapping here.</summary>
    public static bool IsFakeIpRange(IPAddress address) => In(FakeIpRanges, address);

    private static bool In(IpNetwork[] networks, IPAddress address) => networks.Any(n => n.Contains(address));

    private static IpNetwork[] Networks(params string[] cidrs) => [.. cidrs.Select(IpNetwork.Parse)];
}
