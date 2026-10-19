using DesktopMcp.Desktop.Capture;

namespace DesktopMcp.Desktop.Tests;

[Trait("Category", "Desktop")]
public class GraphicsCaptureServiceTests
{
    public GraphicsCaptureServiceTests() => AppRuntime.EnsureInitialized();

    [Fact]
    public async Task CapturesThePrimaryMonitor()
    {
        Assert.True(GraphicsCaptureService.IsSupported);
        var image = await new GraphicsCaptureService().CapturePrimaryMonitorAsync(CancellationToken.None);

        Assert.True(image.Width > 0 && image.Height > 0);
        Assert.Equal(image.Width * image.Height * 4, image.Bgra.Length);
        var nonBlack = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4 * 97)
            if (image.Bgra[i] != 0 || image.Bgra[i + 1] != 0 || image.Bgra[i + 2] != 0) nonBlack++;
        Assert.True(nonBlack > 100, "the capture is entirely black");
    }
}
