using DesktopMcp.Core.Input;
using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/input.py:279-297 with the keyboard-target check after the click (P3 Decisions 7) and the read-back
/// (Decisions 4): a mismatch adds one warning line to upstream's text. Arguments first, then the input lock, then the
/// target inside it (preflight F12).
/// </summary>
public sealed class TypeTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        // The schema's checks first (text, caret_position), then upstream's order: clear, press_enter, either-or, the shape.
        var text = arguments.RequiredString("text");
        var caret = ActArguments.Choice(arguments.Get("caret_position"), "caret_position", "idle", "start", "idle", "end");
        var label = arguments.OptionalInteger("label");
        var clear = arguments.StrictBool("clear", false);
        var pressEnter = arguments.StrictBool("press_enter", false);
        // A label replaces loc (upstream resolves it before the shape check), so loc is only read without one.
        var loc = label is null ? ActArguments.Point(arguments.Get("loc"), "loc", PointerTools.LocationShape) : null;
        if (loc is null && label is null) throw new ArgumentException(PointerTools.EitherLocOrLabel);

        using (await services.InputLock.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            // The desktop-session check (P2 Decisions 18) before any target is resolved.
            services.Targets.CheckSession();
            var target = await services.Targets.ResolveTargetAsync(label, loc, cancellationToken).ConfigureAwait(false);
            var warning = await Sequence(services).RunAsync(target, text, ToCaret(caret), clear, pressEnter, null, cancellationToken)
                .ConfigureAwait(false);
            var typed = PointerTools.Text($"Typed {text} at ({target.Point.X},{target.Point.Y}).");
            return ToolResults.Text(warning is null ? typed : $"{typed}\n{warning}");
        }
    }

    /// <summary>The Type sequence over the services' input, foreground, read-back and clock (MultiEdit reuses it).</summary>
    internal static TypeSequence Sequence(ToolServices services) =>
        new(services.Input, services.Foreground, services.ReadBack, (delay, ct) => Task.Delay(delay, services.Time, ct));

    private static CaretPosition ToCaret(string caret) => caret switch
    {
        "start" => CaretPosition.Start,
        "end" => CaretPosition.End,
        _ => CaretPosition.Idle,
    };
}
