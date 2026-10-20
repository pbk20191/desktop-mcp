using DesktopMcp.Core.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/input.py:238-256 with Decisions 17 (click counts) and 22 (a label reports the point used). Arguments
/// are checked first; the input lock is taken next and the target resolved inside it (preflight F12).
/// </summary>
public sealed class ClickTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var label = arguments.OptionalInteger("label");
        var button = ActArguments.Choice(arguments.Get("button"), "button", "left", "left", "right", "middle");
        var clicks = arguments.Integer("clicks", 1);
        // A label replaces loc (upstream resolves it before the shape check), so loc is only read without one.
        var loc = label is null ? ActArguments.Point(arguments.Get("loc"), "loc", PointerTools.LocationShape) : null;
        if (loc is null && label is null) throw new ArgumentException(PointerTools.EitherLocOrLabel);
        if (clicks < 0) throw new ArgumentException("clicks must be 0 or more");

        using (await services.InputLock.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            // The desktop-session check (P2 Decisions 18) before any target is resolved.
            services.Targets.CheckSession();
            var point = await services.Targets.ResolveAsync(label, loc, cancellationToken).ConfigureAwait(false);
            await services.Input.ClickAsync(point, ToButton(button), clicks, cancellationToken).ConfigureAwait(false);
            return ToolResults.Text(clicks switch
            {
                0 => PointerTools.Text($"Hover {button} clicked at ({point.X},{point.Y})."),
                1 => PointerTools.Text($"Single {button} clicked at ({point.X},{point.Y})."),
                2 => PointerTools.Text($"Double {button} clicked at ({point.X},{point.Y})."),
                _ => PointerTools.Text($"Clicked {button} {clicks} times at ({point.X},{point.Y})."),
            });
        }
    }

    private static MouseButton ToButton(string button) => button switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => MouseButton.Left,
    };
}
