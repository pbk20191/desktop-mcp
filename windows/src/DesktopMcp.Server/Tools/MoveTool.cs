using DesktopMcp.Core.Input;
using DesktopMcp.Core.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/input.py:354-404 (move and drag). Both ends of a drag get the point checks (Decisions 15); the start
/// is the cursor when from_loc is absent. Arguments first, then the input lock, then the targets (preflight F12).
/// </summary>
public sealed class MoveTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var label = arguments.OptionalInteger("label");
        var loc = label is null ? ActArguments.Point(arguments.Get("loc"), "loc", "loc must be a list of exactly 2 integers [x, y]") : null;
        var from = ActArguments.Point(arguments.Get("from_loc"), "from_loc", "from_loc must be a list of exactly 2 integers [x, y]");
        var drag = arguments.StrictBool("drag", false);
        if (loc is null && label is null) throw new ArgumentException(ClickTool.EitherLocOrLabel);
        if ((from is not null || arguments.Get("duration") is not null) && !drag)
            throw new ArgumentException("from_loc and duration require drag=True");
        var duration = drag ? ActArguments.Duration(arguments.Get("duration")) : null;

        using (await services.InputLock.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            var to = await services.Targets.ResolveAsync(label, loc, cancellationToken).ConfigureAwait(false);
            if (!drag)
            {
                await services.Input.MoveAsync(to, cancellationToken).ConfigureAwait(false);
                return ToolResults.Text(ClickTool.Text($"Moved the mouse pointer to ({to.X},{to.Y})."));
            }

            services.Targets.Point(from ?? services.Input.Cursor);
            var result = await services.Input.DragAsync(from, to, duration, cancellationToken).ConfigureAwait(false);
            var text = ClickTool.Text($"Dragged from ({result.Start.X},{result.Start.Y}) to ({to.X},{to.Y})");
            return ToolResults.Text(result.Duration is { } seconds ? $"{text} over {PyText.Fixed(seconds, 3)} seconds." : $"{text}.");
        }
    }
}
