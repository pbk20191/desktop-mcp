using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Overlay;
using DesktopMcp.Desktop.Tests.Dispatching;

namespace DesktopMcp.Desktop.Tests.Overlay;

// Default run: fake layers, an inline dispatcher whose 30 ms frame timer the rig ticks, a fake art and a manual
// Stopwatch-unit clock. No window is created and nothing is shown.
public class CaptureFlashTests
{
    private static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData("1", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("on", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheDisableVariable(string? value, bool disabled) => Assert.Equal(disabled, CaptureFlash.Disabled(value));

    [Fact]
    public async Task AFullCaptureFadesInAndOutAndCloses()
    {
        var rig = new FlashRig();
        rig.Flash.Show(null, [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Settled();
        rig.Advance(0.875); Assert.All(rig.Layers, l => Assert.Equal((byte)128, l.Opacity));   // t = 0.25: intensity 0.5
        rig.Advance(2.7); Assert.All(rig.Layers, l => Assert.True(l.Disposed));
    }

    [Fact]
    public async Task ANewFlashReplacesTheRunningOne()
    {
        var rig = new FlashRig();
        rig.Flash.Show(new ScreenRect(10, 10, 100, 100), [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Settled();
        var first = rig.Layers.ToList();
        rig.Flash.Show(null, [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Settled();
        Assert.All(first, l => Assert.True(l.Disposed));
    }

    [Fact]
    public void ADisabledFlashCreatesNothing()
    {
        var rig = new FlashRig(env: "1");
        rig.Flash.Show(null, [new ScreenRect(0, 0, 1920, 1080)]);
        Assert.Empty(rig.Layers);
    }

    [Fact]
    public async Task AFailureNeverEscapes()
    {
        var rig = new FlashRig(failCreate: true);
        rig.Flash.Show(null, [new ScreenRect(0, 0, 1920, 1080)]);
        await rig.Settled();
        // F20: the failure is logged once at debug level, and the layer created before it is not left behind.
        var entry = Assert.Single(rig.Log);
        Assert.Equal(DesktopLogLevel.Debug, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Error);
        Assert.NotEmpty(rig.Layers);
        Assert.All(rig.Layers, l => Assert.True(l.Disposed));
    }

    [Fact]
    public async Task ARegionGetsFourOutwardStripsAndAFullCaptureFourInwardStripsPerMonitor()
    {
        var rig = new FlashRig(virtualScreen: new ScreenRect(0, 0, 3840, 1080));
        rig.Flash.Show(new ScreenRect(200, 200, 600, 500), [Monitor]);
        await rig.Settled();
        Assert.Equal(["flash_region_top", "flash_region_bottom", "flash_region_left", "flash_region_right"], rig.Layers.Select(l => l.Name));
        Assert.Equal(new ScreenRect(150, 150, 650, 200), rig.Layers[0].Bounds);

        var second = new FlashRig(virtualScreen: new ScreenRect(0, 0, 3840, 1080));
        second.Flash.Show(null, [Monitor, new ScreenRect(1920, 0, 3840, 1080)]);
        await second.Settled();
        Assert.Equal(8, second.Layers.Count);
        Assert.Equal(new ScreenRect(6, 6, 1914, 56), second.Layers[0].Bounds);
        Assert.Equal("flash_1_top", second.Layers[4].Name);
    }

    [Fact]
    public async Task TheLayersStayHiddenUntilTheFlashIsVisibleAndCarryTheirContent()
    {
        var rig = new FlashRig();
        rig.Flash.Show(null, [Monitor]);
        await rig.Settled();
        Assert.Equal(4, rig.Layers.Count);
        Assert.All(rig.Layers, l => Assert.NotNull(l.Content));
        Assert.All(rig.Layers, l => Assert.False(l.IsVisible));           // t = 0: intensity 0, below the 0.04 cut-off
        Assert.Single(rig.Dispatcher.Timers, t => !t.Stopped);
        Assert.Equal(OverlayLayout.FlashFrame, rig.Dispatcher.Timers[0].Interval);
        rig.Advance(0.5);
        Assert.All(rig.Layers, l => Assert.True(l.IsVisible));
    }

    [Fact]
    public async Task ARegionFlashHoldsFullIntensityInTheMiddle()
    {
        var rig = new FlashRig();
        rig.Flash.Show(new ScreenRect(200, 200, 600, 500), [Monitor]);
        await rig.Settled();
        rig.Advance(1.75);                                                  // t = 0.5: the region curve holds 1.0
        Assert.All(rig.Layers, l => Assert.Equal((byte)255, l.Opacity));
        rig.Advance(1.6);                                                   // t ~ 0.957: 0.12
        Assert.All(rig.Layers, l => Assert.True(l.IsVisible));
        rig.Advance(0.2);
        Assert.All(rig.Layers, l => Assert.True(l.Disposed));
        Assert.All(rig.Dispatcher.Timers, t => Assert.True(t.Stopped));
    }

    [Fact]
    public async Task TheArtIsRenderedOncePerSize()
    {
        var rig = new FlashRig();
        rig.Flash.Show(null, [Monitor]);
        await rig.Settled();
        rig.Flash.Show(null, [Monitor]);
        await rig.Settled();
        Assert.Equal(4, rig.Art.Renders);
        Assert.Equal(8, rig.Layers.Count);
    }

    [Fact]
    public async Task ShowReturnsAtOnceWhileTheArtRenders()
    {
        var rig = new FlashRig();
        rig.Art.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Flash.Show(null, [Monitor]);
        Assert.Empty(rig.Layers);
        rig.Art.Hold.SetResult();
        await rig.Settled();
        Assert.Equal(4, rig.Layers.Count);
    }

    [Fact]
    public async Task ARenderFailureIsLoggedAndCreatesNothing()
    {
        var rig = new FlashRig();
        rig.Art.Fail = new InvalidOperationException("Win2D failed");
        rig.Flash.Show(null, [Monitor]);
        await rig.Settled();
        Assert.Empty(rig.Layers);
        Assert.Equal(DesktopLogLevel.Debug, Assert.Single(rig.Log).Level);
    }

    [Fact]
    public async Task AFailingFrameClosesTheFlash()
    {
        var rig = new FlashRig();
        rig.Flash.Show(null, [Monitor]);
        await rig.Settled();
        rig.Factory.FailShow = true;
        rig.Advance(0.5);
        Assert.All(rig.Layers, l => Assert.True(l.Disposed));
        Assert.Equal(DesktopLogLevel.Debug, Assert.Single(rig.Log).Level);
        Assert.All(rig.Dispatcher.Timers, t => Assert.True(t.Stopped));
    }

    [Fact]
    public async Task BrokenInputsNeverEscapeShow()
    {
        var rig = new FlashRig(virtualScreenFails: true);
        rig.Flash.Show(null, [Monitor]);
        await rig.Settled();
        Assert.Empty(rig.Layers);
        Assert.Single(rig.Log);

        var environmentFails = new FlashRig(environmentFails: true);
        environmentFails.Flash.Show(null, [Monitor]);
        Assert.Single(environmentFails.Log);
        Assert.Empty(environmentFails.Layers);

        var noMonitors = new FlashRig();
        noMonitors.Flash.Show(null, null!);
        await noMonitors.Settled();
        Assert.Empty(noMonitors.Layers);
        Assert.Single(noMonitors.Log);
    }

    [Fact]
    public async Task NothingToFlashCreatesNothing()
    {
        var rig = new FlashRig();
        rig.Flash.Show(null, []);
        await rig.Settled();
        Assert.Empty(rig.Layers);
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void TheDefaultFlashIsCheap()
    {
        // Creating the real flash starts no dispatcher and loads no graphics; nothing to observe but that it does not throw.
        Assert.IsType<CaptureFlash>(CaptureFlash.CreateDefault());
    }

    [Fact]
    public void NoFlashDoesNothing() => NoFlash.Instance.Show(null, [Monitor]);
}

internal sealed class FlashRig
{
    private readonly StopwatchClock _clock = new();

    public FlashRig(string? env = null, bool failCreate = false, ScreenRect? virtualScreen = null, bool virtualScreenFails = false,
        bool environmentFails = false)
    {
        Factory = new FakeLayerFactory { FailCreateAfter = failCreate ? 1 : null };
        var screen = virtualScreen ?? new ScreenRect(0, 0, 1920, 1080);
        Flash = new CaptureFlash(new DispatcherHost(() => Dispatcher), Factory, Art,
            () => virtualScreenFails ? throw new InvalidOperationException("GetSystemMetrics failed") : screen,
            name => environmentFails
                ? throw new InvalidOperationException("environment unavailable")
                : name == CaptureFlash.DisableVariable ? env : null,
            _clock, (level, message, error) => { lock (Log) Log.Add((level, message, error)); });
    }

    public FakeDispatcher Dispatcher { get; } = new();
    public FakeLayerFactory Factory { get; }
    public FakeOverlayArt Art { get; } = new();
    public CaptureFlash Flash { get; }
    public List<(DesktopLogLevel Level, string Message, Exception? Error)> Log { get; } = [];
    public IReadOnlyList<FakeLayer> Layers => Factory.Created;

    /// <summary>Waits for the last <see cref="CaptureFlash.Show"/>'s render and dispatcher step.</summary>
    public Task Settled() => Flash.Pending.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>Moves the clock, then runs one frame of every running timer.</summary>
    public void Advance(double seconds)
    {
        _clock.Advance(seconds);
        Dispatcher.TickAll();
    }

    /// <summary>A clock whose timestamps (ticks of 100 ns) move only when the test says.</summary>
    private sealed class StopwatchClock : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerHour;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _ticks);

        public void Advance(double seconds) => Interlocked.Add(ref _ticks, (long)(seconds * TimeSpan.TicksPerSecond));
    }
}
