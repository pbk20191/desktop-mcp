using DesktopMcp.Core.Processes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/process.py:20-42; force uses the loose rule (Decisions 6).</summary>
public sealed class ProcessTool(Tool protocolTool, ProcessService processes) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var mode = arguments.RequiredString("mode");
        var name = arguments.OptionalString("name");
        var pid = arguments.OptionalInteger("pid");
        var sortBy = arguments.String("sort_by", "memory");
        var limit = arguments.Integer("limit", 20);
        var text = mode switch
        {
            "list" => await processes.ListAsync(name, sortBy, limit, cancellationToken).ConfigureAwait(false),
            "kill" => await Task.Run(() => processes.Kill(name, pid, arguments.LooseBool("force", false)), cancellationToken).ConfigureAwait(false),
            _ => "Error: mode must be either \"list\" or \"kill\".",
        };
        return ToolResults.Text(text);
    }
}
