using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Capture;

public interface IScreenCapture
{
    Task<CapturedImage> CapturePrimaryMonitorAsync(CancellationToken cancellationToken);
}
