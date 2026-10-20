using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Tests.Tree;

public class TreeWalkerTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);
    private static readonly WindowContext App = new("App", IsBrowser: false, new ScreenRect(0, 0, 800, 600), Screen);

    private static readonly COMException DeadElement = new("gone", unchecked((int)0x80040201)); // UIA_E_ELEMENTNOTAVAILABLE
    private static readonly COMException UiaTimeout = new("slow", unchecked((int)0x80131505)); // UIA_E_TIMEOUT

    /// <summary>An in-memory UIA tree; children and words are keyed by element instance.</summary>
    private sealed class FakeSource : ITreeSource
    {
        public Dictionary<UiaElementData, UiaElementData[]> Children { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<UiaElementData, WordBox[]> Words { get; } = new(ReferenceEqualityComparer.Instance);
        public int Released { get; private set; }
        public int Fetched { get; private set; }

        /// <summary>Elements whose children were read, in visit order.</summary>
        public List<UiaElementData> ChildrenRead { get; } = [];

        /// <summary>Runs before an element's children are read; it may throw to fail the read.</summary>
        public Action<UiaElementData>? BeforeChildren { get; set; }

        /// <summary>Runs before an element's words are read; it may throw to fail the read.</summary>
        public Action<UiaElementData>? BeforeWords { get; set; }

        public IReadOnlyList<UiaElementData> GetChildren(UiaElementData parent)
        {
            ChildrenRead.Add(parent);
            BeforeChildren?.Invoke(parent);
            var children = Children.GetValueOrDefault(parent, []);
            Fetched += children.Length;
            return children;
        }

        public IReadOnlyList<WordBox> GetWords(UiaElementData element, int maxWords)
        {
            BeforeWords?.Invoke(element);
            return Words.GetValueOrDefault(element, []).Take(maxWords).ToArray();
        }

        public void Release(UiaElementData element) => Released++;

        public UiaElementData Add(UiaElementData parent, params UiaElementData[] children)
        {
            Children[parent] = children;
            return parent;
        }
    }

    private static int _ids;

    private static UiaElementData Window(string name = "App") => new() { Name = name, ControlType = TreeConfig.Window, LocalizedControlType = "window", Bounds = new ScreenRect(0, 0, 800, 600), RuntimeId = $"w{++_ids}" };

    private static UiaElementData Button(string name, int x = 10) => new()
    {
        Name = name, ControlType = TreeConfig.Button, LocalizedControlType = "button", LegacyRole = 0x2B,
        Bounds = new ScreenRect(x, 10, x + 50, 30), RuntimeId = $"b{++_ids}",
    };

    private static UiaElementData Pane(string name, bool scrollable = false) => new()
    {
        Name = name, ControlType = TreeConfig.Pane, LocalizedControlType = "pane", Bounds = new ScreenRect(0, 0, 400, 300),
        VerticallyScrollable = scrollable, VerticalScrollPercent = 33.333, RuntimeId = $"p{++_ids}",
    };

    private static UiaElementData Text(string name) => new()
    {
        Name = name, ControlType = TreeConfig.Edit, LocalizedControlType = "edit", LegacyRole = 0x2A, Bounds = new ScreenRect(0, 0, 300, 40),
    };

    private static Func<int> Counter()
    {
        var next = -1;
        return () => Interlocked.Increment(ref next);
    }

    private static (WindowWalk Walk, ElementBudget Budget) Walk(FakeSource source, UiaElementData root, int limit = 500, WindowContext? context = null, int maxDepth = TreeWalker.MaxDepth)
    {
        var budget = new ElementBudget(limit);
        return (new TreeWalker(source, budget, Counter(), maxDepth).Walk(root, context ?? App), budget);
    }

    [Fact]
    public void ChildrenAreNumberedRightToLeftButTheTreeKeepsDocumentOrder()
    {
        var source = new FakeSource();
        var root = source.Add(Window(), Button("A", 10), Button("B", 100));
        var (walk, _) = Walk(source, root);
        Assert.Equal(["B", "A"], walk.Interactive.Select(e => e.Name));
        Assert.Equal([0, 1], walk.Interactive.Select(e => e.Id));
        Assert.Equal(["A", "B"], walk.Window.Children.Select(c => c.Name));
        Assert.Equal(new ScreenPoint(35, 20), walk.Interactive[1].Center);
        Assert.Equal(3, source.Released);
    }

    [Fact]
    public void ElementsThatAreHiddenDisabledEmptyOrNotInteractiveAreLeftOut()
    {
        var source = new FakeSource();
        var root = source.Add(Window(),
            Button("Offscreen") with { IsOffscreen = true },
            Button("Disabled") with { IsEnabled = false },
            Button("Flat") with { Bounds = new ScreenRect(10, 10, 60, 10) },
            Button("No role") with { LegacyRole = 0x0A },
            Button("   "),
            Button("Kept"));
        Assert.Equal(["Kept"], Walk(source, root).Walk.Interactive.Select(e => e.Name));
    }

    [Fact]
    public void AnOffscreenEditIsStillListedWithItsValue()
    {
        var source = new FakeSource();
        var edit = new UiaElementData
        {
            Name = "Search", ControlType = TreeConfig.Edit, LocalizedControlType = "edit", IsOffscreen = true, LegacyRole = 0x2A,
            LegacyValue = "  query ", Bounds = new ScreenRect(0, 0, 100, 20), HasKeyboardFocus = true,
        };
        var element = Assert.Single(Walk(source, source.Add(Window(), edit)).Walk.Interactive);
        Assert.Equal(new UiMetadata { HasFocus = true, Value = "query" }, element.Metadata);
    }

    [Fact]
    public void ScrollablePanesAreListedAtTheirCentreAndDoNotBecomeStructuralNodes()
    {
        var source = new FakeSource();
        var pane = source.Add(Pane("", scrollable: true) with { AutomationId = "", LocalizedControlType = "PANE" }, Button("Inside"));
        var (walk, _) = Walk(source, source.Add(Window(), pane));
        var scrollable = Assert.Single(walk.Scrollable);
        Assert.Equal(("Pane", new ScreenPoint(200, 150)), (scrollable.Name, scrollable.Center));
        Assert.Equal(new UiMetadata { VerticallyScrollable = true, VerticalScrollPercent = 33.33 }, scrollable.Metadata);
        // The pane's children hang off the window, not the pane, and the window's children come back in reverse visit order.
        Assert.Equal([SemanticKind.Interactive, SemanticKind.Scrollable], walk.Window.Children.Select(c => c.Kind));
    }

    [Fact]
    public void AnAxisThatCannotScrollReportsZeroPercentNotNoScroll()
    {
        // UIA reports UIA_ScrollPatternNoScroll (-1) for an axis that cannot scroll; upstream (tree/service.py:498-500) uses 0.
        var source = new FakeSource();
        var pane = Pane("List", scrollable: true) with { HorizontallyScrollable = false, HorizontalScrollPercent = -1 };
        var scrollable = Assert.Single(Walk(source, source.Add(Window(), pane)).Walk.Scrollable);
        Assert.Equal(new UiMetadata { VerticallyScrollable = true, VerticalScrollPercent = 33.33 }, scrollable.Metadata);

        var both = Pane("Grid", scrollable: true) with { HorizontallyScrollable = true, HorizontalScrollPercent = 12.345 };
        var element = Assert.Single(Walk(source, source.Add(Window(), both)).Walk.Scrollable);
        Assert.Equal((true, 12.35), (element.Metadata.HorizontallyScrollable, element.Metadata.HorizontalScrollPercent));
    }

    [Fact]
    public void NamedContainersGroupTheirChildren()
    {
        var source = new FakeSource();
        var toolbar = source.Add(new UiaElementData { Name = "Tools", ControlType = 50021, LocalizedControlType = "tool bar", Bounds = new ScreenRect(0, 0, 10, 10) }, Button("Save"));
        var unnamed = source.Add(Pane(""), Button("Open"));
        var (walk, _) = Walk(source, source.Add(Window(), toolbar, unnamed));
        Assert.Equal(["Tools", "Open"], walk.Window.Children.Select(c => c.Name));
        Assert.Equal("Save", Assert.Single(walk.Window.Children[0].Children).Name);
    }

    [Fact]
    public void AModalDialogDiscardsTheElementsFoundBeforeIt()
    {
        var source = new FakeSource();
        var dialog = source.Add(Window("Confirm") with { WindowIsModal = true }, Button("Yes"));
        // Visited right to left: "Behind" first, then the dialog.
        var (walk, _) = Walk(source, source.Add(Window(), dialog, Button("Behind")));
        Assert.Equal(["Yes"], walk.Interactive.Select(e => e.Name));
    }

    [Fact]
    public void ABranchRepeatingAnAncestorsRuntimeIdIsCut()
    {
        var source = new FakeSource();
        var root = Window();
        var pane = Pane("Loop");
        source.Add(root, pane);
        source.Add(pane, Button("OK"), root with { Name = "Again" });
        var (walk, _) = Walk(source, root);
        Assert.Equal(["OK"], walk.Interactive.Select(e => e.Name));
    }

    [Fact]
    public void ElementsBelowTheDepthCapAreNotVisited()
    {
        var source = new FakeSource();
        var root = Window();
        var current = root;
        for (var depth = 1; depth <= 10; depth++)
        {
            var next = depth == 10 ? Button("Deep") : Pane("");
            source.Add(current, next);
            current = next;
        }
        Assert.Empty(Walk(source, root, maxDepth: 5).Walk.Interactive);
        // Elements at the cap are visited but their children are not read: depths 0-4 only.
        Assert.Equal(5, source.ChildrenRead.Count);
        source.ChildrenRead.Clear();
        Assert.Single(Walk(source, root, maxDepth: 10).Walk.Interactive);
        Assert.Equal(10, source.ChildrenRead.Count);
    }

    [Fact]
    public void AVeryDeepTreeIsWalkedToTheBottomWithoutRecursion()
    {
        const int Depth = 100_000;
        var source = new FakeSource();
        var root = Window();
        var current = root;
        for (var depth = 1; depth <= Depth; depth++)
        {
            var next = depth == Depth ? Button("Bottom") : Pane("");
            source.Add(current, next);
            current = next;
        }
        // No visit cap and no depth cap, so every level is really walked. A 256 KB stack holds a few thousand frames at
        // most: a recursive walk would end the test process with a StackOverflow long before the bottom.
        var budget = new ElementBudget(limit: 500, maxVisits: int.MaxValue);
        WindowWalk? walk = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { walk = new TreeWalker(source, budget, Counter(), maxDepth: int.MaxValue).Walk(root, App); }
            catch (Exception e) { error = e; }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.Equal(Depth, source.Fetched);
        Assert.Equal(Depth + 1, source.Released);
        Assert.Equal("Bottom", Assert.Single(walk!.Interactive).Name);
        Assert.False(budget.Truncated);
    }

    [Fact]
    public void TheBudgetStopsTheWalkAndMarksItTruncated()
    {
        var source = new FakeSource();
        var root = source.Add(Window(), Enumerable.Range(0, 5).Select(i => Button($"B{i}", i * 60)).ToArray());
        var (walk, budget) = Walk(source, root, limit: 2);
        Assert.Equal(["B4", "B3"], walk.Interactive.Select(e => e.Name));
        Assert.True(budget.Truncated);
        Assert.Equal(source.Fetched + 1, source.Released);
    }

    [Fact]
    public void TheVisitCapStopsATreeWithoutOutputElements()
    {
        var source = new FakeSource();
        var root = source.Add(Window(), Enumerable.Range(0, 100).Select(_ => Pane("")).ToArray());
        var budget = new ElementBudget(limit: 500, maxVisits: 10);
        new TreeWalker(source, budget, Counter()).Walk(root, App);
        Assert.True(budget.Truncated);
        Assert.True(budget.Exhausted);
        // The window and nine panes, then the walk stops.
        Assert.Equal(10, source.ChildrenRead.Count);
        Assert.Equal(source.Fetched + 1, source.Released);
    }

    [Fact]
    public void ACancelledWalkStopsAtTheNextElement()
    {
        var source = new FakeSource();
        var root = source.Add(Window(), Button("A", 10), Button("B", 100), Button("C", 200));
        using var cancellation = new CancellationTokenSource();
        var visited = new List<string>();
        source.BeforeChildren = element =>
        {
            visited.Add(element.Name);
            if (element.Name == "C") cancellation.Cancel(); // the first child visited (right to left)
        };
        var walker = new TreeWalker(source, new ElementBudget(500), Counter());
        Assert.ThrowsAny<OperationCanceledException>(() => walker.Walk(root, App, cancellation.Token));
        Assert.Equal(["App", "C"], visited);
        Assert.Equal(source.Fetched + 1, source.Released);
    }

    [Fact]
    public void AWalkCancelledDuringAWordReadTouchesNeitherTheBudgetNorTheIds()
    {
        var source = new FakeSource();
        var edit = Text("Body");
        source.Words[edit] = [new WordBox("hello", new ScreenRect(5, 5, 40, 20))];
        var root = source.Add(Window(), edit);
        using var cancellation = new CancellationTokenSource();
        source.BeforeWords = _ => cancellation.Cancel(); // the read is abandoned, then returns
        var budget = new ElementBudget(500);
        var ids = 0;
        var walker = new TreeWalker(source, budget, () => Interlocked.Increment(ref ids));
        Assert.ThrowsAny<OperationCanceledException>(() => walker.Walk(root, App, cancellation.Token));
        Assert.Equal((0, 0), (budget.Count, ids));
        Assert.Equal(source.Fetched + 1, source.Released);
    }

    [Fact]
    public void AWalkCancelledDuringAChildrenReadStopsBeforeTheChildren()
    {
        var source = new FakeSource();
        var root = source.Add(Window(), Button("A"));
        using var cancellation = new CancellationTokenSource();
        source.BeforeChildren = _ => cancellation.Cancel();
        var budget = new ElementBudget(500);
        var walker = new TreeWalker(source, budget, Counter());
        Assert.ThrowsAny<OperationCanceledException>(() => walker.Walk(root, App, cancellation.Token));
        Assert.Single(source.ChildrenRead);
        Assert.Equal(0, budget.Count);
        Assert.Equal(source.Fetched + 1, source.Released);
    }

    [Fact]
    public void AnUnlimitedBudgetStillReadsWords()
    {
        // Remaining + 1 would wrap to int.MinValue in int arithmetic and read no words.
        var source = new FakeSource();
        var edit = Text("Body");
        source.Words[edit] = [new WordBox("hello", new ScreenRect(5, 5, 40, 20))];
        var (walk, _) = Walk(source, source.Add(Window(), edit), limit: int.MaxValue);
        Assert.Equal(["Body", "hello"], walk.Interactive.Select(e => e.Name));
    }

    [Fact]
    public void AnAlreadyCancelledWalkVisitsNothing()
    {
        var source = new FakeSource();
        var root = source.Add(Window(), Button("A"));
        var walker = new TreeWalker(source, new ElementBudget(500), Counter());
        Assert.ThrowsAny<OperationCanceledException>(() => walker.Walk(root, App, new CancellationToken(canceled: true)));
        Assert.Equal((0, 1), (source.Fetched, source.Released));
    }

    [Fact]
    public void ADeadElementLosesOnlyItsOwnSubtree()
    {
        var source = new FakeSource();
        var gone = source.Add(Pane("Gone"), Button("Lost"));
        var root = source.Add(Window(), Button("Kept"), gone);
        source.BeforeChildren = element => { if (element.Name == "Gone") throw DeadElement; };
        var (walk, _) = Walk(source, root);
        Assert.Equal(["Kept"], walk.Interactive.Select(e => e.Name));
        Assert.Equal(source.Fetched + 1, source.Released);
    }

    [Fact]
    public void ATimeoutReadingChildrenEndsTheWalk()
    {
        // The caller blocks the hung window on a timeout; it must not be taken for a dead element.
        var source = new FakeSource();
        var root = source.Add(Window(), Button("A", 10), source.Add(Pane("Slow"), Button("Inner")));
        source.BeforeChildren = element => { if (element.Name == "Slow") throw UiaTimeout; };
        var error = Assert.Throws<COMException>(() => Walk(source, root));
        Assert.Same(UiaTimeout, error);
        Assert.Equal(source.Fetched + 1, source.Released);
    }

    [Fact]
    public void ATimeoutReadingWordsEndsTheWalkButOtherWordFailuresAreIgnored()
    {
        var source = new FakeSource();
        var edit = Text("Body");
        source.Words[edit] = [new WordBox("hello", new ScreenRect(5, 5, 40, 20))];
        var root = source.Add(Window(), edit);

        source.BeforeWords = _ => throw UiaTimeout;
        Assert.Same(UiaTimeout, Assert.Throws<COMException>(() => Walk(source, root)));

        source.BeforeWords = _ => throw new COMException("no text pattern", unchecked((int)0x80004005));
        Assert.Equal(["Body"], Walk(source, root).Walk.Interactive.Select(e => e.Name));

        source.BeforeWords = _ => throw DeadElement;
        Assert.Equal(["Body"], Walk(source, root).Walk.Interactive.Select(e => e.Name));
    }

    [Fact]
    public void TextControlsAddOneWordElementPerWordBox()
    {
        var source = new FakeSource();
        var edit = Text("Body");
        source.Words[edit] = [new WordBox("hello", new ScreenRect(5, 5, 40, 20)), new WordBox("away", new ScreenRect(900, 5, 950, 20)), new WordBox("world", new ScreenRect(45, 5, 90, 20))];
        var (walk, _) = Walk(source, source.Add(Window(), edit));
        Assert.Equal(["Body", "hello", "world"], walk.Interactive.Select(e => e.Name));
        Assert.Equal("Word", walk.Interactive[1].ControlType);
        Assert.Equal(["Body", "hello", "world"], walk.Window.Children.Select(c => c.Name).Reverse());
    }

    [Fact]
    public void PasswordFieldsHideTheirWords()
    {
        var source = new FakeSource();
        var password = Text("Password") with { IsPassword = true };
        source.Words[password] = [new WordBox("secret", new ScreenRect(5, 5, 40, 20))];
        var element = Assert.Single(Walk(source, source.Add(Window(), password)).Walk.Interactive);
        Assert.True(element.Metadata.IsPassword);
    }

    [Fact]
    public void ControlStateMetadata()
    {
        var source = new FakeSource();
        var toggle = Button("Bold") with { ToggleState = 1, AcceleratorKey = "Ctrl+B" };
        var combo = new UiaElementData
        {
            Name = "Font", ControlType = TreeConfig.ComboBox, LocalizedControlType = "combo box", LegacyRole = 0x2E, LegacyValue = "",
            ExpandCollapseState = 0, Bounds = new ScreenRect(0, 50, 100, 70),
        };
        var slider = new UiaElementData
        {
            Name = "Zoom", ControlType = TreeConfig.Slider, LocalizedControlType = "slider", LegacyRole = 0x33,
            RangeValue = 100, RangeMinimum = 0, RangeMaximum = 225.005, Bounds = new ScreenRect(0, 80, 100, 90),
        };
        var elements = Walk(source, source.Add(Window(), slider, combo, toggle)).Walk.Interactive.ToDictionary(e => e.Name, e => e.Metadata);
        Assert.Equal(new UiMetadata { ToggleState = "on", Shortcut = "Ctrl+B" }, elements["Bold"]);
        Assert.Equal(new UiMetadata { Value = "(empty)", ExpandCollapseState = "collapsed" }, elements["Font"]);
        Assert.Equal(new UiMetadata { Value = "100.0", Minimum = "0.0", Maximum = "225.0" }, elements["Zoom"]);
    }

    [Fact]
    public void BrowserGroupsWithAnExpandCollapseStateAreInteractive()
    {
        var source = new FakeSource();
        var group = new UiaElementData
        {
            Name = "Menu", ControlType = TreeConfig.Group, LocalizedControlType = "group", ExpandCollapseState = 3, Bounds = new ScreenRect(0, 0, 10, 10),
        };
        Assert.Empty(Walk(source, source.Add(Window(), group)).Walk.Interactive);
        Assert.Single(Walk(source, source.Add(Window(), group), context: App with { IsBrowser = true }).Walk.Interactive);
    }

    [Fact]
    public void BoxesAreClippedToTheWindowAndTheScreen()
    {
        var source = new FakeSource();
        var element = Assert.Single(Walk(source, source.Add(Window(), Button("Edge") with { Bounds = new ScreenRect(780, 10, 900, 30) })).Walk.Interactive);
        Assert.Equal((new ScreenRect(780, 10, 800, 30), new ScreenPoint(790, 20)), (element.Box, element.Center));
    }
}
