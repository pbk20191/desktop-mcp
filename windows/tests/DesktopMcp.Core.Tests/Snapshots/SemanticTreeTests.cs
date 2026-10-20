using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class SemanticTreeTests
{
    [Fact]
    public void ReverseChildrenRestoresDocumentOrderAtEveryLevel()
    {
        var root = Node(SemanticKind.Desktop, "desktop", Node(SemanticKind.Window, "w", Leaf("b"), Leaf("a")));
        SemanticTree.ReverseChildren(root);
        Assert.Equal(["a", "b"], root.Children[0].Children.Select(c => c.Name));
    }

    [Fact]
    public void PruneStructuralRemovesEmptyContainersBottomUp()
    {
        var root = Node(SemanticKind.Desktop, "desktop",
            Node(SemanticKind.Window, "w",
                Node(SemanticKind.Structural, "outer", Node(SemanticKind.Structural, "inner")),
                Node(SemanticKind.Structural, "kept", Leaf("x"))));
        SemanticTree.PruneStructural(root);
        var window = Assert.Single(root.Children);
        Assert.Equal(["kept"], window.Children.Select(c => c.Name));
    }

    [Fact]
    public void DropElementsRemovesNodesWhoseElementIsGone()
    {
        var root = Node(SemanticKind.Desktop, "desktop", Node(SemanticKind.Window, "w", Leaf("a", 1), Leaf("b", 2)));
        SemanticTree.DropElements(root, new HashSet<int> { 2 });
        Assert.Equal(["b"], root.Children[0].Children.Select(c => c.Name));
    }

    [Fact]
    public void ADeepChainDoesNotOverflowTheStack()
    {
        var root = Node(SemanticKind.Desktop, "desktop");
        var current = root;
        for (var i = 0; i < 200_000; i++)
        {
            var next = Node(SemanticKind.Structural, "s");
            current.Children.Add(next);
            current = next;
        }
        SemanticTree.PruneStructural(root);
        Assert.Empty(root.Children);
    }

    private static SemanticNode Leaf(string name, int id = 0) => new(SemanticKind.Interactive, "button", name) { ElementId = id };

    private static SemanticNode Node(SemanticKind kind, string name, params SemanticNode[] children)
    {
        var node = new SemanticNode(kind, kind.ToString(), name);
        node.Children.AddRange(children);
        return node;
    }
}
