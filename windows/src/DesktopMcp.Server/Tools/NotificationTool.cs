using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Toasts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/notification.py and notifications/service.py (Decisions 14).</summary>
public sealed class NotificationTool(Tool protocolTool, IToastSender toasts) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var title = arguments.RequiredString("title");
        var message = arguments.RequiredString("message");
        var appId = arguments.RequiredString("app_id");
        try
        {
            // The sender bounds its own WinRT call; WaitAsync lets the caller stop waiting sooner.
            await Task.Run(() => toasts.Show(title, message, appId), cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return ToolResults.Text($"Notification sent: \"{title}\" - {message}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ToolResults.Text($"Notification may have been sent. Error: {PyText.Prefix(e.Message, 200)}");
        }
    }
}
