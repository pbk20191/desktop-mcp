using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Interop;
using Microsoft.UI.Windowing;

namespace DesktopMcp.Desktop.Tests;

[Trait("Category", "Desktop")]
public partial class AppRuntimeTests
{
    [Fact]
    public void EnsureInitializedIsIdempotentAndThreadSafe()
    {
        var errors = new Exception?[8];
        Parallel.For(0, errors.Length, new ParallelOptions { MaxDegreeOfParallelism = errors.Length }, i =>
        {
            try { AppRuntime.EnsureInitialized(); }
            catch (Exception e) { errors[i] = e; }
        });
        Assert.All(errors, Assert.Null);
    }

    [Fact]
    public void AppSdkWinRtTypesActivate()
    {
        AppRuntime.EnsureInitialized();
        var primary = DisplayArea.Primary;
        Assert.NotNull(primary);
        Assert.True(primary.OuterBounds.Width > 0 && primary.OuterBounds.Height > 0, $"empty bounds {primary.OuterBounds.Width}x{primary.OuterBounds.Height}");
    }

    [Fact]
    public void PrimaryDisplayIdMatchesTheMonitorInterop()
    {
        AppRuntime.EnsureInitialized();
        var monitor = Native.MonitorFromPoint(default, Native.MonitorDefaultToPrimary);
        Assert.Equal(Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor(monitor).Value, DisplayArea.Primary.DisplayId.Value);
    }

    [Fact]
    public void BootstrapsWithoutTheBootstrapDll()
    {
        AppRuntime.EnsureInitialized();
        Assert.Equal(0, GetModuleHandleW("Microsoft.WindowsAppRuntime.Bootstrap.dll"));
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string moduleName);
}
