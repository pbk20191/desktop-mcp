using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// Makes the test process PerMonitorV2 DPI aware, like desktop-mcp.exe (app.manifest), so Win32, UIA and capture report
/// physical pixels. The first call sets the process awareness; every call then checks that the calling thread really
/// runs PerMonitorV2 and throws if it does not (a host that already chose another awareness, or a thread override), so a
/// geometry test never runs silently under a scaled context.
/// </summary>
internal static partial class TestDpi
{
    private const nint PerMonitorAwareV2 = -4; // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

    private static readonly Lazy<bool> Applied = new(() => SetProcessDpiAwarenessContext(PerMonitorAwareV2));

    public static void EnsurePerMonitorV2()
    {
        var set = Applied.Value;
        var current = GetThreadDpiAwarenessContext();
        if (!AreDpiAwarenessContextsEqual(current, PerMonitorAwareV2))
            throw new InvalidOperationException(
                "The test thread is not PerMonitorV2 DPI aware (DPI_AWARENESS " + GetAwarenessFromDpiAwarenessContext(current)
                + "; SetProcessDpiAwarenessContext " + (set ? "succeeded" : "failed") + "), so Win32, UIA and capture "
                + "would report scaled coordinates. Run the tests in a host that has not set another DPI awareness.");
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    private static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AreDpiAwarenessContextsEqual(nint a, nint b);

    [LibraryImport("user32.dll")]
    private static partial int GetAwarenessFromDpiAwarenessContext(nint context);
}
