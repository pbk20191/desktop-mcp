using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Tests.Tree;

public class TreeServiceTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);

    /// <summary>Each window yields one button named after it, unless a failure is queued for it.</summary>
    private sealed class FakeWalker : IWindowWalker
    {
        public List<long> Walked { get; } = [];
        public Dictionary<long, Queue<Exception>> Failures { get; } = [];

        /// <summary>Runs before the failure is thrown: what a walk did before it failed or was abandoned.</summary>
        public Action<ElementBudget, Func<int>>? BeforeFailure { get; set; }

        public Task<WindowWalk> WalkAsync(long window, bool isBrowser, ScreenRect screen, ElementBudget budget, Func<int> nextId, CancellationToken cancellationToken)
        {
            Walked.Add(window);
            if (Failures.TryGetValue(window, out var failures) && failures.Count > 0)
            {
                BeforeFailure?.Invoke(budget, nextId);
                throw failures.Dequeue();
            }
            var walk = new WindowWalk(new SemanticNode(SemanticKind.Window, "Window", $"W{window}"));
            budget.TryConsume();
            var element = new UiElement(nextId(), $"B{window}{(isBrowser ? " (browser)" : "")}", "button", $"W{window}", new ScreenRect(0, 0, 10, 10), new ScreenPoint(5, 5), UiMetadata.Empty);
            walk.Interactive.Add(element);
            walk.Window.Children.Add(new SemanticNode(SemanticKind.Interactive, "button", element.Name) { ElementId = element.Id });
            return Task.FromResult(walk);
        }
    }

    private readonly FakeWindowSystem _system = new();
    private readonly FakeWalker _walker = new();
    private readonly HangBlockList _blocks = new(new ManualTime(), HangBlockList.DefaultDuration);
    private readonly Dictionary<string, string> _environment = [];
    private readonly List<TimeSpan> _delays = [];

    private TreeService Service() => new(_system, _walker, _blocks, name => _environment.GetValueOrDefault(name),
        (delay, _) => { _delays.Add(delay); return Task.CompletedTask; });

    [Fact]
    public async Task TheActiveWindowIsWalkedFirstAndLabelsFollowTheWalkOrder()
    {
        var tree = await Service().CaptureAsync(2, [1, 3], Screen, default);
        Assert.Equal([2L, 1L, 3L], _walker.Walked);
        Assert.Equal(["B2", "B1", "B3"], tree.Interactive.Select(e => e.Name));
        Assert.Equal([0, 1, 2], tree.Interactive.Select(e => e.Id));
        Assert.Equal(["W2", "W1", "W3"], tree.Root!.Children.Select(c => c.Name));
    }

    [Fact]
    public async Task ExcludedBlockedAndDesktopWindowsAreSkippedBeforeAnyUiaCall()
    {
        _environment[SnapshotSettings.ExcludeProcessesVariable] = "Secret.exe";
        _system.Processes[1] = 300;
        _system.ProcessNames[300] = "secret.EXE";
        _system.Classes[3] = "Progman";
        _system.Processes[4] = 400;
        _blocks.Block(99, 400);
        _system.ProcessNames[100] = "chrome.exe";

        var tree = await Service().CaptureAsync(2, [1, 3, 4], Screen, default);

        Assert.Equal([2L], _walker.Walked);
        Assert.Equal("B2 (browser)", Assert.Single(tree.Interactive).Name);
    }

    [Fact]
    public async Task TheDesktopIsWalkedWhenNothingIsFocused()
    {
        _system.Classes[3] = "Progman";
        await Service().CaptureAsync(null, [3], Screen, default);
        Assert.Equal([3L], _walker.Walked);
    }

    [Fact]
    public async Task FailuresAreRetriedAfterHalfASecondThenOneSecond()
    {
        _walker.Failures[1] = new Queue<Exception>([new InvalidOperationException("busy"), new InvalidOperationException("busy")]);
        var tree = await Service().CaptureAsync(null, [1], Screen, default);
        Assert.Equal([TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)], _delays);
        Assert.Single(tree.Interactive);
    }

    [Fact]
    public async Task AWindowThatKeepsFailingIsLeftOutAfterThreeRetries()
    {
        _walker.Failures[1] = new Queue<Exception>(Enumerable.Range(0, 4).Select(_ => new InvalidOperationException("busy")));
        var tree = await Service().CaptureAsync(null, [1, 2], Screen, default);
        Assert.Equal([1L, 1L, 1L, 1L, 2L], _walker.Walked);
        Assert.Equal(["B2"], tree.Interactive.Select(e => e.Name));
    }

    [Fact]
    public async Task ATimeoutBlocksTheWindowWithoutRetrying()
    {
        _walker.Failures[1] = new Queue<Exception>([new UiaTimeoutException("hung")]);
        await Service().CaptureAsync(null, [1], Screen, default);
        Assert.Equal([1L], _walker.Walked);
        Assert.Empty(_delays);
        Assert.True(_blocks.IsBlocked(1, 100));
    }

    [Fact]
    public async Task AComTimeoutIsLeftOutWithoutRetryingOrBlocking()
    {
        _walker.Failures[1] = new Queue<Exception>([new COMException("timeout", unchecked((int)0x80131505))]); // UIA_E_TIMEOUT
        var tree = await Service().CaptureAsync(null, [1, 2], Screen, default);
        Assert.Equal([1L, 2L], _walker.Walked);
        Assert.Empty(_delays);
        Assert.Equal(0, _blocks.Count);
        Assert.Equal(["B2"], tree.Interactive.Select(e => e.Name));
    }

    [Fact]
    public async Task ADeadElementIsRetriedWhileTheWindowExists()
    {
        _walker.Failures[1] = new Queue<Exception>([new COMException("gone", unchecked((int)0x80040201))]); // UIA_E_ELEMENTNOTAVAILABLE
        var tree = await Service().CaptureAsync(null, [1], Screen, default);
        Assert.Equal([1L, 1L], _walker.Walked);
        Assert.Equal([TimeSpan.FromSeconds(0.5)], _delays);
        Assert.Equal(["B1"], tree.Interactive.Select(e => e.Name));
        Assert.Equal(0, _blocks.Count);
    }

    [Fact]
    public async Task AClosedWindowIsDroppedWithoutWaiting()
    {
        _walker.Failures[1] = new Queue<Exception>([new COMException("gone", unchecked((int)0x80040201))]);
        _system.Gone.Add(1);
        var tree = await Service().CaptureAsync(null, [1, 2], Screen, default);
        Assert.Equal([1L, 2L], _walker.Walked);
        Assert.Empty(_delays);
        Assert.Equal(0, _blocks.Count);
        Assert.Equal(["B2"], tree.Interactive.Select(e => e.Name));
    }

    [Fact]
    public async Task ATimeoutSkipsTheOtherWindowsOfTheSameProcess()
    {
        _system.Processes[1] = 500;
        _system.Processes[2] = 500;
        _walker.Failures[1] = new Queue<Exception>([new UiaTimeoutException("hung")]);
        var tree = await Service().CaptureAsync(null, [1, 2, 3], Screen, default);
        Assert.Equal([1L, 3L], _walker.Walked);
        Assert.Equal(["B3"], tree.Interactive.Select(e => e.Name));
    }

    [Fact]
    public async Task NoWindowIsWalkedTwice()
    {
        var tree = await Service().CaptureAsync(2, [1, 2, 3, 1], Screen, default);
        Assert.Equal([2L, 1L, 3L], _walker.Walked);
        Assert.Equal(["W2", "W1", "W3"], tree.Root!.Children.Select(c => c.Name));
    }

    [Fact]
    public async Task NothingFromAWalkThatTimedOutReachesTheTree()
    {
        _walker.Failures[1] = new Queue<Exception>([new UiaTimeoutException("hung")]);
        _system.Processes[2] = 200; // another process: the timeout blocks process 100
        _walker.BeforeFailure = (budget, nextId) =>
        {
            budget.TryConsume();
            nextId();
        };
        var tree = await Service().CaptureAsync(null, [1, 2], Screen, default);
        Assert.Equal(["B2"], tree.Interactive.Select(e => e.Name));
        Assert.Equal(["W2"], tree.Root!.Children.Select(c => c.Name));
    }

    [Fact]
    public async Task IdsStayUniqueWhenAnAbandonedWalkStillTakesThem()
    {
        _walker.Failures[1] = new Queue<Exception>([new UiaTimeoutException("hung")]);
        _system.Processes[2] = 200; // another process: the timeout blocks process 100
        // An abandoned walk keeps numbering on its own thread while the capture goes on: the counter must be atomic.
        _walker.BeforeFailure = (_, nextId) => Parallel.For(0, 10_000, _ => nextId());
        var tree = await Service().CaptureAsync(null, [1, 2], Screen, default);
        Assert.Equal(10_000, Assert.Single(tree.Interactive).Id);
    }

    [Fact]
    public async Task ACancelledCaptureWalksNothingAndBlocksNothing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().CaptureAsync(null, [1, 2], Screen, cancellation.Token));
        Assert.Empty(_walker.Walked);
        Assert.Equal(0, _blocks.Count);
    }

    [Fact]
    public async Task ACancelledWalkEndsTheCaptureWithoutRetryingOrBlocking()
    {
        _walker.Failures[1] = new Queue<Exception>([new OperationCanceledException()]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().CaptureAsync(null, [1, 2], Screen, default));
        Assert.Equal([1L], _walker.Walked);
        Assert.Empty(_delays);
        Assert.Equal(0, _blocks.Count);
    }

    [Fact]
    public async Task TheBudgetStopsTheCaptureAndTheTreeIsMarkedTruncated()
    {
        _environment[SnapshotSettings.MaxTreeElementsVariable] = "2";
        var tree = await Service().CaptureAsync(null, [1, 2, 3], Screen, default);
        Assert.Equal([1L, 2L], _walker.Walked);
        Assert.True(tree.Truncated);
        Assert.Equal(2, tree.ElementLimit);
    }

    [Fact]
    public async Task ATimedOutWalkConsumesNothingFromTheCaptureBudget()
    {
        _environment[SnapshotSettings.MaxTreeElementsVariable] = "2";
        _walker.Failures[1] = new Queue<Exception>([new UiaTimeoutException("hung")]);
        _system.Processes[2] = 200; // another process: the timeout blocks process 100
        _walker.BeforeFailure = (budget, _) => budget.TryConsume(2);
        var tree = await Service().CaptureAsync(null, [1, 2], Screen, default);
        Assert.Equal([1L, 2L], _walker.Walked);
        Assert.Equal(["B2"], tree.Interactive.Select(e => e.Name));
        Assert.False(tree.Truncated);
    }

    [Fact]
    public async Task AFailedAttemptsElementsAreNotCountedButTheSuccessfulOnesAre()
    {
        _environment[SnapshotSettings.MaxTreeElementsVariable] = "2";
        _walker.Failures[1] = new Queue<Exception>([new InvalidOperationException("busy")]);
        _walker.BeforeFailure = (budget, _) => budget.TryConsume(2);
        var tree = await Service().CaptureAsync(null, [1, 2, 3], Screen, default);
        // The retry of window 1 and window 2 consume one element each: the capture stops before window 3.
        Assert.Equal([1L, 1L, 2L], _walker.Walked);
        Assert.Equal(["B1", "B2"], tree.Interactive.Select(e => e.Name));
        Assert.True(tree.Truncated);
    }

    [Fact]
    public void AssembleDropsNodesOfDiscardedElementsAndEmptyContainers()
    {
        var walk = new WindowWalk(new SemanticNode(SemanticKind.Window, "Window", "App"));
        var kept = new UiElement(1, "Kept", "button", "App", default, default, UiMetadata.Empty);
        walk.Interactive.Add(kept);
        var group = new SemanticNode(SemanticKind.Structural, "group", "Old");
        group.Children.Add(new SemanticNode(SemanticKind.Interactive, "button", "Discarded") { ElementId = 0 });
        walk.Window.Children.AddRange([group, new SemanticNode(SemanticKind.Interactive, "button", "Kept") { ElementId = 1 }]);

        var tree = TreeService.Assemble([walk], new ElementBudget(500));

        Assert.Equal(["Kept"], Assert.Single(tree.Root!.Children).Children.Select(c => c.Name));
        Assert.False(tree.Truncated);
    }
}

[Trait("Category", "Desktop")]
public sealed class WindowCatalogDesktopTests : IDisposable
{
    private readonly TestWindow _window = new();
    private readonly UiaWorker<UiaSession> _worker = new(UiaSession.Create);
    private readonly HangBlockList _blocks = new(TimeProvider.System, HangBlockList.DefaultDuration);

    public void Dispose()
    {
        _worker.Dispose();
        _window.Dispose();
    }

    [Fact]
    public async Task ListsTheTestWindowAsANormalApplicationWindow()
    {
        // ownProcessId -1: the test window lives in this process, which the server would otherwise skip.
        var catalog = new WindowCatalog(new Win32WindowSystem(), new UiaWindowProbe(_worker), new VirtualDesktopService(), _blocks, -1, Task.Delay);
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var scan = await catalog.ScanAsync(limit.Token);
        var window = Assert.Single(scan.Windows, w => w.Handle == _window.Handle);
        Assert.Equal((_window.Title, WindowStatus.Normal, Environment.ProcessId), (window.Name, window.Status, window.ProcessId));
        Assert.Equal(scan.Candidates.ToList().IndexOf(_window.Handle), window.Depth);
    }

    [Fact]
    public async Task WalksTheTestWindowIntoTheTree()
    {
        var service = new TreeService(new Win32WindowSystem(), new UiaWindowWalker(_worker), _blocks, _ => null, Task.Delay);
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var tree = await service.CaptureAsync(null, [_window.Handle], new Win32Displays().GetVirtualScreen(), limit.Token);
        var window = Assert.Single(tree.Root!.Children);
        Assert.Equal(_window.Title, window.Name);
        Assert.Contains(tree.Interactive, e => e.Name == TestWindow.ButtonText); // ControlType is the localized name ("button", "단추", ...)
        Assert.Contains(tree.Interactive, e => e.ControlType == "Word" && e.Name == "world");
    }
}
