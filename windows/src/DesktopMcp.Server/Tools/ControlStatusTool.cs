using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Control;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/control_status.py: the control status as its JSON object, as text and as structuredContent (spec §4).
/// The gate never holds it back, so it answers while the user has control or another call runs.
/// </summary>
public sealed class ControlStatusTool(Tool protocolTool, IDesktopControl control) : SpecTool(protocolTool)
{
    protected override ValueTask<CallToolResult> InvokeCoreAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolResults.Object(ControlJson.Status(control.Status())));
}
