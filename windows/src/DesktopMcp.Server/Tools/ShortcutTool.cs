using DesktopMcp.Core.Input;
using DesktopMcp.Desktop.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/input.py:418-420. The shortcut is parsed before anything is sent (P3 Decisions 16), then, inside the
/// input lock, the foreground window's integrity is checked (Decisions 8) and the keys are pressed. No outputSchema, so
/// the result is plain content.
/// </summary>
public sealed class ShortcutTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var shortcut = ToolArguments.From(request).RequiredString("shortcut");
        var tokens = ShortcutParser.Parse(shortcut);

        using (await services.InputLock.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            services.Targets.CheckForegroundInput();
            await services.Input.PressKeysAsync(tokens, InputTiming.ShortcutInterval, InputTiming.OperationWait, cancellationToken)
                .ConfigureAwait(false);
            return ToolResults.Content($"Pressed {shortcut}.", null);
        }
    }
}
