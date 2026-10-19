using DesktopMcp.Core;
using DesktopMcp.Core.Config;
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
        return RunAsync(plan, server => server.WithStdioServerTransport(), cancellationToken);
    }

    /// <summary>The server <paramref name="plan"/> describes over the transport <paramref name="addTransport"/> adds; tests pass in-memory streams.</summary>
    internal static async Task<int> RunAsync(ServePlan plan, Action<IMcpServerBuilder> addTransport, CancellationToken cancellationToken)
    {
        var catalog = ToolSpecCatalog.LoadEmbedded();
        var kept = ToolFilter.Apply(catalog.Names, plan.ExplicitTools, plan.ExcludeTools).ToHashSet(StringComparer.Ordinal);
        // Typed as IEnumerable so WithTools binds to its tool-instance overload, not WithTools<T>(T target), which
        // reflects over T and breaks NativeAOT (IL3050).
        IEnumerable<McpServerTool> tools = ToolRegistry.CreateAll(catalog, ToolServices.CreateDefault()).Where(tool => kept.Contains(tool.ProtocolTool.Name)).ToArray();

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
        logger.LogDebug("Starting desktop-mcp (transport={Transport}, tools={Tools}, exclude={Exclude})",
            plan.Transport,
            plan.ExplicitTools.Count > 0 ? string.Join(",", plan.ExplicitTools) : "all",
            plan.ExcludeTools.Count > 0 ? string.Join(",", plan.ExcludeTools) : "none");
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
