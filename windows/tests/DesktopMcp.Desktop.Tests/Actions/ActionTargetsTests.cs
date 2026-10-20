using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Desktop.Tests.Tree;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tests.Actions;

public class ActionTargetsTests
{
    [Fact]
    public async Task AnEmptyStoreAsksForASnapshot() =>
        Assert.Equal("Desktop state is empty. Please call Snapshot first.",
            (await Assert.ThrowsAsync<ArgumentException>(() => new TargetsRig(snapshot: null).Targets.LabelAsync(0, default))).Message);

    [Fact]
    public async Task LabelsCountInteractiveThenScrollable()
    {
        var rig = new TargetsRig(interactive: 2, scrollable: 1);
        Assert.Equal(rig.Scrollable[0].Center, (await rig.Targets.LabelAsync(2, default)).Point);
        Assert.Equal("Failed to find element with label 3: Label 3 out of range",
            (await Assert.ThrowsAsync<ArgumentException>(() => rig.Targets.LabelAsync(3, default))).Message);
        Assert.Equal("Failed to find element with label -1: Label -1 out of range",
            (await Assert.ThrowsAsync<ArgumentException>(() => rig.Targets.LabelAsync(-1, default))).Message);
    }

    [Fact]
    public async Task TheLocatedPointIsReported()
    {
        var rig = new TargetsRig(interactive: 1, locate: new LocateResult(null, new ScreenPoint(7, 8), null));
        Assert.Equal(new ActionTarget(new(7, 8), rig.Interactive[0].Identity!.Window, "Notepad"), await rig.Targets.LabelAsync(0, default));
    }

    [Theory]
    [InlineData(LocateFailure.Stale, "Label 0 is stale: button \"OK\" is no longer in Notepad. Call Snapshot again to get current labels.")]
    [InlineData(LocateFailure.Disabled, "Label 0 (button \"OK\") is disabled.")]
    [InlineData(LocateFailure.OffScreen, "Label 0 (button \"OK\") is off screen and could not be scrolled into view. Call Snapshot again.")]
    [InlineData(LocateFailure.NotResponding, "Notepad is not responding; no input was sent.")]
    public async Task LocateFailuresHaveSpecificTexts(LocateFailure failure, string text)
    {
        var rig = new TargetsRig(interactive: 1, locate: new LocateResult(null, default, failure));
        Assert.Equal(text, (await Assert.ThrowsAsync<DesktopException>(() => rig.Targets.LabelAsync(0, default))).Message);
    }

    [Fact]
    public async Task TheLocateDeadlineIsNotResponding()
    {
        var rig = new TargetsRig(interactive: 1, locateThrows: new UiaTimeoutException("UI Automation label lookup timed out"));
        Assert.Equal("Notepad is not responding; no input was sent.",
            (await Assert.ThrowsAsync<DesktopException>(() => rig.Targets.LabelAsync(0, default))).Message);
    }

    [Theory]
    [InlineData((int)WindowResponse.Hung)]
    [InlineData((int)WindowResponse.NotResponding)]
    public async Task ALocateTimeoutBlocksAWindowThatAlsoFailsTheResponseCheck(int response)
    {
        var rig = new TargetsRig(interactive: 1, locateThrows: new UiaTimeoutException("UI Automation label lookup timed out"));
        rig.Windows.Responses[TargetsRig.NotepadWindow] = (WindowResponse)response;
        await Assert.ThrowsAsync<DesktopException>(() => rig.Targets.LabelAsync(0, default));
        Assert.True(rig.Blocks.IsBlocked(TargetsRig.NotepadWindow, 0));
        Assert.True(rig.Blocks.IsBlocked(0, TargetsRig.NotepadProcess));
    }

    [Fact]
    public async Task ALocateTimeoutOfASlowButRespondingWindowDoesNotBlockIt()
    {
        var rig = new TargetsRig(interactive: 1, locateThrows: new UiaTimeoutException("UI Automation label lookup timed out"));
        Assert.Equal("Notepad is not responding; no input was sent.",
            (await Assert.ThrowsAsync<DesktopException>(() => rig.Targets.LabelAsync(0, default))).Message);
        Assert.False(rig.Blocks.IsBlocked(TargetsRig.NotepadWindow, TargetsRig.NotepadProcess));
    }

    [Fact]
    public async Task ABlockedWindowMakesNoLocateCall()
    {
        var rig = new TargetsRig(interactive: 1);
        rig.Blocks.Block(TargetsRig.NotepadWindow, 0);
        Assert.Equal("Notepad is not responding; no input was sent.",
            (await Assert.ThrowsAsync<DesktopException>(() => rig.Targets.LabelAsync(0, default))).Message);
        Assert.Equal(0, rig.Locates);
    }

    [Fact]
    public async Task ABlockedProcessMakesNoLocateCall()
    {
        var rig = new TargetsRig(interactive: 1);
        rig.Blocks.Block(999, TargetsRig.NotepadProcess);
        await Assert.ThrowsAsync<DesktopException>(() => rig.Targets.LabelAsync(0, default));
        Assert.Equal(0, rig.Locates);
    }

    [Fact]
    public void TheInternalConstructorRejectsNullSeams()
    {
        var store = new DesktopStateStore();
        var windows = new FakeWindowSystem();
        var blocks = new HangBlockList(TimeProvider.System, HangBlockList.DefaultDuration);
        Func<ElementIdentity, ScreenPoint, CancellationToken, Task<LocateResult>> locate = (_, p, _) => Task.FromResult(new LocateResult(null, p, null));
        Func<ScreenRect> screen = () => default;
        Func<int, bool> higher = _ => false;
        Action session = () => { };
        Assert.Throws<ArgumentNullException>(() => new ActionTargets(null!, locate, windows, screen, higher, blocks, session));
        Assert.Throws<ArgumentNullException>(() => new ActionTargets(store, null!, windows, screen, higher, blocks, session));
        Assert.Throws<ArgumentNullException>(() => new ActionTargets(store, locate, null!, screen, higher, blocks, session));
        Assert.Throws<ArgumentNullException>(() => new ActionTargets(store, locate, windows, null!, higher, blocks, session));
        Assert.Throws<ArgumentNullException>(() => new ActionTargets(store, locate, windows, screen, null!, blocks, session));
        Assert.Throws<ArgumentNullException>(() => new ActionTargets(store, locate, windows, screen, higher, null!, session));
        Assert.Throws<ArgumentNullException>(() => new ActionTargets(store, locate, windows, screen, higher, blocks, null!));
    }

    [Fact]
    public async Task ControlCharactersInTheElementTextsAreEscaped()
    {
        var rig = new TargetsRig(interactive: 1, name: "O\nK", locate: new LocateResult(null, default, LocateFailure.Disabled));
        Assert.Equal("Label 0 (button \"O\\nK\") is disabled.",
            (await Assert.ThrowsAsync<DesktopException>(() => rig.Targets.LabelAsync(0, default))).Message);
    }

    [Fact]
    public async Task AWordLabelUsesTheStoredCentreWithoutALookup()
    {
        var rig = new TargetsRig(interactive: 1, identity: false);
        Assert.Equal(rig.Interactive[0].Center, (await rig.Targets.LabelAsync(0, default)).Point);
        Assert.Equal(0, rig.Locates);
    }

    [Fact]
    public async Task ALocatedPointOutsideTheDesktopIsRejected() =>
        Assert.Equal("Coordinates (5000,5) are outside the desktop bounds x=0..1919, y=0..1079",
            (await Assert.ThrowsAsync<ArgumentException>(() =>
                new TargetsRig(interactive: 1, locate: new LocateResult(null, new ScreenPoint(5000, 5), null)).Targets.LabelAsync(0, default))).Message);

    [Fact]
    public async Task AnElevatedWindowUnderALocatedPointIsBlocked() =>
        await Assert.ThrowsAsync<InputBlockedException>(() =>
            new TargetsRig(interactive: 1, elevatedAtPoint: true).Targets.LabelAsync(0, default));

    [Fact]
    public void PointsOutsideTheDesktopAreRejected() =>
        Assert.Equal("Coordinates (5000,5) are outside the desktop bounds x=0..1919, y=0..1079",
            Assert.Throws<ArgumentException>(() => new TargetsRig().Targets.Point(new(5000, 5))).Message);

    [Fact]
    public void APointReportsTheWindowUnderIt() =>
        Assert.Equal(new ActionTarget(new(5, 5), TargetsRig.PointWindow, "Task Manager"), new TargetsRig().Targets.Point(new(5, 5)));

    [Fact]
    public void AnElevatedWindowUnderThePointIsBlockedBeforeInput() =>
        Assert.StartsWith("Input blocked: the target window (Taskmgr.exe) runs elevated",
            Assert.Throws<InputBlockedException>(() => new TargetsRig(elevatedAtPoint: true).Targets.Point(new(5, 5))).Message);

    [Fact]
    public void AnElevatedForegroundWindowIsBlocked() =>
        Assert.StartsWith("Input blocked: the target window (Taskmgr.exe) runs elevated",
            Assert.Throws<InputBlockedException>(() => new TargetsRig(elevatedForeground: true).Targets.CheckForegroundInput()).Message);

    [Fact]
    public void AnOrdinaryForegroundWindowPasses() => new TargetsRig().Targets.CheckForegroundInput();

    [Fact]
    public void CheckLabelsUsesTheCallersWrapper() =>
        Assert.Equal("Failed to resolve labels [0, 9]: Label 9 out of range",
            Assert.Throws<ArgumentException>(() => new TargetsRig(interactive: 1).Targets.CheckLabels([0, 9], "Failed to resolve labels [0, 9]")).Message);

    [Fact]
    public void CheckLabelsOnAnEmptyStoreAsksForASnapshot() =>
        Assert.Equal("Desktop state is empty. Please call Snapshot first.",
            Assert.Throws<ArgumentException>(() => new TargetsRig(snapshot: null).Targets.CheckLabels([0], "Failed to process labels")).Message);

    [Fact]
    public void CheckSessionRunsTheSessionCheck()
    {
        var rig = new TargetsRig(sessionError: "locked");
        Assert.Equal("locked", Assert.Throws<DesktopException>(rig.Targets.CheckSession).Message);
        Assert.Equal(1, rig.SessionChecks);
        Assert.Equal(0, rig.Locates);
    }

    [Fact]
    public void CheckLabelsAcceptsLabelsInRange() => new TargetsRig(interactive: 1, scrollable: 1).Targets.CheckLabels([0, 1], "Failed");
}

/// <summary>
/// A store holding a "Notepad" window's <c>button "OK"</c> elements, a 1920x1080 virtual screen, a counted fake locate,
/// and a window under every point (<see cref="PointWindow"/>, Taskmgr.exe, process 900).
/// </summary>
internal sealed class TargetsRig
{
    public const long NotepadWindow = 100;
    public const long PointWindow = 5;
    public const int NotepadProcess = 300;
    private const int TaskManagerProcess = 900;

    /// <param name="snapshot">Null: nothing is stored (no Snapshot yet).</param>
    public TargetsRig(bool? snapshot = true, int interactive = 0, int scrollable = 0, LocateResult? locate = null, Exception? locateThrows = null,
        bool identity = true, string name = "OK", bool elevatedAtPoint = false, bool elevatedForeground = false, string? sessionError = null)
    {
        Interactive = Enumerable.Range(0, interactive).Select(i => Element(i, identity, name)).ToArray();
        Scrollable = Enumerable.Range(interactive, scrollable).Select(i => Element(i, identity, name)).ToArray();
        var store = new DesktopStateStore();
        if (snapshot is not null)
        {
            store.Replace(new DesktopSnapshot
            {
                ActiveDesktop = VirtualDesktopInfo.Default,
                AllDesktops = [VirtualDesktopInfo.Default],
                Windows = [],
                Displays = [],
                Tree = new TreeSnapshot(Interactive, Scrollable, null, false, 0),
            }, 1);
        }

        var windows = Windows;
        windows.PointWindow = PointWindow;
        windows.Titles[PointWindow] = "Task Manager";
        windows.Processes[PointWindow] = TaskManagerProcess;
        windows.ProcessNames[TaskManagerProcess] = "Taskmgr.exe";
        windows.Processes[NotepadWindow] = NotepadProcess;
        if (elevatedForeground) windows.Foreground.Enqueue(PointWindow);
        var elevated = elevatedAtPoint || elevatedForeground;

        Targets = new ActionTargets(store, (element, center, _) =>
        {
            Locates++;
            if (locateThrows is not null) throw locateThrows;
            return Task.FromResult(locate ?? new LocateResult(null, center, null));
        }, windows, () => new ScreenRect(0, 0, 1920, 1080), processId => elevated && processId == TaskManagerProcess, Blocks, () =>
        {
            SessionChecks++;
            if (sessionError is not null) throw new DesktopException(sessionError);
        });
    }

    public UiElement[] Interactive { get; }
    public UiElement[] Scrollable { get; }
    public ActionTargets Targets { get; }
    public int Locates { get; private set; }
    public int SessionChecks { get; private set; }
    public FakeWindowSystem Windows { get; } = new();
    public HangBlockList Blocks { get; } = new(TimeProvider.System, HangBlockList.DefaultDuration);

    private static UiElement Element(int i, bool identity, string name) =>
        new(i, name, "Button", "Notepad", new ScreenRect(10 + 50 * i, 10, 50 + 50 * i, 30), new ScreenPoint(30 + 50 * i, 20), UiMetadata.Empty)
        {
            Identity = identity ? new ElementIdentity(NotepadWindow, $"1.{i}", TreeConfig.Button, "ok", name, null) : null,
        };
}
