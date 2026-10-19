using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/input.py:422-436.</summary>
public sealed class WaitTool(Tool protocolTool) : SpecTool(protocolTool)
{
    // Task.Delay accepts at most uint.MaxValue - 1 milliseconds.
    private const long MaxSeconds = (uint.MaxValue - 1L) / 1000;

    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var duration = ToolArguments.From(request).RequiredInteger("duration");
        if (duration < 0) throw new ArgumentException("sleep length must be non-negative");
        if (duration > MaxSeconds) throw new ArgumentException("sleep length is too large");
        await Task.Delay(TimeSpan.FromSeconds(duration), cancellationToken).ConfigureAwait(false);
        return ToolResults.Text($"Waited for {duration} seconds.");
    }
}
