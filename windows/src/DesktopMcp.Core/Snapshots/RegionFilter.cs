namespace DesktopMcp.Core.Snapshots;

/// <summary>
/// Cuts a snapshot down to a capture rectangle (upstream desktop/service.py:1527-1657). Elements and windows are clipped
/// to the rectangle and dropped when nothing is left; the semantic tree is filtered bottom up without recursion.
/// </summary>
public static class RegionFilter
{
    public static WindowInfo? Window(WindowInfo? window, ScreenRect region) =>
        window is not null && ScreenRect.Clip(window.Bounds, region) is { } clipped ? window with { Bounds = clipped } : null;

    public static IReadOnlyList<WindowInfo> Windows(IEnumerable<WindowInfo> windows, ScreenRect region) =>
        windows.Select(window => Window(window, region)).OfType<WindowInfo>().ToArray();

    /// <summary>upstream desktop/service.py:217-218: a cursor outside the region is not reported.</summary>
    public static ScreenPoint? Cursor(ScreenPoint? cursor, ScreenRect region) =>
        cursor is { } point && region.Contains(point) ? point : null;

    public static TreeSnapshot Tree(TreeSnapshot tree, ScreenRect region) => tree with
    {
        Interactive = Elements(tree.Interactive, region),
        Scrollable = Elements(tree.Scrollable, region),
        Root = tree.Root is null ? null : Semantic(tree.Root, region),
    };

    private static IReadOnlyList<UiElement> Elements(IReadOnlyList<UiElement> elements, ScreenRect region) =>
        elements
            .Select(element => ScreenRect.Clip(element.Box, region) is { } clipped ? element with { Box = clipped, Center = clipped.Center } : null)
            .OfType<UiElement>()
            .ToArray();

    // upstream desktop/service.py:1579-1612 and 1631-1637.
    private static SemanticNode? Semantic(SemanticNode root, ScreenRect region)
    {
        var kept = new Dictionary<SemanticNode, SemanticNode?>(ReferenceEqualityComparer.Instance);
        foreach (var node in SemanticTree.PostOrder(root))
        {
            ScreenRect? clipped = null;
            if (node.Box is { } box)
            {
                clipped = ScreenRect.Clip(box, region);
                if (clipped is null)
                {
                    kept[node] = null;
                    continue;
                }
            }
            var children = node.Children.Select(child => kept[child]).OfType<SemanticNode>().ToList();
            if (node.Kind != SemanticKind.Desktop && clipped is null && children.Count == 0)
            {
                kept[node] = null;
                continue;
            }
            // The root takes the region itself as its box (upstream desktop/service.py:1631-1637).
            var isRoot = ReferenceEquals(node, root);
            var copy = new SemanticNode(node.Kind, node.ControlType, node.Name)
            {
                Box = isRoot ? region : clipped,
                Center = isRoot ? region.Center : clipped?.Center ?? node.Center,
                ElementId = node.ElementId,
                Metadata = node.Metadata,
            };
            copy.Children.AddRange(children);
            kept[node] = copy;
        }
        return kept[root];
    }
}
