using DesktopMcp.Core;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Control;
using DesktopMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Hosting;

/// <summary>The MCP server setup both hosts share (stdio and HTTP); each host adds its own transport.</summary>
internal static class McpServerSetup
{
    /// <summary>
    /// The registry key of a session whose <see cref="McpSession.SessionId"/> is null: stdio's one session, and every
    /// stateless HTTP request, which must therefore get a session TTL of zero (Task 2) so they never share this entry.
    /// The SDK gives every request its own <see cref="McpServer"/> object, so the call's server cannot be the key
    /// (preflight F1).
    /// </summary>
    internal static readonly object NoSessionIdKey = new();

    /// <summary>
    /// Registers, in this order, the control, the control notifier, the MCP server (server info, instructions, the
    /// logging capability), the tools <paramref name="plan"/> keeps and the control gate filter, which remembers each
    /// calling session in <paramref name="sessions"/>. The caller adds the transport.
    /// </summary>
    public static IMcpServerBuilder Add(IServiceCollection services, ServePlan plan, ToolServices tools, SessionRegistry sessions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(sessions);
        var catalog = ToolSpecCatalog.LoadEmbedded();
        var kept = ToolFilter.Apply(catalog.Names, plan.ExplicitTools, plan.ExcludeTools).ToHashSet(StringComparer.Ordinal);
        // Typed as IEnumerable so WithTools binds to its tool-instance overload, not WithTools<T>(T target), which
        // reflects over T and breaks NativeAOT (IL3050).
        IEnumerable<McpServerTool> served = ToolRegistry.CreateAll(catalog, tools).Where(tool => kept.Contains(tool.ProtocolTool.Name)).ToArray();

        // Before AddMcpServer: hosted services start in registration order and stop in reverse, so the control is
        // running before the first request and stops after the last one. (WebApplication appends its server after
        // every user-added hosted service, so this holds for Kestrel too.)
        services.AddSingleton<IHostedService>(provider => new ControlHostedService(tools.Control,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("desktop-mcp"), tools.Dispatcher));
        // After the control and before the MCP server: the notifier starts after the control and is closed before it.
        services.AddSingleton<IHostedService>(provider => new ControlNotifierHostedService(tools.Control, sessions,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("desktop-mcp")));
        var server = services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = ServerText.Name, Version = ServerText.Version };
            options.ServerInstructions = ServerText.Instructions;
            // The control notifications are MCP log messages (spec §7); declared as raw JSON (McpLogging).
            options.Capabilities = McpLogging.DeclareLogging(options.Capabilities);
        });
        server.WithTools(served);
        var gate = new ControlGate(tools.Control, tools.Time);
        // One registry entry per session (spec §7): the key is the session, the value the latest call's server, through
        // which a notification reaches the session (a newer call replaces it).
        server.WithRequestFilters(filters => filters.AddCallToolFilter(ControlGateFilter.Create(gate,
            session => sessions.Remember(session.SessionId ?? NoSessionIdKey, new McpServerSession(session)))));
        return server;
    }

    /// <summary>
    /// The wiring once the host exists: <paramref name="desktopLog"/> and the dispatcher's faults go to the server's
    /// logger, and the plan's warnings are logged. Returns that logger.
    /// </summary>
    public static ILogger AfterBuild(IServiceProvider provider, ServePlan plan, ToolServices tools, DesktopLogBridge? desktopLog)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tools);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("desktop-mcp");
        desktopLog?.Attach(logger);
        foreach (var warning in plan.Warnings) logger.LogWarning("{Warning}", warning);
        // Exceptions from dispatcher timer ticks and posted callbacks never escape into its queue; they are logged here.
        tools.Dispatcher.Report = e => logger.LogWarning(e, "Dispatcher work failed: {Message}", e.Message);
        logger.LogDebug("Starting desktop-mcp (transport={Transport}, tools={Tools}, exclude={Exclude})",
            plan.Transport,
            plan.ExplicitTools.Count > 0 ? string.Join(",", plan.ExplicitTools) : "all",
            plan.ExcludeTools.Count > 0 ? string.Join(",", plan.ExcludeTools) : "none");
        return logger;
    }

    /// <summary>
    /// Decisions 19: whatever key or button the input still holds is released when the host stops, and again when the
    /// result is disposed, in case the host ended without ApplicationStopping (a second release finds nothing held). New
    /// presses are blocked first, so a call still running cannot hold a key again after the release.
    /// </summary>
    public static IDisposable ReleaseHeldInputOnStop(IServiceProvider services, ToolServices tools, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(log);
        void ReleaseHeldInput()
        {
            try
            {
                tools.Ledger.BlockNew();
                tools.Ledger.ReleaseAll();
            }
            catch (Exception e)
            {
                log.LogError(e, "Could not release held input on shutdown: {Message}", e.Message);
            }
        }
        var registration = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(ReleaseHeldInput);
        return new ReleaseOnDispose(registration, ReleaseHeldInput);
    }

    private sealed class ReleaseOnDispose(CancellationTokenRegistration registration, Action release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            registration.Dispose();
            release();
        }
    }
}
