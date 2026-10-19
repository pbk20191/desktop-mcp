using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopMcp.Desktop;
using Microsoft.UI.Windowing;

namespace DesktopMcp.Probe;

/// <summary>
/// Self-bootstraps the Windows App SDK through the OS Dynamic Dependencies API and checks that its WinRT
/// types activate: DisplayArea.Primary, and Win32Interop agreeing with it on the primary display id.
/// </summary>
internal static partial class BootstrapProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string name);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromPoint(Point point, uint flags);
    [LibraryImport("ole32.dll")] private static partial int CoGetApartmentType(out int type, out int qualifier);

    public static bool Run()
    {
        var clock = Stopwatch.StartNew();
        AppRuntime.EnsureInitialized();
        var initMs = clock.ElapsedMilliseconds;
        AppRuntime.EnsureInitialized();
        Console.WriteLine($"bootstrap: initialized in {initMs} ms, package={AppRuntime.PackageFullName}");

        var primary = DisplayArea.Primary;
        var bounds = primary.OuterBounds;
        var interopId = Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor(MonitorFromPoint(default, 1)).Value;
        var bootstrapDllLoaded = GetModuleHandleW("Microsoft.WindowsAppRuntime.Bootstrap.dll") != 0;
        Console.WriteLine($"bootstrap: DisplayArea.Primary {bounds.Width}x{bounds.Height} at ({bounds.X},{bounds.Y}), displayId={primary.DisplayId.Value}, win32InteropId={interopId}, bootstrapDllLoaded={bootstrapDllLoaded}");
        var ok = bounds.Width > 0 && bounds.Height > 0 && primary.DisplayId.Value == interopId && !bootstrapDllLoaded;
        Console.WriteLine($"bootstrap: {(ok ? "OK" : "FAIL")}");
        return ok;
    }

    /// <summary>The calling thread's COM apartment (CoGetApartmentType), e.g. "STA" or "MTA (implicit)".</summary>
    public static string DescribeApartment()
    {
        var hr = CoGetApartmentType(out var type, out var qualifier);
        if (hr < 0) return $"none (0x{hr:X8})";
        var name = type switch { 0 => "STA", 1 => "MTA", 2 => "NA", 3 => "MainSTA", -1 => "current", _ => type.ToString() };
        var detail = qualifier switch { 0 => "", 1 => " (implicit MTA)", 2 => " (NA on MTA)", 3 => " (NA on STA)", 4 => " (NA on implicit MTA)", 5 => " (NA on main STA)", 6 => " (ASTA)", 7 => " (application STA)", _ => $" (qualifier {qualifier})" };
        return name + detail;
    }
}
