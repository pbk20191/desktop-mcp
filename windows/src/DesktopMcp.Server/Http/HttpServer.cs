using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Net;
using DesktopMcp.Core.Security;
using DesktopMcp.Server.Control;
using DesktopMcp.Server.Hosting;
using DesktopMcp.Server.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace DesktopMcp.Server.Http;

/// <summary>
/// serve --transport streamable-http|sse: the MCP server on Kestrel (spec §8 Transports; Decisions 4, 7, 8). The host
/// reads no ambient configuration: no appsettings, no ASPNETCORE_*/DOTNET_* settings, endpoints only from the plan.
/// </summary>
public static class HttpServer
{
    /// <summary>
    /// The SDK's documented AppContext switch that turns on the legacy SSE endpoints, in place of the obsolete
    /// <c>EnableLegacySse</c> property (Decisions 4). Each <see cref="HttpServerTransportOptions"/> reads it once, when it
    /// is constructed (preflight F6).
    /// </summary>
    internal const string LegacySseSwitch = "ModelContextProtocol.AspNetCore.EnableLegacySse";

    /// <summary>Held from setting <see cref="LegacySseSwitch"/> until this host's transport options have read it.</summary>
    private static readonly Lock LegacySseLock = new();

    /// <summary>The production HTTP server.</summary>
    public static Task<int> RunAsync(ServePlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var desktopLog = new DesktopLogBridge();
        return RunAsync(plan, ToolServices.Create(plan, desktopLog.Write), ct, desktopLog: desktopLog);
    }

    /// <summary>
    /// Serves <paramref name="plan"/> with the tools over <paramref name="services"/> until <paramref name="ct"/> is
    /// cancelled or the host stops. Once Kestrel is bound, the announcement line goes to <paramref name="announce"/>
    /// (default stderr) and <paramref name="listening"/> gets the announced endpoint. A bad host, a TLS certificate or key
    /// that does not load, or a failed bind prints one <c>Error:</c> line there and returns 1.
    /// </summary>
    internal static async Task<int> RunAsync(ServePlan plan, ToolServices services, CancellationToken ct,
        Action<Uri>? listening = null, TextWriter? announce = null, DesktopLogBridge? desktopLog = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(services);
        // Decisions 5: stateless Streamable HTTP cannot send the client a request, so Scrape never asks it to sample.
        services = services with { SamplingEnabled = SessionTtl(plan) != TimeSpan.Zero };
        announce ??= Console.Error;
        IReadOnlyList<ListenTarget> targets;
        X509Certificate2? loaded;
        try
        {
            targets = ListenTarget.Resolve(plan.Host, LookUp);
            // Decisions 11: loaded before the host is built; ServePlan has checked that both files are given.
            loaded = plan.SslCertfile is { } certPath && plan.SslKeyfile is { } keyPath ? TlsCertificate.Load(certPath, keyPath) : null;
        }
        catch (ConfigException e)
        {
            announce.WriteLine($"Error: {e.Message}");
            return 1;
        }
        // Declared before the host, so it is disposed (and its temporary key container removed) after the host stops.
        using var certificate = loaded;
        Action<ListenOptions> protocols = certificate is null ? Http1 : listen => Https(listen, certificate);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            // Fixed, so ASPNETCORE_ENVIRONMENT=Development cannot add the developer exception page (preflight F3).
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.Sources.Clear();
        // The builder has already copied ASPNETCORE_URLS and ASPNETCORE_PREFERHOSTINGURLS into its host options, and the
        // web host falls back to that copy when "urls" is empty. ";" is a non-empty list of no URLs: no fallback, no
        // server address, so only the ListenTargets below bind and Kestrel has nothing to warn that it overrides.
        builder.Configuration.AddInMemoryCollection([new(WebHostDefaults.ServerUrlsKey, ";"), new(WebHostDefaults.PreferHostingUrlsKey, "false")]);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new StderrLoggerProvider());
        builder.Logging.SetMinimumLevel(plan.Debug ? LogLevel.Debug : LogLevel.Warning);
        // A failed start is reported as one Error: line below; the host's own "Hosting failed to start" and its stack
        // trace only with --debug (Review Focus 5). Only while starting: later host errors (a failed background
        // service) are logged as usual.
        var starting = 1;
        var minimum = plan.Debug ? LogLevel.Debug : LogLevel.Warning;
        builder.Logging.AddFilter(HostCategory, level => level >= minimum && (plan.Debug || Volatile.Read(ref starting) == 0));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (var target in targets)
            {
                switch (target)
                {
                    // Kestrel cannot bind localhost on a dynamic port (preflight F2): port 0 binds 127.0.0.1 only.
                    case ListenTarget.Localhost when plan.Port == 0:
                        kestrel.Listen(IPAddress.Loopback, 0, protocols);
                        break;
                    case ListenTarget.Localhost:
                        kestrel.ListenLocalhost(plan.Port, protocols);
                        break;
                    case ListenTarget.Address address:
                        kestrel.Listen(address.Ip, plan.Port, protocols);
                        break;
                }
            }
        });
        McpServerSetup.Add(builder.Services, plan, services, new SessionRegistry(services.Time, SessionTtl(plan)))
            .WithHttpTransport(options => ConfigureTransport(plan, options));

        await using var app = builder.Build();
        var logger = McpServerSetup.AfterBuild(app.Services, plan, services, desktopLog);
        using var releaseOnStop = McpServerSetup.ReleaseHeldInputOnStop(app.Services, services, logger);
        // Decisions 12: the OAuth routes and their tokens only when both the client id and secret are configured. The
        // issuer names the port Kestrel bound, known once the host has started (no request arrives before that).
        var boundPort = plan.Port;
        OAuthServer? oauth = plan.OAuthClientId is { } clientId && plan.OAuthClientSecret is { } clientSecret
            ? new OAuthServer(clientId, clientSecret, services.Time)
            : null;
        HttpPipeline.Use(app, plan, oauth is null ? null : oauth.ValidateToken);
        app.MapGet("/health", new RequestDelegate(HttpPipeline.Health));
        if (oauth is not null) OAuthEndpoints.Map(app, oauth, context => Issuer(plan, Volatile.Read(ref boundPort), context));
        MapTransport(app, plan);

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (BindFailure(e, plan) is { } line)
        {
            announce.WriteLine(line);
            await StopAfterFailedStartAsync(app, logger).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopAfterFailedStartAsync(app, logger).ConfigureAwait(false);
            return 0;
        }

        Volatile.Write(ref starting, 0);
        // With a DNS name that resolves to several addresses and port 0, each listener gets its own ephemeral port and
        // the announcement names the first one's (Decisions 7).
        Volatile.Write(ref boundPort, BoundPort(app, plan));
        var endpoint = new Uri($"{plan.Scheme}://{UrlHost(plan.Host)}:{boundPort}{EndpointPath(plan)}");
        // Decisions 8: one line whatever the log level, as uvicorn's "Uvicorn running on ...".
        announce.WriteLine($"desktop-mcp: serving {plan.Transport} on {endpoint.OriginalString}");
        listening?.Invoke(endpoint);
        await app.WaitForShutdownAsync(ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The one <c>Error:</c> line for a failed bind, or null when <paramref name="e"/> is not one.</summary>
    internal static string? BindFailure(Exception e, ServePlan plan) => e switch
    {
        // Kestrel wraps most bind failures (AddressInUseException or a SocketException) in an IOException.
        IOException or SocketException => $"Error: Could not listen on {UrlHost(plan.Host)}:{plan.Port}: {(e.InnerException ?? e).Message}",
        _ => null,
    };

    /// <summary>
    /// The OAuth issuer (Decisions 12): <c>{scheme}://{host}:{port}</c> with the port Kestrel bound and an IPv6 host
    /// bracketed; for a wildcard bind (<c>0.0.0.0</c>, <c>::</c>, empty), which no client can connect to, the request's
    /// <c>Host</c> header in place of <c>{host}:{port}</c> (the local address the request arrived on when it has none).
    /// </summary>
    internal static string Issuer(ServePlan plan, int boundPort, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        if (!IsWildcard(plan.Host)) return $"{plan.Scheme}://{UrlHost(plan.Host)}:{boundPort}";
        var host = context.Request.Host;
        if (host.HasValue) return $"{plan.Scheme}://{host.ToUriComponent()}";
        var local = context.Connection.LocalIpAddress;
        var address = local is null ? "127.0.0.1" : local.IsIPv4MappedToIPv6 ? local.MapToIPv4().ToString() : local.ToString();
        return $"{plan.Scheme}://{UrlHost(address)}:{(context.Connection.LocalPort > 0 ? context.Connection.LocalPort : boundPort)}";
    }

    /// <summary>The MCP endpoint's path: <c>/sse</c> for the legacy SSE transport, else <c>/mcp</c>.</summary>
    internal static string EndpointPath(ServePlan plan) => plan.Transport == "sse" ? "/sse" : "/mcp";

    /// <summary>
    /// How long a session that called no tool still gets control notifications (Decisions 18; upstream
    /// <c>__main__.py:512-516</c>): zero for stateless Streamable HTTP, whose requests have no session to notify (they
    /// would all share <see cref="McpServerSetup.NoSessionIdKey"/>), else 60 s.
    /// </summary>
    internal static TimeSpan? SessionTtl(ServePlan plan) => IsStateless(plan) ? TimeSpan.Zero : TimeSpan.FromSeconds(60);

    private const string HostCategory = "Microsoft.Extensions.Hosting.Internal.Host";

    /// <summary>Plain listeners speak HTTP/1.1 only, so cleartext HTTP/2 (h2c) is not offered (Decisions 11).</summary>
    private const HttpProtocols Plain = HttpProtocols.Http1;

    /// <summary>TLS listeners offer HTTP/2 and HTTP/1.1 through ALPN (<c>h2</c>, <c>http/1.1</c>).</summary>
    private const HttpProtocols Tls = HttpProtocols.Http1AndHttp2;

    private static void Http1(ListenOptions listen) => listen.Protocols = Plain;

    private static void Https(ListenOptions listen, X509Certificate2 certificate)
    {
        listen.Protocols = Tls;
        listen.UseHttps(certificate);
    }

    /// <summary>Decisions 4: stateful sessions unless --stateless-http (legacy SSE needs sessions, so it stays stateful).</summary>
    private static void ConfigureTransport(ServePlan plan, HttpServerTransportOptions options) =>
        options.SessionMode = IsStateless(plan) ? HttpServerSessionMode.Stateless : HttpServerSessionMode.Stateful;

    /// <summary>Streamable HTTP with --stateless-http; --stateless-http has no effect on SSE, as upstream.</summary>
    private static bool IsStateless(ServePlan plan) => plan.StatelessHttp && plan.Transport != "sse";

    /// <summary>
    /// Maps only the selected transport (Decisions 4): <c>/mcp</c> for Streamable HTTP; for <c>sse</c>,
    /// <c>MapMcp("")</c>'s <c>GET /sse</c> and <c>POST /message</c>, with the Streamable endpoint it also maps at
    /// <c>/</c> answering 404. The switch is process-wide, so it is set for every host and read while the lock is held.
    /// </summary>
    private static void MapTransport(WebApplication app, ServePlan plan)
    {
        var sse = plan.Transport == "sse";
        if (sse) app.Use(StreamableEndpointNotFound);
        lock (LegacySseLock)
        {
            AppContext.SetSwitch(LegacySseSwitch, sse);
            app.MapMcp(sse ? "" : "/mcp");
            // MapMcp reads the options already; resolving them here makes sure they were built under the lock.
            _ = app.Services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;
        }
    }

    /// <summary>For <c>sse</c>: 404 for <c>/</c> and <c>/mcp</c> (with or without the trailing slash).</summary>
    private static Task StreamableEndpointNotFound(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.Value is null or "" or "/" or "/mcp" or "/mcp/")
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        return next(context);
    }

    private static IPAddress[] LookUp(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Whether <paramref name="host"/> binds every address: empty, or an any-address literal (brackets allowed).</summary>
    private static bool IsWildcard(string host)
    {
        if (host.Length == 0) return true;
        var bare = host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        return IpAddressText.TryParse(bare, out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any));
    }

    /// <summary>The host as a URL names it: an IPv6 address in brackets (the empty host is <c>::</c>).</summary>
    private static string UrlHost(string host)
    {
        if (host.Length == 0) return "[::]";
        var bare = host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        return IpAddressText.TryParse(bare, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{bare}]" : host;
    }

    /// <summary>The port Kestrel bound (the plan's, or the ephemeral one for port 0).</summary>
    private static int BoundPort(WebApplication app, ServePlan plan)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        foreach (var address in addresses ?? [])
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri)) return uri.Port;
        return plan.Port;
    }

    /// <summary>Stops the hosted services that did start (the control, its dispatcher), so a failed start leaves nothing running.</summary>
    private static async Task StopAfterFailedStartAsync(WebApplication app, ILogger logger)
    {
        try
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogError(e, "The HTTP host failed to stop after a failed start: {Message}", e.Message);
        }
    }
}
