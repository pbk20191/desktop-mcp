using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Probe;

/// <summary>
/// P2 Task 21: what makes a Chromium window hang under Snapshot's UIA walk. Runs UIA steps against ONE window (the caller
/// passes the HWND of a process it started) on an MTA thread, while a second thread pings the window's UI thread with
/// <c>SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 100 ms)</c> every 50 ms. Per step it prints the longest unresponsive
/// stretch (a failed ping up to the next answered one) and the total, then the same for a settle period after the step.
/// Read-only: it never sets focus or invokes a pattern.
/// </summary>
/// <remarks>
/// Usage: <c>uia-hang &lt;hwnd&gt; &lt;step,step,...&gt; [--budget N] [--settle ms] [--baseline ms] [--final ms] [--doc name]</c>. Steps:
/// <list type="bullet">
/// <item><c>element</c>: ElementFromHandleBuildCache with the product's ElementRequest (the first call turns on the
/// app's accessibility).</item>
/// <item><c>probe</c>: the WindowCatalog probe (<see cref="UiaTreeSource.ProbeWindow"/>).</item>
/// <item><c>walk</c>: the product's single-window walk (UiaTreeSource, TreeWalker, ElementBudget, same caps).</item>
/// <item><c>walk-mc100</c>: the walk with at most 100 children kept per element (the product's MaxChildren cut).</item>
/// <item><c>walk-min</c>: the walk with a minimal property set (no patterns).</item>
/// <item><c>walk-sib</c>, <c>walk-sib100</c>, <c>walk-sib-min</c>: children read one at a time through the control view
/// walker (one cross-process call per child) instead of one BuildUpdatedCache(TreeScope_Children); <c>100</c> stops
/// after 100 children per element, <c>min</c> uses the minimal property set.</item>
/// <item><c>walk-findall</c>: children through FindAllBuildCache(TreeScope_Children, true condition) with the
/// product's ElementRequest.</item>
/// <item><c>text-ranges</c>, <c>text-attr</c>, <c>text-words</c>: the walker's TextPattern word read on the window's
/// first DocumentControl named like --doc, in phases (see <see cref="Text"/>).</item>
/// <item><c>w.…</c>: a composite walk (see <see cref="Composite"/>).</item>
/// <item><c>subtree-min</c>: one BuildUpdatedCache(TreeScope_Subtree) with the minimal set on the window (a contrast:
/// the product never uses TreeScope_Subtree).</item>
/// </list>
/// </remarks>
internal static class UiaHangProbe
{
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(90);

    private static readonly UIA_PROPERTY_ID[] MinimalProperties =
    [
        UIA_PROPERTY_ID.UIA_NamePropertyId,
        UIA_PROPERTY_ID.UIA_ControlTypePropertyId,
        UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId,
        UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId,
        UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId,
        UIA_PROPERTY_ID.UIA_IsControlElementPropertyId,
        UIA_PROPERTY_ID.UIA_IsEnabledPropertyId,
        UIA_PROPERTY_ID.UIA_IsKeyboardFocusablePropertyId,
        UIA_PROPERTY_ID.UIA_HasKeyboardFocusPropertyId,
    ];

    public static bool Run(string[] args)
    {
        if (args.Length < 3 || !TryParseHandle(args[1], out var hwnd))
        {
            Console.Error.WriteLine("usage: uia-hang <hwnd> <step,step,...> [--budget N] [--settle ms] [--baseline ms] [--final ms] [--doc name]");
            return false;
        }
        var steps = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var budget = Option(args, "--budget", SnapshotSettings.DefaultMaxTreeElements);
        var settle = TimeSpan.FromMilliseconds(Option(args, "--settle", 2000));
        var baseline = TimeSpan.FromMilliseconds(Option(args, "--baseline", 2000));
        var final = TimeSpan.FromMilliseconds(Option(args, "--final", 60_000));
        var docIndex = Array.IndexOf(args, "--doc");
        if (docIndex >= 0 && docIndex + 1 < args.Length) DocumentName = args[docIndex + 1];

        using var monitor = new ResponsivenessMonitor(hwnd);
        var segments = new List<Segment>();
        Thread.Sleep(baseline);
        segments.Add(new Segment("baseline", TimeSpan.Zero, monitor.Now, true, 0, null));

        var ok = true;
        var thread = new Thread(() =>
        {
            UiaSession session;
            try { session = UiaSession.Create(); }
            catch (Exception e)
            {
                Console.WriteLine($"uia-hang: session failed: {Describe(e)}");
                ok = false;
                return;
            }
            foreach (var step in steps)
            {
                var start = monitor.Now;
                var stats = new WalkStats();
                string? error = null;
                try { RunStep(session, step, hwnd, budget, stats); }
                catch (Exception e)
                {
                    error = Describe(e);
                    ok = false;
                }
                var end = monitor.Now;
                lock (segments) segments.Add(new Segment(step, start, end, error is null, stats.Elements, error, stats));
                if (ReferenceEquals(step, steps[^1]))
                {
                    // After the last step: wait until the window has answered for 3 s in a row, at most --final ms.
                    var recovered = monitor.WaitForRecovery(final, TimeSpan.FromSeconds(3));
                    lock (segments) segments.Add(new Segment(step + "+final", end, monitor.Now, recovered, 0, recovered ? null : "still unresponsive"));
                }
                else
                {
                    Thread.Sleep(settle);
                    lock (segments) segments.Add(new Segment(step + "+settle", end, monitor.Now, true, 0, null));
                }
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(StepLimit * steps.Length + settle * steps.Length + final))
        {
            Console.WriteLine($"uia-hang: the steps did not finish within {(StepLimit * steps.Length).TotalSeconds} s");
            ok = false;
        }
        monitor.Stop();

        Console.WriteLine($"uia-hang: hwnd=0x{hwnd:X} jit={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled} budget={budget}");
        lock (segments)
        {
            foreach (var segment in segments) Console.WriteLine(monitor.Describe(segment));
        }
        return ok;
    }

    private static void RunStep(UiaSession session, string step, nint hwnd, int budget, WalkStats stats)
    {
        var source = new UiaTreeSource(session);
        switch (step)
        {
            case "element":
                _ = UiaTreeSource.Read(session.Automation.ElementFromHandleBuildCache((HWND)hwnd, session.ElementRequest));
                stats.Elements = 1;
                return;
            case "probe":
                _ = source.ProbeWindow(hwnd);
                stats.Elements = 1;
                return;
            case "walk":
                Walk(session, hwnd, budget, new Counting(source, stats), stats);
                return;
            case "walk-mc100":
                Walk(session, hwnd, budget, new Variant(session, session.ElementRequest, session.ChildrenRequest, false, 100, false, stats), stats);
                return;
            case "walk-min":
                Walk(session, hwnd, budget, Minimal(session, siblings: false, 10_000, stats), stats);
                return;
            case "walk-sib":
                Walk(session, hwnd, budget, new Variant(session, session.ElementRequest, session.ChildrenRequest, true, 10_000, false, stats), stats);
                return;
            case "walk-sib100":
                Walk(session, hwnd, budget, new Variant(session, session.ElementRequest, session.ChildrenRequest, true, 100, false, stats), stats);
                return;
            case "walk-findall":
                Walk(session, hwnd, budget, new Variant(session, session.ElementRequest, session.ChildrenRequest, false, 10_000, false, stats, findAll: true), stats);
                return;
            case "walk-sib-min":
                Walk(session, hwnd, budget, Minimal(session, siblings: true, 10_000, stats), stats);
                return;
            case "subtree-min":
            {
                var request = Request(session, MinimalProperties, TreeScope.TreeScope_Subtree);
                var clock = Stopwatch.StartNew();
                var root = session.Automation.ElementFromHandleBuildCache((HWND)hwnd, request);
                stats.Calls = 1;
                stats.MaxCallMs = clock.ElapsedMilliseconds;
                stats.Elements = CountCached(root);
                return;
            }
            case "text-ranges" or "text-attr" or "text-words":
                Text(session, hwnd, step, budget, DocumentName, stats);
                return;
            case not null when step.StartsWith("w.", StringComparison.Ordinal):
                Walk(session, hwnd, budget, Composite(session, step, stats), stats);
                return;
            default:
                throw new ArgumentException($"unknown step '{step}'");
        }
    }

    // Property groups added to the minimal set by a composite step's "plus=" part, to find what makes the walk hang.
    private static readonly Dictionary<string, (UIA_PROPERTY_ID[] Properties, UIA_PATTERN_ID[] Patterns)> Groups = new()
    {
        ["ids"] = ([UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, UIA_PROPERTY_ID.UIA_LocalizedControlTypePropertyId,
            UIA_PROPERTY_ID.UIA_AcceleratorKeyPropertyId, UIA_PROPERTY_ID.UIA_ClassNamePropertyId, UIA_PROPERTY_ID.UIA_IsContentElementPropertyId,
            UIA_PROPERTY_ID.UIA_FrameworkIdPropertyId, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId], []),
        ["help"] = ([UIA_PROPERTY_ID.UIA_HelpTextPropertyId], []),
        ["click"] = ([UIA_PROPERTY_ID.UIA_ClickablePointPropertyId], []),
        ["legacy"] = ([UIA_PROPERTY_ID.UIA_LegacyIAccessibleRolePropertyId, UIA_PROPERTY_ID.UIA_LegacyIAccessibleValuePropertyId,
            UIA_PROPERTY_ID.UIA_LegacyIAccessibleDefaultActionPropertyId, UIA_PROPERTY_ID.UIA_LegacyIAccessibleStatePropertyId],
            [UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId]),
        ["legacyrole"] = ([UIA_PROPERTY_ID.UIA_LegacyIAccessibleRolePropertyId], []),
        ["legacyvalue"] = ([UIA_PROPERTY_ID.UIA_LegacyIAccessibleValuePropertyId], []),
        ["legacyaction"] = ([UIA_PROPERTY_ID.UIA_LegacyIAccessibleDefaultActionPropertyId], []),
        ["legacystate"] = ([UIA_PROPERTY_ID.UIA_LegacyIAccessibleStatePropertyId], []),
        ["legacypattern"] = ([], [UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId]),
        ["scroll"] = ([UIA_PROPERTY_ID.UIA_ScrollHorizontallyScrollablePropertyId, UIA_PROPERTY_ID.UIA_ScrollVerticallyScrollablePropertyId,
            UIA_PROPERTY_ID.UIA_ScrollHorizontalScrollPercentPropertyId, UIA_PROPERTY_ID.UIA_ScrollVerticalScrollPercentPropertyId],
            [UIA_PATTERN_ID.UIA_ScrollPatternId]),
        ["state"] = ([UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId, UIA_PROPERTY_ID.UIA_SelectionCanSelectMultiplePropertyId,
            UIA_PROPERTY_ID.UIA_SelectionIsSelectionRequiredPropertyId, UIA_PROPERTY_ID.UIA_SelectionItemIsSelectedPropertyId,
            UIA_PROPERTY_ID.UIA_ToggleToggleStatePropertyId, UIA_PROPERTY_ID.UIA_RangeValueValuePropertyId,
            UIA_PROPERTY_ID.UIA_RangeValueMinimumPropertyId, UIA_PROPERTY_ID.UIA_RangeValueMaximumPropertyId], []),
        ["window"] = ([UIA_PROPERTY_ID.UIA_WindowIsModalPropertyId], [UIA_PATTERN_ID.UIA_WindowPatternId]),
    };

    /// <summary>
    /// A composite walk step, parts separated by dots: <c>w</c>, then <c>sib</c> (sibling navigation; default one
    /// BuildUpdatedCache with TreeScope_Children), <c>cap&lt;N&gt;</c> (children per parent), <c>chunk&lt;N&gt;p&lt;ms&gt;</c>
    /// (sibling navigation pausing ms after every N children), <c>full</c> (the product's requests) or
    /// <c>plus=g1+g2</c> (the minimal set plus property groups; without either, the minimal set).
    /// Example: <c>w.sib.chunk300p30.plus=ids+scroll</c>.
    /// </summary>
    /// <summary>The text steps' document: the first DocumentControl whose name contains this (--doc; default any).</summary>
    private static string DocumentName = "";

    private static Variant Composite(UiaSession session, string step, WalkStats stats)
    {
        bool siblings = false, full = false, noWords = false;
        int cap = 10_000, chunk = 0, pause = 0;
        var properties = new List<UIA_PROPERTY_ID>(MinimalProperties);
        var patterns = new List<UIA_PATTERN_ID>();
        foreach (var part in step.Split('.').Skip(1))
        {
            if (part == "sib") siblings = true;
            else if (part == "full") full = true;
            else if (part == "nowords") noWords = true;
            else if (part.StartsWith("cap", StringComparison.Ordinal)) cap = int.Parse(part.AsSpan(3), CultureInfo.InvariantCulture);
            else if (part.StartsWith("chunk", StringComparison.Ordinal))
            {
                siblings = true;
                var numbers = part[5..].Split('p');
                chunk = int.Parse(numbers[0], CultureInfo.InvariantCulture);
                pause = int.Parse(numbers[1], CultureInfo.InvariantCulture);
            }
            else if (part.StartsWith("plus=", StringComparison.Ordinal))
            {
                foreach (var name in part[5..].Split('+'))
                {
                    var (groupProperties, groupPatterns) = Groups[name];
                    properties.AddRange(groupProperties);
                    patterns.AddRange(groupPatterns);
                }
            }
            else throw new ArgumentException($"unknown step part '{part}'");
        }
        if (full)
            return new Variant(session, session.ElementRequest, session.ChildrenRequest, siblings, cap, false, stats) { Chunk = chunk, Pause = pause, NoWords = noWords };
        var props = properties.Distinct().ToArray();
        var pats = patterns.Distinct().ToArray();
        return new Variant(session, Request(session, props, TreeScope.TreeScope_Element, pats), Request(session, props, TreeScope.TreeScope_Children, pats),
            siblings, cap, minimal: true, stats) { Chunk = chunk, Pause = pause };
    }

    private static Variant Minimal(UiaSession session, bool siblings, int maxChildren, WalkStats stats) => new(session,
        Request(session, MinimalProperties, TreeScope.TreeScope_Element), Request(session, MinimalProperties, TreeScope.TreeScope_Children),
        siblings, maxChildren, minimal: true, stats);

    private static void Walk(UiaSession session, nint hwnd, int budget, ITreeSource source, WalkStats stats)
    {
        var minimal = source is Variant { IsMinimal: true };
        var request = source is Variant variant ? variant.ElementRequest : session.ElementRequest;
        var element = session.Automation.ElementFromHandleBuildCache((HWND)hwnd, request);
        var root = minimal ? Variant.ReadMinimal(element) : UiaTreeSource.Read(element);
        stats.Elements++;
        var screen = new ScreenRect(-32768, -32768, 32767, 32767);
        var limit = new ElementBudget(budget);
        using var cancel = new CancellationTokenSource(StepLimit);
        var walk = new TreeWalker(source, limit, () => stats.Output++).Walk(root, new WindowContext(root.Name, false, root.Bounds, screen), cancel.Token);
        stats.Interactive = walk.Interactive.Count;
        stats.Truncated = limit.Truncated;
    }

    /// <summary>
    /// The walker's word read on the window's first DocumentControl, found breadth-first with the minimal request:
    /// <c>text-ranges</c> stops after GetVisibleRanges, <c>text-attr</c> also reads the FontSize attribute of each visible
    /// range, <c>text-words</c> runs the product's <see cref="UiaTreeSource.GetWords"/>.
    /// </summary>
    private static unsafe void Text(UiaSession session, nint hwnd, string step, int budget, string documentName, WalkStats stats)
    {
        var childrenRequest = Request(session, MinimalProperties, TreeScope.TreeScope_Children);
        var queue = new Queue<IUIAutomationElement>();
        queue.Enqueue(session.Automation.ElementFromHandleBuildCache((HWND)hwnd, Request(session, MinimalProperties, TreeScope.TreeScope_Element)));
        IUIAutomationElement? document = null;
        while (document is null && queue.Count > 0 && stats.Elements < 5_000)
        {
            var children = queue.Dequeue().BuildUpdatedCache(childrenRequest).GetCachedChildren();
            for (var i = 0; children is not null && i < children.get_Length() && document is null; i++)
            {
                var child = children.GetElement(i);
                stats.Elements++;
                if ((int)child.get_CachedControlType() == TreeConfig.Document && UiaValues.Take(child.get_CachedName()).Contains(documentName, StringComparison.Ordinal))
                    document = child;
                else queue.Enqueue(child);
            }
        }
        if (document is null) throw new InvalidOperationException("no DocumentControl found");
        Console.WriteLine($"uia-hang: document '{UiaValues.Take(document.get_CachedName())}' found after {stats.Elements} elements");
        var clock = Stopwatch.StartNew();
        if (step == "text-words")
        {
            var data = UiaTreeSource.Read(session.Automation.ElementFromHandleBuildCache((HWND)hwnd, session.ElementRequest)) with { Handle = document };
            stats.Output = new UiaTreeSource(session).GetWords(data, budget + 1).Count;
        }
        else
        {
            var iid = typeof(IUIAutomationTextPattern).GUID;
            document.GetCurrentPatternAs(UIA_PATTERN_ID.UIA_TextPatternId, &iid, out var raw);
            if (raw is null) throw new InvalidOperationException("the document has no TextPattern");
            var pattern = System.Runtime.InteropServices.Marshalling.ComInterfaceMarshaller<IUIAutomationTextPattern>.ConvertToManaged(raw)!;
            System.Runtime.InteropServices.Marshalling.ComInterfaceMarshaller<IUIAutomationTextPattern>.Free(raw);
            var ranges = pattern.GetVisibleRanges();
            stats.Calls = 1;
            stats.MaxChildren = ranges?.get_Length() ?? 0;
            Console.WriteLine($"uia-hang: GetVisibleRanges gave {stats.MaxChildren} range(s) in {clock.ElapsedMilliseconds} ms");
            if (step == "text-attr")
            {
                for (var r = 0; r < stats.MaxChildren; r++)
                {
                    var call = Stopwatch.StartNew();
                    using var value = ranges!.GetElement(r).GetAttributeValue(UIA_TEXTATTRIBUTE_ID.UIA_FontSizeAttributeId);
                    stats.Calls++;
                    stats.MaxCallMs = Math.Max(stats.MaxCallMs, call.ElapsedMilliseconds);
                    Console.WriteLine($"uia-hang: FontSize of visible range {r}: {value.VarType} in {call.ElapsedMilliseconds} ms");
                }
            }
        }
        stats.CallMs = clock.ElapsedMilliseconds;
    }

    // The cached subtree's size, counted with an explicit stack.
    private static int CountCached(IUIAutomationElement root)
    {
        var count = 0;
        var stack = new Stack<IUIAutomationElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            count++;
            var children = element.GetCachedChildren();
            for (var i = 0; children is not null && i < children.get_Length(); i++) stack.Push(children.GetElement(i));
        }
        return count;
    }

    private static IUIAutomationCacheRequest Request(UiaSession session, UIA_PROPERTY_ID[] properties, TreeScope scope, UIA_PATTERN_ID[]? patterns = null)
    {
        var request = session.Automation.CreateCacheRequest();
        foreach (var property in properties) request.AddProperty(property);
        foreach (var pattern in patterns ?? []) request.AddPattern(pattern);
        request.put_TreeScope(scope);
        return request;
    }

    private static int Option(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static bool TryParseHandle(string text, out nint hwnd)
    {
        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            : long.TryParse(text, CultureInfo.InvariantCulture, out value);
        hwnd = (nint)value;
        return ok && value != 0;
    }

    private static string Describe(Exception e) =>
        $"{e.GetType().Name} 0x{e.HResult:X8}{(ComErrors.IsTimeout(e) ? " (UIA timeout)" : "")} {e.Message.ReplaceLineEndings(" ")}";

    internal sealed class WalkStats
    {
        public int Elements;
        public int Output;
        public int Interactive;
        public int Calls;
        public long MaxCallMs;
        public long CallMs;
        public int MaxChildren;
        public int Navigations;
        public bool Truncated;
    }

    private sealed record Segment(string Name, TimeSpan Start, TimeSpan End, bool Ok, int Elements, string? Error, WalkStats? Stats = null);

    /// <summary>The product source, timed per GetChildren call.</summary>
    private sealed class Counting(UiaTreeSource inner, WalkStats stats) : ITreeSource
    {
        public IReadOnlyList<UiaElementData> GetChildren(UiaElementData parent)
        {
            var clock = Stopwatch.StartNew();
            var children = inner.GetChildren(parent);
            stats.Record(clock.ElapsedMilliseconds, children.Count);
            return children;
        }

        public IReadOnlyList<WordBox> GetWords(UiaElementData element, int maxWords) => inner.GetWords(element, maxWords);

        public void Release(UiaElementData element) => inner.Release(element);
    }

    /// <summary>A walk source with another property set, child cap or child-reading strategy.</summary>
    private sealed class Variant(
        UiaSession session, IUIAutomationCacheRequest elementRequest, IUIAutomationCacheRequest childrenRequest, bool siblings,
        int maxChildren, bool minimal, WalkStats stats, bool findAll = false) : ITreeSource
    {
        public IUIAutomationCacheRequest ElementRequest => elementRequest;
        public bool IsMinimal => minimal;

        /// <summary>Sibling navigation only: pause <see cref="Pause"/> ms after every <see cref="Chunk"/> children (0: never).</summary>
        public int Chunk { get; init; }

        /// <summary>Skip the TextPattern word read the walker does on Edit, Document and Image controls.</summary>
        public bool NoWords { get; init; }
        public int Pause { get; init; }

        public IReadOnlyList<UiaElementData> GetChildren(UiaElementData parent)
        {
            var element = (IUIAutomationElement)parent.Handle!;
            var clock = Stopwatch.StartNew();
            var result = new List<UiaElementData>();
            try
            {
                if (findAll)
                {
                    // FindAllBuildCache(TreeScope_Children, true condition): the provider-side search over the children.
                    var children = element.FindAllBuildCache(TreeScope.TreeScope_Children, session.Automation.CreateTrueCondition(), elementRequest);
                    var count = children?.get_Length() ?? 0;
                    for (var i = Math.Max(0, count - maxChildren); i < count; i++) result.Add(Read(children!.GetElement(i)));
                }
                else if (siblings)
                {
                    // One cross-process call per child; the cap stops the provider work too.
                    var child = session.ControlView.GetFirstChildElementBuildCache(element, elementRequest);
                    stats.Navigations++;
                    while (child is not null && result.Count < maxChildren)
                    {
                        result.Add(Read(child));
                        if (Chunk > 0 && result.Count % Chunk == 0) Thread.Sleep(Pause);
                        child = session.ControlView.GetNextSiblingElementBuildCache(child, elementRequest);
                        stats.Navigations++;
                    }
                }
                else
                {
                    // One BuildUpdatedCache: the provider computes every child; the cap only cuts what is read back.
                    var children = element.BuildUpdatedCache(childrenRequest).GetCachedChildren();
                    var count = children?.get_Length() ?? 0;
                    for (var i = Math.Max(0, count - maxChildren); i < count; i++) result.Add(Read(children!.GetElement(i)));
                }
            }
            catch (Exception e) when (ComErrors.IsDeadElement(e))
            {
            }
            stats.Record(clock.ElapsedMilliseconds, result.Count);
            return result;
        }

        public IReadOnlyList<WordBox> GetWords(UiaElementData element, int maxWords) =>
            minimal || NoWords ? [] : new UiaTreeSource(session).GetWords(element, maxWords);

        public void Release(UiaElementData element)
        {
        }

        private UiaElementData Read(IUIAutomationElement element) => minimal ? ReadMinimal(element) : UiaTreeSource.Read(element);

        public static UiaElementData ReadMinimal(IUIAutomationElement element) => new()
        {
            Name = UiaValues.Take(element.get_CachedName()),
            ControlType = (int)element.get_CachedControlType(),
            RuntimeId = UiaValues.CachedRuntimeId(element),
            Bounds = DesktopMcp.Desktop.Interop.Win32Geometry.ToScreenRect(element.get_CachedBoundingRectangle()),
            IsOffscreen = element.get_CachedIsOffscreen(),
            IsControlElement = element.get_CachedIsControlElement(),
            IsEnabled = element.get_CachedIsEnabled(),
            IsKeyboardFocusable = element.get_CachedIsKeyboardFocusable(),
            HasKeyboardFocus = element.get_CachedHasKeyboardFocus(),
            Handle = element,
        };
    }

    private static void Record(this WalkStats stats, long ms, int children)
    {
        stats.Calls++;
        stats.CallMs += ms;
        stats.MaxCallMs = Math.Max(stats.MaxCallMs, ms);
        stats.MaxChildren = Math.Max(stats.MaxChildren, children);
        stats.Elements += children;
    }

    /// <summary>Pings the window's UI thread every 50 ms from its own thread and keeps every ping's result.</summary>
    private sealed unsafe class ResponsivenessMonitor : IDisposable
    {
        private readonly nint _hwnd;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(TimeSpan Start, TimeSpan End, bool Answered)> _pings = [];
        private readonly Thread _thread;
        private volatile bool _stop;

        public ResponsivenessMonitor(nint hwnd)
        {
            _hwnd = hwnd;
            _thread = new Thread(Loop) { IsBackground = true, Name = "WM_NULL monitor" };
            _thread.Start();
        }

        public TimeSpan Now => _clock.Elapsed;

        public void Stop()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        public void Dispose() => Stop();

        /// <summary>Waits until the last pings have all been answered for <paramref name="quiet"/>; false after <paramref name="limit"/>.</summary>
        public bool WaitForRecovery(TimeSpan limit, TimeSpan quiet)
        {
            var until = Now + limit;
            var since = Now;
            while (Now < until)
            {
                Thread.Sleep(100);
                lock (_pings)
                {
                    var lastFailure = _pings.FindLastIndex(p => !p.Answered);
                    var calmSince = lastFailure < 0 ? since : _pings[lastFailure].End;
                    if (calmSince < since) calmSince = since;
                    if (_pings.Count > 0 && _pings[^1].Answered && Now - calmSince >= quiet) return true;
                }
            }
            return false;
        }

        private void Loop()
        {
            while (!_stop)
            {
                var start = _clock.Elapsed;
                nuint result;
                var answered = SendMessageTimeoutW(_hwnd, WmNull, 0, 0, SmtoAbortIfHung, (uint)PingTimeout.TotalMilliseconds, &result) != 0;
                var end = _clock.Elapsed;
                lock (_pings) _pings.Add((start, end, answered));
                var wait = start + PingInterval - _clock.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            }
        }

        /// <summary>
        /// Stretches: from the start of a failed ping to the end of the next answered one. Longest is the longest stretch
        /// that starts in the segment (its full length); total is the time stretches overlap the segment.
        /// </summary>
        public string Describe(Segment segment)
        {
            List<(TimeSpan Start, TimeSpan End, bool Answered)> pings;
            lock (_pings) pings = [.. _pings];
            var stretches = new List<(TimeSpan Start, TimeSpan End)>();
            TimeSpan? open = null;
            foreach (var ping in pings)
            {
                if (!ping.Answered) open ??= ping.Start;
                else if (open is { } from)
                {
                    stretches.Add((from, ping.End));
                    open = null;
                }
            }
            if (open is { } still) stretches.Add((still, pings[^1].End));

            var longest = TimeSpan.Zero;
            var total = TimeSpan.Zero;
            foreach (var (start, end) in stretches)
            {
                if (start >= segment.Start && start < segment.End && end - start > longest) longest = end - start;
                var overlap = Min(end, segment.End) - Max(start, segment.Start);
                if (overlap > TimeSpan.Zero) total += overlap;
            }
            var inside = pings.Where(p => p.Start >= segment.Start && p.Start < segment.End).ToList();
            var slowest = inside.Where(p => p.Answered).Select(p => (p.End - p.Start).TotalMilliseconds).DefaultIfEmpty(0).Max();
            var stats = segment.Stats;
            return FormattableString.Invariant(
                       $"step={segment.Name} ok={segment.Ok} ms={(segment.End - segment.Start).TotalMilliseconds:F0} ") +
                FormattableString.Invariant(
                    $"longest={longest.TotalMilliseconds:F0} total={total.TotalMilliseconds:F0} pings={inside.Count} ") +
                FormattableString.Invariant($"failed={inside.Count(p => !p.Answered)} slowestAnswer={slowest:F0}") +
                (stats is null ? "" : FormattableString.Invariant(
                    $" elements={stats.Elements} output={stats.Output} interactive={stats.Interactive} calls={stats.Calls} callMs={stats.CallMs} maxCallMs={stats.MaxCallMs} maxChildren={stats.MaxChildren} navigations={stats.Navigations} truncated={stats.Truncated}")) +
                (segment.Error is null ? "" : $" error={segment.Error}");
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }

    // A probe-only import (global constraints: hand-declared interop is allowed in probes).
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern unsafe nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, nuint* result);
}
