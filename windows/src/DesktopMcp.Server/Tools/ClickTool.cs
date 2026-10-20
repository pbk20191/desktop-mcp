using System.Globalization;
using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Actions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/input.py:238-256 with Decisions 17 (click counts) and 22 (a label reports the point used). Arguments
/// are checked first; the input lock is taken next and the target resolved inside it (preflight F12).
/// </summary>
public sealed class ClickTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    internal const string LocationShape = "Location must be a list of exactly 2 integers [x, y]";
    internal const string EitherLocOrLabel = "Either loc or label must be provided.";

    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var label = arguments.OptionalInteger("label");
        var button = ActArguments.Choice(arguments.Get("button"), "button", "left", "left", "right", "middle");
        var clicks = arguments.Integer("clicks", 1);
        // A label replaces loc (upstream resolves it before the shape check), so loc is only read without one.
        var loc = label is null ? ActArguments.Point(arguments.Get("loc"), "loc", LocationShape) : null;
        if (loc is null && label is null) throw new ArgumentException(EitherLocOrLabel);
        if (clicks < 0) throw new ArgumentException("clicks must be 0 or more");

        using (await services.InputLock.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            var point = await services.Targets.ResolveAsync(label, loc, cancellationToken).ConfigureAwait(false);
            await services.Input.ClickAsync(point, ToButton(button), clicks, cancellationToken).ConfigureAwait(false);
            return ToolResults.Text(clicks switch
            {
                0 => Text($"Hover {button} clicked at ({point.X},{point.Y})."),
                1 => Text($"Single {button} clicked at ({point.X},{point.Y})."),
                2 => Text($"Double {button} clicked at ({point.X},{point.Y})."),
                _ => Text($"Clicked {button} {clicks} times at ({point.X},{point.Y})."),
            });
        }
    }

    private static MouseButton ToButton(string button) => button switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => MouseButton.Left,
    };

    internal static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The target resolution the pointer tools share.</summary>
internal static class ActionTargetsExtensions
{
    /// <summary>The label's checked point (Decisions 1, 22), else the checked <paramref name="loc"/> (Decisions 8, 15).</summary>
    public static async Task<ScreenPoint> ResolveAsync(this IActionTargets targets, long? label, ScreenPoint? loc, CancellationToken ct)
    {
        if (label is { } value) return (await targets.LabelAsync(value, ct).ConfigureAwait(false)).Point;
        return targets.Point(loc ?? throw new ArgumentException(ClickTool.EitherLocOrLabel)).Point;
    }
}
