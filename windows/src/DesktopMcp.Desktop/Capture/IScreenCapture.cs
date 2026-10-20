using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Capture;

public interface IScreenCapture
{
    /// <summary>
    /// Captures <paramref name="area"/> (physical virtual-desktop pixels) from the monitors in <paramref name="monitors"/>
    /// that overlap it; pixels no monitor covers are black. Failures are <see cref="Errors.DesktopException"/>s, and a
    /// missing Windows App SDK runtime is an <see cref="InvalidOperationException"/> with the install hint.
    /// </summary>
    Task<CapturedImage> CaptureAsync(ScreenRect area, IReadOnlyList<ScreenRect> monitors, CancellationToken cancellationToken);
}
