using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tests.Tree;

/// <summary>
/// <see cref="UiaTreeSource.ReadSiblings{T}"/> over fake elements: children come one sibling step at a time and stop at
/// the per-parent cap (Decisions 37).
/// </summary>
public class SiblingReadTests
{
    private static readonly COMException DeadElement = new("gone", unchecked((int)0x80040201)); // UIA_E_ELEMENTNOTAVAILABLE
    private static readonly COMException UiaTimeout = new("slow", unchecked((int)0x80131505)); // UIA_E_TIMEOUT

    /// <summary>A parent (index -1) with <paramref name="count"/> children 0..count-1; counts every navigation.</summary>
    private sealed class FakeSiblings(int count)
    {
        public int Navigations { get; private set; }
        public Func<int, Exception?> FailAt { get; set; } = _ => null;

        public Node? First(Node parent) => Step(0);
        public Node? Next(Node child) => Step(child.Index + 1);

        private Node? Step(int index)
        {
            Navigations++;
            if (FailAt(index) is { } error) throw error;
            return index < count ? new Node(index) : null;
        }
    }

    private sealed record Node(int Index);

    private static UiaElementData Read(Node node) => new() { Name = $"c{node.Index}" };

    private static List<UiaElementData> ReadAll(FakeSiblings siblings) => ReadAll(siblings, out _);

    private static List<UiaElementData> ReadAll(FakeSiblings siblings, out bool capped) =>
        UiaTreeSource.ReadSiblings(new Node(-1), siblings.First, siblings.Next, Read, UiaTreeSource.MaxChildren, out capped);

    [Fact]
    public void AThousandChildrenGiveTheFirst300InDocumentOrder()
    {
        var siblings = new FakeSiblings(1_000);
        var children = ReadAll(siblings, out var capped);
        Assert.Equal(300, UiaTreeSource.MaxChildren);
        Assert.Equal(Enumerable.Range(0, 300).Select(i => $"c{i}"), children.Select(c => c.Name));
        Assert.True(capped);
        Assert.Equal(301, siblings.Navigations); // one step past the cap confirms a 301st child, which is not read
    }

    [Fact]
    public void FewerChildrenThanTheCapAreAllRead()
    {
        var siblings = new FakeSiblings(3);
        Assert.Equal(["c0", "c1", "c2"], ReadAll(siblings, out var capped).Select(c => c.Name));
        Assert.False(capped);
        Assert.Equal(4, siblings.Navigations);
    }

    [Fact]
    public void ExactlyThreeHundredChildrenAreNotCut()
    {
        var siblings = new FakeSiblings(300);
        Assert.Equal(300, ReadAll(siblings, out var capped).Count);
        Assert.False(capped);
        Assert.Equal(301, siblings.Navigations);
    }

    [Fact]
    public void AThreeHundredAndFirstChildThatDiesDoesNotCountAsCut()
    {
        var siblings = new FakeSiblings(1_000) { FailAt = i => i == 300 ? DeadElement : null };
        Assert.Equal(300, ReadAll(siblings, out var capped).Count);
        Assert.False(capped);
    }

    [Fact]
    public void ADeadParentHasNoChildren()
    {
        var siblings = new FakeSiblings(5) { FailAt = i => i == 0 ? DeadElement : null };
        Assert.Empty(ReadAll(siblings));
    }

    [Fact]
    public void ADeadSiblingEndsTheListAndKeepsWhatWasRead()
    {
        var siblings = new FakeSiblings(5) { FailAt = i => i == 2 ? DeadElement : null };
        Assert.Equal(["c0", "c1"], ReadAll(siblings).Select(c => c.Name));
    }

    [Fact]
    public void TimeoutsAndOtherFailuresPropagate()
    {
        Assert.Same(UiaTimeout, Assert.Throws<COMException>(() => ReadAll(new FakeSiblings(5) { FailAt = i => i == 1 ? UiaTimeout : null })));
        var other = new COMException("broken", unchecked((int)0x80004005));
        Assert.Same(other, Assert.Throws<COMException>(() => ReadAll(new FakeSiblings(5) { FailAt = i => i == 0 ? other : null })));
    }
}
