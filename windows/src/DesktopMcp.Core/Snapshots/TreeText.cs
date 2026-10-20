using System.Text;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Snapshots;

/// <summary>
/// The "UI Tree:" text (upstream tree/views.py:7-209, semantic_tree_to_string). Interactive and scrollable lines start
/// with their label (spec §6): the index in the interactive list, then the scrollable list continuing after it (upstream
/// desktop/service.py:663-675 resolves labels that way).
/// </summary>
public static class TreeText
{
    // upstream tree/views.py:7-16.
    private static readonly Dictionary<string, string> Actions = new(StringComparer.Ordinal)
    {
        ["edit"] = "fill",
        ["check box"] = "toggle",
        ["checkbox"] = "toggle",
        ["combo box"] = "select",
        ["combobox"] = "select",
        ["slider"] = "slide",
        ["radio button"] = "select",
        ["document"] = "scroll",
    };

    /// <summary>upstream tree/views.py:19-20; the key is the lower-cased control type.</summary>
    public static string ActionFor(string controlType) => Actions.GetValueOrDefault(controlType, "click");

    /// <summary>upstream tree/views.py:23-51.</summary>
    public static string InteractiveMetadata(UiMetadata metadata)
    {
        var parts = new List<string>();
        if (metadata.HasFocus) parts.Add("focused");
        if (metadata.IsPassword) parts.Add("password");
        if (metadata.Value is { } value && value is not ("" or "(empty)")) parts.Add($"value:\"{UnicodeCleanup.EscapeControl(value)}\"");
        if (metadata is { Minimum: { } minimum, Maximum: { } maximum }) parts.Add($"range:{minimum}-{maximum}");
        if (!string.IsNullOrEmpty(metadata.ToggleState)) parts.Add($"toggle:{metadata.ToggleState}");
        if (!string.IsNullOrEmpty(metadata.ExpandCollapseState)) parts.Add($"state:{metadata.ExpandCollapseState}");
        if (!string.IsNullOrEmpty(metadata.Shortcut)) parts.Add($"shortcut:{UnicodeCleanup.EscapeControl(metadata.Shortcut)}");
        return Join(parts);
    }

    /// <summary>
    /// upstream tree/views.py:54-66. Upstream stores the int 0 as the percent of a non-scrollable axis
    /// (tree/service.py:146-148 and 498-500) but prints an axis only when it is scrollable, so that 0 is never printed;
    /// a scrollable axis holds round(percent, 2), a Python float, printed as its repr.
    /// </summary>
    public static string ScrollMetadata(UiMetadata metadata)
    {
        var parts = new List<string>();
        if (metadata.HasFocus) parts.Add("focused");
        if (metadata.VerticallyScrollable) parts.Add($"v:{PyText.FloatRepr(metadata.VerticalScrollPercent)}%");
        if (metadata.HorizontallyScrollable) parts.Add($"h:{PyText.FloatRepr(metadata.HorizontalScrollPercent)}%");
        return Join(parts);
    }

    /// <summary>upstream tree/views.py:89-95, with this port's variable name.</summary>
    public static string TruncationNote(int elementLimit) =>
        $"... [truncated: reached the {elementLimit}-element capture limit — some elements were not visited. Narrow the view " +
        "(filter by window, scroll to the area of interest, use_dom=True for a browser page) or raise " +
        $"{SnapshotSettings.MaxTreeElementsVariable} for a complete tree.]";

    /// <summary>Port-only (Decisions 37): some child lists were cut at the per-parent cap.</summary>
    public static string ChildLimitNote(int childLimit) =>
        $"... [truncated: some elements have more than {childLimit} children; only the first {childLimit} of each were read.]";

    /// <summary>upstream tree/views.py:185-193 semantic_tree_to_string, iterative, with labels.</summary>
    public static string Render(TreeSnapshot tree)
    {
        if (tree.Root is null) return "No elements";
        var labels = new Dictionary<int, int>();
        for (var i = 0; i < tree.Interactive.Count; i++) labels[tree.Interactive[i].Id] = i;
        for (var i = 0; i < tree.Scrollable.Count; i++) labels[tree.Scrollable[i].Id] = tree.Interactive.Count + i;

        // upstream tree/views.py:141-154 _render_semantic_node, with an explicit stack.
        var lines = new List<string>();
        var stack = new Stack<(SemanticNode Node, string Prefix, bool IsLast)>();
        stack.Push((tree.Root, "", true));
        while (stack.Count > 0)
        {
            var (node, prefix, isLast) = stack.Pop();
            lines.Add(node.Kind == SemanticKind.Desktop ? "desktop" : $"{prefix}{(isLast ? "└── " : "├── ")}{Format(node, labels)}");
            var childPrefix = prefix + (isLast ? "    " : "│   ");
            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push((node.Children[i], childPrefix, i == node.Children.Count - 1));
        }
        var text = string.Join("\n", lines);
        var notes = new List<string>();
        if (tree.Truncated) notes.Add(TruncationNote(tree.ElementLimit));
        if (tree.ChildLimit is { } childLimit) notes.Add(ChildLimitNote(childLimit));
        return notes.Count == 0 ? text : text + "\n\n" + string.Join("\n", notes);
    }

    // upstream tree/views.py:126-138, plus the label.
    private static string Format(SemanticNode node, IReadOnlyDictionary<int, int> labels)
    {
        var controlType = UnicodeCleanup.EscapeControl(node.ControlType.ToLowerInvariant());
        var name = UnicodeCleanup.EscapeControl(node.Name);
        switch (node.Kind)
        {
            case SemanticKind.Window:
                return $"window \"{name}\"";
            case SemanticKind.Interactive or SemanticKind.Scrollable:
                var builder = new StringBuilder();
                if (node.ElementId is { } id && labels.TryGetValue(id, out var label)) builder.Append('[').Append(label).Append("] ");
                builder.Append(node.Center?.ToText() ?? "(?)").Append(' ').Append(controlType)
                    .Append(" \"").Append(name).Append("\"  [action: ").Append(ActionFor(controlType)).Append(']')
                    .Append(node.Kind == SemanticKind.Interactive ? InteractiveMetadata(node.Metadata) : ScrollMetadata(node.Metadata));
                return builder.ToString();
            default:
                return $"{controlType} \"{name}\"";
        }
    }

    private static string Join(List<string> parts) =>
        parts.Count == 0 ? "" : "  " + string.Join("  ", parts.Select(part => $"[{part}]"));
}
