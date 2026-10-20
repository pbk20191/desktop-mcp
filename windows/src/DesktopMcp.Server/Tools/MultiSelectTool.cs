using System.Globalization;
using System.Text;
using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Control;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/multi.py:31-55 with Decisions 23: every label is range-checked first (upstream's wrapper text), then
/// each target is resolved just before its own click, locs first, then labels. Arguments and the range check come
/// first; the targets are resolved inside the control gate's lock (preflight F12). A control interruption is not wrapped
/// in the progress text: it reaches the gate as is.
/// </summary>
public sealed class MultiSelectTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var locs = arguments.Get("locs") is { } locsValue ? ActArguments.PointList(locsValue, "locs") : null;
        var labels = arguments.Get("labels") is { } labelsValue ? ActArguments.LabelList(labelsValue, "labels") : null;
        if (locs is null && labels is null) throw new ArgumentException(PointerTools.EitherLocsOrLabels);
        locs ??= [];
        // Upstream resolves the labels before it reads press_ctrl.
        if (labels is not null) services.Targets.CheckLabels(labels, $"Failed to resolve labels {PythonList(labels)}");
        labels ??= [];
        var pressCtrl = arguments.StrictBool("press_ctrl", true);

        // The desktop-session check (P2 Decisions 18) before any target is resolved.
        services.Targets.CheckSession();
        var count = locs.Count + labels.Count;
        var points = await services.Input.MultiSelectAsync(pressCtrl, count, async (i, ct) =>
        {
            try
            {
                return i < locs.Count
                    ? services.Targets.Point(locs[i]).Point
                    : (await services.Targets.LabelAsync(labels[i - locs.Count], ct).ConfigureAwait(false)).Point;
            }
            catch (Exception e) when (e is not OperationCanceledException && !ControlErrors.IsInterruption(e) && i > 0)
            {
                throw PointerTools.Stopped(e, i, count);
            }
        }, cancellationToken).ConfigureAwait(false);

        var text = new StringBuilder("Multi-selected elements at:\n");
        text.AppendJoin('\n', points.Select(point => PointerTools.Text($"({point.X},{point.Y})")));
        return ToolResults.Text(text.ToString());
    }

    /// <summary>The labels as Python prints a list of ints: <c>[0, 9]</c>.</summary>
    private static string PythonList(IReadOnlyList<long> labels) =>
        "[" + string.Join(", ", labels.Select(label => label.ToString(CultureInfo.InvariantCulture))) + "]";
}
