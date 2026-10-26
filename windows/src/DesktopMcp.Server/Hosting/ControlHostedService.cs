using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop.Dispatching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopMcp.Server.Hosting;

/// <summary>
/// Starts and stops the desktop control with the host (Decisions 14). Registered before the MCP server, so it starts
/// before the first request and stops after the last one; after the control stops, the dispatcher the monitor,
/// indicator and flash share is shut down (spec §5: the queue after the hooks and windows; preflight F17).
/// </summary>
public sealed class ControlHostedService(IDesktopControl control, ILogger log, DispatcherHost? dispatcher = null) : IHostedService
{
    private readonly DispatcherHost _dispatcher = dispatcher ?? DispatcherHost.Shared;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await control.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The control reports its own unavailability; the tools keep serving (gated calls are rejected).
            log.LogError(e, "Desktop control failed to start: {Message}", e.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await control.StopAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.LogError(e, "Desktop control failed to stop: {Message}", e.Message);
        }
        try
        {
            await _dispatcher.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.LogError(e, "The desktop dispatcher failed to shut down: {Message}", e.Message);
        }
    }
}
