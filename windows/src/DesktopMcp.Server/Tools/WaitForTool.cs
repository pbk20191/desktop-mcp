using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Snapshots;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/input.py:438-504 wait_for_tool with P3 Decisions 6 and 24-28: each poll reads only what the condition
/// needs (<see cref="ToolServices.WaitConditions"/>), never a full snapshot, and the label store is left alone. The
/// arguments are checked in upstream's order, then use_dom (True is refused, P2 Decisions 39), then the desktop session
/// once. The polls run on <see cref="ToolServices.Time"/>. WaitFor only reads, so it takes no input lock.
/// </summary>
public sealed class WaitForTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var condition = arguments.RequiredString("condition");
        var text = arguments.OptionalString("text");
        var windowName = arguments.OptionalString("window_name");
        var timeout = arguments.Get("timeout") is { } timeoutValue ? ActArguments.FiniteNumber(timeoutValue, "timeout") : 10.0;
        var interval = arguments.Get("interval") is { } intervalValue ? ActArguments.FiniteNumber(intervalValue, "interval") : 0.25;
        var normalized = WaitConditions.Validate(condition, text, windowName, timeout, interval);
        if (arguments.StrictBool("use_dom", false)) throw new NotSupportedException(SnapshotService.UseDomUnavailable);
        services.WaitConditions.EnsureSession();

        // upstream input.py:473-504.
        var time = services.Time;
        var name = WaitConditions.Name(normalized);
        var startedAt = time.GetTimestamp();
        var limit = TimeSpan.FromSeconds(timeout);
        var pause = TimeSpan.FromSeconds(interval);
        var attempts = 0;
        var lastDetail = "condition was not evaluated";
        while (true)
        {
            attempts++;
            var (matched, detail) = await services.WaitConditions.CheckAsync(normalized, text, windowName, cancellationToken).ConfigureAwait(false);
            // Decisions 28: a transiently failed poll (no detail) keeps the previous detail.
            if (detail is not null) lastDetail = detail;
            var elapsed = time.GetElapsedTime(startedAt);
            if (matched)
                return ToolResults.Text($"WaitFor condition '{name}' satisfied after {PyText.Fixed(elapsed.TotalSeconds, 2)}s and {attempts} attempt(s): {lastDetail}.");
            var remaining = limit - elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Timed out after {PyText.Fixed(timeout, 2)}s waiting for '{name}': {lastDetail}.");
            await Task.Delay(remaining < pause ? remaining : pause, time, cancellationToken).ConfigureAwait(false);
        }
    }
}
