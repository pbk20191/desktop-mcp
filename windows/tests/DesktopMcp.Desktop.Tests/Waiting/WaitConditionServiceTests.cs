using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tests.Tree;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.Waiting;

namespace DesktopMcp.Desktop.Tests.Waiting;

/// <summary>
/// A <see cref="WaitConditionService"/> over fakes that log each call: <c>ScanActive</c>, <c>Scan</c>, <c>Find</c>,
/// <c>Focused</c>. The scan's active window is handle 1; the other windows are 11, 12, ... (application windows), and
/// the candidates add the non-application window 99.
/// </summary>
internal sealed class WaitRig
{
    public WaitRig(string? active, string[]? windows = null, UiElement? found = null, Exception? findThrows = null, UiElement? focused = null)
    {
        var activeWindow = active is null ? null : new WindowInfo(active, 0, WindowStatus.Normal, new ScreenRect(0, 0, 100, 100), 1, 10, false);
        var others = (windows ?? []).Select((name, i) => new WindowInfo(name, i + 1, WindowStatus.Normal, new ScreenRect(0, 0, 100, 100), 11 + i, 20 + i, false)).ToArray();
        IReadOnlyList<WindowInfo> all = activeWindow is null ? others : [activeWindow, .. others];
        var scan = new WindowScan([.. all.Select(w => w.Handle), 99], all, activeWindow);
        Service = new WaitConditionService(new Scanner(this, scan), new Finder(this, found, findThrows), () => new ScreenRect(0, 0, 1920, 1080),
            _ =>
            {
                Calls.Add("Focused");
                return Task.FromResult(focused);
            });
    }

    public List<string> Calls { get; } = [];
    public WaitConditionService Service { get; }

    /// <summary>The last FindAsync request: the active window and the others.</summary>
    public (long? Active, long[] Others)? FindRequest { get; private set; }

    /// <summary>The last FindAsync predicate.</summary>
    public Func<UiElement, bool>? Predicate { get; private set; }

    private sealed class Scanner(WaitRig rig, WindowScan scan) : IWindowScanner
    {
        public Task<WindowScan> ScanAsync(CancellationToken cancellationToken)
        {
            rig.Calls.Add("Scan");
            return Task.FromResult(scan);
        }

        public Task<WindowInfo?> ScanActiveAsync(CancellationToken cancellationToken)
        {
            rig.Calls.Add("ScanActive");
            return Task.FromResult(scan.Active);
        }
    }

    /// <summary>Gives <c>found</c> when the predicate accepts it, as a walk that reaches it would.</summary>
    private sealed class Finder(WaitRig rig, UiElement? found, Exception? error) : ITreeCapture
    {
        public Task<TreeSnapshot> CaptureAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("WaitFor must not capture a tree.");

        public Task<UiElement?> FindAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, Func<UiElement, bool> match, CancellationToken ct)
        {
            rig.Calls.Add("Find");
            rig.FindRequest = (active, others.ToArray());
            rig.Predicate = match;
            if (error is not null) throw error;
            return Task.FromResult(found is not null && match(found) ? found : null);
        }
    }
}

public class WaitConditionServiceTests
{
    private static UiElement Element(string name, bool enabled = true, string type = "button", string window = "A", UiMetadata? metadata = null) =>
        new(0, name, type, window, new ScreenRect(0, 0, 10, 10), new ScreenPoint(5, 5), (metadata ?? UiMetadata.Empty) with { IsEnabled = enabled });

    [Fact]
    public async Task ActiveWindowProbesOnlyTheForegroundWindow()
    {
        var rig = new WaitRig(active: "Untitled - Notepad");
        Assert.Equal((true, "active window matched 'Untitled - Notepad'"), await rig.Service.CheckAsync(WaitCondition.ActiveWindow, null, "notepad", default));
        Assert.Equal(["ScanActive"], rig.Calls);
    }

    [Fact]
    public async Task ActiveWindowPrefersTheWindowNameAndReportsAMissingWindowAsEmpty()
    {
        Assert.Equal((false, "active window was 'Paint'"), await new WaitRig(active: "Paint").Service.CheckAsync(WaitCondition.ActiveWindow, "paint", "notepad", default));
        Assert.Equal((true, "active window matched 'Paint'"), await new WaitRig(active: "Paint").Service.CheckAsync(WaitCondition.ActiveWindow, "paint", "", default));
        Assert.Equal((false, "active window was ''"), await new WaitRig(active: null).Service.CheckAsync(WaitCondition.ActiveWindow, "x", null, default));
    }

    [Fact]
    public async Task TextExistsChecksWindowNamesBeforeWalking()
    {
        var rig = new WaitRig(active: "A", windows: ["Downloads"]);
        Assert.Equal((true, "text 'down' appeared"), await rig.Service.CheckAsync(WaitCondition.TextExists, "down", null, default));
        Assert.DoesNotContain("Find", rig.Calls);
    }

    [Fact]
    public async Task TextExistsWalksWithATextPredicateOverNamesTypesWindowsAndMetadata()
    {
        var rig = new WaitRig(active: "A", windows: ["B"]);
        Assert.Equal((false, "text 'draft' was absent"), await rig.Service.CheckAsync(WaitCondition.TextExists, "draft", null, default));
        Assert.Equal(["Scan", "Find"], rig.Calls);
        var match = rig.Predicate!;
        Assert.True(match(Element("Draft folder")));
        Assert.True(match(Element("x", type: "draft view")));
        Assert.True(match(Element("x", window: "Drafts - Mail")));
        Assert.True(match(Element("x", metadata: new UiMetadata { Value = "my DRAFT" })));
        Assert.False(match(Element("x", metadata: new UiMetadata { HasFocus = true })));
    }

    [Fact]
    public async Task TheWalkCoversTheActiveWindowAndTheNonApplicationWindows()
    {
        var rig = new WaitRig(active: "A", windows: ["B", "C"]);
        await rig.Service.CheckAsync(WaitCondition.ElementExists, "ok", null, default);
        Assert.Equal(1L, rig.FindRequest!.Value.Active);
        Assert.Equal([99L], rig.FindRequest.Value.Others);
    }

    [Fact]
    public async Task ElementExistsStopsAtTheFirstMatch()
    {
        var rig = new WaitRig(active: "A", found: Element("OK"));
        Assert.Equal((true, "element matched 'OK'"), await rig.Service.CheckAsync(WaitCondition.ElementExists, "ok", null, default));
        Assert.Equal(["Scan", "Find"], rig.Calls);
    }

    [Fact]
    public async Task ElementExistsMatchesTheWindowName()
    {
        var rig = new WaitRig(active: "A", found: Element("OK", window: "Notepad"));
        Assert.Equal((false, "matching element was absent"), await rig.Service.CheckAsync(WaitCondition.ElementExists, "ok", "paint", default));
        Assert.Equal((true, "element matched 'OK'"), await rig.Service.CheckAsync(WaitCondition.ElementExists, null, "note", default));
    }

    [Fact]
    public async Task ElementEnabledRejectsADisabledScrollable()
    {
        var rig = new WaitRig(active: "A", found: Element("List", enabled: false));
        Assert.False((await rig.Service.CheckAsync(WaitCondition.ElementEnabled, "list", null, default)).Matched);
        Assert.True((await new WaitRig(active: "A", found: Element("List")).Service.CheckAsync(WaitCondition.ElementEnabled, "list", null, default)).Matched);
    }

    [Fact]
    public async Task ATransientUiaFailureIsNotAMatch()
    {
        // Decisions 28: no match and no detail, so WaitFor keeps the previous one.
        var rig = new WaitRig(active: "A", findThrows: new UiaNotRespondingException("busy"));
        Assert.Equal((false, (string?)null), await rig.Service.CheckAsync(WaitCondition.ElementExists, "ok", null, default));
    }

    [Theory]
    [InlineData(0x80040201)] // UIA_E_ELEMENTNOTAVAILABLE: a dead element
    [InlineData(0x80131505)] // UIA_E_TIMEOUT
    public async Task ADeadElementOrAUiaTimeoutIsNotAMatch(uint hresult)
    {
        var rig = new WaitRig(active: "A", findThrows: new COMException("x", unchecked((int)hresult)));
        Assert.Equal((false, (string?)null), await rig.Service.CheckAsync(WaitCondition.TextExists, "ok", null, default));
        Assert.Equal((false, (string?)null),
            await new WaitRig(active: "A", findThrows: new UiaTimeoutException("hung")).Service.CheckAsync(WaitCondition.ElementEnabled, "ok", null, default));
    }

    [Fact]
    public async Task OtherFailuresEndTheCall()
    {
        var rig = new WaitRig(active: "A", findThrows: new DesktopException("broken"));
        Assert.Equal("broken", (await Assert.ThrowsAsync<DesktopException>(() => rig.Service.CheckAsync(WaitCondition.ElementExists, "ok", null, default))).Message);
    }

    [Fact]
    public async Task FocusedElementReadsOnlyTheFocusedElement()
    {
        var rig = new WaitRig(active: "A", focused: Element("Search", window: "Explorer", metadata: new UiMetadata { HasFocus = true }));
        Assert.Equal((true, "focused element matched 'Search'"), await rig.Service.CheckAsync(WaitCondition.FocusedElement, "sea", "explorer", default));
        Assert.Equal(["Focused"], rig.Calls);
    }

    [Fact]
    public async Task FocusedElementNeedsTheFocusFlagAndAMatch()
    {
        var unfocused = new WaitRig(active: "A", focused: Element("Search"));
        Assert.Equal((false, "matching focused element was absent"), await unfocused.Service.CheckAsync(WaitCondition.FocusedElement, null, null, default));
        var none = new WaitRig(active: "A");
        Assert.Equal((false, "matching focused element was absent"), await none.Service.CheckAsync(WaitCondition.FocusedElement, null, null, default));
        var other = new WaitRig(active: "A", focused: Element("Search", metadata: new UiMetadata { HasFocus = true }));
        Assert.Equal((false, "matching focused element was absent"), await other.Service.CheckAsync(WaitCondition.FocusedElement, "save", null, default));
    }
}

public class FocusedElementTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);

    /// <summary>A focused edit inside a nameless pane whose parent has native window 7 (top-level 5, "Untitled - Notepad").</summary>
    private sealed class FakeFocus : IFocusSource
    {
        public List<string> Calls { get; } = [];
        public UiaElementData? FocusedElement { get; set; } = new()
        {
            Name = "Text editor", ControlType = TreeConfig.Edit, LocalizedControlType = "edit", LegacyRole = 0x2A, HasKeyboardFocus = true,
            Bounds = new ScreenRect(10, 10, 300, 200), LegacyValue = "hello", IsPassword = true, RuntimeId = "42.7",
        };

        public UiaElementData Pane { get; } = new() { ControlType = TreeConfig.Pane, Bounds = new ScreenRect(0, 0, 400, 300) };
        public UiaElementData Host { get; } = new() { ControlType = TreeConfig.Pane, NativeWindowHandle = 7 };
        public int Depth { get; set; }

        public UiaElementData? Focused()
        {
            Calls.Add("Focused");
            return FocusedElement;
        }

        public UiaElementData? Parent(UiaElementData element)
        {
            Calls.Add("Parent");
            if (Depth > 0) return --Depth == 0 ? Host : Pane;
            return ReferenceEquals(element, FocusedElement) ? Pane : ReferenceEquals(element, Pane) ? Host : null;
        }

        public UiaElementData ReadWindow(long window)
        {
            Calls.Add($"ReadWindow {window}");
            return new UiaElementData { Name = " Untitled - Notepad ", ClassName = "Notepad", Bounds = new ScreenRect(0, 0, 800, 600) };
        }
    }

    private readonly FakeWindowSystem _windows = new();

    public FocusedElementTests()
    {
        _windows.Roots[7] = 5;
        _windows.Processes[5] = 50;
        _windows.ProcessNames[50] = "notepad.exe";
    }

    [Fact]
    public void TheFocusedElementIsDescribedInItsTopLevelWindow()
    {
        var source = new FakeFocus();
        var element = FocusedElement.Describe(source, _windows, foreground: 5, _ => false, Screen, default);
        Assert.NotNull(element);
        Assert.Equal(("Text editor", "edit", "Untitled - Notepad"), (element.Name, element.ControlType, element.WindowName));
        Assert.True(element.Metadata.HasFocus);
        Assert.Equal(["Focused", "Parent", "Parent", "ReadWindow 5"], source.Calls);
        Assert.Equal(5, element.Identity!.Window);
    }

    [Fact]
    public void AForegroundWindowThatDoesNotAnswerGetsNoUiaCall()
    {
        var windows = new RespondingWindows(_windows, window => window == 5 ? WindowResponse.NotResponding : WindowResponse.Responding);
        var source = new FakeFocus();
        Assert.Null(FocusedElement.Describe(source, windows, foreground: 5, _ => false, Screen, default));
        Assert.Empty(source.Calls);
    }

    [Fact]
    public void TheReadRecordsEachWindowItEntersBeforeItsUiaCalls()
    {
        var source = new FakeFocus();
        var entered = new List<string>();
        void Entering(long window) => entered.Add($"{window} after {source.Calls.Count} calls");
        Assert.NotNull(FocusedElement.Describe(source, _windows, foreground: 3, _ => false, Screen, default, Entering));
        Assert.Equal(["3 after 0 calls", "5 after 3 calls"], entered); // the root before ReadWindow 5
    }

    [Fact]
    public async Task ADeadlineBlocksTheWindowTheReadLastEntered()
    {
        var blocks = new HangBlockList(TimeProvider.System, HangBlockList.DefaultDuration);
        _windows.Foreground.Enqueue(3);
        var reader = new UiaFocusedElementReader(_windows, blocks, () => Screen, _ => null, (foreground, skip, screen, entering, ct) =>
        {
            entering(5);
            throw new UiaTimeoutException("hung");
        });
        await Assert.ThrowsAsync<UiaTimeoutException>(() => reader.ReadAsync(default));
        Assert.True(blocks.IsBlocked(5, 50));
        Assert.False(blocks.IsBlocked(3, 100));
    }

    [Fact]
    public async Task ADeadlineBeforeAnyOtherWindowBlocksTheForegroundWindow()
    {
        var blocks = new HangBlockList(TimeProvider.System, HangBlockList.DefaultDuration);
        _windows.Foreground.Enqueue(3);
        var reader = new UiaFocusedElementReader(_windows, blocks, () => Screen, _ => null, (foreground, skip, screen, entering, ct) =>
        {
            entering(foreground);
            throw new UiaTimeoutException("hung");
        });
        await Assert.ThrowsAsync<UiaTimeoutException>(() => reader.ReadAsync(default));
        Assert.True(blocks.IsBlocked(3, 100));
        Assert.False(blocks.IsBlocked(5, 50));
    }

    [Fact]
    public void AnotherTopLevelWindowIsCheckedAndSkippedBeforeItIsRead()
    {
        var source = new FakeFocus();
        Assert.Null(FocusedElement.Describe(source, _windows, foreground: 3, window => window == 5, Screen, default));
        Assert.DoesNotContain("ReadWindow 5", source.Calls);

        var hung = new RespondingWindows(_windows, window => window == 5 ? WindowResponse.Hung : WindowResponse.Responding);
        var again = new FakeFocus();
        Assert.Null(FocusedElement.Describe(again, hung, foreground: 3, _ => false, Screen, default));
        Assert.DoesNotContain("ReadWindow 5", again.Calls);
    }

    [Fact]
    public void TheClimbStopsAfter32Ancestors()
    {
        var source = new FakeFocus { Depth = FocusedElement.MaxAncestors + 1 };
        Assert.Null(FocusedElement.Describe(source, _windows, foreground: 5, _ => false, Screen, default));
        Assert.Equal(FocusedElement.MaxAncestors, source.Calls.Count(c => c == "Parent"));

        var within = new FakeFocus { Depth = FocusedElement.MaxAncestors };
        Assert.NotNull(FocusedElement.Describe(within, _windows, foreground: 5, _ => false, Screen, default));
    }

    [Fact]
    public void NoFocusedElementIsNoMatch() =>
        Assert.Null(FocusedElement.Describe(new FakeFocus { FocusedElement = null }, _windows, foreground: 5, _ => false, Screen, default));

    /// <summary><see cref="FakeWindowSystem"/> with a scripted WM_NULL answer.</summary>
    private sealed class RespondingWindows(FakeWindowSystem inner, Func<long, WindowResponse> answer) : IWindowSystem
    {
        public IReadOnlyList<long> EnumerateTopLevel() => inner.EnumerateTopLevel();
        public bool IsWindow(long window) => inner.IsWindow(window);
        public bool IsVisible(long window) => inner.IsVisible(window);
        public bool IsMinimized(long window) => inner.IsMinimized(window);
        public bool IsMaximized(long window) => inner.IsMaximized(window);
        public int GetProcessId(long window) => inner.GetProcessId(window);
        public string GetClassName(long window) => inner.GetClassName(window);
        public long FindWindow(string className) => inner.FindWindow(className);
        public long GetForegroundWindow() => inner.GetForegroundWindow();
        public long GetRoot(long window) => inner.GetRoot(window);
        public string? GetProcessName(int processId) => inner.GetProcessName(processId);
        public uint GetThreadId(long window) => inner.GetThreadId(window);
        public long GetRootOwner(long window) => inner.GetRootOwner(window);
        public ScreenRect GetBounds(long window) => inner.GetBounds(window);
        public long WindowFromPoint(ScreenPoint point) => inner.WindowFromPoint(point);
        public string GetTitle(long window) => inner.GetTitle(window);
        public WindowResponse CheckResponding(long window) => answer(window);
    }
}
