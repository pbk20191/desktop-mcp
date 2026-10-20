using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Capture;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Overlay;
using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Tests.Overlay;

/// <summary>
/// The real capture flash (layered windows, Win2D art) on a dispatcher host of its own. Each test shows the orange flash on
/// the screen for 3.5 s; the capture test also takes the foreground with an <see cref="ActiveTestWindow"/>. Run in the
/// user's interactive session, hands off; never in the default run.
/// </summary>
[Trait("Category", "Desktop")]
public class CaptureFlashDesktopTests
{
    private const string RegionLayers = LayeredWindow.ClassPrefix + "flash_region_";
    private static readonly TimeSpan Visible = TimeSpan.FromMilliseconds(1200);   // the region curve holds full intensity
    private static readonly TimeSpan Closed = TimeSpan.FromMilliseconds(4500);

    public CaptureFlashDesktopTests() => TestDpi.EnsurePerMonitorV2();

    private static ScreenRect Primary() => new Win32Displays().GetDisplays().First(d => d.Primary).Bounds;

    private static IReadOnlyList<long> VisibleFlashLayers()
    {
        var windows = new Win32WindowSystem();
        return windows.EnumerateTopLevel()
            .Where(w => windows.GetClassName(w).StartsWith(RegionLayers, StringComparison.Ordinal) && windows.IsVisible(w))
            .ToArray();
    }

    private static async Task WithFlashAsync(Func<CaptureFlash, Task> test)
    {
        var host = new DispatcherHost();
        var log = new List<Exception?>();
        try
        {
            var flash = new CaptureFlash(host, new LayeredWindowFactory(), OverlayArt.Shared, new Win32Displays().GetVirtualScreen,
                _ => null, TimeProvider.System, (_, _, error) => { lock (log) log.Add(error); });
            await test(flash);
            lock (log) Assert.Empty(log);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public Task TheFlashAppearsAndClosesAfterItsDuration() => WithFlashAsync(async flash =>
    {
        var primary = Primary();
        var region = new ScreenRect(primary.Left + 300, primary.Top + 300, primary.Left + 700, primary.Top + 600);
        flash.Show(region, [primary]);
        await flash.Pending;
        await Task.Delay(Visible);
        Assert.Equal(4, VisibleFlashLayers().Count);
        await Task.Delay(Closed - Visible);
        Assert.Empty(VisibleFlashLayers());
    });

    [Fact]
    public Task TheFlashIsExcludedFromCapture() => WithFlashAsync(async flash =>
    {
        var primary = Primary();
        using var target = new ActiveTestWindow(primary.Left + 100, primary.Top + 100);
        var client = TestForeground.ClientRect(target.Handle);
        // A region inside the client area, so its outward strips lie over the window's plain grey face.
        var region = new ScreenRect(client.Left + 60, client.Top + 60, client.Right - 60, client.Bottom - 60);
        flash.Show(region, [primary]);
        await flash.Pending;
        await Task.Delay(Visible);
        Assert.Equal(4, VisibleFlashLayers().Count);

        var image = await new GraphicsCaptureService().CaptureAsync(client, new Win32Displays().GetDisplays().Select(d => d.Bounds).ToArray(), default);
        var orange = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4)
        {
            byte b = image.Bgra[i], r = image.Bgra[i + 2];
            if (r - b > 24) orange++; // the dialog face is grey; any blend of the flash over it turns orange
        }
        Assert.True(orange == 0, $"{orange} orange pixels of {image.Width * image.Height}");
        await Task.Delay(Closed - Visible);
    });
}
