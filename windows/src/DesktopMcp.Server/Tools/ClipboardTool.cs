using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.ClipboardAccess;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/clipboard.py:20-54.</summary>
public sealed class ClipboardTool(Tool protocolTool, IClipboard clipboard) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var mode = arguments.RequiredString("mode");
        var text = arguments.OptionalString("text");
        switch (mode)
        {
            case "get":
                var data = await clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);
                return ToolResults.Text(data is null ? "Clipboard is empty or contains non-text data." : $"Clipboard content:\n{data}");
            case "set":
                if (text is null) return ToolResults.Text("Error: text parameter required for set mode.");
                await clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
                var cut = PyText.CodePointLength(text) > 100 ? "..." : "";
                return ToolResults.Text($"Clipboard set to: {PyText.Prefix(text, 100)}{cut}");
            default:
                return ToolResults.Text("Error: mode must be either \"get\" or \"set\".");
        }
    }
}
