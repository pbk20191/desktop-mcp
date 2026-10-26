using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Snapshots;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The shared part of Snapshot and Screenshot (upstream tools/snapshot.py): argument type errors are checked first (an
/// isError result); everything after that, including a missing Windows App SDK runtime, becomes upstream's
/// "Error capturing ..." text as a normal result (Decisions 17).
/// </summary>
public abstract class SnapshotToolBase(Tool protocolTool, IDesktopSnapshots snapshots) : SpecTool(protocolTool)
{
    /// <summary>The prefix of the error text: "desktop state" or "screenshot".</summary>
    private protected abstract string Failure { get; }

    private protected abstract SnapshotRequest ReadRequest(ToolArguments arguments);

    private protected abstract string RenderText(DesktopSnapshot snapshot);

    protected sealed override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var snapshotRequest = ReadRequest(ToolArguments.From(request));
        try
        {
            // The scan and the tree walk run synchronously until their first await: keep them off the caller's thread.
            var snapshot = await Task.Run(() => snapshots.CaptureAsync(snapshotRequest, cancellationToken), CancellationToken.None)
                .ConfigureAwait(false);
            return ToolResults.Content(RenderText(snapshot), snapshot.Png);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Messages that already end with a period (the missing-runtime hint, deadline errors) must not end with "..".
            return ToolResults.Content($"Error capturing {Failure}: {e.Message.TrimEnd('.')}. Please try again.", null);
        }
    }

    /// <summary>The arguments both tools share (display, region, reference lines), with the type checks.</summary>
    private protected static SnapshotRequest WithSelection(SnapshotRequest request, ToolArguments arguments) => request with
    {
        WidthReferenceLine = arguments.OptionalInteger("width_reference_line"),
        HeightReferenceLine = arguments.OptionalInteger("height_reference_line"),
        Displays = SnapshotSelection.ReadDisplayArgument(arguments.Get("display")),
        Region = SnapshotSelection.ReadRegionArgument(arguments.Get("region")),
    };
}

/// <summary>upstream tools/snapshot.py:37-72.</summary>
public sealed class SnapshotTool(Tool protocolTool, IDesktopSnapshots snapshots) : SnapshotToolBase(protocolTool, snapshots)
{
    private protected override string Failure => "desktop state";

    private protected override SnapshotRequest ReadRequest(ToolArguments arguments) => WithSelection(new SnapshotRequest
    {
        UseVision = arguments.LooseBool("use_vision", false),
        UseDom = arguments.LooseBool("use_dom", false),
        UseAnnotation = arguments.LooseBool("use_annotation", true),
        UseUiTree = arguments.LooseBool("use_ui_tree", true),
    }, arguments);

    private protected override string RenderText(DesktopSnapshot snapshot) =>
        SnapshotText.Render(snapshot, includeUiDetails: true, uiDetailNote: null);
}

/// <summary>upstream tools/snapshot.py:86-120: vision always on, no UI tree, no DOM.</summary>
public sealed class ScreenshotTool(Tool protocolTool, IDesktopSnapshots snapshots) : SnapshotToolBase(protocolTool, snapshots)
{
    private protected override string Failure => "screenshot";

    private protected override SnapshotRequest ReadRequest(ToolArguments arguments) => WithSelection(new SnapshotRequest
    {
        UseVision = true,
        UseDom = false,
        UseAnnotation = arguments.LooseBool("use_annotation", false),
        UseUiTree = false,
    }, arguments);

    private protected override string RenderText(DesktopSnapshot snapshot) =>
        SnapshotText.Render(snapshot, includeUiDetails: false, SnapshotText.ScreenshotNote);
}
