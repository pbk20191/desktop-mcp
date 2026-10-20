using System.Collections.Concurrent;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Displays;

namespace DesktopMcp.Desktop.Overlay;

/// <summary>The orange-red glow shown after every screenshot (spec §7, Decisions 27; upstream <c>flash_overlay.py</c>).</summary>
public interface ICaptureFlash
{
    /// <summary>
    /// Flashes <paramref name="region"/> (four outward strips) or, without a region, every monitor of
    /// <paramref name="capturedMonitors"/> (four inward strips each). Returns at once and never throws.
    /// </summary>
    void Show(ScreenRect? region, IReadOnlyList<ScreenRect> capturedMonitors);
}

/// <summary>A flash that shows nothing.</summary>
public sealed class NoFlash : ICaptureFlash
{
    private NoFlash()
    {
    }

    public static NoFlash Instance { get; } = new();

    public void Show(ScreenRect? region, IReadOnlyList<ScreenRect> capturedMonitors)
    {
    }
}

/// <summary>
/// <c>flash_overlay.py:72-133, 487-587</c> on the shared dispatcher (Decisions 13, 27). <see cref="Show"/> reads
/// <c>DESKTOP_MCP_DISABLE_FLASH</c>, then on the thread pool lays out the strips and renders their art (cached per strip;
/// Win2D under <see cref="GpuGate"/>, never on the dispatcher), and only the finished pixels reach the dispatcher. There the
/// running flash is cancelled (its layers disposed), the new layers are created hidden with their content, and a 30 ms
/// timer sets every layer's opacity to <see cref="OverlayLayout.FlashOpacity"/> (hiding them below the cut-off) and
/// disposes them after 3.5 s. The layers are excluded from capture, so the flash never shows in the next screenshot.
/// Every failure is logged at debug level and dropped: the flash never delays or fails the capture.
/// </summary>
internal sealed class CaptureFlash : ICaptureFlash
{
    internal const string DisableVariable = "DESKTOP_MCP_DISABLE_FLASH";

    private const int CacheLimit = 32;
    private const string Failed = "capture flash failed";

    private readonly DispatcherHost _host;
    private readonly IOverlayLayerFactory _layers;
    private readonly IOverlayArt _art;
    private readonly Func<ScreenRect> _virtualScreen;
    private readonly Func<string, string?> _environment;
    private readonly TimeProvider _time;
    private readonly DesktopLog? _log;
    private readonly ConcurrentDictionary<LayerArt, OverlayBitmap> _cache = new();

    // Any thread.
    private long _requested;
    private CancellationTokenSource? _rendering;
    private Task _pending = Task.CompletedTask;

    // Dispatcher thread only.
    private Running? _running;

    public CaptureFlash(DispatcherHost host, IOverlayLayerFactory layers, IOverlayArt art, Func<ScreenRect> virtualScreen,
        Func<string, string?> environment, TimeProvider time, DesktopLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(virtualScreen);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(time);
        _host = host;
        _layers = layers;
        _art = art;
        _virtualScreen = virtualScreen;
        _environment = environment;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// The real flash: layered windows on <see cref="DispatcherHost.Shared"/>, <see cref="OverlayArt.Shared"/>, the real
    /// virtual screen, environment and clock. Cheap: it starts no thread and loads nothing until the first flash.
    /// </summary>
    public static ICaptureFlash CreateDefault(DesktopLog? log = null)
    {
        var displays = new Win32Displays();
        return new CaptureFlash(DispatcherHost.Shared, new LayeredWindowFactory(log), OverlayArt.Shared, displays.GetVirtualScreen,
            Environment.GetEnvironmentVariable, TimeProvider.System, log);
    }

    /// <summary>upstream <c>_flash_disabled</c>: 1, true, yes or on, trimmed, in any case.</summary>
    internal static bool Disabled(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    /// <summary>The last <see cref="Show"/>'s render and dispatcher step (it never faults), for tests.</summary>
    internal Task Pending => Volatile.Read(ref _pending);

    public void Show(ScreenRect? region, IReadOnlyList<ScreenRect> capturedMonitors)
    {
        try
        {
            if (Disabled(_environment(DisableVariable))) return;
            var monitors = capturedMonitors.ToArray();
            var request = Interlocked.Increment(ref _requested);
            // A newer flash replaces this one: a render still waiting for the graphics slot gives way, and a flash that does
            // not render within its own duration is not worth showing.
            // The sources are not disposed (their 3.5 s timer releases itself), so this cancel never races a disposal; it runs
            // the replaced render's callbacks on the thread pool, not on the capturing thread.
            var rendering = new CancellationTokenSource(OverlayLayout.FlashDuration, _time);
            _ = Interlocked.Exchange(ref _rendering, rendering)?.CancelAsync();
            Volatile.Write(ref _pending, Task.Run(() => RunAsync(request, region, monitors, rendering)));
        }
        catch (Exception e)
        {
            Log(e);
        }
    }

    // ---- thread pool ----

    private async Task RunAsync(long request, ScreenRect? region, ScreenRect[] monitors, CancellationTokenSource rendering)
    {
        try
        {
            var specs = OverlayLayout.Flash(region, monitors, _virtualScreen());
            if (specs.Count == 0) return;
            var ct = rendering.Token;
            var bitmaps = await Task.WhenAll(specs.Select(spec => RenderAsync(spec.Art, ct))).ConfigureAwait(false);
            if (!Current(request)) return;
            var dispatcher = await _host.GetAsync(ct).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => Start(dispatcher, request, specs, bitmaps, fullScreen: region is null)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (rendering.IsCancellationRequested && !Current(request))
        {
            // Replaced by a newer flash.
        }
        catch (Exception e)
        {
            Log(e);
        }
        finally
        {
            Interlocked.CompareExchange(ref _rendering, null, rendering);
        }
    }

    private async Task<OverlayBitmap> RenderAsync(LayerArt art, CancellationToken ct)
    {
        if (_cache.TryGetValue(art, out var cached)) return cached;
        var bitmap = await _art.RenderAsync(art, ct).ConfigureAwait(false);
        // Region flashes come in every size: the cache keeps the most recent strips only.
        if (_cache.Count >= CacheLimit) _cache.Clear();
        _cache[art] = bitmap;
        return bitmap;
    }

    private bool Current(long request) => Volatile.Read(ref _requested) == request;

    // ---- dispatcher thread ----

    private sealed class Running(IOverlayLayer[] layers, long start, bool fullScreen)
    {
        public IOverlayLayer[] Layers { get; } = layers;
        public long Start { get; } = start;
        public bool FullScreen { get; } = fullScreen;
        public bool Shown { get; set; }
        public Task<IDisposable>? Timer { get; set; }
    }

    private void Start(IDesktopDispatcher dispatcher, long request, IReadOnlyList<LayerSpec> specs, OverlayBitmap[] bitmaps, bool fullScreen)
    {
        if (!Current(request)) return; // a newer flash is on its way and replaces this one
        if (_running is { } previous) Close(previous);

        var created = new List<IOverlayLayer>(specs.Count);
        try
        {
            for (var i = 0; i < specs.Count; i++)
            {
                var layer = _layers.Create(specs[i].Bounds, specs[i].Name);
                created.Add(layer);
                layer.SetContent(bitmaps[i]);
            }
        }
        catch
        {
            foreach (var layer in created) DisposeQuietly(layer);
            throw;
        }

        var running = new Running([.. created], _time.GetTimestamp(), fullScreen);
        _running = running;
        Frame(running);
        if (_running != running) return; // the first frame failed and closed the flash
        try
        {
            running.Timer = dispatcher.StartTimerAsync(OverlayLayout.FlashFrame, () => Frame(running));
        }
        catch
        {
            Close(running);
            throw;
        }
    }

    // The 30 ms frame (flash_overlay.py:540-575): fade, then close at 3.5 s.
    private void Frame(Running running)
    {
        if (_running != running) return;
        try
        {
            var elapsed = _time.GetElapsedTime(running.Start);
            if (elapsed >= OverlayLayout.FlashDuration)
            {
                Close(running);
                return;
            }
            if (OverlayLayout.FlashOpacity(elapsed / OverlayLayout.FlashDuration, running.FullScreen) is { } opacity)
            {
                foreach (var layer in running.Layers) layer.SetOpacity(opacity);
                if (!running.Shown)
                {
                    foreach (var layer in running.Layers) layer.Show();
                    running.Shown = true;
                }
            }
            else if (running.Shown)
            {
                foreach (var layer in running.Layers) layer.Hide();
                running.Shown = false;
            }
        }
        catch (Exception e)
        {
            Log(e);
            Close(running);
        }
    }

    private void Close(Running running)
    {
        if (_running == running) _running = null;
        StopTimer(running);
        foreach (var layer in running.Layers) DisposeQuietly(layer);
    }

    private static void StopTimer(Running running)
    {
        var timer = running.Timer;
        running.Timer = null;
        if (timer is null) return;
        if (timer.IsCompletedSuccessfully) timer.Result.Dispose();
        else
            timer.ContinueWith(static t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void DisposeQuietly(IOverlayLayer layer)
    {
        try
        {
            layer.Dispose();
        }
        catch (Exception e)
        {
            Log(e);
        }
    }

    // ---- any thread ----

    private void Log(Exception error)
    {
        try
        {
            _log?.Invoke(DesktopLogLevel.Debug, Failed, error);
        }
        catch (Exception)
        {
            // A failing log sink never changes the flash.
        }
    }
}
