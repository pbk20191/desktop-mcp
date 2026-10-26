using System.Diagnostics;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Capture;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Overlay;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Tests.Overlay;

/// <summary>
/// The real layered windows on a real dispatcher (Decisions 28). Each test shows the blue indicator (or one layer) on the
/// screen for under 2 s, most of them over an <see cref="ActiveTestWindow"/> of their own (so nothing else covers it; it
/// takes the foreground). Run in the user's interactive session, hands off; never in the default run.
/// </summary>
[Trait("Category", "Desktop")]
public class OverlayWindowDesktopTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);
    private static readonly (byte R, byte G, byte B) Blue = OverlayLayout.IndicatorBlue;

    public OverlayWindowDesktopTests() => TestDpi.EnsurePerMonitorV2();

    private static ScreenRect Primary() => new Win32Displays().GetDisplays().First(d => d.Primary).Bounds;

    [Fact]
    public async Task LayersAreClickThrough()
    {
        var primary = Primary();
        using var target = new ActiveTestWindow(primary.Left + 100, primary.Top + 10);
        await using var shown = await ShownIndicator.StartAsync();
        var point = new ScreenPoint(primary.Left + 200, primary.Top + 30); // inside the top edge layer and the test window
        Assert.Contains(shown.VisibleLayers(), layer => Contains(layer.Bounds, point));
        Assert.Equal((long)target.Handle, new Win32WindowSystem().WindowFromPoint(point));
    }

    [Fact]
    public async Task LayersAreExcludedFromCapture()
    {
        var primary = Primary();
        // The caption above the screen, so the test window's plain client area lies under the top edge.
        using var target = new ActiveTestWindow(primary.Left + 100, primary.Top - 80);
        await using var shown = await ShownIndicator.StartAsync();
        var edge = new ScreenRect(primary.Left, primary.Top, primary.Right, primary.Top + OverlayLayout.Border);
        Assert.Contains(shown.VisibleLayers(), layer => layer.Bounds == edge);
        var area = ScreenRect.Clip(TestForeground.ClientRect(target.Handle), edge)
            ?? throw new InvalidOperationException("The test window's client area is not under the top edge.");

        var image = await new GraphicsCaptureService().CaptureAsync(area, new Win32Displays().GetDisplays().Select(d => d.Bounds).ToArray(), default);
        int near = 0, bluish = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4)
        {
            byte b = image.Bgra[i], g = image.Bgra[i + 1], r = image.Bgra[i + 2];
            if (Math.Abs(r - Blue.R) <= 8 && Math.Abs(g - Blue.G) <= 8 && Math.Abs(b - Blue.B) <= 8) near++;
            if (b - r > 24) bluish++; // the dialog face is grey; any blend of the glow over it turns blue
        }
        Assert.True(near == 0 && bluish == 0, $"{near} pixels within 8 of the indicator blue, {bluish} bluish, of {image.Width * image.Height}");
    }

    [Fact]
    public async Task LayersAreNotInTheWindowCatalog()
    {
        await using var shown = await ShownIndicator.StartAsync();
        var machine = DesktopMachine.CreateDefault();
        var layers = machine.Windows.EnumerateTopLevel()
            .Where(w => machine.Windows.GetClassName(w).StartsWith(LayeredWindow.ClassPrefix, StringComparison.Ordinal)
                && machine.Windows.IsVisible(w))
            .ToArray();
        Assert.NotEmpty(layers);
        var candidates = machine.Catalog.Candidates();
        Assert.DoesNotContain(candidates, w => machine.Windows.GetClassName(w).StartsWith(LayeredWindow.ClassPrefix, StringComparison.Ordinal));
        Assert.Empty(candidates.Intersect(layers));
    }

    [Fact]
    public async Task ALabelUnderTheIndicatorIsNotCovered()
    {
        var primary = Primary();
        using var target = new ActiveTestWindow(primary.Left + 200, primary.Top + 200);
        var button = TestForeground.Center(target.Window.ButtonHandle);
        var host = new DispatcherHost();
        try
        {
            var dispatcher = await host.GetAsync(default);
            var bounds = new ScreenRect(button.X - 40, button.Y - 20, button.X + 40, button.Y + 20);
            var pixels = new byte[bounds.Width * bounds.Height * 4];
            for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (Blue.B, Blue.G, Blue.R, 255);
            var layer = await dispatcher.InvokeAsync(() =>
            {
                var created = new LayeredWindowFactory().Create(bounds, "label_test");
                created.SetContent(new OverlayBitmap(bounds.Width, bounds.Height, pixels));
                created.Show();
                return created;
            });
            try
            {
                await Task.Delay(Settle);
                Assert.True(await dispatcher.InvokeAsync(() => layer.IsVisible));
                // ActionTargets' covered check (P3 Decisions 42): the top-level window at the label's point.
                var windows = new Win32WindowSystem();
                var root = windows.WindowFromPoint(button);
                Assert.True(LabelWindows.IsLabelWindow(windows, root, target.Handle), $"the root at the button is 0x{root:X}, not the test window");
            }
            finally
            {
                await dispatcher.InvokeAsync(layer.Dispose);
            }
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task LayersNeverTakeTheForeground()
    {
        var primary = Primary();
        using var target = new ActiveTestWindow(primary.Left + 100, primary.Top + 100);
        await using var shown = await ShownIndicator.StartAsync();
        Assert.Equal(target.Handle, TestForeground.GetForegroundWindow());
    }

    private static bool Contains(ScreenRect rect, ScreenPoint point) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    /// <summary>The real indicator on its own dispatcher host, shown for every monitor, then destroyed and shut down.</summary>
    private sealed class ShownIndicator : IAsyncDisposable
    {
        private readonly DispatcherHost _host;
        private readonly Win32WindowSystem _windows = new();

        private ShownIndicator(DispatcherHost host, ControlIndicator indicator)
        {
            _host = host;
            Indicator = indicator;
        }

        public ControlIndicator Indicator { get; }

        public static async Task<ShownIndicator> StartAsync()
        {
            var host = new DispatcherHost();
            var indicator = new ControlIndicator(host, new LayeredWindowFactory(), OverlayArt.Shared,
                () => new Win32Displays().GetDisplays().Select(d => d.Bounds).ToArray(), DesktopSession.CursorPosition, Stopwatch.GetTimestamp);
            var shown = new ShownIndicator(host, indicator);
            try
            {
                await indicator.ShowAsync(1, default);
                await Task.Delay(Settle);
                Assert.True(indicator.Healthy);
                return shown;
            }
            catch
            {
                await shown.DisposeAsync();
                throw;
            }
        }

        /// <summary>The visible overlay windows (any thread may read window rectangles and visibility).</summary>
        public IReadOnlyList<(long Window, ScreenRect Bounds)> VisibleLayers() =>
            _windows.EnumerateTopLevel()
                .Where(w => _windows.GetClassName(w).StartsWith(LayeredWindow.ClassPrefix, StringComparison.Ordinal) && _windows.IsVisible(w))
                .Select(w => (w, Rect(w)))
                .ToArray();

        private ScreenRect Rect(long window) => _windows.GetBounds(window);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Indicator.DisposeAsync();
            }
            finally
            {
                await _host.ShutdownAsync();
            }
        }
    }
}
