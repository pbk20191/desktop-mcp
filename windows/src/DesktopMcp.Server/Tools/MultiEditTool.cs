using System.Text;
using DesktopMcp.Core.Input;
using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/multi.py:68-104 with Decisions 23 (labels range-checked first, each located just before its item) and
/// Decisions 4 and 7 per item: each item is the shared Type sequence (preflight F14) with <c>clear</c>, the caret idle and
/// no Enter, so the keyboard-target check runs after each click and a read-back mismatch adds a warning line naming the
/// item's point. Arguments and the range check come first; the input lock is taken next and the targets resolved inside
/// it (preflight F12).
/// </summary>
public sealed class MultiEditTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var locs = arguments.Get("locs") is { } locsValue ? ActArguments.EditLocs(locsValue) : null;
        var labels = arguments.Get("labels") is { } labelsValue ? ActArguments.EditLabels(labelsValue) : null;
        if (locs is null && labels is null) throw new ArgumentException(PointerTools.EitherLocsOrLabels);
        locs ??= [];
        if (labels is not null) services.Targets.CheckLabels(labels.Select(item => item.Label).ToArray(), "Failed to process labels");
        labels ??= [];

        using (await services.InputLock.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            var sequence = TypeTool.Sequence(services);
            var count = locs.Count + labels.Count;
            var items = new List<string>(count);
            var warnings = new List<string>();
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ActionTarget target;
                string text;
                try
                {
                    if (i < locs.Count)
                    {
                        target = services.Targets.Point(locs[i].Point);
                        text = locs[i].Text;
                    }
                    else
                    {
                        var (label, labelText) = labels[i - locs.Count];
                        target = await services.Targets.LabelAsync(label, cancellationToken).ConfigureAwait(false);
                        text = labelText;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException && i > 0)
                {
                    throw PointerTools.Stopped(e, i, count);
                }

                string? warning;
                try
                {
                    warning = await sequence.RunAsync(target, text, CaretPosition.Idle, clear: true, pressEnter: false,
                        warningAt: target.Point, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException && i > 0)
                {
                    // Earlier items were typed, so the item's "nothing was typed" needs the progress (Decisions 23).
                    throw PointerTools.Stopped(e, i, count);
                }
                // Upstream's f-string puts the text raw inside the quotes.
                items.Add(PointerTools.Text($"({target.Point.X},{target.Point.Y}) with text '") + text + "'");
                if (warning is not null) warnings.Add(warning);
            }

            var result = new StringBuilder("Multi-edited elements at: ").AppendJoin(", ", items);
            foreach (var warning in warnings) result.Append('\n').Append(warning);
            return ToolResults.Text(result.ToString());
        }
    }
}
