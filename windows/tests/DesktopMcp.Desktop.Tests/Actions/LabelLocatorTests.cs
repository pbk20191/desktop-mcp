using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tests.Actions;

public class LabelLocatorTests
{
    private static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);
    private static ElementIdentity Id(string runtimeId = "1.2", string automationId = "ok", string name = "OK") =>
        new(100, runtimeId, TreeConfig.Button, automationId, name, null);

    private static FakeElement Button(string runtimeId, ScreenRect bounds, string automationId = "ok", string name = "OK",
        bool enabled = true, ScreenRect? scrollTo = null, ScreenPoint? clickable = null) => new(
        new UiaElementData
        {
            RuntimeId = runtimeId, ControlType = TreeConfig.Button, AutomationId = automationId, Name = name,
            IsEnabled = enabled, Bounds = bounds, ClickablePoint = clickable,
        },
        scrollTo);

    [Fact]
    public void FindsTheElementByRuntimeIdUnderTheStoredPoint()
    {
        var source = FakeElementSource.Window(Button("1.2", new ScreenRect(10, 10, 50, 30)), pointMap: true);
        var result = new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default);
        Assert.Equal(new ScreenPoint(30, 20), result.Point);
        Assert.DoesNotContain("Children", source.Calls);
        Assert.Equal([100L], source.ElementAtWindows);
    }

    [Fact]
    public void FindsAMovedElementByWalkingTheWindow()
    {
        var source = FakeElementSource.Window(Button("1.2", new ScreenRect(200, 200, 240, 220)), pointMap: false);
        Assert.Equal(new ScreenPoint(220, 210), new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Point);
    }

    [Fact]
    public void ReFindsARecreatedElementByIdentityWhenExactlyOneMatches()
    {
        var source = FakeElementSource.Window(Button("9.9", new ScreenRect(10, 10, 50, 30)), pointMap: false);
        Assert.Null(new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void AmbiguousIdentityIsStale()
    {
        var source = FakeElementSource.Window([Button("9.8", new(10, 10, 50, 30)), Button("9.9", new(60, 10, 90, 30))], pointMap: false);
        Assert.Equal(LocateFailure.Stale, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void AWalkCutByACapIsStaleEvenWithOneMatch()
    {
        var source = FakeElementSource.Window(Button("9.9", new(10, 10, 50, 30)), pointMap: false, capChildren: true);
        Assert.Equal(LocateFailure.Stale, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void NoIdentityFieldsMeansNoReFind()
    {
        var source = FakeElementSource.Window(Button("9.9", new(10, 10, 50, 30), automationId: "", name: ""), pointMap: false);
        Assert.Equal(LocateFailure.Stale, new LabelLocator(source, [Monitor], Monitor).Locate(Id(automationId: "", name: ""), new(30, 20), default).Failure);
    }

    [Fact]
    public void NotRespondingWindowMakesNoUiaCall()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30)), pointMap: true, response: WindowResponse.NotResponding);
        Assert.Equal(LocateFailure.NotResponding, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
        // Both are Win32 calls, not UIA (preflight F21).
        Assert.Equal(["WindowExists", "CheckWindow"], source.Calls);
    }

    [Fact]
    public void APointOverAnotherWindowMakesNoUiaCallThereAndWalks()
    {
        var source = FakeElementSource.Window(Button("1.2", new ScreenRect(10, 10, 50, 30)), pointMap: true);
        source.PointRoot = 555;
        Assert.Equal(new ScreenPoint(30, 20), new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Point);
        Assert.DoesNotContain("ElementAt", source.Calls);
        Assert.DoesNotContain("Parent", source.Calls);
        Assert.Contains("ReadWindow", source.Calls);
        Assert.Contains("Children", source.Calls);
    }

    [Fact]
    public void AnOwnedPopupOnTheSameThreadUnderThePointUsesThePointStep()
    {
        var source = FakeElementSource.Window(Button("1.2", new ScreenRect(10, 10, 50, 30)), pointMap: true);
        source.PointRoot = 555;
        source.SameThreadOwnedRoots.Add(555);
        Assert.Equal(new ScreenPoint(30, 20), new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Point);
        Assert.Contains("ElementAt", source.Calls);
        Assert.Equal([100L], source.ElementAtWindows);
        Assert.DoesNotContain("ReadWindow", source.Calls);
    }

    [Fact]
    public void WindowGoneIsStale()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30)), pointMap: true, windowExists: false);
        Assert.Equal(LocateFailure.Stale, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void DisabledIsReported()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30), enabled: false), pointMap: true);
        Assert.Equal(LocateFailure.Disabled, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void OffScreenScrollsIntoViewThenUsesTheNewBounds()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 2000, 50, 2020), scrollTo: new ScreenRect(10, 500, 50, 520)), pointMap: false);
        var result = new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default);
        Assert.Equal(new ScreenPoint(30, 510), result.Point);
        Assert.Contains("ScrollIntoView", source.Calls);
    }

    [Fact]
    public void OffScreenWithoutScrollItemFails()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 2000, 50, 2020)), pointMap: false);
        Assert.Equal(LocateFailure.OffScreen, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void ClickablePointOutsideTheBoundsFallsBackToTheCentre()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30), clickable: new ScreenPoint(500, 500)), pointMap: true);
        Assert.Equal(new ScreenPoint(30, 20), new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Point);
    }

    [Fact]
    public void ClickablePointInsideTheBoundsIsUsed()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30), clickable: new ScreenPoint(12, 12)), pointMap: true);
        Assert.Equal(new ScreenPoint(12, 12), new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Point);
    }

    // Beyond the brief's list: the decisions' remaining rules.

    [Fact]
    public void ARuntimeIdMatchOnAnAncestorOfThePointIsUsed()
    {
        // The point hits a child of the labelled element (a button's text, say); the climb finds the button.
        var text = new FakeElement(new UiaElementData { RuntimeId = "1.2.1", ControlType = 50020, Bounds = new(12, 12, 48, 28) }, null);
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30)), pointMap: true, pointChild: text);
        var result = new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default);
        Assert.Equal("1.2", result.Element?.RuntimeId);
        Assert.DoesNotContain("Children", source.Calls);
    }

    [Fact]
    public void ANotRespondingWindowDuringTheWalkStopsIt()
    {
        var source = FakeElementSource.Window(Button("9.9", new(10, 10, 50, 30)), pointMap: false);
        source.ResponseAfterFirstCheck = WindowResponse.Hung;
        Assert.Equal(LocateFailure.NotResponding, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
        Assert.DoesNotContain("Children", source.Calls);
    }

    [Fact]
    public void TheWalkChecksTheWindowBeforeEachChildrenRead()
    {
        var source = FakeElementSource.Window([Button("9.8", new(10, 10, 50, 30), name: "A"), Button("9.9", new(60, 10, 90, 30), name: "B")], pointMap: false);
        new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default);
        var walk = source.Calls.SkipWhile(c => c != "ReadWindow").Skip(1).ToList();
        Assert.Equal(3, walk.Count(c => c == "Children"));
        for (var i = 0; i < walk.Count; i++)
            if (walk[i] == "Children") Assert.Equal("CheckWindow", walk[i - 1]);
    }

    [Fact]
    public void TheWalkVisitsAtMostMaxVisitedElementsAndIsThenStale()
    {
        // The one identity match comes first; without the cap it would be used.
        List<FakeElement> many = [Button("9.9", new(10, 10, 50, 30))];
        many.AddRange(Enumerable.Range(0, LabelLocator.MaxVisited + 10)
            .Select(i => Button($"5.{i}", new(10, 10, 50, 30), automationId: $"x{i}", name: $"n{i}")));
        var source = FakeElementSource.Window([.. many], pointMap: false);
        Assert.Equal(LocateFailure.Stale, new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void TheWalkStopsAsStaleOnceItsTimeBudgetIsSpent()
    {
        // The RuntimeId match is the tenth element; every children read takes a second of the four-second budget.
        List<FakeElement> elements = [.. Enumerable.Range(0, 9).Select(i => Button($"5.{i}", new(10, 10, 50, 30), automationId: $"x{i}", name: $"n{i}"))];
        elements.Add(Button("1.2", new(10, 10, 50, 30)));
        var source = FakeElementSource.Window([.. elements], pointMap: false);
        var time = new ManualTime();
        source.OnChildren = () => time.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(TimeSpan.FromSeconds(4), LabelLocator.WalkBudget);
        Assert.Equal(LocateFailure.Stale, new LabelLocator(source, [Monitor], Monitor, time).Locate(Id(), new(30, 20), default).Failure);
        Assert.Equal(4, source.Calls.Count(call => call == "Children"));
    }

    [Fact]
    public void AWalkWithinItsTimeBudgetFindsTheElement()
    {
        List<FakeElement> elements = [.. Enumerable.Range(0, 9).Select(i => Button($"5.{i}", new(10, 10, 50, 30), automationId: $"x{i}", name: $"n{i}"))];
        elements.Add(Button("1.2", new(10, 10, 50, 30)));
        var source = FakeElementSource.Window([.. elements], pointMap: false);
        var time = new ManualTime();
        source.OnChildren = () => time.Now += TimeSpan.FromMilliseconds(300);
        Assert.Null(new LabelLocator(source, [Monitor], Monitor, time).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void OffscreenFlagAloneNeverFailsALabel()
    {
        var source = FakeElementSource.Window(
            new FakeElement(Button("1.2", new(10, 10, 50, 30)).Data with { IsOffscreen = true }, null), pointMap: true);
        Assert.Null(new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void TheCentreIsClippedToTheWindowAndTheMonitor()
    {
        var source = FakeElementSource.Window(Button("1.2", new(-100, 10, 100, 30)), pointMap: true);
        // Bounds ∩ window (0..60) ∩ monitor = (0,10,60,30).
        Assert.Equal(new ScreenPoint(30, 20),
            new LabelLocator(source, [Monitor], new ScreenRect(0, 0, 60, 1080)).Locate(Id(), new(30, 20), default).Point);
    }

    [Fact]
    public void AClippedCentreOutsideTheWindowIsOffScreen()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30)), pointMap: true);
        Assert.Equal(LocateFailure.OffScreen,
            new LabelLocator(source, [Monitor], new ScreenRect(500, 500, 900, 900)).Locate(Id(), new(30, 20), default).Failure);
    }

    [Fact]
    public void CancellationStopsTheLocate()
    {
        var source = FakeElementSource.Window(Button("1.2", new(10, 10, 50, 30)), pointMap: true);
        Assert.Throws<OperationCanceledException>(() =>
            new LabelLocator(source, [Monitor], Monitor).Locate(Id(), new(30, 20), new CancellationToken(canceled: true)));
    }
}

internal sealed record FakeElement(UiaElementData Data, ScreenRect? ScrollTo);

/// <summary>A window element with children, a point map and per-element ScrollItem support; logs every call by name.</summary>
internal sealed class FakeElementSource : IElementSource
{
    private readonly UiaElementData _root = new() { RuntimeId = "42", ControlType = 50032, Name = "Window", Bounds = new(0, 0, 1920, 1080) };
    private readonly Dictionary<string, FakeElement> _elements = [];
    private readonly Dictionary<string, UiaElementData> _parents = [];
    private readonly Dictionary<string, List<UiaElementData>> _children = [];
    private readonly HashSet<string> _scrolled = [];
    private UiaElementData? _atPoint;
    private bool _capChildren;
    private bool _windowExists;
    private WindowResponse _response;
    private int _checks;

    public List<string> Calls { get; } = [];

    /// <summary>The window argument of every ElementAt call.</summary>
    public List<long> ElementAtWindows { get; } = [];

    /// <summary>The answer of every CheckWindow after the first.</summary>
    public WindowResponse? ResponseAfterFirstCheck { get; set; }

    public static FakeElementSource Window(FakeElement element, bool pointMap, bool capChildren = false,
        WindowResponse response = WindowResponse.Responding, bool windowExists = true, FakeElement? pointChild = null) =>
        Window([element], pointMap, capChildren, response, windowExists, pointChild);

    public static FakeElementSource Window(FakeElement[] elements, bool pointMap, bool capChildren = false,
        WindowResponse response = WindowResponse.Responding, bool windowExists = true, FakeElement? pointChild = null)
    {
        var source = new FakeElementSource { _capChildren = capChildren, _windowExists = windowExists, _response = response };
        source._children[source._root.RuntimeId!] = [.. elements.Select(e => e.Data)];
        foreach (var element in elements)
        {
            source._elements[element.Data.RuntimeId!] = element;
            source._parents[element.Data.RuntimeId!] = source._root;
        }
        if (pointChild is not null)
        {
            source._elements[pointChild.Data.RuntimeId!] = pointChild;
            source._parents[pointChild.Data.RuntimeId!] = elements[0].Data;
            source._children[elements[0].Data.RuntimeId!] = [pointChild.Data];
        }
        source._atPoint = pointMap ? (pointChild ?? elements[0]).Data : source._root;
        return source;
    }

    public WindowResponse CheckWindow(long window)
    {
        Calls.Add("CheckWindow");
        return _checks++ > 0 && ResponseAfterFirstCheck is { } later ? later : _response;
    }

    public bool WindowExists(long window)
    {
        Calls.Add("WindowExists");
        return _windowExists;
    }

    /// <summary>The top-level window under every point; the label's window (100) unless a test moves it.</summary>
    public long PointRoot { get; set; } = 100;

    public long RootAt(ScreenPoint point)
    {
        Calls.Add("RootAt");
        return PointRoot;
    }

    /// <summary>Top-level windows owned by the label's window (100) and on its GUI thread.</summary>
    public HashSet<long> SameThreadOwnedRoots { get; } = [];

    public bool IsSameThreadOwnedRoot(long root, long window) => window == 100 && SameThreadOwnedRoots.Contains(root);

    public UiaElementData? ElementAt(ScreenPoint point, long window)
    {
        Calls.Add("ElementAt");
        ElementAtWindows.Add(window);
        return _atPoint;
    }

    public UiaElementData? Parent(UiaElementData element)
    {
        Calls.Add("Parent");
        return _parents.GetValueOrDefault(element.RuntimeId!);
    }

    public UiaElementData ReadWindow(long window)
    {
        Calls.Add("ReadWindow");
        return _root;
    }

    /// <summary>Runs on every Children call (a test moves its clock here).</summary>
    public Action? OnChildren { get; set; }

    public IReadOnlyList<UiaElementData> Children(UiaElementData parent, out bool capped)
    {
        Calls.Add("Children");
        OnChildren?.Invoke();
        capped = _capChildren && parent == _root;
        return _children.GetValueOrDefault(parent.RuntimeId!) ?? [];
    }

    public bool CanScrollIntoView(UiaElementData element)
    {
        Calls.Add("CanScrollIntoView");
        return _elements.TryGetValue(element.RuntimeId!, out var e) && e.ScrollTo is not null;
    }

    public bool ScrollIntoView(UiaElementData element)
    {
        Calls.Add("ScrollIntoView");
        if (!CanScrollIntoViewQuiet(element)) return false;
        _scrolled.Add(element.RuntimeId!);
        return true;
    }

    public UiaElementData Refresh(UiaElementData element)
    {
        Calls.Add("Refresh");
        return _scrolled.Contains(element.RuntimeId!) && _elements[element.RuntimeId!].ScrollTo is { } bounds
            ? element with { Bounds = bounds }
            : element;
    }

    private bool CanScrollIntoViewQuiet(UiaElementData element) =>
        _elements.TryGetValue(element.RuntimeId!, out var e) && e.ScrollTo is not null;
}

[Trait("Category", "Desktop")]
public sealed partial class LabelLocatorDesktopTests : IDisposable
{
    // Off-screen, like UiaTreeSourceDesktopTests, so nothing shows on the user's desktop; a fake monitor covers that area,
    // so the gate sees the window as on screen. Only the test's own window is read and moved (no activation, no input).
    private const int Origin = -20000;
    private static readonly ScreenRect FakeMonitor = new(Origin - 1000, Origin - 1000, Origin + 3000, Origin + 3000);

    private readonly TestWindow _window = new(left: Origin, top: Origin);
    private readonly UiaWorker<UiaSession> _worker = new(UiaSession.Create);

    private bool _closed;

    public void Dispose()
    {
        try { _worker.Dispose(); }
        finally { if (!_closed) _window.Dispose(); }
    }

    private sealed class OffScreenDisplays : IDisplayProvider
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => [new(0, "FAKE", FakeMonitor, true, FakeMonitor, 96, 1.0, "Landscape")];
        public ScreenRect GetVirtualScreen() => FakeMonitor;
    }

    private UiaLabelLocator Locator() => new(_worker, new OffScreenDisplays(), new Win32WindowSystem());

    // What Snapshot stores for the button (TreeWalker.Identity), read with the walk's own source.
    private async Task<(ElementIdentity Identity, UiaElementData Button, bool CanScroll)> ReadButton()
    {
        var handle = (long)_window.Handle;
        return await _worker.RunAsync((session, token) =>
        {
            var tree = new UiaTreeSource(session, handle);
            var button = tree.GetChildren(tree.ReadWindow(handle), out _).Single(c => c.Name == TestWindow.ButtonText);
            var identity = new ElementIdentity(handle, button.RuntimeId!, button.ControlType, button.AutomationId, button.Name.Trim(), button.ClickablePoint);
            var source = new UiaElementSource(session, new Win32WindowSystem());
            return (identity, button, source.CanScrollIntoView(source.Refresh(button)));
        }, TimeSpan.FromSeconds(20), default);
    }

    private static ScreenPoint Expected(UiaElementData button) =>
        button.ClickablePoint is { } p && button.Bounds.Contains(p) ? p : button.Bounds.Center;

    [Fact]
    public async Task FindsTheTestButtonAtItsCentre()
    {
        var (identity, button, canScroll) = await ReadButton();
        Assert.False(canScroll); // so the locate below never calls ScrollIntoView
        var result = await Locator().LocateAsync(identity, button.Bounds.Center, default);
        Assert.Null(result.Failure);
        Assert.Equal(Expected(button), result.Point);
        Assert.Equal(identity.RuntimeId, result.Element?.RuntimeId);
        Assert.Equal((long)_window.ButtonHandle, result.Element?.NativeWindowHandle);
    }

    [Fact]
    public async Task FindsTheTestButtonAfterTheWindowMoves()
    {
        var (identity, button, canScroll) = await ReadButton();
        Assert.False(canScroll);
        Assert.True(SetWindowPos(_window.Handle, 0, Origin + 100, Origin, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate));
        var result = await Locator().LocateAsync(identity, button.Bounds.Center, default);
        Assert.Null(result.Failure);
        var moved = button.Bounds with { Left = button.Bounds.Left + 100, Right = button.Bounds.Right + 100 };
        Assert.Equal(moved, result.Element?.Bounds);
        Assert.True(moved.Contains(result.Point));
    }

    [Fact]
    public async Task ADisabledTestButtonIsDisabled()
    {
        var (identity, button, canScroll) = await ReadButton();
        Assert.False(canScroll);
        EnableWindow(_window.ButtonHandle, false);
        Assert.Equal(LocateFailure.Disabled, (await Locator().LocateAsync(identity, button.Bounds.Center, default)).Failure);
    }

    [Fact]
    public async Task AClosedTestWindowIsStale()
    {
        var (identity, button, _) = await ReadButton();
        _closed = true;
        _window.Dispose();
        Assert.Equal(LocateFailure.Stale, (await Locator().LocateAsync(identity, button.Bounds.Center, default)).Failure);
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enable);
}
