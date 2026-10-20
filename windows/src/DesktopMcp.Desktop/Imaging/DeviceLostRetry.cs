using Microsoft.Graphics.Canvas;

namespace DesktopMcp.Desktop.Imaging;

/// <summary>
/// Retry once when the shared <see cref="CanvasDevice"/> is lost (driver update, TDR, GPU removed): Win2D replaces a lost
/// shared device on the next <see cref="CanvasDevice.GetSharedDevice()"/> call, so the work runs once more on the new one.
/// A second loss, and every other failure, is thrown. The one copy of this rule; the renderer and the capture use it.
/// </summary>
internal static class DeviceLostRetry
{
    /// <summary>Runs <paramref name="work"/> on the shared device, and once more on its replacement if that device was lost.</summary>
    public static Task<T> RunAsync<T>(Func<CanvasDevice, Task<T>> work) =>
        RunAsync(CanvasDevice.GetSharedDevice, static (device, error) => device.IsDeviceLost(error.HResult), work);

    /// <summary>The rule with the device source and the loss check injected (tests use fakes).</summary>
    internal static async Task<T> RunAsync<TDevice, T>(Func<TDevice> getDevice, Func<TDevice, Exception, bool> isLost, Func<TDevice, Task<T>> work)
    {
        var device = getDevice();
        try
        {
            return await work(device).ConfigureAwait(false);
        }
        catch (Exception error) when (isLost(device, error))
        {
            // Fall through to one more attempt on the replacement device.
        }
        return await work(getDevice()).ConfigureAwait(false);
    }
}
