using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.Net;

namespace DesktopMcp.Core.Scrape;

/// <summary>upstream Desktop.scrape over safe_get and validate_url (Decisions 16).</summary>
public sealed partial class ScrapeClient : IWebScraper
{
    internal const int MaxRequests = 5;
    internal const int MaxDownloadBytes = 8 * 1024 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<IPAddress, bool> _isBlocked;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<Uri, Uri?> _proxyFor;

    public ScrapeClient() : this(AddressPolicy.IsBlocked, Dns.GetHostAddressesAsync, SystemProxy) { }

    /// <summary>
    /// Test seam: tests replace the address rule, the resolver and the proxy choice (<c>null</c> is a direct
    /// connection). Production always uses the defaults.
    /// </summary>
    internal ScrapeClient(Func<IPAddress, bool> isBlocked, Func<string, CancellationToken, Task<IPAddress[]>> resolve, Func<Uri, Uri?> proxyFor)
    {
        _isBlocked = isBlocked;
        _resolve = resolve;
        _proxyFor = proxyFor;
    }

    // HttpClient.DefaultProxy reads the Windows proxy settings (and HTTP(S)_PROXY / NO_PROXY).
    internal static Uri? SystemProxy(Uri target)
    {
        var proxy = HttpClient.DefaultProxy;
        if (proxy.IsBypassed(target)) return null;
        var address = proxy.GetProxy(target);
        return address is null || address == target ? null : address;
    }

    /// <summary>The page as Markdown. Validation, HTTP and network failures throw <see cref="ScrapeException"/>.</summary>
    public async Task<string> FetchMarkdownAsync(string url, CancellationToken cancellationToken)
    {
        var current = url;
        for (var request = 1; ; request++)
        {
            var response = await GetAsync(current, cancellationToken).ConfigureAwait(false);
            if (response.Location is null)
            {
                if (response.Status is >= 400 and < 600)
                {
                    var kind = response.Status < 500 ? "Client" : "Server";
                    throw new ScrapeException($"HTTP error for {current}: {response.Status} {kind} Error: {response.Reason} for url: {response.RequestUri}");
                }
                return HtmlToMarkdown.Convert(Decode(response.Body, response.Charset));
            }
            if (request == MaxRequests) throw new ScrapeException("Too many redirects while fetching URL");
            // urljoin
            if (!Uri.TryCreate(new Uri(current), response.Location, out var next)) throw new ScrapeException($"Invalid URL: {response.Location}");
            current = next.AbsoluteUri;
        }
    }

    /// <summary>upstream validate_url: the address to connect to, or a ScrapeException with upstream's message.</summary>
    internal async Task<IPAddress> ValidateAsync(string url, CancellationToken cancellationToken)
    {
        UrlParts parts;
        // upstream wraps every urlparse failure the same way.
        try { parts = UrlParts.Parse(url); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new ScrapeException($"Invalid URL: {url}"); }
        if (parts.Scheme is not ("http" or "https")) throw new ScrapeException($"URL scheme '{parts.Scheme}' is not allowed; use http or https.");
        if (string.IsNullOrEmpty(parts.Hostname)) throw new ScrapeException($"URL has no hostname: {url}");
        if (!string.IsNullOrEmpty(parts.Username) || !string.IsNullOrEmpty(parts.Password))
            throw new ScrapeException("URLs with embedded credentials are not allowed.");
        try { parts.ReadPort(); }
        catch (FormatException e) { throw new ScrapeException(e.Message); }
        return await ResolveAllowedAsync(parts.Hostname, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The hostname's first address after every resolved address has passed the address rule.</summary>
    private async Task<IPAddress> ResolveAllowedAsync(string hostname, CancellationToken cancellationToken)
    {
        // getaddrinfo returns an IP literal as itself; resolving it here would only add Dns's own refusals.
        var hostnameIsLiteral = IpAddressText.TryParse(hostname, out var literal);
        IPAddress[] addresses;
        if (hostnameIsLiteral) addresses = [literal!];
        else
        {
            try { addresses = await _resolve(hostname, cancellationToken).ConfigureAwait(false); }
            // Dns throws ArgumentException for names it parses as the unspecified address ("0").
            catch (Exception e) when (e is SocketException or ArgumentException)
            {
                throw new ScrapeException($"Could not resolve hostname '{hostname}': {e.Message}");
            }
        }
        if (addresses.Length == 0) throw new ScrapeException($"Could not resolve hostname '{hostname}': no addresses");
        foreach (var address in addresses)
        {
            if (!_isBlocked(address)) continue;
            if (!hostnameIsLiteral && AddressPolicy.IsFakeIpRange(address))
            {
                throw new ScrapeException(
                    $"Hostname '{hostname}' resolved to {address}, which is in a benchmarking range commonly used by proxy or VPN Fake-IP DNS. " +
                    "The address remains blocked by SSRF protection. Configure the proxy/VPN to return the hostname's real public address or " +
                    "exclude this hostname from Fake-IP mode.");
            }
            throw new ScrapeException($"Private, loopback, link-local, multicast, and reserved addresses are blocked: {address}");
        }
        return addresses[0];
    }

    private sealed record Fetched(int Status, string Reason, string? Location, byte[] Body, string? Charset, string RequestUri);

    // upstream safe_get: one validated request, never following redirects, connecting only to the validated address.
    private async Task<Fetched> GetAsync(string url, CancellationToken cancellationToken)
    {
        var pinned = await ValidateAsync(url, cancellationToken).ConfigureAwait(false);
        Uri target;
        try { target = new Uri(url, UriKind.Absolute); }
        catch (UriFormatException) { throw new ScrapeException($"Invalid URL: {url}"); }

        var proxy = _proxyFor(target);
        SocketsHttpHandler handler;
        if (proxy is null)
        {
            // Direct: the socket dials the validated address whatever host System.Uri read from the URL.
            handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(pinned.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(pinned, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
        }
        else
        {
            // Through a proxy there is no pin: the proxy resolves the host itself, so a DNS answer that changes after
            // validation (rebinding) can still redirect it, as with upstream behind a proxy. What can be closed is the
            // parser gap: the proxy is asked for System.Uri's host, which may differ from the urlsplit host validated
            // above ("0x7f.1" is 127.0.0.1 to System.Uri), so that host must pass the same rule.
            if (!SameHost(target.IdnHost, UrlParts.Parse(url).Hostname!))
                await ResolveAllowedAsync(target.IdnHost.Trim('[', ']'), cancellationToken).ConfigureAwait(false);
            handler = new SocketsHttpHandler { UseProxy = true, Proxy = new WebProxy(proxy) };
        }
        handler.AllowAutoRedirect = false;
        handler.AutomaticDecompression = DecompressionMethods.All;
        handler.ConnectTimeout = RequestTimeout;

        using (handler)
        using (var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan })
        using (var request = new HttpRequestMessage(HttpMethod.Get, target)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        })
        {
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("desktop-mcp", ServerText.Version));
            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                // requests' is_redirect: a Location header and 301, 302, 303, 307 or 308. The raw value, as urljoin sees it.
                var location = status is 301 or 302 or 303 or 307 or 308 && response.Headers.NonValidated.TryGetValues("Location", out var values)
                    ? values.First()
                    : null;
                var body = location is null ? await ReadBodyAsync(response.Content, linked.Token).ConfigureAwait(false) : [];
                return new Fetched(status, response.ReasonPhrase ?? "", location, body, response.Content.Headers.ContentType?.CharSet,
                    request.RequestUri!.AbsoluteUri);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ScrapeException($"Request timed out for {url}: no complete response within {RequestTimeout.TotalSeconds:0} seconds");
            }
            catch (HttpRequestException e)
            {
                throw new ScrapeException($"Failed to connect to {url}: {e.Message}");
            }
        }
    }

    // System.Uri's host against urlsplit's: case and IPv6 brackets aside.
    private static bool SameHost(string uriHost, string hostname) =>
        string.Equals(uriHost.Trim('[', ']'), hostname, StringComparison.OrdinalIgnoreCase);

    // The body up to MaxDownloadBytes; a page that keeps streaming cannot fill memory.
    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < MaxDownloadBytes && (read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            buffer.Write(chunk, 0, (int)Math.Min(read, MaxDownloadBytes - buffer.Length));
        return buffer.ToArray();
    }

    // The header's charset, else a <meta charset> in the first 1024 bytes, else UTF-8.
    internal static string Decode(byte[] body, string? charset)
    {
        var name = charset ?? MetaCharset(body);
        if (name is not null)
        {
            try { return TextEncodings.Get(name).Encoding.GetString(body); }
            catch (ArgumentException) { }
        }
        var text = Encoding.UTF8.GetString(body);
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    private static string? MetaCharset(byte[] body)
    {
        var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 1024));
        var match = MetaCharsetPattern().Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""<meta[^>]+charset\s*=\s*["']?([A-Za-z0-9_\-:.]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharsetPattern();
}
