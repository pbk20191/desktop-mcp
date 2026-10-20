using System.Globalization;
using System.Text;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Snapshots;

/// <summary>
/// The Snapshot and Screenshot text (upstream tools/_snapshot_helpers.py:143-239 build_snapshot_response,
/// desktop/views.py:82-103) with spec §6's cleanups: no indentation from dedent, every section header at column 0, one
/// blank line between sections (Decisions 10).
/// </summary>
public static class SnapshotText
{
    /// <summary>upstream tools/snapshot.py:119, the note Screenshot adds.</summary>
    public const string ScreenshotNote =
        "UI Tree: Skipped for fast screenshot-only capture. Call Snapshot when you need interactive or scrollable elements.";

    private static readonly string[] WindowHeaders = ["Name", "Depth", "Status", "Width", "Height", "Handle"];

    public static string Render(DesktopSnapshot snapshot, bool includeUiDetails, string? uiDetailNote)
    {
        var text = new StringBuilder()
            .Append(Metadata(snapshot, uiDetailNote))
            .Append("\n\nActive Desktop:\n").Append(DesktopTable([snapshot.ActiveDesktop]))
            .Append("\n\nAll Desktops:\n").Append(DesktopTable(snapshot.AllDesktops))
            .Append("\n\nFocused Window:\n").Append(snapshot.ActiveWindow is { } active ? WindowTable([active]) : "No active window found")
            .Append("\n\nOpened Windows:\n").Append(snapshot.Windows.Count > 0 ? WindowTable(snapshot.Windows) : "No windows found");
        // upstream tools/_snapshot_helpers.py:162-164 strips private-use characters from the tree text only.
        if (includeUiDetails)
            text.Append("\n\nUI Tree:\n").Append(UnicodeCleanup.RemovePrivateUse(TreeText.Render(snapshot.Tree)));
        return UnicodeCleanup.RepairSurrogates(text.ToString());
    }

    // upstream tools/_snapshot_helpers.py:185-214, without the trailing newline.
    private static string Metadata(DesktopSnapshot snapshot, string? uiDetailNote)
    {
        var lines = new List<string>
        {
            "Cursor Position: " + (snapshot.Cursor is { } cursor ? $"({cursor.X}, {cursor.Y})" : "None"),
        };
        if (snapshot.OriginalSize is { } size)
        {
            var scale = snapshot.Scale ?? 1.0;
            if (scale < 1.0)
            {
                var coordinateScale = PyText.Round(1.0 / scale, 6);
                var factor = PyText.FloatRepr(coordinateScale);
                lines.Add($"Screenshot Original Size: {size.ToText()}");
                lines.Add($"Screenshot Coordinate Scale: {factor} — image pixels are downscaled; multiply every image pixel coordinate by " +
                    $"{factor} before passing to Click, Move, Scroll, or any loc= argument (e.g. image pixel (200, 150) → screen coordinate " +
                    $"({PyText.RoundToInteger(200 * coordinateScale)}, {PyText.RoundToInteger(150 * coordinateScale)}))");
            }
            else
            {
                lines.Add($"Screenshot Size: {size.ToText()}");
            }
        }
        if (snapshot.Displays.Count > 0)
            lines.Add("Visible Displays: " + string.Join("; ", snapshot.Displays.Select(d =>
                $"{d.Index}:{d.DeviceName} {d.Bounds.ToXyxy()}{(d.Primary ? " primary" : "")}")));
        if (snapshot.SelectedDisplays is { Count: > 0 } selected)
            lines.Add("Selected Displays: " + string.Join(",", selected.Select(i => i.ToString(CultureInfo.InvariantCulture))));
        if (snapshot.Region is { } region)
        {
            lines.Add($"Screenshot Region: {region.ToXyxy()}");
            lines.Add("Coordinate Space: Virtual desktop coordinates");
        }
        if (snapshot.Backend is { } backend) lines.Add($"Screenshot Backend: {backend}");
        if (uiDetailNote is not null) lines.Add(uiDetailNote);
        return string.Join("\n", lines);
    }

    // upstream desktop/views.py:82-90.
    private static string DesktopTable(IEnumerable<VirtualDesktopInfo> desktops) =>
        TableFormatter.Simple(["Name"], desktops.Select(d => (IReadOnlyList<string>)[UnicodeCleanup.EscapeControl(d.Name)]).ToArray());

    // upstream desktop/views.py:37-45 and 92-103.
    private static string WindowTable(IEnumerable<WindowInfo> windows) =>
        TableFormatter.Simple(WindowHeaders, windows.Select(w => (IReadOnlyList<string>)
        [
            UnicodeCleanup.EscapeControl(w.Name),
            w.Depth.ToString(CultureInfo.InvariantCulture),
            w.Status.ToText(),
            w.Bounds.Width.ToString(CultureInfo.InvariantCulture),
            w.Bounds.Height.ToString(CultureInfo.InvariantCulture),
            w.Handle.ToString(CultureInfo.InvariantCulture),
        ]).ToArray());
}
