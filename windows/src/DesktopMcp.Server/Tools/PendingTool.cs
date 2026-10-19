using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>A contract entry whose implementation lands in a later phase.</summary>
public sealed class PendingTool(Tool protocolTool) : SpecTool(protocolTool)
{
    protected override ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("not available in this build yet");
}
