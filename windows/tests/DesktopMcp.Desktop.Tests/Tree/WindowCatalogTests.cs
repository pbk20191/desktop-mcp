using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Tests.Tree;

internal sealed class FakeWindowSystem : IWindowSystem
{
    public List<long> TopLevel { get; } = [];
    public HashSet<long> Hidden { get; } = [];
    public HashSet<long> Minimized { get; } = [];
    public Dictionary<long, int> Processes { get; } = [];
    public Dictionary<int, string> ProcessNames { get; } = [];
    public Dictionary<long, string> Classes { get; } = [];
    public Dictionary<long, long> Roots { get; } = [];
    public Queue<long> Foreground { get; } = new();
    public HashSet<long> Gone { get; } = [];

    public IReadOnlyList<long> EnumerateTopLevel() => TopLevel;
    public bool IsWindow(long window) => !Gone.Contains(window);
    public bool IsVisible(long window) => !Hidden.Contains(window);
    public bool IsMinimized(long window) => Minimized.Contains(window);
    public bool IsMaximized(long window) => false;
    public int GetProcessId(long window) => Processes.GetValueOrDefault(window, 100);
    public string GetClassName(long window) => Classes.GetValueOrDefault(window, "AppWindow");
    public long FindWindow(string className) => Classes.FirstOrDefault(c => c.Value == className).Key;
    public long GetForegroundWindow() => Foreground.Count > 0 ? Foreground.Dequeue() : 0;
    public long GetRoot(long window) => Roots.GetValueOrDefault(window, window);
    public string? GetProcessName(int processId) => ProcessNames.GetValueOrDefault(processId);
    public uint GetThreadId(long window) => 0;
    public ScreenRect GetBounds(long window) => default;
    public long WindowFromPoint(ScreenPoint point) => 0;
    public string GetTitle(long window) => "";
    public WindowResponse CheckResponding(long window) => WindowResponse.Responding;
}

public class WindowCatalogTests
{
    private sealed class FakeProbe : IWindowProbe
    {
        public Dictionary<long, Func<WindowProbe>> Windows { get; } = [];
        public List<long> Probed { get; } = [];

        public Task<WindowProbe> ProbeAsync(long window, CancellationToken cancellationToken)
        {
            Probed.Add(window);
            return Task.FromResult(Windows.TryGetValue(window, out var probe) ? probe() : throw new InvalidOperationException("gone"));
        }
    }

    private sealed class AllOnCurrentDesktop(params long[] elsewhere) : IVirtualDesktops
    {
        public IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows) => windows.Where(w => !elsewhere.Contains(w)).ToArray();
        public (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops() => (VirtualDesktopInfo.Default, [VirtualDesktopInfo.Default]);
    }

    private static WindowProbe App(string name, int processId = 100) =>
        new(name, TreeConfig.Window, "AppWindow", new ScreenRect(0, 0, 800, 600), processId, true, true, true, true);

    private readonly FakeWindowSystem _system = new();
    private readonly FakeProbe _probe = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly HangBlockList _blocks = new(new ManualTime(), HangBlockList.DefaultDuration);

    private WindowCatalog Catalog(IVirtualDesktops? desktops = null) =>
        new(_system, _probe, desktops ?? new AllOnCurrentDesktop(), _blocks, ownProcessId: 7, (delay, _) => { _delays.Add(delay); return Task.CompletedTask; });

    [Fact]
    public void CandidatesAreVisibleForeignWindowsOnThisDesktopThenTheShellWindows()
    {
        _system.TopLevel.AddRange([1, 2, 3, 4, 5]);
        _system.Hidden.Add(2);
        _system.Processes[3] = 7;              // this server's own window
        _system.Classes[5] = "Shell_TrayWnd";  // already listed: not added twice
        _system.Classes[90] = "Progman";
        Assert.Equal([1L, 5L, 90L], Catalog(new AllOnCurrentDesktop(4)).Candidates());
    }

    [Fact]
    public async Task OnlyApplicationWindowsAreListedWithTheirZOrderDepth()
    {
        _system.TopLevel.AddRange([1, 2, 3, 4, 5, 6, 7]);
        _system.Minimized.Add(6);
        _system.ProcessNames[200] = "msedge.exe";
        _probe.Windows[1] = () => App("Notepad");
        _probe.Windows[2] = () => App("NVIDIA GeForce Overlay");
        _probe.Windows[3] = () => App("") with { HasChildren = false };
        _probe.Windows[4] = () => App("Dialog") with { CanMaximize = false };
        _probe.Windows[5] = () => App("Empty") with { Bounds = new ScreenRect(0, 0, 0, 0) };
        _probe.Windows[6] = () => App("Minimized") with { Bounds = new ScreenRect(0, 0, 0, 0) };
        _probe.Windows[7] = () => App("Edge", processId: 200) with { ControlType = TreeConfig.Pane };

        var scan = await Catalog().ScanAsync(default);

        Assert.Equal(["Notepad", "Minimized", "Edge"], scan.Windows.Select(w => w.Name));
        Assert.Equal([0, 5, 6], scan.Windows.Select(w => w.Depth));
        Assert.Equal(WindowStatus.Minimized, scan.Windows[1].Status);
        Assert.True(scan.Windows[2].IsBrowser);
        Assert.Equal(7, scan.Candidates.Count);
    }

    [Fact]
    public async Task AProbeTimeoutBlocksTheWindowForLaterScans()
    {
        _system.TopLevel.Add(1);
        _probe.Windows[1] = () => throw new UiaTimeoutException("hung");
        await Catalog().ScanAsync(default);
        await Catalog().ScanAsync(default);
        Assert.Equal([1L], _probe.Probed);
        Assert.True(_blocks.IsBlocked(1, 100));
    }

    [Theory]
    [InlineData(0x80040201u)] // UIA_E_ELEMENTNOTAVAILABLE: the window is gone
    [InlineData(0x80131505u)] // UIA_E_TIMEOUT: UIA's own timeout, not the hang breaker's deadline
    [InlineData(0x80004005u)] // E_FAIL
    public async Task OtherProbeFailuresSkipTheWindowWithoutBlockingIt(uint hresult)
    {
        _system.TopLevel.AddRange([1, 2]);
        _probe.Windows[1] = () => throw new COMException("failed", unchecked((int)hresult));
        _probe.Windows[2] = () => App("Notepad");
        var scan = await Catalog().ScanAsync(default);
        Assert.Equal(["Notepad"], scan.Windows.Select(w => w.Name));
        Assert.Equal(0, _blocks.Count);
    }

    [Fact]
    public async Task ACancelledScanProbesNothing()
    {
        _system.TopLevel.Add(1);
        _probe.Windows[1] = () => App("Notepad");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog().ScanAsync(cancellation.Token));
        Assert.Empty(_probe.Probed);
    }

    [Fact]
    public async Task ACancelledProbeEndsTheScanWithoutBlocking()
    {
        _system.TopLevel.AddRange([1, 2]);
        _probe.Windows[1] = () => throw new OperationCanceledException();
        _probe.Windows[2] = () => App("Notepad");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog().ScanAsync(default));
        Assert.Equal([1L], _probe.Probed);
        Assert.Equal(0, _blocks.Count);
    }

    [Fact]
    public async Task TheFocusedWindowIsTheForegroundWindowsRoot()
    {
        _system.TopLevel.AddRange([1, 2]);
        _probe.Windows[1] = () => App("Notepad");
        _probe.Windows[2] = () => App("Paint");
        _system.Roots[55] = 2;
        _system.Foreground.Enqueue(55);
        var scan = await Catalog().ScanAsync(default);
        Assert.Same(scan.Windows[1], scan.Active);
    }

    [Fact]
    public async Task AFocusedWindowOutsideTheListIsDescribedWithDepthZero()
    {
        _system.TopLevel.Add(1);
        _probe.Windows[1] = () => App("Notepad");
        _probe.Windows[9] = () => App("Tool") with { CanMaximize = false };
        _system.Foreground.Enqueue(9);
        var active = (await Catalog().ScanAsync(default)).Active;
        Assert.Equal(("Tool", 0, 9L), (active!.Name, active.Depth, active.Handle));
    }

    [Fact]
    public async Task AFailedReadOfTheFocusedWindowIsRetried()
    {
        var reads = 0;
        _probe.Windows[9] = () => ++reads == 1 ? throw new InvalidOperationException("busy") : App("Tool");
        _system.Foreground.Enqueue(9);
        _system.Foreground.Enqueue(9);
        var active = (await Catalog().ScanAsync(default)).Active;
        Assert.Equal("Tool", active!.Name);
        Assert.Equal([9L, 9L], _probe.Probed);
        Assert.Equal([WindowCatalog.ActiveWindowRetryDelay], _delays);
    }

    [Theory]
    [InlineData(false)] // the hang breaker's deadline: blocked
    [InlineData(true)]  // UIA's own timeout
    public async Task AFocusedWindowThatTimesOutIsNotRetried(bool comTimeout)
    {
        _probe.Windows[9] = () => throw (comTimeout ? new COMException("timeout", unchecked((int)0x80131505)) : new UiaTimeoutException("hung"));
        _system.Foreground.Enqueue(9);
        _system.Foreground.Enqueue(9);
        Assert.Null((await Catalog().ScanAsync(default)).Active);
        Assert.Equal([9L], _probe.Probed);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task ABlockedFocusedWindowIsNotRetried()
    {
        _blocks.Block(9, 100);
        _system.Foreground.Enqueue(9);
        _system.Foreground.Enqueue(9);
        Assert.Null((await Catalog().ScanAsync(default)).Active);
        Assert.Empty(_probe.Probed);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task TheDesktopIsNeverTheFocusedWindow()
    {
        _system.Classes[90] = "Progman";
        _system.Foreground.Enqueue(90);
        Assert.Null((await Catalog().ScanAsync(default)).Active);
    }

    [Fact]
    public async Task NoForegroundWindowIsRetriedThreeTimes()
    {
        Assert.Null((await Catalog().ScanAsync(default)).Active);
        Assert.Equal([WindowCatalog.ActiveWindowRetryDelay, WindowCatalog.ActiveWindowRetryDelay, WindowCatalog.ActiveWindowRetryDelay], _delays);
    }
}
