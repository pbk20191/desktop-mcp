using DesktopMcp.Core.Config;
using DesktopMcp.Desktop;
using DesktopMcp.Desktop.Control;
using DesktopMcp.Server.Control;
using DesktopMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Hosting;

/// <summary>serve --transport stdio: Generic Host + the SDK's stdio transport; logs on stderr only (spec §8).</summary>
public static class StdioServer
{
    /// <summary>
    /// The production stdio server. <see cref="ServeCommand"/> has already put the process in its kill-on-close Job
    /// Object (Decisions 19).
    /// </summary>
    public static Task<int> RunAsync(ServePlan plan, CancellationToken cancellationToken)
    {
        // upstream serve sets NO_COLOR for stdio (__main__.py:725-726).
        if (Environment.GetEnvironmentVariable("NO_COLOR") is null) Environment.SetEnvironmentVariable("NO_COLOR", "1");
        var desktopLog = new DesktopLogBridge();
        return RunAsync(plan, ToolServices.Create(plan, desktopLog.Write), server => server.WithStdioServerTransport(), cancellationToken, desktopLog);
    }

    /// <summary>
    /// The server <paramref name="plan"/> describes over the transport <paramref name="addTransport"/> adds, with the tools
    /// over <paramref name="services"/>; tests pass in-memory streams and fakes. The control starts before the MCP server
    /// and stops after it; every tool call goes through the <see cref="ControlGate"/> filter (<see cref="McpServerSetup"/>).
    /// <paramref name="desktopLog"/>, when given, is pointed at the server's logger once the host exists.
    /// </summary>
    internal static async Task<int> RunAsync(ServePlan plan, ToolServices services, Action<IMcpServerBuilder> addTransport,
        CancellationToken cancellationToken, DesktopLogBridge? desktopLog = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new StderrLoggerProvider());
        builder.Logging.SetMinimumLevel(plan.Debug ? LogLevel.Debug : LogLevel.Warning);
        // stdio has one session for the process's life: it is never forgotten for being idle (spec §7, Decisions 22).
        var sessions = new SessionRegistry(services.Time, ttl: null);
        addTransport(McpServerSetup.Add(builder.Services, plan, services, sessions));

        using var host = builder.Build();
        var logger = McpServerSetup.AfterBuild(host.Services, plan, services, desktopLog);
        using var releaseOnStop = McpServerSetup.ReleaseHeldInputOnStop(host.Services, services, logger);
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}

/// <summary>The <see cref="ControlNotifier"/> with the host: started after the control, closed before it.</summary>
internal sealed class ControlNotifierHostedService(IDesktopControl control, SessionRegistry sessions, ILogger log) : IHostedService
{
    private readonly ControlNotifier _notifier = new(control, sessions, log);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _notifier.Start();
        }
        catch (Exception e)
        {
            // Notifications are best effort; the tools keep serving without them.
            log.LogError(e, "Control notifications failed to start: {Message}", e.Message);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.LogError(e, "Control notifications failed to stop: {Message}", e.Message);
        }
    }
}

/// <summary>
/// The desktop services' <see cref="DesktopLog"/> on the server's logger. The services are created before the host that
/// owns the logger, so lines written before <see cref="Attach"/> are dropped (nothing logs while being created).
/// </summary>
internal sealed class DesktopLogBridge
{
    private ILogger? _logger;

    public void Attach(ILogger logger) => Volatile.Write(ref _logger, logger);

    public void Write(DesktopLogLevel level, string message, Exception? error)
    {
        try
        {
            var logger = Volatile.Read(ref _logger);
            if (logger is null) return;
            var logLevel = level switch
            {
                DesktopLogLevel.Debug => LogLevel.Debug,
                DesktopLogLevel.Warning => LogLevel.Warning,
                _ => LogLevel.Error,
            };
            logger.Log(logLevel, error, "{Message}", message);
        }
        catch (Exception)
        {
            // DesktopLog sinks must not throw.
        }
    }
}
