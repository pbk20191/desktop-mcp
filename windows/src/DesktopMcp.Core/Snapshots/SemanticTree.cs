namespace DesktopMcp.Core.Snapshots;

/// <summary>
/// Whole-tree operations on <see cref="SemanticNode"/>s. Upstream recurses (tree/views.py:157-169); a UIA tree can be
/// deep, so these use explicit stacks and never recurse.
/// </summary>
public static class SemanticTree
{
    /// <summary>upstream tree/views.py:165-169: the walk collects children right to left; restore document order.</summary>
    public static void ReverseChildren(SemanticNode root)
    {
        var stack = new Stack<SemanticNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            node.Children.Reverse();
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    /// <summary>upstream tree/views.py:157-162: removes structural nodes left without children, bottom up.</summary>
    public static void PruneStructural(SemanticNode root)
    {
        foreach (var node in PostOrder(root))
            node.Children.RemoveAll(child => child.Kind == SemanticKind.Structural && child.Children.Count == 0);
    }

    /// <summary>Removes interactive and scrollable nodes whose element is not in <paramref name="liveIds"/> (Decisions 7).</summary>
    public static void DropElements(SemanticNode root, IReadOnlySet<int> liveIds)
    {
        foreach (var node in PostOrder(root))
            node.Children.RemoveAll(child => child.ElementId is { } id && !liveIds.Contains(id));
    }

    /// <summary>Every node with its children before it.</summary>
    public static List<SemanticNode> PostOrder(SemanticNode root)
    {
        var order = new List<SemanticNode>();
        var stack = new Stack<SemanticNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            foreach (var child in node.Children) stack.Push(child);
        }
        order.Reverse();
        return order;
    }
}
