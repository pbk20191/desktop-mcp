using DesktopMcp.Core;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Hosting;

/// <summary>serve --transport stdio: Generic Host + the SDK's stdio transport; logs on stderr only (spec §8).</summary>
public static class StdioServer
{
    public static Task<int> RunAsync(ServePlan plan, CancellationToken cancellationToken)
    {
        // upstream serve sets NO_COLOR for stdio (__main__.py:725-726).
        if (Environment.GetEnvironmentVariable("NO_COLOR") is null) Environment.SetEnvironmentVariable("NO_COLOR", "1");
        // spec §5: every process the server starts dies with it (Decisions 19). Here and not in the shared overload
        // below, so the test host never joins a kill-on-close job.
        if (!ProcessJob.TryAssignKillOnClose())
            plan = plan with { Warnings = [.. plan.Warnings, "Could not put the server in a kill-on-close Job Object; PowerShell children may outlive it."] };
        return RunAsync(plan, ToolServices.CreateDefault(), server => server.WithStdioServerTransport(), cancellationToken);
    }

    /// <summary>
    /// The server <paramref name="plan"/> describes over the transport <paramref name="addTransport"/> adds, with the tools
    /// over <paramref name="services"/>; tests pass in-memory streams and fakes.
    /// </summary>
    internal static async Task<int> RunAsync(ServePlan plan, ToolServices services, Action<IMcpServerBuilder> addTransport,
        CancellationToken cancellationToken)
    {
        var catalog = ToolSpecCatalog.LoadEmbedded();
        var kept = ToolFilter.Apply(catalog.Names, plan.ExplicitTools, plan.ExcludeTools).ToHashSet(StringComparer.Ordinal);
        // Typed as IEnumerable so WithTools binds to its tool-instance overload, not WithTools<T>(T target), which
        // reflects over T and breaks NativeAOT (IL3050).
        IEnumerable<McpServerTool> tools = ToolRegistry.CreateAll(catalog, services).Where(tool => kept.Contains(tool.ProtocolTool.Name)).ToArray();

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new StderrLoggerProvider());
        builder.Logging.SetMinimumLevel(plan.Debug ? LogLevel.Debug : LogLevel.Warning);
        var server = builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = ServerText.Name, Version = ServerText.Version };
            options.ServerInstructions = ServerText.Instructions;
        });
        addTransport(server);
        server.WithTools(tools);

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("desktop-mcp");
        foreach (var warning in plan.Warnings) logger.LogWarning("{Warning}", warning);
        // Exceptions from dispatcher timer ticks and posted callbacks never escape into its queue; they are logged here.
        services.Dispatcher.Report = e => logger.LogWarning(e, "Dispatcher work failed: {Message}", e.Message);
        // Decisions 19: whatever key or button the input still holds is released when the host stops, and again after
        // it returns in case the transport ended without ApplicationStopping (a second release finds nothing held). New
        // presses are blocked first, so a call still running cannot hold a key again after the release.
        void ReleaseHeldInput()
        {
            try
            {
                services.Ledger.BlockNew();
                services.Ledger.ReleaseAll();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Could not release held input on shutdown: {Message}", e.Message);
            }
        }
        using var releaseOnStop = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(ReleaseHeldInput);
        logger.LogDebug("Starting desktop-mcp (transport={Transport}, tools={Tools}, exclude={Exclude})",
            plan.Transport,
            plan.ExplicitTools.Count > 0 ? string.Join(",", plan.ExplicitTools) : "all",
            plan.ExcludeTools.Count > 0 ? string.Join(",", plan.ExcludeTools) : "none");
        try
        {
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseHeldInput();
        }
        return 0;
    }
}
