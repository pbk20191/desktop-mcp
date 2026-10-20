using DesktopMcp.Desktop.Control;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// Base class for every tool. The protocol definition comes from the contract file; subclasses
/// only implement the call. Exceptions become upstream-style error results, except a cancellation of the call and a
/// control interruption (Decisions 17), which the control gate turns into its rejection.
/// </summary>
public abstract class SpecTool(Tool protocolTool) : McpServerTool
{
    public override Tool ProtocolTool { get; } = protocolTool;

    public override IReadOnlyList<object> Metadata => [];

    public sealed override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await InvokeCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (ControlErrors.IsInterruption(e))
        {
            throw;
        }
        catch (Exception e)
        {
            return ToolResults.Error(ProtocolTool.Name, e.Message);
        }
    }

    protected abstract ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken);
}
