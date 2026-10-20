using System.Diagnostics;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Dispatching;

namespace DesktopMcp.Desktop.Overlay;

/// <summary>The blue AI-control indicator (spec §7, Decisions 26), as the control coordinator sees it.</summary>
internal interface IControlIndicator : IAsyncDisposable
{
    /// <summary>
    /// Shows the indicator for state generation <paramref name="generation"/> and completes when every layer is visible. A
    /// generation older than the newest show or hide returns without showing (<c>control_overlay.py:248-249</c>). The art is
    /// rendered first, on the thread pool, bounded only by <paramref name="ct"/> (it may wait for the graphics slot behind a
    /// capture): a render failure or cancellation throws and leaves <see cref="Healthy"/> true (preflight F19). The
    /// dispatcher part (creating, filling and showing the layers) is bounded by 3 s: a failure or a timeout
    /// (<see cref="TimeoutException"/>) throws and makes <see cref="Healthy"/> false.
    /// </summary>
    Task ShowAsync(long generation, CancellationToken ct);

    /// <summary>Hides the indicator (any thread, returns at once; the layers are kept). Older generations are ignored.</summary>
    void Hide(long generation);

    /// <summary>
    /// False after any failure on the dispatcher (sticky), after disposal, or while a visible indicator has not been
    /// refreshed for more than 1 s (<c>control_overlay.py:469-479</c>).
    /// </summary>
    bool Healthy { get; }
}

/// <summary>
/// <c>control_overlay.py:213-479</c> on the shared dispatcher (Decisions 13, 15, 26). The art is rendered on the thread pool
/// (cached by monitor layout) and only the finished pixels reach the dispatcher, which creates the layers (once per layout),
/// uploads them, shows them and runs the 50 ms refresh: breathing opacity, the ring at the cursor, <c>LastRefresh</c>, and a
/// monitor-layout check on the thread pool that rebuilds the layers when the monitors change (the old layers are disposed
/// after the new ones show).
/// </summary>
internal sealed class ControlIndicator : IControlIndicator
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StaleRefresh = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(3);

    private readonly DispatcherHost _host;
    private readonly IOverlayLayerFactory _layers;
    private readonly IOverlayArt _art;
    private readonly Func<IReadOnlyList<ScreenRect>> _monitors;
    private readonly Func<ScreenPoint> _cursor;
    private readonly Func<long> _now;
    private readonly DesktopLog? _log;
    private readonly CancellationTokenSource _lifetime = new();

    // Any thread, under _gate: the wanted state.
    private readonly Lock _gate = new();
    private long _generation = long.MinValue;
    private bool _wanted;
    private bool _disposed;
    private IDesktopDispatcher? _dispatcher;

    // Any thread.
    private volatile bool _faulted;
    private volatile bool _visible;
    private long _lastRefresh;
    private long _sequence;
    private RenderedSet? _cache;            // the last rendered layout (thread pool)
    private OverlayBitmap? _ring;           // the ring does not depend on the layout
    private ScreenRect[]? _builtMonitors;   // what the layers on screen were built for
    private int _checking;
    private Task _layoutCheck = Task.CompletedTask;

    // Dispatcher thread only.
    private Built? _built;
    private readonly List<IOverlayLayer> _retired = [];
    private Task<IDisposable>? _timer;
    private long _breathStart;

    public ControlIndicator(DispatcherHost host, IOverlayLayerFactory layers, IOverlayArt art, Func<IReadOnlyList<ScreenRect>> monitors,
        Func<ScreenPoint> cursor, Func<long> now, DesktopLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(now);
        _host = host;
        _layers = layers;
        _art = art;
        _monitors = monitors;
        _cursor = cursor;
        _now = now;
        _log = log;
    }

    /// <summary>The bound of the dispatcher part of a show (Decisions 15, <c>control_overlay.py:269-283</c>).</summary>
    internal TimeSpan ShowLimit { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The monitor-layout check the last refresh started (and its rebuild), for tests.</summary>
    internal Task LayoutCheck => Volatile.Read(ref _layoutCheck);

    public bool Healthy
    {
        get
        {
            if (_faulted || Volatile.Read(ref _disposed)) return false;
            if (!_visible) return true;
            var age = _now() - Volatile.Read(ref _lastRefresh);
            return age <= StaleRefresh.TotalSeconds * Stopwatch.Frequency;
        }
    }

    public async Task ShowAsync(long generation, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (generation < _generation) return;
            if (_faulted) throw new InvalidOperationException("AI control indicator failed");
            _generation = generation;
            _wanted = true;
        }

        // The monitor list and the notice size are read on the thread pool, never on the dispatcher (Task 8).
        var set = await Task.Run(() => RenderAsync(_monitors(), ct), ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (generation != _generation || !_wanted || _disposed) return; // a newer hide or show came while rendering
        }

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(ShowLimit);
        try
        {
            var dispatcher = await DispatcherAsync(bound.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => Apply(generation, set)).WaitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (bound.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            var timeout = new TimeoutException("AI control indicator did not acknowledge state change", e);
            Fault(timeout);
            throw timeout;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Fault(e);
            throw;
        }
    }

    public void Hide(long generation)
    {
        IDesktopDispatcher? dispatcher;
        lock (_gate)
        {
            if (_disposed || generation < _generation) return;
            _generation = generation;
            _wanted = false;
            dispatcher = _dispatcher;
        }
        if (dispatcher is null) return; // never shown
        try
        {
            Observe(dispatcher.InvokeAsync(HideLayers));
        }
        catch (Exception e)
        {
            Fault(e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        IDesktopDispatcher? dispatcher;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _wanted = false;
            dispatcher = _dispatcher;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (dispatcher is not null)
        {
            try
            {
                await dispatcher.InvokeAsync(DestroyLayers).WaitAsync(StopLimit).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(DesktopLogLevel.Warning, "AI control indicator windows could not be destroyed", e);
            }
        }
    }

    private async Task<IDesktopDispatcher> DispatcherAsync(CancellationToken ct)
    {
        var dispatcher = await _host.GetAsync(ct).ConfigureAwait(false);
        lock (_gate) _dispatcher ??= dispatcher;
        return dispatcher;
    }

    // ---- thread pool ----

    private sealed record RenderedSet(long Sequence, ScreenRect[] Monitors, IReadOnlyList<LayerSpec> Specs, OverlayBitmap[] Bitmaps, OverlayBitmap Ring);

    private async Task<RenderedSet> RenderAsync(IReadOnlyList<ScreenRect> monitors, CancellationToken ct)
    {
        var layout = monitors.ToArray();
        if (Volatile.Read(ref _cache) is { } cached && cached.Monitors.AsSpan().SequenceEqual(layout)) return cached;
        var specs = OverlayLayout.Indicator(layout, _art.NoticeSize);
        var bitmaps = await Task.WhenAll(specs.Select(spec => _art.RenderAsync(spec.Art, ct))).ConfigureAwait(false);
        var ring = Volatile.Read(ref _ring);
        if (ring is null)
        {
            ring = await _art.RenderAsync(OverlayLayout.CursorRing(default).Art, ct).ConfigureAwait(false);
            Volatile.Write(ref _ring, ring);
        }
        var set = new RenderedSet(Interlocked.Increment(ref _sequence), layout, specs, bitmaps, ring);
        Volatile.Write(ref _cache, set);
        return set;
    }

    // Started by the refresh tick, one at a time: reads the monitors and rebuilds the layers when they changed.
    private void StartLayoutCheck()
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        Volatile.Write(ref _layoutCheck, Task.Run(CheckLayoutAsync));
    }

    private async Task CheckLayoutAsync()
    {
        try
        {
            var monitors = _monitors();
            if (Volatile.Read(ref _builtMonitors) is not { } built || built.AsSpan().SequenceEqual(monitors.ToArray())) return;
            long generation;
            IDesktopDispatcher? dispatcher;
            lock (_gate)
            {
                if (!_wanted || _disposed || _faulted) return;
                generation = _generation;
                dispatcher = _dispatcher;
            }
            if (dispatcher is null) return;
            var set = await RenderAsync(monitors, _lifetime.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => Apply(generation, set)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // A failed render is tried again on a later tick; a failure on the dispatcher has already faulted the indicator.
            if (!_lifetime.IsCancellationRequested) Log(DesktopLogLevel.Debug, "AI control indicator could not follow the monitor layout", e);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    // ---- dispatcher thread ----

    private sealed record Built(RenderedSet Set, IReadOnlyList<(LayerSpec Spec, IOverlayLayer Layer)> Layers, IOverlayLayer Ring);

    /// <summary>Builds the layers for <paramref name="set"/> when they are not built yet, then shows everything and starts the refresh.</summary>
    private void Apply(long generation, RenderedSet set)
    {
        lock (_gate)
        {
            if (generation != _generation || !_wanted || _disposed || _faulted) return;
        }
        try
        {
            var wasVisible = _visible;
            // A layout older than the one built (a show that rendered before a rebuild's newer layout) is not built again.
            if (_built is null || set.Sequence > _built.Set.Sequence)
            {
                var previous = _built;
                _built = Build(set);
                if (previous is not null) Retire(previous); // disposed once the new layers show
            }
            var now = _now();
            if (!wasVisible) _breathStart = now; // a rebuild keeps the breathing phase (control_overlay.py:318-319)
            Refresh(now);
            foreach (var (_, layer) in _built.Layers) layer.Show();
            _built.Ring.Show();
            _visible = true;
            Volatile.Write(ref _builtMonitors, _built.Set.Monitors);
            DisposeRetired();
            _timer ??= StartTimer();
        }
        catch (Exception e)
        {
            Fault(e);
            throw;
        }
    }

    private Built Build(RenderedSet set)
    {
        var created = new List<(LayerSpec, IOverlayLayer)>();
        IOverlayLayer? ring = null;
        try
        {
            for (var i = 0; i < set.Specs.Count; i++)
            {
                var spec = set.Specs[i];
                var layer = _layers.Create(spec.Bounds, spec.Name);
                created.Add((spec, layer));
                layer.SetContent(set.Bitmaps[i]);
            }
            var ringSpec = OverlayLayout.CursorRing(_cursor());
            ring = _layers.Create(ringSpec.Bounds, ringSpec.Name);
            ring.SetContent(set.Ring);
            return new Built(set, created, ring);
        }
        catch
        {
            foreach (var (_, layer) in created) DisposeQuietly(layer);
            if (ring is not null) DisposeQuietly(ring);
            throw;
        }
    }

    // The 50 ms tick (and the first frame of a show): breathing, the ring at the cursor, LastRefresh.
    private void Refresh(long now)
    {
        if (_built is not { } built) return;
        var opacity = OverlayLayout.BreathOpacity((double)(now - _breathStart) / Stopwatch.Frequency);
        foreach (var (spec, layer) in built.Layers)
            if (spec.Breathes) layer.SetOpacity(opacity);
        built.Ring.SetOpacity(opacity);
        var cursor = _cursor();
        built.Ring.MoveTo(new ScreenPoint(cursor.X - OverlayLayout.CursorSize / 2, cursor.Y - OverlayLayout.CursorSize / 2));
        Volatile.Write(ref _lastRefresh, now);
    }

    private void Tick()
    {
        if (!_visible || _faulted) return;
        try
        {
            Refresh(_now());
        }
        catch (Exception e)
        {
            Fault(e);
            return;
        }
        StartLayoutCheck();
    }

    private Task<IDisposable> StartTimer()
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The indicator has no dispatcher.");
        return dispatcher.StartTimerAsync(RefreshInterval, Tick);
    }

    private void StopTimer()
    {
        var timer = _timer;
        _timer = null;
        if (timer is null) return;
        // A timer whose start has not finished yet is stopped when it does; its ticks do nothing meanwhile (_visible).
        if (timer.IsCompletedSuccessfully) timer.Result.Dispose();
        else
            timer.ContinueWith(static t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void HideLayers()
    {
        try
        {
            _visible = false;
            StopTimer();
            if (_built is { } built)
            {
                foreach (var (_, layer) in built.Layers) layer.Hide();
                built.Ring.Hide();
            }
        }
        catch (Exception e)
        {
            Fault(e);
            throw;
        }
    }

    private void DestroyLayers()
    {
        _visible = false;
        StopTimer();
        if (_built is { } built)
        {
            Retire(built);
            _built = null;
        }
        DisposeRetired();
    }

    private void Retire(Built built)
    {
        foreach (var (_, layer) in built.Layers) _retired.Add(layer);
        _retired.Add(built.Ring);
    }

    private void DisposeRetired()
    {
        foreach (var layer in _retired) DisposeQuietly(layer);
        _retired.Clear();
    }

    private void DisposeQuietly(IOverlayLayer layer)
    {
        try
        {
            layer.Dispose();
        }
        catch (Exception e)
        {
            Log(DesktopLogLevel.Debug, "An AI control indicator window could not be destroyed", e);
        }
    }

    // ---- any thread ----

    /// <summary>
    /// Sticky. On the dispatcher it also stops the refresh and hides what it can, so a broken indicator does not stay on screen
    /// (upstream closes its windows when its thread fails); elsewhere that is queued.
    /// </summary>
    private void Fault(Exception error)
    {
        if (!_faulted) Log(DesktopLogLevel.Error, "AI control indicator failed", error);
        _faulted = true;
        IDesktopDispatcher? dispatcher;
        lock (_gate) dispatcher = _dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.HasThreadAccess)
        {
            HideAfterFault();
            return;
        }
        try
        {
            Observe(dispatcher.InvokeAsync(HideAfterFault));
        }
        catch (Exception)
        {
            // The dispatcher has shut down, and its windows with it.
        }
    }

    private void HideAfterFault()
    {
        _visible = false;
        StopTimer();
        if (_built is not { } built) return;
        foreach (var layer in built.Layers.Select(l => l.Layer).Append(built.Ring))
        {
            try
            {
                layer.Hide();
            }
            catch (Exception)
            {
                // Best effort: the indicator is already faulted.
            }
        }
    }

    private void Observe(Task task) =>
        task.ContinueWith(static (t, state) => ((ControlIndicator)state!).Fault(t.Exception!.GetBaseException()), this,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void Log(DesktopLogLevel level, string message, Exception? error)
    {
        try
        {
            _log?.Invoke(level, message, error);
        }
        catch (Exception)
        {
            // A failing log sink never changes the indicator.
        }
    }
}
