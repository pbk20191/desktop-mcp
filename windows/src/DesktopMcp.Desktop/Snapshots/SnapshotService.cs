using System.Diagnostics;
using System.Globalization;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Capture;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Imaging;
using DesktopMcp.Desktop.Overlay;
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
    private sealed record Entry(long Sequence, DesktopSnapshot Snapshot);

    private Entry? _latest;

    public DesktopSnapshot? Latest => Volatile.Read(ref _latest)?.Snapshot;

    /// <summary>
    /// Stores <paramref name="snapshot"/> unless a snapshot that started later is already stored: snapshots run
    /// concurrently (spec §5), and a slow earlier one must not overwrite a newer one. <paramref name="sequence"/> is the
    /// snapshot's start order.
    /// </summary>
    internal void Replace(DesktopSnapshot snapshot, long sequence)
    {
        var entry = new Entry(sequence, snapshot);
        var current = Volatile.Read(ref _latest);
        while (current is null || current.Sequence < sequence)
        {
            var seen = Interlocked.CompareExchange(ref _latest, entry, current);
            if (ReferenceEquals(seen, current)) return;
            current = seen;
        }
    }
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

    /// <summary>Shown after every successful screen capture (Decisions 27); none unless set.</summary>
    public ICaptureFlash Flash { get; init; } = NoFlash.Instance;

    /// <summary>
    /// The real parts over <paramref name="machine"/>. Creating them starts no thread and touches no COM, UIA, desktop or
    /// Windows App SDK: the UIA worker starts its thread on the first job, and the capture and the renderer initialise the
    /// runtime on their first call.
    /// </summary>
    /// <remarks>
    /// The stateful services (the virtual desktop service's one-call gate, the UIA worker's thread and stuck-worker count,
    /// the hang block list and the window catalog) come from <paramref name="machine"/>, which the act tools share.
    /// </remarks>
    public static SnapshotDependencies CreateDefault(DesktopMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return new SnapshotDependencies
        {
            Displays = machine.Displays,
            Windows = machine.Catalog,
            Tree = machine.Tree,
            Desktops = machine.Desktops,
            Capture = new GraphicsCaptureService(),
            Renderer = new Win2DRenderer(),
            Cursor = DesktopSession.CursorPosition,
            EnsureSession = DesktopSession.ThrowIfUnavailable,
            Environment = System.Environment.GetEnvironmentVariable,
            ProfileLog = Console.Error,
            Flash = machine.Flash,
        };
    }
}

/// <summary>
/// upstream Desktop.get_state (desktop/service.py:87-315) and capture_desktop_state's checks
/// (tools/_snapshot_helpers.py:175-215).
/// </summary>
/// <remarks>
/// Bounded blocking (global constraints): the window scan and the tree walk are bounded inside, call by call (the UIA
/// worker's probe and walk deadlines, its stuck-worker fail-fast, and bounded retries), and get only the caller's token:
/// like upstream, no global deadline cuts off a capture that is still making progress. The screen capture and the
/// rendering also get an outer deadline here, which covers the runtime initialisation and the code outside the services'
/// own deadlines; past it they fail with a <see cref="DesktopException"/>. The virtual desktop read falls back to the
/// default desktop past its deadline. Each bounded call runs on the thread pool, so an implementation that blocks before
/// its first await is bounded too; past a deadline its token is cancelled and it is abandoned, not waited for.
/// </remarks>
public sealed class SnapshotService : IDesktopSnapshots
{
    /// <summary>The "Screenshot Backend" value (Decisions 11): Windows.Graphics.Capture.</summary>
    public const string Backend = "wgc";

    /// <summary>use_dom=True: this port has no browser DOM mode (P2 Decisions 39).</summary>
    public const string UseDomUnavailable = "use_dom=True is not supported: this server reads browsers through UI Automation only. For web page content, use a browser automation MCP server such as Playwright MCP or Chrome DevTools MCP";

    private readonly SnapshotDependencies _machine;

    /// <summary>A new real machine (<see cref="DesktopMachine.CreateDefault"/>); cheap: no thread, COM or runtime.</summary>
    public SnapshotService() : this(DesktopMachine.CreateDefault())
    {
    }

    /// <summary>The real parts over <paramref name="machine"/>, shared with the act tools; cheap: no thread, COM or runtime.</summary>
    public SnapshotService(DesktopMachine machine) : this(SnapshotDependencies.CreateDefault(machine))
    {
    }

    internal SnapshotService(SnapshotDependencies machine) => _machine = machine;

    // The start order of the snapshots, so the store keeps the one that started last.
    private long _started;

    public DesktopStateStore State { get; } = new();

    /// <summary>The virtual desktop read: the service's gate wait plus its own call (2 x 2 s).</summary>
    internal TimeSpan DesktopsTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Tests: replaces <see cref="CaptureDeadline"/>.</summary>
    internal TimeSpan? CaptureTimeout { get; init; }

    /// <summary>The rendering: Win2D's own 10 s deadline plus 5 s.</summary>
    internal TimeSpan RenderTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The screen capture's deadline for an area that overlaps <paramref name="monitors"/> monitors: the 2 s borderless
    /// access request, 6 s per monitor (GraphicsCaptureService's own bounds) and 5 s for the rest.
    /// </summary>
    internal static TimeSpan CaptureDeadline(int monitors) => TimeSpan.FromSeconds(2 + 6 * monitors + 5);

    public async Task<DesktopSnapshot> CaptureAsync(SnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sequence = Interlocked.Increment(ref _started);
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
            ? await _machine.Windows.ScanAsync(cancellationToken).ConfigureAwait(false)
            : WindowScan.Empty;
        ScreenPoint? cursor = _machine.Cursor();
        var (activeDesktop, allDesktops) = await DesktopsAsync(cancellationToken).ConfigureAwait(false);
        var active = scan.Active;
        IReadOnlyList<WindowInfo> windows = scan.Windows.Where(window => window.Handle != active?.Handle).ToArray();
        Stage("desktop_context");

        var tree = TreeSnapshot.Empty;
        if (request.UseUiTree)
        {
            var (treeActive, others) = TreeTargets(scan, captureRect);
            tree = await _machine.Tree.CaptureAsync(treeActive, others, screen, cancellationToken).ConfigureAwait(false);
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
            var captured = monitors.Where(monitor => ScreenRect.Clip(monitor, area) is not null).ToArray();
            var deadline = CaptureTimeout ?? CaptureDeadline(captured.Length);
            var image = await WithRuntimeAsync(() => BoundedAsync(token => _machine.Capture.CaptureAsync(area, monitors, token), deadline,
                "Screen capture", cancellationToken)).ConfigureAwait(false);
            Stage("screenshot_capture");
            originalSize = new PixelSize(image.Width, image.Height);
            scale = SnapshotSettings.ImageScale(_machine.Environment(SnapshotSettings.ScreenshotScaleVariable), image.Width, image.Height);
            var options = request.UseAnnotation ? Annotate(tree, cursor, area, request, scale.Value) : RenderOptions.Plain(scale.Value);
            png = await WithRuntimeAsync(() => BoundedAsync(token => _machine.Renderer.RenderPngAsync(image, options, token), RenderTimeout,
                "Image rendering", cancellationToken)).ConfigureAwait(false);
            Stage("screenshot_render");
            // After the render, not between capture and render: the flash's art takes the same graphics slot (GpuGate).
            ShowFlash(region, captured);
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
        State.Replace(snapshot, sequence);
        if (SnapshotSettings.ProfileEnabled(_machine.Environment(SnapshotSettings.ProfileSnapshotVariable)))
            Profile(stages, clock.Elapsed.TotalMilliseconds, request, displayIndices);
        return snapshot;
    }

    // upstream desktop/service.py:1382: the flash follows a successful screenshot (and is excluded from the next one). A region gets
    // outward strips; a display selection or a full capture gets inward strips inside each captured monitor (Decisions 27,
    // so not the region-style halo around a display union). It returns at once; nothing it does may fail the capture.
    private void ShowFlash(ScreenRect? region, ScreenRect[] captured)
    {
        try
        {
            _machine.Flash.Show(region, captured);
        }
        catch (Exception)
        {
            // ICaptureFlash.Show never throws; a broken implementation still must not fail the screenshot.
        }
    }

    /// <summary>
    /// upstream desktop/service.py:174-193: the windows the tree walks. The focused window (when it is inside
    /// <paramref name="area"/>), then the candidates that are not application windows and, with an area, the application
    /// windows inside it. The focused window is never among the others. WaitFor walks the same selection without an area.
    /// </summary>
    internal static (long? Active, IReadOnlyList<long> Others) TreeTargets(WindowScan scan, ScreenRect? area)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var active = scan.Active;
        var applications = scan.Windows.Select(window => window.Handle).ToHashSet();
        var inArea = area is { } rect
            ? scan.Windows.Where(window => window.Handle != active?.Handle && RegionFilter.Window(window, rect) is not null)
                .Select(window => window.Handle).ToHashSet()
            : [];
        long? treeActive = active is not null && (area is not { } bounds || RegionFilter.Window(active, bounds) is not null)
            ? active.Handle
            : null;
        var others = scan.Candidates
            .Where(handle => handle != active?.Handle && (!applications.Contains(handle) || inArea.Contains(handle)))
            .Distinct()
            .ToArray();
        return (treeActive, others);
    }

    // upstream desktop/service.py:154-162: the desktops cannot be read, so the report names the default desktop.
    // VirtualDesktopService already fails open on shell errors and bounds itself; only this outer deadline is caught
    // here, so a bug in the service is not hidden.
    private async Task<(VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All)> DesktopsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await BoundedAsync(_ => Task.FromResult(_machine.Desktops.GetDesktops()), DesktopsTimeout, "Virtual desktop read",
                cancellationToken).ConfigureAwait(false);
        }
        catch (DeadlineExceededException)
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
            throw new DeadlineExceededException(string.Create(CultureInfo.InvariantCulture,
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

/// <summary>A call bounded by <see cref="SnapshotService"/> missed its deadline.</summary>
internal sealed class DeadlineExceededException(string message) : DesktopException(message);
