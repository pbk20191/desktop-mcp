using System.Diagnostics;
using System.Globalization;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Capture;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Snapshots;

/// <summary>A Snapshot or Screenshot request after the argument type checks (upstream tools/snapshot.py:37-60, 86-106).</summary>
public sealed record SnapshotRequest
{
    public bool UseVision { get; init; }
    public bool UseDom { get; init; }
    public bool UseAnnotation { get; init; } = true;
    public bool UseUiTree { get; init; } = true;
    public long? WidthReferenceLine { get; init; }
    public long? HeightReferenceLine { get; init; }
    public IReadOnlyList<long>? Displays { get; init; }
    public RegionArgument? Region { get; init; }
}

/// <summary>The latest snapshot; labels resolve against it (spec §5 "Shared state").</summary>
public sealed class DesktopStateStore
{
    private DesktopSnapshot? _latest;

    public DesktopSnapshot? Latest => Volatile.Read(ref _latest);

    internal void Replace(DesktopSnapshot snapshot) => Volatile.Write(ref _latest, snapshot);
}

public interface IDesktopSnapshots
{
    DesktopStateStore State { get; }

    /// <summary>
    /// upstream Desktop.get_state (desktop/service.py:87-315). Errors are exceptions whose message is the text upstream
    /// puts after "Error capturing desktop state: ".
    /// </summary>
    Task<DesktopSnapshot> CaptureAsync(SnapshotRequest request, CancellationToken cancellationToken);
}

/// <summary>Everything <see cref="SnapshotService"/> reads from the machine; tests replace each part.</summary>
internal sealed record SnapshotDependencies
{
    public required IDisplayProvider Displays { get; init; }
    public required IWindowScanner Windows { get; init; }
    public required ITreeCapture Tree { get; init; }
    public required IVirtualDesktops Desktops { get; init; }
    public required IScreenCapture Capture { get; init; }
    public required IImageRenderer Renderer { get; init; }
    public required Func<ScreenPoint> Cursor { get; init; }
    public required Action EnsureSession { get; init; }
    public required Func<string, string?> Environment { get; init; }
    public required TextWriter ProfileLog { get; init; }

    /// <summary>
    /// The real machine. Creating it starts no thread and touches no COM, UIA, desktop or Windows App SDK: the UIA worker
    /// starts its thread on the first job, and the capture and the renderer initialise the runtime on their first call.
    /// </summary>
    /// <remarks>
    /// Each stateful service is created once here and shared by every part that uses it: the virtual desktop service (its
    /// one-call gate), the UIA worker (its thread and stuck-worker count) and the hang block list. The owner (one
    /// <see cref="SnapshotService"/> held for the server's lifetime) must therefore not call this per snapshot.
    /// </remarks>
    public static SnapshotDependencies CreateDefault()
    {
        var uia = new UiaWorker<UiaSession>(UiaSession.Create);
        var blocks = new HangBlockList(TimeProvider.System, HangBlockList.DefaultDuration);
        var windows = new Win32WindowSystem();
        var desktops = new VirtualDesktopService();
        return new SnapshotDependencies
        {
            Displays = new Win32Displays(),
            Windows = new WindowCatalog(windows, new UiaWindowProbe(uia), desktops, blocks, System.Environment.ProcessId, Task.Delay),
            Tree = new TreeService(windows, new UiaWindowWalker(uia), blocks, System.Environment.GetEnvironmentVariable, Task.Delay),
            Desktops = desktops,
            Capture = new GraphicsCaptureService(),
            Renderer = new Win2DRenderer(),
            Cursor = DesktopSession.CursorPosition,
            EnsureSession = DesktopSession.ThrowIfUnavailable,
            Environment = System.Environment.GetEnvironmentVariable,
            ProfileLog = Console.Error,
        };
    }
}

/// <summary>
/// upstream Desktop.get_state (desktop/service.py:87-315) and capture_desktop_state's checks
/// (tools/_snapshot_helpers.py:175-215).
/// </summary>
/// <remarks>
/// Every machine call that can block is bounded here as well as inside the real services (global constraints, bounded
/// blocking): the window scan, the tree walk, the screen capture and the rendering fail with a
/// <see cref="DesktopException"/> past their deadline, and the virtual desktop read falls back to the default desktop.
/// Each runs on the thread pool, so an implementation that blocks before its first await is bounded too; past a deadline
/// the work is cancelled and abandoned, not waited for. The deadlines sit above the services' own bounds, so they only
/// fire when a service misses its own.
/// </remarks>
public sealed class SnapshotService : IDesktopSnapshots
{
    /// <summary>The "Screenshot Backend" value (Decisions 11): Windows.Graphics.Capture.</summary>
    public const string Backend = "wgc";

    /// <summary>use_dom=True before the browser DOM exists (P6; Decisions 4).</summary>
    public const string UseDomUnavailable = "use_dom=True is not supported yet: browser DOM extraction arrives in a later release";

    private readonly SnapshotDependencies _machine;

    /// <summary>The real machine (<see cref="SnapshotDependencies.CreateDefault"/>); cheap: no thread, COM or runtime.</summary>
    public SnapshotService() : this(SnapshotDependencies.CreateDefault())
    {
    }

    internal SnapshotService(SnapshotDependencies machine) => _machine = machine;

    public DesktopStateStore State { get; } = new();

    /// <summary>The window scan: 5 s per hung probe, and at most four hung probes before UIA reports itself unavailable.</summary>
    internal TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The tree walk: 20 s per hung window, with retries after 0.5, 1 and 2 s for failing ones.</summary>
    internal TimeSpan TreeTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>The virtual desktop read: the service's gate wait plus its own call (2 x 2 s).</summary>
    internal TimeSpan DesktopsTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The screen capture: the 2 s access request plus 6 s per monitor.</summary>
    internal TimeSpan CaptureTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The rendering: Win2D's own 10 s deadline.</summary>
    internal TimeSpan RenderTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<DesktopSnapshot> CaptureAsync(SnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clock = Stopwatch.StartNew();
        var stages = new List<(string Name, double Milliseconds)>();
        void Stage(string name)
        {
            stages.Add((name, clock.Elapsed.TotalMilliseconds - stages.Sum(s => s.Milliseconds)));
        }

        if (request.UseDom && !request.UseUiTree) throw new ArgumentException("use_dom=True requires use_ui_tree=True");
        var displayIndices = SnapshotSelection.ParseDisplays(request.Displays);
        if (request.UseDom) throw new NotSupportedException(UseDomUnavailable);

        var displays = _machine.Displays.GetDisplays();
        var screen = _machine.Displays.GetVirtualScreen();
        var region = SnapshotSelection.ParseRegion(request.Region, screen);
        var captureRect = region ?? (displayIndices is null ? null : SnapshotSelection.DisplayUnion(displayIndices, displays));
        _machine.EnsureSession();

        // upstream desktop/service.py:137-150: the Screenshot path skips the window scan.
        var scan = request.UseUiTree
            ? await BoundedAsync(_machine.Windows.ScanAsync, ScanTimeout, "Window scan", cancellationToken).ConfigureAwait(false)
            : WindowScan.Empty;
        ScreenPoint? cursor = _machine.Cursor();
        var (activeDesktop, allDesktops) = await DesktopsAsync(cancellationToken).ConfigureAwait(false);
        var active = scan.Active;
        IReadOnlyList<WindowInfo> windows = scan.Windows.Where(window => window.Handle != active?.Handle).ToArray();
        Stage("desktop_context");

        var tree = TreeSnapshot.Empty;
        if (request.UseUiTree)
        {
            // upstream desktop/service.py:174-193: the focused window, the windows that are not applications, and with a
            // capture rectangle the application windows inside it. The focused window is never among the others.
            var applications = scan.Windows.Select(window => window.Handle).ToHashSet();
            var inArea = captureRect is { } area
                ? windows.Where(window => RegionFilter.Window(window, area) is not null).Select(window => window.Handle).ToHashSet()
                : [];
            long? treeActive = active is not null && (captureRect is not { } bounds || RegionFilter.Window(active, bounds) is not null)
                ? active.Handle
                : null;
            var others = scan.Candidates
                .Where(handle => handle != active?.Handle && (!applications.Contains(handle) || inArea.Contains(handle)))
                .Distinct()
                .ToArray();
            tree = await BoundedAsync(token => _machine.Tree.CaptureAsync(treeActive, others, screen, token), TreeTimeout, "UI tree capture",
                cancellationToken).ConfigureAwait(false);
        }
        Stage("tree_capture");

        if (captureRect is { } rect)
        {
            active = RegionFilter.Window(active, rect);
            windows = RegionFilter.Windows(windows, rect);
            if (request.UseUiTree) tree = RegionFilter.Tree(tree, rect);
            cursor = RegionFilter.Cursor(cursor, rect);
        }
        Stage("region_filter");

        PixelSize? originalSize = null;
        double? scale = null;
        byte[]? png = null;
        if (request.UseVision)
        {
            var area = captureRect ?? screen;
            var monitors = displays.Select(display => display.Bounds).ToArray();
            var image = await WithRuntimeAsync(() => BoundedAsync(token => _machine.Capture.CaptureAsync(area, monitors, token), CaptureTimeout,
                "Screen capture", cancellationToken)).ConfigureAwait(false);
            Stage("screenshot_capture");
            originalSize = new PixelSize(image.Width, image.Height);
            scale = SnapshotSettings.ImageScale(_machine.Environment(SnapshotSettings.ScreenshotScaleVariable), image.Width, image.Height);
            var options = request.UseAnnotation ? Annotate(tree, cursor, area, request, scale.Value) : RenderOptions.Plain(scale.Value);
            png = await WithRuntimeAsync(() => BoundedAsync(_ => _machine.Renderer.RenderPngAsync(image, options), RenderTimeout,
                "Image rendering", cancellationToken)).ConfigureAwait(false);
            Stage("screenshot_render");
        }

        var snapshot = new DesktopSnapshot
        {
            ActiveDesktop = activeDesktop,
            AllDesktops = allDesktops,
            ActiveWindow = active,
            Windows = windows,
            Cursor = cursor,
            OriginalSize = originalSize,
            Scale = scale,
            Region = captureRect,
            SelectedDisplays = displayIndices,
            Displays = displays,
            Backend = request.UseVision ? Backend : null,
            Tree = tree,
            Png = png,
        };
        State.Replace(snapshot);
        if (SnapshotSettings.ProfileEnabled(_machine.Environment(SnapshotSettings.ProfileSnapshotVariable)))
            Profile(stages, clock.Elapsed.TotalMilliseconds, request, displayIndices);
        return snapshot;
    }

    // upstream desktop/service.py:154-162: the desktops cannot be read (an error, or here a missed deadline), so the
    // report names the default desktop.
    private async Task<(VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All)> DesktopsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await BoundedAsync(_ => Task.FromResult(_machine.Desktops.GetDesktops()), DesktopsTimeout, "Virtual desktop read",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (VirtualDesktopInfo.Default, [VirtualDesktopInfo.Default]);
        }
    }

    // The capture and the renderer initialise the Windows App SDK runtime; when it is missing they throw an
    // InvalidOperationException whose message carries the install hint (AppRuntime), which reaches the tool as a
    // DesktopException.
    private static async Task<T> WithRuntimeAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException)
        {
            throw new DesktopException(e.Message, e);
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> on the thread pool and waits up to <paramref name="timeout"/>. Past it the call's token
    /// is cancelled (without running its callbacks on this thread) and the call is abandoned; the error names
    /// <paramref name="operation"/>. A call that finishes, however it finishes, gives its own result or exception.
    /// </summary>
    private static async Task<T> BoundedAsync<T>(
        Func<CancellationToken, Task<T>> call, TimeSpan timeout, string operation, CancellationToken cancellationToken)
    {
        var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = Task.Run(() => call(abandon.Token), CancellationToken.None);
        try
        {
            return await work.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException e) when (work.Exception?.InnerException != e)
        {
            // The deadline passed; the call may still have finished just after it.
            if (work.IsCompleted) return await work.ConfigureAwait(false);
            throw new DesktopException(string.Create(CultureInfo.InvariantCulture,
                $"{operation} failed: it did not finish within {timeout.TotalSeconds:0.###} s."));
        }
        finally
        {
            if (work.IsCompleted)
            {
                abandon.Dispose();
            }
            else
            {
                // CancelAsync, not Cancel: Cancel would run the abandoned call's callbacks (possibly blocking WinRT teardown)
                // on this thread. The token is released once the call and the cancellation end, and the call's failure is
                // observed so it does not surface as an unobserved task exception.
                var cancelling = abandon.CancelAsync();
                _ = Task.WhenAll(work, cancelling).ContinueWith(static (task, state) =>
                {
                    _ = task.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                }, abandon, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    // upstream desktop/service.py:1385-1486: the interactive elements' boxes, the grid when both counts are set, and the
    // cursor, all relative to the captured area.
    private static RenderOptions Annotate(TreeSnapshot tree, ScreenPoint? cursor, ScreenRect area, SnapshotRequest request, double scale)
    {
        var annotations = tree.Interactive
            .Select((element, label) => new Annotation(
                new PixelRect(element.Box.Left - area.Left, element.Box.Top - area.Top, element.Box.Right - area.Left, element.Box.Bottom - area.Top),
                label.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        GridLines? grid = request is { WidthReferenceLine: { } columns and not 0, HeightReferenceLine: { } rows and not 0 }
            ? new GridLines((int)Math.Clamp(columns, int.MinValue, int.MaxValue), (int)Math.Clamp(rows, int.MinValue, int.MaxValue))
            : null;
        PixelPoint? marker = cursor is { } point ? new PixelPoint(point.X - area.Left, point.Y - area.Top) : null;
        return new RenderOptions(scale, annotations, marker, grid);
    }

    // upstream desktop/service.py:294-311: one line of stage timings.
    private void Profile(List<(string Name, double Milliseconds)> stages, double total, SnapshotRequest request, IReadOnlyList<long>? displays)
    {
        var parts = stages.Select(stage => FormattableString.Invariant($"{stage.Name}_ms={stage.Milliseconds:0.0}"))
            .Append(FormattableString.Invariant($"total_ms={total:0.0}"))
            .Append($"use_vision={request.UseVision} use_dom={request.UseDom} use_ui_tree={request.UseUiTree} use_annotation={request.UseAnnotation}")
            .Append($"displays={(displays is null ? "None" : "[" + string.Join(", ", displays) + "]")}");
        _machine.ProfileLog.WriteLine("Snapshot profile: " + string.Join(" ", parts));
    }
}
