using System.Diagnostics;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Overlay;
using DesktopMcp.Desktop.Tests.Dispatching;

namespace DesktopMcp.Desktop.Tests.Overlay;

// Default run: fake layers, an inline dispatcher whose timers the rig ticks, a fake art and a manual clock. No window is created.
public class ControlIndicatorTests
{
    [Fact]
    public async Task ShowCreatesEveryLayerAndTheRingAndShowsThem()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        Assert.Equal(7, rig.Layers.Count);                     // 4 edges, notice glow, notice, ring
        Assert.All(rig.Layers, l => Assert.True(l.IsVisible));
    }

    [Fact]
    public async Task AnOlderShowNeverOverridesANewerHide()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        rig.Indicator.Hide(5);
        await rig.Indicator.ShowAsync(4, default);
        Assert.DoesNotContain(rig.Layers, l => l.IsVisible);
    }

    [Fact]
    public async Task IndicatorRebuildsWhenTheMonitorsChange()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        rig.Monitors = [new ScreenRect(-1280, 0, 0, 1024), new ScreenRect(0, 0, 1920, 1080)];
        rig.TickRefresh();
        Assert.Contains(rig.Layers, l => l.IsVisible && l.Bounds == new ScreenRect(-1280, 0, 0, 56));
        Assert.All(rig.Layers.Where(l => l.Disposed), l => Assert.False(l.IsVisible));
    }

    [Fact]
    public async Task TheRingFollowsTheCursorAndEdgesBreathe()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        rig.Cursor = new ScreenPoint(500, 400);
        rig.Advance(1.5); rig.TickRefresh();
        Assert.Equal(new ScreenPoint(456, 356), rig.Ring.Position);
        Assert.Equal((byte)140, rig.Layers[0].Opacity);
        Assert.Null(rig.Layers.Single(l => l.Name == "0_notice").Opacity);   // the notice text does not breathe
    }

    [Fact]
    public async Task AFailingLayerMakesTheIndicatorUnhealthyAndShowThrows()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)], failShow: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Indicator.ShowAsync(1, default));
        Assert.False(rig.Indicator.Healthy);
    }

    // ---- beyond the brief: the F19 show bound, hide, health and dispose rules ----

    [Fact]
    public async Task ARenderFailureRejectsTheShowWithoutFaulting()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        rig.Art.Fail = new DesktopException("Control indicator failed: Win2D did not finish within 10 s.");
        await Assert.ThrowsAsync<DesktopException>(() => rig.Indicator.ShowAsync(1, default));
        Assert.True(rig.Indicator.Healthy);
        Assert.Empty(rig.Layers);

        rig.Art.Fail = null;
        await rig.Indicator.ShowAsync(2, default);
        Assert.All(rig.Layers, l => Assert.True(l.IsVisible));
    }

    [Fact]
    public async Task ARenderWaitsWithTheCallersTokenNotTheShowBound()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)], showLimit: TimeSpan.FromMilliseconds(50));
        rig.Art.Hold = new TaskCompletionSource();
        var show = rig.Indicator.ShowAsync(1, default);
        await Task.Delay(200);                                 // well past the show bound, still rendering
        Assert.False(show.IsCompleted);
        rig.Art.Hold.SetResult();
        await show;
        Assert.True(rig.Indicator.Healthy);
        Assert.All(rig.Layers, l => Assert.True(l.IsVisible));
    }

    [Fact]
    public async Task ACancelledRenderRejectsTheShowWithoutFaulting()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        rig.Art.Hold = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();
        var show = rig.Indicator.ShowAsync(1, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => show);
        Assert.True(rig.Indicator.Healthy);
        Assert.Empty(rig.Layers);
    }

    [Fact]
    public async Task ADispatcherThatNeverAcknowledgesTheShowFaultsAfterTheBound()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)], showLimit: TimeSpan.FromMilliseconds(100),
            dispatcher: new StalledDispatcher());
        await Assert.ThrowsAsync<TimeoutException>(() => rig.Indicator.ShowAsync(1, default));
        Assert.False(rig.Indicator.Healthy);
    }

    [Fact]
    public async Task HideStopsTheRefreshAndKeepsTheLayersForTheNextShow()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        var shown = rig.Layers.ToArray();
        rig.Indicator.Hide(2);
        Assert.DoesNotContain(rig.Layers, l => l.IsVisible);
        Assert.All(rig.Dispatcher.Timers, t => Assert.True(t.Stopped));

        await rig.Indicator.ShowAsync(3, default);
        Assert.Equal(shown, rig.Layers);                       // the same layers, none created and none disposed
        Assert.All(rig.Layers, l => Assert.True(l.IsVisible && !l.Disposed));
        Assert.Single(rig.Dispatcher.Timers, t => !t.Stopped);
    }

    [Fact]
    public async Task AShowAfterANewerShowOfTheSameGenerationIsNotStale()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(2, default);
        await rig.Indicator.ShowAsync(2, default);
        rig.Indicator.Hide(1);                                 // older: ignored
        Assert.All(rig.Layers, l => Assert.True(l.IsVisible));
    }

    [Fact]
    public async Task AVisibleIndicatorWithoutARecentRefreshIsUnhealthy()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        rig.Advance(0.9);
        Assert.True(rig.Indicator.Healthy);
        rig.Advance(0.2);
        Assert.False(rig.Indicator.Healthy);
        rig.TickRefresh();
        Assert.True(rig.Indicator.Healthy);

        rig.Indicator.Hide(2);
        rig.Advance(5);
        Assert.True(rig.Indicator.Healthy);                    // a hidden indicator is not refreshed
    }

    [Fact]
    public async Task ARefreshFailureIsSticky()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        rig.CursorFails = true;
        rig.TickRefresh();
        Assert.False(rig.Indicator.Healthy);
        rig.CursorFails = false;
        rig.TickRefresh();
        Assert.False(rig.Indicator.Healthy);
        Assert.Contains(rig.Log, e => e.Level == DesktopLogLevel.Error);
    }

    [Fact]
    public async Task DisposeDestroysEveryLayer()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        await rig.Indicator.DisposeAsync();
        Assert.All(rig.Layers, l => Assert.True(l.Disposed));
        Assert.All(rig.Dispatcher.Timers, t => Assert.True(t.Stopped));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Indicator.ShowAsync(2, default));
    }

    [Fact]
    public async Task TheArtIsRenderedOncePerMonitorLayout()
    {
        var rig = new IndicatorRig(monitors: [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Indicator.ShowAsync(1, default);
        var renders = rig.Art.Renders;
        rig.Indicator.Hide(2);
        await rig.Indicator.ShowAsync(3, default);
        Assert.Equal(renders, rig.Art.Renders);
    }

    /// <summary>A dispatcher that accepts work and never runs it (a stalled dispatcher thread).</summary>
    private sealed class StalledDispatcher : IDesktopDispatcher
    {
        public bool HasThreadAccess => false;
        public int ManagedThreadId => -1;
        public event Action<Exception>? UnhandledException { add { } remove { } }
        public Task InvokeAsync(Action action) => new TaskCompletionSource().Task;
        public Task<T> InvokeAsync<T>(Func<T> func) => new TaskCompletionSource<T>().Task;
        public Task InvokeAsync(Func<Task> func) => new TaskCompletionSource().Task;
        public Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick) => new TaskCompletionSource<IDisposable>().Task;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class IndicatorRig
{
    private long _now = 1_000_000;

    public IndicatorRig(IReadOnlyList<ScreenRect> monitors, bool failShow = false, TimeSpan? showLimit = null,
        IDesktopDispatcher? dispatcher = null)
    {
        Monitors = monitors;
        Factory = new FakeLayerFactory { FailShow = failShow };
        IDesktopDispatcher used = dispatcher ?? Dispatcher;
        Indicator = new ControlIndicator(new DispatcherHost(() => used), Factory, Art, () => Monitors,
            () => CursorFails ? throw new InvalidOperationException("GetCursorPos failed") : Cursor, () => Volatile.Read(ref _now),
            (level, message, error) => { lock (Log) Log.Add((level, message, error)); })
        {
            ShowLimit = showLimit ?? TimeSpan.FromSeconds(3),
        };
    }

    public FakeDispatcher Dispatcher { get; } = new();
    public FakeLayerFactory Factory { get; }
    public FakeOverlayArt Art { get; } = new();
    public ControlIndicator Indicator { get; }
    public IReadOnlyList<ScreenRect> Monitors { get; set; }
    public ScreenPoint Cursor { get; set; } = new(100, 100);
    public bool CursorFails { get; set; }
    public List<(DesktopLogLevel Level, string Message, Exception? Error)> Log { get; } = [];

    public IReadOnlyList<FakeLayer> Layers => Factory.Created;
    public FakeLayer Ring => Factory.Created.Last(l => l.Name == "cursor");

    public void Advance(double seconds) => Interlocked.Add(ref _now, (long)(seconds * Stopwatch.Frequency));

    /// <summary>One 50 ms refresh tick, then the monitor-layout check it started on the thread pool (and any rebuild).</summary>
    public void TickRefresh()
    {
        Dispatcher.TickAll();
        Indicator.LayoutCheck.GetAwaiter().GetResult();
    }
}

/// <summary>Records every layer it creates; a layer records its content, opacity, position, visibility and disposal.</summary>
internal sealed class FakeLayerFactory : IOverlayLayerFactory
{
    private readonly List<FakeLayer> _created = [];

    public bool FailShow { get; set; }

    public IReadOnlyList<FakeLayer> Created
    {
        get { lock (_created) return _created.ToArray(); }
    }

    public IOverlayLayer Create(ScreenRect bounds, string name)
    {
        var layer = new FakeLayer(this, bounds, name);
        lock (_created) _created.Add(layer);
        return layer;
    }
}

internal sealed class FakeLayer(FakeLayerFactory factory, ScreenRect bounds, string name) : IOverlayLayer
{
    public string Name { get; } = name;
    public ScreenRect Bounds { get; private set; } = bounds;
    public bool IsVisible { get; private set; }
    public bool Disposed { get; private set; }
    public byte? Opacity { get; private set; }
    public OverlayBitmap? Content { get; private set; }
    public ScreenPoint Position => new(Bounds.Left, Bounds.Top);

    public void SetContent(OverlayBitmap bitmap)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Content = bitmap;
    }

    public void SetOpacity(byte opacity)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Opacity = opacity;
    }

    public void MoveTo(ScreenPoint topLeft)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Bounds = new ScreenRect(topLeft.X, topLeft.Y, topLeft.X + Bounds.Width, topLeft.Y + Bounds.Height);
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (factory.FailShow) throw new InvalidOperationException("AI control indicator window could not be shown");
        IsVisible = true;
    }

    public void Hide()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        IsVisible = false;
    }

    // As DestroyWindow: a destroyed window is not visible.
    public void Dispose()
    {
        Disposed = true;
        IsVisible = false;
    }
}

/// <summary>A notice that fits every monitor and 1x1 bitmaps; a render can be held or made to fail.</summary>
internal sealed class FakeOverlayArt : IOverlayArt
{
    private int _renders;

    public Exception? Fail { get; set; }
    public TaskCompletionSource? Hold { get; set; }
    public int Renders => Volatile.Read(ref _renders);

    public PixelSize? NoticeSize(int screenWidth) => new PixelSize(400, 120);

    public async Task<OverlayBitmap> RenderAsync(LayerArt art, CancellationToken ct)
    {
        Interlocked.Increment(ref _renders);
        if (Hold is { } hold) await hold.Task.WaitAsync(ct).ConfigureAwait(false);
        if (Fail is { } fail) throw fail;
        return new OverlayBitmap(1, 1, new byte[4]);
    }
}
