using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Capture;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Tests.Snapshots;

public class SnapshotServiceTests
{
    private static readonly DisplayInfo Left = new(1, @"\\.\DISPLAY2", new ScreenRect(-1920, 0, 0, 1080), false, null, 96, 1.0, "landscape");
    private static readonly DisplayInfo Main = new(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1600), true, null, 144, 1.5, "landscape");
    private static readonly ScreenRect Screen = new(-1920, 0, 2560, 1600);

    private static readonly WindowInfo Notepad = new("Notepad", 1, WindowStatus.Normal, new ScreenRect(100, 100, 900, 700), 11, 50, false);
    private static readonly WindowInfo Paint = new("Paint", 2, WindowStatus.Normal, new ScreenRect(-1500, 100, -500, 700), 12, 60, false);

    /// <summary>How long a test waits for a bounded failure before it calls the snapshot hung.</summary>
    private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(200);

    private sealed class FakeDisplays : IDisplayProvider
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => [Main, Left];
        public ScreenRect GetVirtualScreen() => Screen;
    }

    private sealed class FakeWindows(WindowScan scan) : IWindowScanner
    {
        public int Scans { get; private set; }
        public Task<WindowScan> ScanAsync(CancellationToken cancellationToken)
        {
            Scans++;
            return Task.FromResult(scan);
        }
    }

    private sealed class FakeTree : ITreeCapture
    {
        public (long? Active, long[] Others)? Request { get; private set; }

        public Task<TreeSnapshot> CaptureAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, CancellationToken cancellationToken)
        {
            Request = (active, others.ToArray());
            var near = new UiElement(0, "Save", "button", "Notepad", new ScreenRect(110, 110, 150, 130), new ScreenPoint(130, 120), UiMetadata.Empty);
            var far = new UiElement(1, "Brush", "button", "Paint", new ScreenRect(-1400, 110, -1300, 130), new ScreenPoint(-1350, 120), UiMetadata.Empty);
            var root = new SemanticNode(SemanticKind.Desktop, "Desktop", "Desktop");
            return Task.FromResult(new TreeSnapshot([near, far], [], root, false, 500));
        }
    }

    private sealed class FakeDesktops : IVirtualDesktops
    {
        public IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows) => windows;
        public (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops() =>
            (new VirtualDesktopInfo("{A}", "Desktop 1"), [new VirtualDesktopInfo("{A}", "Desktop 1")]);
    }

    private sealed class FakeCapture : IScreenCapture
    {
        public ScreenRect? Area { get; private set; }
        public Task<CapturedImage> CaptureAsync(ScreenRect area, IReadOnlyList<ScreenRect> monitors, CancellationToken cancellationToken)
        {
            Area = area;
            return Task.FromResult(new CapturedImage(area.Width, area.Height, []));
        }
    }

    private sealed class FakeRenderer : IImageRenderer
    {
        public RenderOptions? Options { get; private set; }
        public Task<byte[]> RenderPngAsync(CapturedImage image, RenderOptions options)
        {
            Options = options;
            return Task.FromResult<byte[]>([0x89, 0x50]);
        }
    }

    /// <summary>Blocks its caller's thread (ignoring cancellation) until the test releases it.</summary>
    private sealed class HungDesktops(ManualResetEventSlim release) : IVirtualDesktops
    {
        public IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows)
        {
            release.Wait();
            return windows;
        }

        public (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops()
        {
            release.Wait();
            return (new VirtualDesktopInfo("{A}", "Desktop 1"), [new VirtualDesktopInfo("{A}", "Desktop 1")]);
        }
    }

    private sealed class ThrowingDesktops : IVirtualDesktops
    {
        public IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows) => throw new InvalidOperationException("shell");
        public (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops() => throw new InvalidOperationException("shell");
    }

    /// <summary>Never finishes and ignores cancellation, like a WinRT call blocked under NativeAOT.</summary>
    private sealed class HungCapture : IScreenCapture
    {
        public Task<CapturedImage> CaptureAsync(ScreenRect area, IReadOnlyList<ScreenRect> monitors, CancellationToken cancellationToken) =>
            new TaskCompletionSource<CapturedImage>().Task;
    }

    private sealed class HungRenderer : IImageRenderer
    {
        public Task<byte[]> RenderPngAsync(CapturedImage image, RenderOptions options) => new TaskCompletionSource<byte[]>().Task;
    }

    /// <summary>Takes its time, like a walk over many slow windows, then finishes.</summary>
    private sealed class SlowTree(TimeSpan duration) : ITreeCapture
    {
        public async Task<TreeSnapshot> CaptureAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, CancellationToken cancellationToken)
        {
            await Task.Delay(duration, cancellationToken);
            return TreeSnapshot.Empty with { ElementLimit = 500 };
        }
    }

    /// <summary>Waits for the test's signal.</summary>
    private sealed class GatedTree(Task gate) : ITreeCapture
    {
        public async Task<TreeSnapshot> CaptureAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, CancellationToken cancellationToken)
        {
            await gate;
            return TreeSnapshot.Empty;
        }
    }

    /// <summary>Never finishes and ignores cancellation, but keeps the token it was given.</summary>
    private sealed class TokenCapture : IScreenCapture
    {
        public CancellationToken Token { get; private set; }

        public Task<CapturedImage> CaptureAsync(ScreenRect area, IReadOnlyList<ScreenRect> monitors, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            return new TaskCompletionSource<CapturedImage>().Task;
        }
    }

    private sealed class ThrowingCapture(Exception error) : IScreenCapture
    {
        public Task<CapturedImage> CaptureAsync(ScreenRect area, IReadOnlyList<ScreenRect> monitors, CancellationToken cancellationToken) =>
            throw error;
    }

    private sealed class ThrowingRenderer(Exception error) : IImageRenderer
    {
        public Task<byte[]> RenderPngAsync(CapturedImage image, RenderOptions options) => throw error;
    }

    private readonly FakeWindows _windows = new(new WindowScan([11, 12, 13], [Notepad, Paint], Notepad));
    private readonly FakeTree _tree = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeRenderer _renderer = new();
    private readonly Dictionary<string, string> _environment = [];
    private readonly StringWriter _profile = new();

    private SnapshotDependencies Dependencies(Action? ensureSession = null) => new()
    {
        Displays = new FakeDisplays(),
        Windows = _windows,
        Tree = _tree,
        Desktops = new FakeDesktops(),
        Capture = _capture,
        Renderer = _renderer,
        Cursor = () => new ScreenPoint(120, 120),
        EnsureSession = ensureSession ?? (() => { }),
        Environment = name => _environment.GetValueOrDefault(name),
        ProfileLog = _profile,
    };

    private SnapshotService Service(Action? ensureSession = null) => new(Dependencies(ensureSession));

    [Fact]
    public async Task ASnapshotWalksTheFocusedWindowFirstAndTheNonApplicationWindows()
    {
        var snapshot = await Service().CaptureAsync(new SnapshotRequest(), default);
        Assert.Equal(11, _tree.Request!.Value.Active);
        Assert.Equal([13L], _tree.Request.Value.Others);
        Assert.Same(Notepad, snapshot.ActiveWindow);
        Assert.Equal([Paint], snapshot.Windows);
        Assert.Equal(new ScreenPoint(120, 120), snapshot.Cursor);
        Assert.Null(snapshot.Png);
        Assert.Null(snapshot.Backend);
        Assert.Null(_capture.Area);
    }

    [Fact]
    public async Task TheStoreHoldsTheLatestSnapshot()
    {
        var service = Service();
        var first = await service.CaptureAsync(new SnapshotRequest(), default);
        Assert.Same(first, service.State.Latest);
        var second = await service.CaptureAsync(new SnapshotRequest { UseUiTree = false }, default);
        Assert.Same(second, service.State.Latest);
    }

    [Fact]
    public async Task AScreenshotSkipsTheWindowScanAndCapturesTheVirtualScreen()
    {
        var snapshot = await Service().CaptureAsync(new SnapshotRequest { UseVision = true, UseUiTree = false, UseAnnotation = false }, default);
        Assert.Equal(0, _windows.Scans);
        Assert.Null(_tree.Request);
        Assert.Equal(Screen, _capture.Area);
        Assert.Equal(new PixelSize(4480, 1600), snapshot.OriginalSize);
        Assert.Equal(1920.0 / 4480, snapshot.Scale);
        Assert.Equal("wgc", snapshot.Backend);
        Assert.Equal(RenderOptions.Plain(1920.0 / 4480).Scale, _renderer.Options!.Scale);
        Assert.Empty(_renderer.Options.Annotations);
        Assert.Same(TreeSnapshot.Empty, snapshot.Tree);
    }

    [Fact]
    public async Task ARegionFiltersWindowsElementsAndTheCursorAndOffsetsTheAnnotations()
    {
        var request = new SnapshotRequest
        {
            UseVision = true, Region = new RegionArgument([100, 100, 1000, 800], null), WidthReferenceLine = 3, HeightReferenceLine = 2,
        };
        var snapshot = await Service().CaptureAsync(request, default);

        Assert.Equal(new ScreenRect(100, 100, 1000, 800), snapshot.Region);
        Assert.Empty(snapshot.Windows);                     // Paint is outside the region
        Assert.Equal(["Save"], snapshot.Tree.Interactive.Select(e => e.Name));
        var options = _renderer.Options!;
        Assert.Equal(new Annotation(new PixelRect(10, 10, 50, 30), "0"), Assert.Single(options.Annotations));
        Assert.Equal(new PixelPoint(20, 20), options.Cursor);
        Assert.Equal(new GridLines(3, 2), options.Grid);
        Assert.Equal(new ScreenRect(100, 100, 1000, 800), _capture.Area);
    }

    [Fact]
    public async Task ADisplaySelectionCapturesTheUnionAndWalksApplicationWindowsInside()
    {
        var snapshot = await Service().CaptureAsync(new SnapshotRequest { UseVision = true, Displays = [1, 1] }, default);
        Assert.Equal(Left.Bounds, _capture.Area);
        Assert.Equal([1L], snapshot.SelectedDisplays);
        Assert.Null(_tree.Request!.Value.Active);         // Notepad is on the other display
        Assert.Equal([12L, 13L], _tree.Request.Value.Others);
        Assert.Null(snapshot.Cursor);
    }

    [Theory]
    [InlineData(true, false, "use_dom=True requires use_ui_tree=True")]
    [InlineData(true, true, SnapshotService.UseDomUnavailable)]
    public async Task UseDomIsRejected(bool useDom, bool useUiTree, string message)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Service().CaptureAsync(new SnapshotRequest { UseDom = useDom, UseUiTree = useUiTree }, default));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task SelectionErrorsUseUpstreamMessages()
    {
        var invalid = await Assert.ThrowsAsync<ArgumentException>(() => Service().CaptureAsync(new SnapshotRequest { Displays = [5] }, default));
        Assert.Equal("Invalid display index 5. Available displays: 0,1", invalid.Message);
        var region = await Assert.ThrowsAsync<ArgumentException>(() => Service().CaptureAsync(new SnapshotRequest { Region = new RegionArgument(null, "[1,2]") }, default));
        Assert.Equal(SnapshotSelection.RegionShapeMessage, region.Message);
    }

    [Fact]
    public async Task NoInteractiveSessionFailsBeforeAnyDesktopRead()
    {
        var error = await Assert.ThrowsAsync<DesktopException>(() => Service(() => throw new DesktopException("locked")).CaptureAsync(new SnapshotRequest(), default));
        Assert.Equal("locked", error.Message);
        Assert.Equal(0, _windows.Scans);
    }

    [Fact]
    public async Task ProfilingWritesOneLineOfStageTimings()
    {
        _environment[SnapshotSettings.ProfileSnapshotVariable] = "1";
        await Service().CaptureAsync(new SnapshotRequest(), default);
        Assert.StartsWith("Snapshot profile: desktop_context_ms=", _profile.ToString());
        Assert.Contains("use_vision=False use_dom=False use_ui_tree=True use_annotation=True displays=None", _profile.ToString());
    }

    [Fact]
    public async Task TheActiveWindowIsNotWalkedAgainAmongTheOthers()
    {
        // The focused window is not an application window here, so it is a non-application candidate too.
        var dialog = new WindowInfo("Dialog", 0, WindowStatus.Normal, new ScreenRect(200, 200, 400, 300), 13, 70, false);
        var service = new SnapshotService(Dependencies() with { Windows = new FakeWindows(new WindowScan([13, 11, 12], [Notepad, Paint], dialog)) });
        await service.CaptureAsync(new SnapshotRequest(), default);
        Assert.Equal(13, _tree.Request!.Value.Active);
        Assert.Empty(_tree.Request.Value.Others);
    }

    [Fact]
    public async Task AHungScreenCaptureFailsWithinItsDeadline()
    {
        var service = new SnapshotService(Dependencies() with { Capture = new HungCapture() }) { CaptureTimeout = ShortDeadline };
        var error = await Assert.ThrowsAnyAsync<DesktopException>(
            () => service.CaptureAsync(new SnapshotRequest { UseVision = true, UseUiTree = false }, default).WaitAsync(TestBound));
        Assert.Equal("Screen capture failed: it did not finish within 0.2 s.", error.Message);
        Assert.Null(service.State.Latest);
    }

    [Fact]
    public async Task AHungRenderFailsWithinItsDeadline()
    {
        var service = new SnapshotService(Dependencies() with { Renderer = new HungRenderer() }) { RenderTimeout = ShortDeadline };
        var error = await Assert.ThrowsAnyAsync<DesktopException>(
            () => service.CaptureAsync(new SnapshotRequest { UseVision = true }, default).WaitAsync(TestBound));
        Assert.Equal("Image rendering failed: it did not finish within 0.2 s.", error.Message);
    }

    [Fact]
    public async Task TheCaptureDeadlineCancelsTheAbandonedCallsToken()
    {
        var capture = new TokenCapture();
        var service = new SnapshotService(Dependencies() with { Capture = capture }) { CaptureTimeout = ShortDeadline };
        await Assert.ThrowsAnyAsync<DesktopException>(
            () => service.CaptureAsync(new SnapshotRequest { UseVision = true, UseUiTree = false }, default).WaitAsync(TestBound));
        Assert.True(capture.Token.CanBeCanceled);
        Assert.True(capture.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(1, 13)]
    [InlineData(2, 19)]
    public void TheCaptureDeadlineGrowsWithTheMonitors(int monitors, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SnapshotService.CaptureDeadline(monitors));

    [Fact]
    public async Task ASlowTreeWalkIsNotCutOff()
    {
        // No global deadline on the walk (upstream has none): a walk that keeps making progress finishes, however long.
        var snapshot = await new SnapshotService(Dependencies() with { Tree = new SlowTree(TimeSpan.FromSeconds(1)) })
            .CaptureAsync(new SnapshotRequest(), default).WaitAsync(TestBound);
        Assert.Equal(500, snapshot.Tree.ElementLimit);
    }

    [Fact]
    public async Task ASlowerEarlierSnapshotDoesNotReplaceANewerOne()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SnapshotService(Dependencies() with { Tree = new GatedTree(gate.Task) });
        // The earlier snapshot walks the (gated) tree; the later one has no tree, so it finishes first.
        var earlier = service.CaptureAsync(new SnapshotRequest(), default);
        var later = await service.CaptureAsync(new SnapshotRequest { UseUiTree = false }, default).WaitAsync(TestBound);
        Assert.Same(later, service.State.Latest);
        gate.SetResult();
        var slow = await earlier.WaitAsync(TestBound);
        Assert.NotSame(slow, later);
        Assert.Same(later, service.State.Latest);
    }

    [Fact]
    public async Task AHungVirtualDesktopCallFallsBackToTheDefaultDesktop()
    {
        using var release = new ManualResetEventSlim();
        try
        {
            var service = new SnapshotService(Dependencies() with { Desktops = new HungDesktops(release) }) { DesktopsTimeout = ShortDeadline };
            var snapshot = await service.CaptureAsync(new SnapshotRequest(), default).WaitAsync(TestBound);
            Assert.Same(VirtualDesktopInfo.Default, snapshot.ActiveDesktop);
            Assert.Equal([VirtualDesktopInfo.Default], snapshot.AllDesktops);
            Assert.Same(Notepad, snapshot.ActiveWindow);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AVirtualDesktopBugIsNotHidden()
    {
        // VirtualDesktopService fails open on shell errors itself; anything else it throws is a bug and is reported.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SnapshotService(Dependencies() with { Desktops = new ThrowingDesktops() }).CaptureAsync(new SnapshotRequest(), default));
        Assert.Equal("shell", error.Message);
    }

    [Fact]
    public async Task CancellationEndsAHungCaptureAtOnce()
    {
        using var cancel = new CancellationTokenSource();
        var service = new SnapshotService(Dependencies() with { Capture = new HungCapture() });
        var capture = service.CaptureAsync(new SnapshotRequest { UseVision = true, UseUiTree = false }, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(TestBound));
    }

    public static TheoryData<bool> RuntimeStages => new() { true, false };

    [Theory]
    [MemberData(nameof(RuntimeStages))]
    public async Task AMissingRuntimeFailsWithTheInstallHint(bool inCapture)
    {
        var missing = new InvalidOperationException(AppRuntime.InstallHint("TryCreatePackageDependency", unchecked((int)0x80070490)));
        var dependencies = inCapture
            ? Dependencies() with { Capture = new ThrowingCapture(missing) }
            : Dependencies() with { Renderer = new ThrowingRenderer(missing) };
        var error = await Assert.ThrowsAnyAsync<DesktopException>(
            () => new SnapshotService(dependencies).CaptureAsync(new SnapshotRequest { UseVision = true }, default));
        Assert.Equal(missing.Message, error.Message);
        Assert.Contains(AppRuntime.InstallHint(), error.Message);
        Assert.Same(missing, error.InnerException);
    }

    [Fact]
    public void TheDefaultServiceIsCheap()
    {
        // Creating the real machine starts no thread and touches no COM, UIA or Windows App SDK; nothing to observe but
        // that it neither throws nor captures.
        var service = new SnapshotService();
        Assert.Null(service.State.Latest);
    }
}

[Trait("Category", "Desktop")]
public sealed class SnapshotServiceDesktopTests : IDisposable
{
    private readonly TestWindow _window = new(left: 120, top: 120, width: 420, height: 260);
    private readonly UiaWorker<UiaSession> _uia = new(UiaSession.Create);

    public void Dispose()
    {
        _uia.Dispose();
        _window.Dispose();
    }

    [Fact]
    public async Task SnapshotsTheTestWindowRegionWithAnAnnotatedImage()
    {
        var blocks = new HangBlockList(TimeProvider.System, HangBlockList.DefaultDuration);
        var system = new Win32WindowSystem();
        var defaults = SnapshotDependencies.CreateDefault();
        // ownProcessId -1: the test window belongs to this process, which the server itself would skip. The environment is
        // empty so a user's DESKTOP_MCP_* settings (the screenshot scale above all) cannot change the result.
        var machine = defaults with
        {
            Windows = new WindowCatalog(system, new UiaWindowProbe(_uia), defaults.Desktops, blocks, -1, Task.Delay),
            Tree = new TreeService(system, new UiaWindowWalker(_uia), blocks, _ => null, Task.Delay),
            Environment = _ => null,
        };
        var request = new SnapshotRequest { UseVision = true, Region = new RegionArgument([120, 120, 540, 380], null) };

        var snapshot = await new SnapshotService(machine).CaptureAsync(request, default);
        var text = SnapshotText.Render(snapshot, includeUiDetails: true, uiDetailNote: null);

        Assert.Contains($"window \"{_window.Title}\"", text);
        Assert.Contains($"\"{TestWindow.ButtonText}\"  [action: click]", text);
        Assert.Contains("Screenshot Region: (120,120,540,380)", text);
        var png = await PngTestDecoder.DecodeAsync(snapshot.Png!);
        Assert.Equal((420, 260), (png.Width, png.Height));
    }
}
