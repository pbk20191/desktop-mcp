using DesktopMcp.Core.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/input.py:311-333 with Decisions 37: an invalid type/direction combination is upstream's text as a
/// normal result; without a point the cursor, read before the first wheel event, is reported. Arguments first, then the
/// input lock, then the target (preflight F12).
/// </summary>
public sealed class ScrollTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var label = arguments.OptionalInteger("label");
        var type = ActArguments.Choice(arguments.Get("type"), "type", "vertical", "horizontal", "vertical");
        var direction = ActArguments.Choice(arguments.Get("direction"), "direction", "down", "up", "down", "left", "right");
        var wheelTimes = arguments.Integer("wheel_times", 1);
        var loc = label is null ? ActArguments.Point(arguments.Get("loc"), "loc", ClickTool.LocationShape) : null;

        using (await services.InputLock.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            if (label is null && loc is null)
            {
                // The wheel goes to the window under the cursor: it gets the same integrity check as a point (Decisions 8).
                var cursor = services.Input.Cursor;
                services.Targets.Point(cursor);
                return ToolResults.Text(await services.Input.ScrollAsync(null, type, direction, wheelTimes, cancellationToken).ConfigureAwait(false)
                    ?? ClickTool.Text($"Scrolled {type} {direction} by {wheelTimes} wheel times at current cursor position ({cursor.X},{cursor.Y})."));
            }

            var point = await services.Targets.ResolveAsync(label, loc, cancellationToken).ConfigureAwait(false);
            return ToolResults.Text(await services.Input.ScrollAsync(point, type, direction, wheelTimes, cancellationToken).ConfigureAwait(false)
                ?? ClickTool.Text($"Scrolled {type} {direction} by {wheelTimes} wheel times at ({point.X},{point.Y})."));
        }
    }
}
