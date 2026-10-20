using DesktopMcp.Core.Input;
using DesktopMcp.Core.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/input.py:354-404 (move and drag). Both ends of a drag get the point checks (Decisions 15); the start
/// is the cursor when from_loc is absent. The targets are resolved inside the control gate's lock (preflight F12).
/// </summary>
public sealed class MoveTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var label = arguments.OptionalInteger("label");
        // upstream's order (tools/input.py:372-379): drag, then either-loc-or-label, then the shapes.
        var drag = arguments.StrictBool("drag", false);
        if (arguments.Get("loc") is null && label is null) throw new ArgumentException(PointerTools.EitherLocOrLabel);
        var loc = label is null ? ActArguments.Point(arguments.Get("loc"), "loc", "loc must be a list of exactly 2 integers [x, y]") : null;
        var from = ActArguments.Point(arguments.Get("from_loc"), "from_loc", "from_loc must be a list of exactly 2 integers [x, y]");
        if ((from is not null || arguments.Get("duration") is not null) && !drag)
            throw new ArgumentException("from_loc and duration require drag=True");
        var duration = drag ? ActArguments.Duration(arguments.Get("duration")) : null;

        // The desktop-session check (P2 Decisions 18) before any target is resolved.
        services.Targets.CheckSession();
        var to = await services.Targets.ResolveAsync(label, loc, cancellationToken).ConfigureAwait(false);
        if (!drag)
        {
            await services.Input.MoveAsync(to, cancellationToken).ConfigureAwait(false);
            return ToolResults.Text(PointerTools.Text($"Moved the mouse pointer to ({to.X},{to.Y})."));
        }

        // The start that was checked is the start pressed, even if the cursor moves during the drag's settle.
        var start = services.Targets.Point(from ?? services.Input.Cursor).Point;
        var result = await services.Input.DragAsync(start, to, duration, cancellationToken).ConfigureAwait(false);
        var text = PointerTools.Text($"Dragged from ({result.Start.X},{result.Start.Y}) to ({to.X},{to.Y})");
        return ToolResults.Text(result.Duration is { } seconds ? $"{text} over {PyText.Fixed(seconds, 3)} seconds." : $"{text}.");
    }
}
