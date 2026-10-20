using System.Net;
using System.Net.Sockets;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Net;
using DesktopMcp.Server.Control;
using DesktopMcp.Server.Hosting;
using DesktopMcp.Server.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace DesktopMcp.Server.Http;

/// <summary>
/// serve --transport streamable-http|sse: the MCP server on Kestrel (spec §8 Transports; Decisions 4, 7, 8). The host
/// reads no ambient configuration: no appsettings, no ASPNETCORE_*/DOTNET_* settings, endpoints only from the plan.
/// </summary>
public static class HttpServer
{
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
    /// (default stderr) and <paramref name="listening"/> gets the announced endpoint. A bad host or a failed bind prints
    /// one <c>Error:</c> line there and returns 1.
    /// </summary>
    internal static async Task<int> RunAsync(ServePlan plan, ToolServices services, CancellationToken ct,
        Action<Uri>? listening = null, TextWriter? announce = null, DesktopLogBridge? desktopLog = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(services);
        announce ??= Console.Error;
        IReadOnlyList<ListenTarget> targets;
        try
        {
            targets = ListenTarget.Resolve(plan.Host, LookUp);
        }
        catch (ConfigException e)
        {
            announce.WriteLine($"Error: {e.Message}");
            return 1;
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            // Fixed, so ASPNETCORE_ENVIRONMENT=Development cannot add the developer exception page (preflight F3).
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new StderrLoggerProvider());
        builder.Logging.SetMinimumLevel(plan.Debug ? LogLevel.Debug : LogLevel.Warning);
        // A failed start is reported as one Error: line below; the host's own "Hosting failed to start" and its stack
        // trace only with --debug (Review Focus 5).
        if (!plan.Debug) builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.Critical);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (var target in targets)
            {
                switch (target)
                {
                    // Kestrel cannot bind localhost on a dynamic port (preflight F2): port 0 binds 127.0.0.1 only.
                    case ListenTarget.Localhost when plan.Port == 0:
                        kestrel.Listen(IPAddress.Loopback, 0, Http1);
                        break;
                    case ListenTarget.Localhost:
                        kestrel.ListenLocalhost(plan.Port, Http1);
                        break;
                    case ListenTarget.Address address:
                        kestrel.Listen(address.Ip, plan.Port, Http1);
                        break;
                }
            }
        });
        McpServerSetup.Add(builder.Services, plan, services, new SessionRegistry(services.Time, SessionTtl(plan)))
            .WithHttpTransport(options => ConfigureTransport(plan, options));

        await using var app = builder.Build();
        var logger = McpServerSetup.AfterBuild(app.Services, plan, services, desktopLog);
        using var releaseOnStop = McpServerSetup.ReleaseHeldInputOnStop(app.Services, services, logger);
        app.MapMcp(EndpointPath(plan) == "/sse" ? "" : "/mcp");

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            // Kestrel wraps the bind failure (AddressInUseException or a SocketException) in an IOException.
            announce.WriteLine($"Error: Could not listen on {UrlHost(plan.Host)}:{plan.Port}: {(e.InnerException ?? e).Message}");
            await StopAfterFailedStartAsync(app, logger).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopAfterFailedStartAsync(app, logger).ConfigureAwait(false);
            return 0;
        }

        var endpoint = new Uri($"{plan.Scheme}://{UrlHost(plan.Host)}:{BoundPort(app, plan)}{EndpointPath(plan)}");
        // Decisions 8: one line whatever the log level, as uvicorn's "Uvicorn running on ...".
        announce.WriteLine($"desktop-mcp: serving {plan.Transport} on {endpoint.OriginalString}");
        listening?.Invoke(endpoint);
        await app.WaitForShutdownAsync(ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The MCP endpoint's path: <c>/sse</c> for the legacy SSE transport, else <c>/mcp</c>.</summary>
    internal static string EndpointPath(ServePlan plan) => plan.Transport == "sse" ? "/sse" : "/mcp";

    /// <summary>How long a session that called no tool still gets control notifications (Decisions 18).</summary>
    internal static TimeSpan? SessionTtl(ServePlan plan) => TimeSpan.FromSeconds(60);

    private const HttpProtocols Plain = HttpProtocols.Http1;

    private static void Http1(ListenOptions listen) => listen.Protocols = Plain;

    /// <summary>Decisions 4: stateful sessions unless --stateless-http (legacy SSE needs sessions, so it stays stateful).</summary>
    private static void ConfigureTransport(ServePlan plan, HttpServerTransportOptions options) =>
        options.SessionMode = plan.StatelessHttp && plan.Transport != "sse" ? HttpServerSessionMode.Stateless : HttpServerSessionMode.Stateful;

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
