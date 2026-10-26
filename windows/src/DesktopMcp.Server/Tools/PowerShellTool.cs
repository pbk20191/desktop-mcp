using DesktopMcp.Core.PowerShell;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/shell.py:22-27.</summary>
public sealed class PowerShellTool(Tool protocolTool, PowerShellRunner runner) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var command = arguments.RequiredString("command");
        var timeout = arguments.Integer("timeout", 30);
        var result = await runner.RunAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        return ToolResults.Text($"Response: {result.Output}\nStatus Code: {result.StatusCode}");
    }
}
