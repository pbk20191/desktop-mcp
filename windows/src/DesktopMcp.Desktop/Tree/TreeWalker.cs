using System.Globalization;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Tree;

/// <summary>The window being walked (upstream tree/service.py:922-952).</summary>
internal sealed record WindowContext(string WindowName, bool IsBrowser, ScreenRect WindowBox, ScreenRect ScreenBox)
{
    /// <summary>The walked top-level window (HWND as long); recorded in each element's identity.</summary>
    public long Handle { get; init; }
}

/// <summary>One window's elements in visit order and its semantic node with children in document order.</summary>
internal sealed class WindowWalk(SemanticNode window)
{
    /// <summary>At least one element had more children than the source reads (Decisions 37).</summary>
    public bool ChildrenCapped { get; set; }

    /// <summary>The element that ended the walk early (the walker's <c>stopWhen</c> accepted it), or null.</summary>
    public UiElement? Match { get; set; }

    public List<UiElement> Interactive { get; } = [];
    public List<UiElement> Scrollable { get; } = [];
    public SemanticNode Window { get; } = window;
}

/// <summary>
/// upstream tree/service.py:446-909 tree_traversal without recursion (Decisions 9): an explicit stack, the RuntimeId cycle
/// guard over the current path only, a depth cap and the element budget. Children are visited right to left as upstream
/// does for applications, so interactive elements are numbered in reverse document order; the semantic tree is put back
/// in document order at the end. There is no browser DOM mode (P2 Decisions 39): RootWebArea is walked like any other
/// element.
/// </summary>
/// <remarks>
/// With <paramref name="stopWhen"/>, the walk ends at the first listed element (interactive, scrollable or word, in visit
/// order) that it accepts, which becomes <see cref="WindowWalk.Match"/>; the elements not yet visited are not read (P3
/// Decisions 6). The walk is then incomplete: its lists and semantic tree stop there.
/// A walk can outlive its caller: the UIA worker abandons a job past its deadline while the thread keeps running. The walk
/// therefore checks its cancellation token before every element, and <paramref name="nextId"/> must be safe to call from
/// such a thread (an <see cref="Interlocked.Increment(ref int)"/> counter) when it is shared beyond one walk.
/// </remarks>
internal sealed class TreeWalker(
    ITreeSource source, ElementBudget budget, Func<int> nextId, int maxDepth = TreeWalker.MaxDepth, Func<UiElement, bool>? stopWhen = null)
{
    /// <summary>Elements deeper than this below the window are not visited.</summary>
    public const int MaxDepth = 256;

    /// <summary>The FrameworkId of Chromium content (Chrome, Edge, WebView2, Electron): its words are not read (Decisions 36).</summary>
    public const string ChromiumFramework = "Chrome";

    private readonly record struct Frame(UiaElementData? Node, SemanticNode? Parent, int Depth, string? ExitKey);

    /// <summary>
    /// Walks the window from <paramref name="root"/>. A dead element loses its own subtree (upstream tree/service.py:902-905);
    /// a timeout (<see cref="ComErrors.IsTimeout"/>) and any other failure end the walk and propagate, so the caller can
    /// block the hung window. Cancellation throws <see cref="OperationCanceledException"/> before the next element.
    /// </summary>
    public WindowWalk Walk(UiaElementData root, WindowContext context, CancellationToken cancellationToken = default)
    {
        var walk = new WindowWalk(new SemanticNode(SemanticKind.Window, "Window", context.WindowName));
        var path = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<Frame>();
        stack.Push(new Frame(root, walk.Window, 0, null));
        try
        {
            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                if (frame.Node is not { } node)
                {
                    path.Remove(frame.ExitKey!);
                    continue;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    source.Release(node);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (frame.Depth > 0)
                {
                    // upstream tree/service.py:846-850: stop descending once the budget is spent.
                    if (budget.Exhausted)
                    {
                        source.Release(node);
                        ReleaseAll(stack);
                        break;
                    }
                    // upstream tree/service.py:862-878: a modal dialog discards the window's elements found so far.
                    if (node.ControlType == TreeConfig.Window && !node.IsOffscreen && node.WindowIsModal == true) walk.Interactive.Clear();
                }
                Visit(node, frame, walk, context, path, stack, cancellationToken);
                if (walk.Match is not null)
                {
                    ReleaseAll(stack);
                    break;
                }
            }
        }
        catch
        {
            ReleaseAll(stack);
            throw;
        }
        SemanticTree.ReverseChildren(walk.Window);
        return walk;
    }

    private void Visit(
        UiaElementData node, Frame frame, WindowWalk walk, WindowContext context, HashSet<string> path, Stack<Frame> stack,
        CancellationToken cancellationToken)
    {
        try
        {
            if (frame.Depth > maxDepth || !budget.Visit()) return;
            // upstream tree/service.py:464-481: a provider can repeat an ancestor's RuntimeId; cut that branch.
            if (node.RuntimeId is { } runtimeId && path.Contains(runtimeId)) return;

            var typeName = TreeConfig.ControlTypeName(node.ControlType);
            var semanticAdded = AddScrollable(node, typeName, frame.Parent, walk, context);
            if (walk.Match is not null) return;
            semanticAdded |= AddInteractive(node, typeName, frame.Parent, walk, context, cancellationToken);
            if (walk.Match is not null) return;

            // upstream tree/service.py:823-839: named structural containers become tree nodes.
            var semanticParent = frame.Parent;
            if (frame.Parent is not null && !node.IsOffscreen && !semanticAdded && TreeConfig.StructuralControlTypes.Contains(typeName)
                && node.Name.Trim() is { Length: > 0 } structuralName)
            {
                semanticParent = new SemanticNode(SemanticKind.Structural, node.LocalizedControlType, structuralName);
                frame.Parent.Children.Add(semanticParent);
            }

            // Children of an element at the cap would not be visited; do not read them over COM.
            if (frame.Depth >= maxDepth) return;
            IReadOnlyList<UiaElementData> children;
            ThrowIfNotResponding(source);
            try
            {
                children = source.GetChildren(node, out var capped);
                if (capped) walk.ChildrenCapped = true;
            }
            catch (Exception e) when (ComErrors.IsDeadElement(e))
            {
                return; // upstream tree/service.py:902-905: skip the dead subtree, keep what was found
            }
            if (node.RuntimeId is { } key && path.Add(key)) stack.Push(new Frame(null, null, 0, key));
            // The children are pushed even when the walk was cancelled during the read: the token check at the next pop then
            // stops the walk, and Walk releases them with the rest of the stack.
            foreach (var child in children) stack.Push(new Frame(child, semanticParent, frame.Depth + 1, null));
        }
        finally
        {
            source.Release(node);
        }
    }

    /// <summary>
    /// The element a walk would list for <paramref name="node"/> alone (WaitFor's focused element, P3 Decisions 6): its
    /// interactive element when it has one, else its scrollable element, else null. No words are read (no source call at
    /// all), and the rules that need the walk's other elements (a modal dialog, structural parents) do not apply. The id is 0.
    /// </summary>
    internal static UiElement? Describe(UiaElementData node, WindowContext context)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        var walker = new TreeWalker(NoSource.Instance, new ElementBudget(int.MaxValue), () => 0);
        var walk = new WindowWalk(new SemanticNode(SemanticKind.Window, "Window", context.WindowName));
        var typeName = TreeConfig.ControlTypeName(node.ControlType);
        walker.AddInteractive(node, typeName, null, walk, context, CancellationToken.None, readWords: false);
        if (walk.Interactive.Count > 0) return walk.Interactive[0];
        walker.AddScrollable(node, typeName, null, walk, context);
        return walk.Scrollable.Count > 0 ? walk.Scrollable[0] : null;
    }

    // Records the element that ends the walk, when the caller asked for one.
    private bool Stop(WindowWalk walk, UiElement element)
    {
        if (stopWhen?.Invoke(element) != true) return false;
        walk.Match = element;
        return true;
    }

    private static ElementIdentity? Identity(UiaElementData node, WindowContext context) => node.RuntimeId is { } runtimeId
        ? new ElementIdentity(context.Handle, runtimeId, node.ControlType, node.AutomationId, node.Name.Trim(), node.ClickablePoint)
        : null;

    // upstream tree/service.py:483-534. The centre is the box centre, not upstream's random point (spec §10).
    private bool AddScrollable(UiaElementData node, string typeName, SemanticNode? parent, WindowWalk walk, WindowContext context)
    {
        if (TreeConfig.InteractiveControlTypes.Contains(typeName) || TreeConfig.InformativeControlTypes.Contains(typeName)
            || node.IsOffscreen || !node.VerticallyScrollable)
            return false;
        var name = node.Name.Trim() is { Length: > 0 } trimmed ? trimmed
            : node.AutomationId.Length > 0 ? node.AutomationId
            : PyText.Capitalize(node.LocalizedControlType) is { Length: > 0 } capitalized ? capitalized
            : "''";
        // upstream tree/service.py:498-500: a percent only for an axis that scrolls, else 0; UIA reports -1
        // (UIA_ScrollPatternNoScroll) for the other axis.
        var metadata = new UiMetadata
        {
            HasFocus = node.HasKeyboardFocus,
            HorizontallyScrollable = node.HorizontallyScrollable,
            HorizontalScrollPercent = node.HorizontallyScrollable ? PyText.Round(node.HorizontalScrollPercent ?? 0, 2) : 0,
            VerticallyScrollable = true,
            VerticalScrollPercent = PyText.Round(node.VerticalScrollPercent ?? 0, 2),
            IsEnabled = node.IsEnabled,
            IsSelected = node.IsSelected,
        };
        budget.TryConsume();
        var element = new UiElement(nextId(), name, node.LocalizedControlType, context.WindowName, node.Bounds, node.Bounds.Center, metadata)
        {
            Identity = Identity(node, context),
        };
        walk.Scrollable.Add(element);
        Stop(walk, element);
        if (parent is null) return false;
        parent.Children.Add(Semantic(SemanticKind.Scrollable, element));
        return true;
    }

    // upstream tree/service.py:536-792.
    private bool AddInteractive(
        UiaElementData node, string typeName, SemanticNode? parent, WindowWalk walk, WindowContext context, CancellationToken cancellationToken,
        bool readWords = true)
    {
        var area = (long)node.Bounds.Width * node.Bounds.Height;
        var visible = area > 0
            && (!node.IsOffscreen || typeName == "EditControl" || (typeName == "ListItemControl" && context.IsBrowser))
            && node.IsControlElement;
        if (!visible || !node.IsEnabled) return false;
        var focusable = TreeConfig.FocusableControlTypes.Contains(typeName) || node.IsKeyboardFocusable;
        if (!IsInteractive(node, typeName, focusable, context.IsBrowser)) return false;

        var (metadata, words) = Metadata(node, typeName, readWords, cancellationToken);
        var box = ClipToWindow(context, node.Bounds);
        var added = false;
        var name = node.Name.Trim();
        if (name.Length > 0)
        {
            budget.TryConsume();
            var element = new UiElement(nextId(), name, node.LocalizedControlType, context.WindowName, box, box.Center, metadata)
            {
                Identity = Identity(node, context),
            };
            walk.Interactive.Add(element);
            if (parent is not null)
            {
                parent.Children.Add(Semantic(SemanticKind.Interactive, element));
                added = true;
            }
            if (Stop(walk, element)) return added;
        }
        // upstream tree/service.py:320-348: one "Word" element per word box, each consuming budget.
        foreach (var word in words)
        {
            if (!budget.TryConsume()) break;
            var wordBox = ClipToWindow(context, word.Box);
            if (wordBox.Width <= 0 || wordBox.Height <= 0) continue;
            var element = new UiElement(nextId(), word.Word, "Word", context.WindowName, wordBox, wordBox.Center, UiMetadata.Empty);
            walk.Interactive.Add(element);
            parent?.Children.Add(Semantic(SemanticKind.Interactive, element));
            if (Stop(walk, element)) break;
        }
        return added;
    }

    // upstream tree/service.py:558-609.
    private static bool IsInteractive(UiaElementData node, string typeName, bool focusable, bool isBrowser)
    {
        var interactive = false;
        var roleInteractive = node.LegacyRole is { } role && TreeConfig.InteractiveRoles.Contains(role);
        if (isBrowser && typeName == "DataItemControl" && !focusable)
        {
            interactive = false;
        }
        else if (!isBrowser && typeName == "ImageControl" && focusable)
        {
            interactive = true;
        }
        else if (TreeConfig.InteractiveControlTypes.Contains(typeName) || TreeConfig.DocumentControlTypes.Contains(typeName))
        {
            var isImage = typeName == "ImageControl" && (node.LocalizedControlType == "graphic" || !focusable);
            interactive = roleInteractive && (!isImage || focusable);
        }
        else if (typeName == "GroupControl" && isBrowser)
        {
            // upstream compares the cached ExpandCollapseState with the enum, so any of its four values counts.
            if (node.ExpandCollapseState is >= 0 and <= 3) interactive = true;
            var defaultAction = node.LegacyDefaultAction is { Length: > 0 } action && TreeConfig.DefaultActions.Contains(TreeConfig.Title(action));
            if (roleInteractive && (defaultAction || focusable)) interactive = true;
        }
        return interactive || typeName == "DocumentControl";
    }

    // upstream tree/service.py:617-750. Only the metadata the tree text renders is read (Decisions 6).
    private (UiMetadata Metadata, IReadOnlyList<WordBox> Words) Metadata(
        UiaElementData node, string typeName, bool readWords, CancellationToken cancellationToken)
    {
        var metadata = new UiMetadata
        {
            HasFocus = node.HasKeyboardFocus,
            IsEnabled = node.IsEnabled,
            IsSelected = node.IsSelected,
            Shortcut = node.AcceleratorKey.Length > 0 ? node.AcceleratorKey : null,
        };
        IReadOnlyList<WordBox> words = [];
        if (typeName is "ButtonControl" or "CheckBoxControl")
            metadata = metadata with { ToggleState = node.ToggleState switch { 1 => "on", 0 => "off", _ => null } };
        if (typeName is "EditControl" or "DocumentControl" or "ImageControl")
        {
            metadata = metadata with { IsPassword = node.IsPassword, Value = LegacyValue(node) };
            if (!node.IsPassword && readWords) words = Words(node, cancellationToken);
        }
        if (typeName == "ComboBoxControl")
        {
            metadata = metadata with
            {
                Value = LegacyValue(node),
                ExpandCollapseState = node.ExpandCollapseState switch
                {
                    0 => "collapsed",
                    1 => "expanded",
                    2 => "partially expanded",
                    _ => null,
                },
            };
        }
        if (typeName == "SliderControl")
        {
            metadata = metadata with
            {
                Value = Float(node.RangeValue),
                Minimum = Float(node.RangeMinimum),
                Maximum = Float(node.RangeMaximum),
            };
        }
        return (metadata, words);
    }

    private IReadOnlyList<WordBox> Words(UiaElementData node, CancellationToken cancellationToken)
    {
        // Decisions 36: Chromium's TextPattern (GetVisibleRanges) holds the app's UI thread for seconds to minutes on a
        // large page (P2 Task 21). Upstream skips words only below a browser's RootWebArea (tree/service.py:656).
        if (string.Equals(node.FrameworkId, ChromiumFramework, StringComparison.Ordinal)) return [];
        ThrowIfNotResponding(source);
        IReadOnlyList<WordBox> words;
        try
        {
            // In long: Remaining is int.MaxValue for an unlimited budget.
            words = source.GetWords(node, (int)Math.Min(int.MaxValue, (long)budget.Remaining + 1));
        }
        catch (Exception e) when (e is not OperationCanceledException && !ComErrors.IsTimeout(e))
        {
            words = []; // upstream tree/service.py:654-663 ignores any failure here, a dead element included
        }
        // An abandoned walk resuming after a hung read must not consume the budget or ids.
        cancellationToken.ThrowIfCancellationRequested();
        return words;
    }

    /// <summary>
    /// Decisions 38: a window that does not answer WM_NULL in time ends its walk like UIA's own timeout (left out, nothing
    /// blocked); a window Windows considers hung ends it like the hang breaker's deadline (the caller blocks the window and
    /// its process). The check cannot interrupt a call already inside the provider.
    /// </summary>
    internal static void ThrowIfNotResponding(ITreeSource source)
    {
        switch (source.CheckWindow())
        {
            case WindowResponse.Hung:
                throw new UiaTimeoutException("UI Automation window walk stopped: the window is not responding (hung).");
            case WindowResponse.NotResponding:
                throw new UiaNotRespondingException(string.Create(CultureInfo.InvariantCulture,
                    $"UI Automation window walk stopped: the window did not answer within {Win32WindowSystem.ResponseTimeout.TotalMilliseconds:0} ms."));
        }
    }

    private static string LegacyValue(UiaElementData node) =>
        string.IsNullOrEmpty(node.LegacyValue) ? "(empty)" : node.LegacyValue.Trim();

    private static string? Float(double? value) => value is { } number ? PyText.FloatRepr(PyText.Round(number, 2)) : null;

    // upstream tree/service.py:307-318 iou_bounding_box: the element clipped to its window and the screen, else empty.
    private static ScreenRect ClipToWindow(WindowContext context, ScreenRect box)
    {
        var clipped = box.Intersect(context.WindowBox).Intersect(context.ScreenBox);
        return clipped.Right > clipped.Left && clipped.Bottom > clipped.Top ? clipped : default;
    }

    private static SemanticNode Semantic(SemanticKind kind, UiElement element) => new(kind, element.ControlType, element.Name)
    {
        Box = element.Box,
        Center = element.Center,
        ElementId = element.Id,
        Metadata = element.Metadata,
    };

    /// <summary>The source of <see cref="Describe"/>, which never reads children or words.</summary>
    private sealed class NoSource : ITreeSource
    {
        public static readonly NoSource Instance = new();

        public IReadOnlyList<UiaElementData> GetChildren(UiaElementData parent, out bool capped) => throw new InvalidOperationException("Describe reads no children.");
        public IReadOnlyList<WordBox> GetWords(UiaElementData element, int maxWords) => throw new InvalidOperationException("Describe reads no words.");
        public void Release(UiaElementData element) { }
        public WindowResponse CheckWindow() => WindowResponse.Responding;
    }

    private void ReleaseAll(Stack<Frame> stack)
    {
        while (stack.TryPop(out var frame))
            if (frame.Node is { } pending) source.Release(pending);
    }
}
