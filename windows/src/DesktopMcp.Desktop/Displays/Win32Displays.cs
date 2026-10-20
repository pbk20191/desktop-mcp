using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopMcp.Desktop.Displays;

/// <summary>The active displays and the virtual screen, in physical pixels (the exe is PerMonitorV2 DPI aware).</summary>
public interface IDisplayProvider
{
    IReadOnlyList<DisplayInfo> GetDisplays();

    /// <summary>upstream desktop/service.py:1251-1260 get_screen_box (SM_XVIRTUALSCREEN and friends).</summary>
    ScreenRect GetVirtualScreen();
}

/// <summary>
/// upstream uia/core.py:659-828 GetDisplays: EnumDisplayMonitors, GetMonitorInfo, GetDpiForMonitor (falling back to the
/// system DPI) and EnumDisplaySettings. Microsoft.UI.Windowing.DisplayArea was evaluated and not adopted (Decisions 15).
/// Needs no Windows App SDK runtime and no COM.
/// </summary>
public sealed unsafe class Win32Displays : IDisplayProvider
{
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var activeIndices = ActiveDisplayIndices();
        var monitors = new List<(HMONITOR Monitor, RECT Rect)>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            PInvoke.EnumDisplayMonitors(default, (RECT*)null, &OnMonitor, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        var displays = new List<DisplayInfo>();
        foreach (var (monitor, enumRect) in monitors)
        {
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            int index;
            string deviceName;
            ScreenRect bounds;
            ScreenRect? workArea;
            bool primary;
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                bounds = info.monitorInfo.rcMonitor.ToScreenRect();
                workArea = info.monitorInfo.rcWork.ToScreenRect();
                deviceName = info.szDevice.ToString();
                index = activeIndices.TryGetValue(deviceName.ToUpperInvariant(), out var active) ? active : NextFallbackIndex(activeIndices, displays);
                primary = (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0;
            }
            else
            {
                bounds = enumRect.ToScreenRect();
                workArea = null;
                index = NextFallbackIndex(activeIndices, displays);
                deviceName = $"DISPLAY{index}";
                primary = false;
            }
            var (dpi, scale) = EffectiveDpi(monitor);
            displays.Add(new DisplayInfo(index, deviceName, bounds, primary, workArea, dpi, scale, Orientation(deviceName, bounds)));
        }
        return displays.OrderBy(display => display.Index).ToArray();
    }

    /// <summary>
    /// Only the monitor rectangles, from <c>EnumDisplayMonitors</c> with no per-device queries: cheap enough for the control
    /// indicator, which reads the layout about 20 times a second while visible.
    /// </summary>
    internal static ScreenRect[] MonitorBounds()
    {
        var monitors = new List<(HMONITOR Monitor, RECT Rect)>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            PInvoke.EnumDisplayMonitors(default, (RECT*)null, &OnMonitor, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return [.. monitors.Select(m => m.Rect.ToScreenRect())];
    }

    public ScreenRect GetVirtualScreen()
    {
        var left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        return new ScreenRect(left, top,
            left + PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
            top + PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));
    }

    /// <summary>upstream uia/core.py:685-705: the monitor's effective DPI, else the system DPI, with scale = round(dpi / 96, 6).</summary>
    internal static (int? Dpi, double? Scale) EffectiveDpi(HMONITOR monitor)
    {
        uint dpiX, dpiY;
        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY).Succeeded && dpiX > 0)
            return DpiMetadata((int)dpiX);
        var system = (int)PInvoke.GetDpiForSystem();
        return system > 0 ? DpiMetadata(system) : (null, null);
    }

    internal static (int? Dpi, double? Scale) DpiMetadata(int dpi) => (dpi, PyText.Round(dpi / 96.0, 6));

    // upstream uia/core.py:731-748: \\.\DISPLAYn names attached to the desktop, numbered in enumeration order.
    private static Dictionary<string, int> ActiveDisplayIndices()
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var device = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        for (uint i = 0; PInvoke.EnumDisplayDevices((string?)null, i, ref device, 0); i++)
        {
            if ((device.StateFlags & DISPLAY_DEVICE_STATE_FLAGS.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                indices[device.DeviceName.ToString().ToUpperInvariant()] = indices.Count;
            device = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        }
        return indices;
    }

    // upstream uia/core.py:766-771.
    private static int NextFallbackIndex(Dictionary<string, int> activeIndices, List<DisplayInfo> displays)
    {
        var used = activeIndices.Values.Concat(displays.Select(d => d.Index)).ToHashSet();
        var index = 0;
        while (used.Contains(index)) index++;
        return index;
    }

    // upstream uia/core.py:708-728.
    private static string Orientation(string deviceName, ScreenRect bounds)
    {
        var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
        if (PInvoke.EnumDisplaySettings(deviceName, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode)
            && mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
            return mode.dmPelsWidth >= mode.dmPelsHeight ? "landscape" : "portrait";
        return bounds.Width >= bounds.Height ? "landscape" : "portrait";
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnMonitor(HMONITOR monitor, HDC dc, RECT* rect, LPARAM data)
    {
        ((List<(HMONITOR, RECT)>)GCHandle.FromIntPtr(data).Target!).Add((monitor, *rect));
        return true;
    }
}
