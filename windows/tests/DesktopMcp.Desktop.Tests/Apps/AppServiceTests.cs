using DesktopMcp.Core.Control;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Apps;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.WindowControl;

namespace DesktopMcp.Desktop.Tests.Apps;

/// <summary>
/// An <see cref="AppService"/> over a fake window scan (the windows in z-order, the active window), a fake foreground
/// (records the handle it switches to) and a resize seam (records the rectangle). Every window has <c>bounds</c>.
/// </summary>
internal sealed class AppRig
{
    public AppRig(
        IReadOnlyList<(string Name, long Handle, bool minimized)> windows, bool switchSucceeds = true, ScreenRect bounds = default,
        (string Name, long Handle, bool minimized)? active = null, bool maximized = false)
    {
        WindowInfo Info((string Name, long Handle, bool Minimized) window, int depth) => new(window.Name, depth,
            window.Minimized ? WindowStatus.Minimized : maximized ? WindowStatus.Maximized : WindowStatus.Normal, bounds, window.Handle, 100, false);
        var list = windows.Select((window, depth) => Info(window, depth)).ToArray();
        var focused = active is { } a ? Info(a, 0) : null;
        Minimized = list.Concat(focused is null ? [] : [focused]).Where(w => w.Status == WindowStatus.Minimized).Select(w => w.Handle).ToHashSet();
        Scanner = new FakeScanner(new WindowScan(list.Select(w => w.Handle).ToArray(), list, focused));
        Foreground = new FakeForeground(this, switchSucceeds);
        Service = new AppService(Scanner, Foreground, (window, name, target, _) =>
        {
            if (Throws is { } thrown) throw thrown;
            if (ResizeError is { } error) throw new DesktopException(error);
            ResizedTo = target;
            return Task.CompletedTask;
        }, new LaunchRig.FakeCatalog([]), new LaunchRig.FakeLauncher(null), new Tree.FakeWindowSystem(), (_, _) => Task.CompletedTask, TimeProvider.System);
    }

    public AppService Service { get; }
    public FakeScanner Scanner { get; }
    public FakeForeground Foreground { get; }
    public long? SwitchedTo { get; private set; }
    public ScreenRect? ResizedTo { get; private set; }
    public HashSet<long> Minimized { get; }

    /// <summary>When set, the resize seam throws a <see cref="DesktopException"/> with this text.</summary>
    public string? ResizeError { get; set; }

    /// <summary>When set, the switch throws a <see cref="DesktopException"/> with this text.</summary>
    public string? SwitchError { get; set; }

    /// <summary>When set, the switch and the resize seam throw this exception.</summary>
    public Exception? Throws { get; set; }

    internal sealed class FakeScanner(WindowScan scan) : IWindowScanner
    {
        public List<string> Calls { get; } = [];

        public Task<WindowScan> ScanAsync(CancellationToken cancellationToken)
        {
            Calls.Add("Scan");
            return Task.FromResult(scan);
        }

        public Task<WindowInfo?> ScanActiveAsync(CancellationToken cancellationToken)
        {
            Calls.Add("ScanActive");
            return Task.FromResult(scan.Active);
        }
    }

    internal sealed class FakeForeground(AppRig rig, bool succeeds) : IForeground
    {
        public Task<ForegroundResult> SwitchToAsync(long window, CancellationToken ct)
        {
            if (rig.Throws is { } thrown) throw thrown;
            if (rig.SwitchError is { } error) throw new DesktopException(error);
            rig.SwitchedTo = window;
            return Task.FromResult(new ForegroundResult(succeeds, rig.Minimized.Contains(window)));
        }

        public Task EnsureKeyboardTargetAsync(long window, string windowName, CancellationToken ct) => throw new NotSupportedException();
    }
}

public class AppServiceTests
{
    [Fact]
    public async Task SwitchUsesUpstreamsTexts()
    {
        var rig = new AppRig(windows: [("Untitled - Notepad", 10, minimized: true)], switchSucceeds: true);
        Assert.Equal(new AppResult("Restored Untitled - Notepad from minimized and switched to it.", false), await rig.Service.SwitchAsync("notepad", default));
        Assert.Equal(new AppResult("Application Calculatorx not found.", true), await rig.Service.SwitchAsync("calculatorx", default));
    }

    [Fact]
    public async Task SwitchToAVisibleWindowTitlesItsName() =>
        Assert.Equal(new AppResult("Switched to Microsoft Edge window.", false),
            await new AppRig(windows: [("microsoft edge", 10, false)]).Service.SwitchAsync("edge", default));

    [Fact]
    public async Task SwitchFailureIsAnError() =>
        Assert.Equal(new AppResult("Failed to bring Untitled - Notepad to the foreground within 1 second.", true),
            await new AppRig(windows: [("Untitled - Notepad", 10, false)], switchSucceeds: false).Service.SwitchAsync("notepad", default));

    public static TheoryData<Exception> Interruptions() =>
    [
        new ControlRejectedException(ControlCodes.ControlPreempted, new ControlStatus(ControlState.User, 1, 0, true, "relaxed", 10), 0),
        new InputUnavailableException("blocked"),
    ];

    [Theory]
    [MemberData(nameof(Interruptions))]
    public async Task SwitchDoesNotSwallowAControlInterruption(Exception interruption)
    {
        var rig = new AppRig(windows: [("Notepad", 10, false)]) { Throws = interruption };
        Assert.Same(interruption, await Assert.ThrowsAnyAsync<Exception>(() => rig.Service.SwitchAsync("notepad", default)));
    }

    [Theory]
    [MemberData(nameof(Interruptions))]
    public async Task ResizeDoesNotSwallowAControlInterruption(Exception interruption)
    {
        var rig = new AppRig(windows: [("Notepad", 10, false)]) { Throws = interruption };
        Assert.Same(interruption, await Assert.ThrowsAnyAsync<Exception>(() => rig.Service.ResizeAsync("notepad", null, (1, 1), default)));
    }

    [Fact]
    public async Task SwitchExceptionsAreErrors()
    {
        var rig = new AppRig(windows: [("Notepad", 10, false)]) { SwitchError = "Notepad is not responding" };
        Assert.Equal(new AppResult("Error switching app: Notepad is not responding", true), await rig.Service.SwitchAsync("notepad", default));
    }

    [Fact]
    public async Task SwitchWithNoWindowsSaysSo() =>
        Assert.Equal(new AppResult("No windows found on the desktop.", true), await new AppRig(windows: []).Service.SwitchAsync("notepad", default));

    [Fact]
    public async Task DuplicateNamesPickTheFirstWindow()
    {
        var rig = new AppRig(windows: [("Notepad", 10, false), ("Notepad", 11, false)], switchSucceeds: true);
        await rig.Service.SwitchAsync("notepad", default);
        Assert.Equal(10, rig.SwitchedTo);
    }

    [Fact]
    public async Task TheActiveWindowComesFirst()
    {
        var rig = new AppRig(windows: [("Notepad", 10, false), ("Notepad", 11, false)], active: ("Notepad", 11, false));
        await rig.Service.SwitchAsync("notepad", default);
        Assert.Equal(11, rig.SwitchedTo);
    }

    [Fact]
    public async Task NonAsciiNamesMatch()
    {
        var rig = new AppRig(windows: [("제목 없음 - 메모장", 10, false), ("그림판", 11, false)]);
        Assert.Equal(new AppResult("Switched to 제목 없음 - 메모장 window.", false), await rig.Service.SwitchAsync("메모장", default));
    }

    [Fact]
    public async Task ResizeKeepsMissingValuesFromTheWindow()
    {
        var rig = new AppRig(windows: [("Notepad", 10, false)], bounds: new ScreenRect(100, 50, 900, 650));
        Assert.Equal(new AppResult("Notepad resized to 800x600 at 0,0.", false), await rig.Service.ResizeAsync("notepad", (0, 0), null, default));
        Assert.Equal(new ScreenRect(0, 0, 800, 600), rig.ResizedTo);
        Assert.Equal(new AppResult("Notepad resized to 300x200 at 100,50.", false), await rig.Service.ResizeAsync("notepad", null, (300, 200), default));
        Assert.Equal(new ScreenRect(100, 50, 400, 250), rig.ResizedTo);
    }

    [Fact]
    public async Task ResizeWithoutANameUsesTheCurrentForegroundWindow()
    {
        var rig = new AppRig(windows: [], active: ("Paint", 20, false), bounds: new ScreenRect(0, 0, 10, 10));
        Assert.Equal(new AppResult("Paint resized to 300x200 at 0,0.", false), await rig.Service.ResizeAsync(null, null, (300, 200), default));
        Assert.Equal(["ScanActive"], rig.Scanner.Calls);
        Assert.Equal(new AppResult("No active window found", false), await new AppRig(windows: []).Service.ResizeAsync(null, null, null, default));
    }

    [Fact]
    public async Task ResizeOfAnUnknownNameIsANormalResult() =>
        Assert.Equal(new AppResult("Application Calculatorx not found.", false),
            await new AppRig(windows: [("Notepad", 10, false)]).Service.ResizeAsync("calculatorx", null, null, default));

    [Fact]
    public async Task ResizeRefusesMaximisedWindows() =>
        Assert.Equal(new AppResult("Notepad is maximized", false),
            await new AppRig(windows: [("Notepad", 10, false)], maximized: true).Service.ResizeAsync("notepad", null, (1, 1), default));

    [Fact]
    public async Task ResizeRefusesMinimisedWindows() =>
        Assert.Equal(new AppResult("Notepad is minimized", false),
            await new AppRig(windows: [("Notepad", 10, true)]).Service.ResizeAsync("notepad", null, (1, 1), default));

    [Fact]
    public async Task ResizeFailuresAreNormalResults()
    {
        var rig = new AppRig(windows: [("Notepad", 10, false)]) { ResizeError = "Notepad is not responding" };
        Assert.Equal(new AppResult("Notepad is not responding", false), await rig.Service.ResizeAsync("notepad", null, (1, 1), default));
    }
}
