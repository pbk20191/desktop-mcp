using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopMcp.Desktop.Tree;

/// <summary>The Win32 window queries the window list needs (no UIA, so they never enter a hung provider).</summary>
internal interface IWindowSystem
{
    /// <summary>Top-level windows in z-order, topmost first.</summary>
    IReadOnlyList<long> EnumerateTopLevel();

    bool IsWindow(long window);
    bool IsVisible(long window);
    bool IsMinimized(long window);
    bool IsMaximized(long window);

    /// <summary>The owning process id, or 0 when it cannot be read.</summary>
    int GetProcessId(long window);

    string GetClassName(long window);

    /// <summary>The first top-level window of the class, or 0.</summary>
    long FindWindow(string className);

    long GetForegroundWindow();

    /// <summary>The top-level window that contains <paramref name="window"/> (GetAncestor GA_ROOT), or the window itself.</summary>
    long GetRoot(long window);

    /// <summary>The executable's file name ("chrome.exe"), or null.</summary>
    string? GetProcessName(int processId);

    /// <summary>The window's GUI thread (GetWindowThreadProcessId), or 0 when it cannot be read.</summary>
    uint GetThreadId(long window);

    /// <summary>The window rectangle (GetWindowRect), or an empty rectangle when it cannot be read.</summary>
    ScreenRect GetBounds(long window);

    /// <summary>The top-level window (GA_ROOT) under the point, or 0.</summary>
    long WindowFromPoint(ScreenPoint point);

    /// <summary>The window's title (GetWindowText, which reads the stored caption without messaging another process), or "".</summary>
    string GetTitle(long window);

    /// <summary>Whether the window's UI thread answers the WM_NULL check (Decisions 38).</summary>
    WindowResponse CheckResponding(long window);
}

/// <summary>Which top-level window a label's point may land on (P3 Decisions 42).</summary>
internal static class LabelWindows
{
    /// <summary>
    /// Whether the top-level window <paramref name="root"/> under a label's point counts as the label's
    /// <paramref name="window"/>: the window itself, or any top-level window that runs on its GUI thread, owned or not,
    /// such as an owned menu, an unowned #32768 menu or a classic combo box's drop-down list (ComboLBox), whose items UIA
    /// shows under the window (the same thread answers the window's WM_NULL check, and it is the same application's UI).
    /// A root on another thread, or a thread that cannot be read, does not count.
    /// </summary>
    public static bool IsLabelWindow(IWindowSystem windows, long root, long window)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (root == window) return true;
        if (root == 0 || window == 0) return false;
        var thread = windows.GetThreadId(root);
        return thread != 0 && thread == windows.GetThreadId(window);
    }
}

internal sealed unsafe class Win32WindowSystem : IWindowSystem
{
    public IReadOnlyList<long> EnumerateTopLevel()
    {
        var windows = new List<long>();
        var handle = GCHandle.Alloc(windows);
        try
        {
            PInvoke.EnumWindows(&OnWindow, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return windows;
    }

    /// <summary>How long <see cref="CheckResponding"/> waits for the window's UI thread (Decisions 38).</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Whether the window's UI thread answers WM_NULL within <see cref="ResponseTimeout"/> (SMTO_ABORTIFHUNG returns at once
    /// for a thread Windows already considers hung). <see cref="WindowResponse.Hung"/> when Windows considers the window
    /// hung (IsHungAppWindow); <see cref="WindowResponse.NotResponding"/> when the call timed out (ERROR_TIMEOUT); any other
    /// failure, such as a window that no longer exists, counts as responding, so the next UIA call reports it.
    /// </summary>
    public static WindowResponse CheckResponding(long window)
    {
        nuint result;
        if (PInvoke.SendMessageTimeout(Hwnd(window), PInvoke.WM_NULL, default, default,
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK,
                (uint)ResponseTimeout.TotalMilliseconds, &result) != 0)
            return WindowResponse.Responding;
        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        if (PInvoke.IsHungAppWindow(Hwnd(window))) return WindowResponse.Hung;
        return error == WIN32_ERROR.ERROR_TIMEOUT ? WindowResponse.NotResponding : WindowResponse.Responding;
    }

    // The static CheckResponding has the same signature, so the interface member is implemented explicitly (preflight F4).
    WindowResponse IWindowSystem.CheckResponding(long window) => CheckResponding(window);

    public uint GetThreadId(long window) => PInvoke.GetWindowThreadProcessId(Hwnd(window), null);

    public ScreenRect GetBounds(long window) =>
        PInvoke.GetWindowRect(Hwnd(window), out var rect) ? new ScreenRect(rect.left, rect.top, rect.right, rect.bottom) : default;

    public long WindowFromPoint(ScreenPoint point)
    {
        var window = PInvoke.WindowFromPoint(new System.Drawing.Point(point.X, point.Y));
        return window.IsNull ? 0 : GetRoot((nint)window);
    }

    public string GetTitle(long window)
    {
        var length = PInvoke.GetWindowTextLength(Hwnd(window));
        if (length <= 0) return "";
        var buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            var copied = PInvoke.GetWindowText(Hwnd(window), text, buffer.Length);
            return copied > 0 ? new string(text, 0, Math.Min(copied, length)) : "";
        }
    }

    public bool IsWindow(long window) => PInvoke.IsWindow(Hwnd(window));
    public bool IsVisible(long window) => PInvoke.IsWindowVisible(Hwnd(window));
    public bool IsMinimized(long window) => PInvoke.IsIconic(Hwnd(window));
    public bool IsMaximized(long window) => PInvoke.IsZoomed(Hwnd(window));

    public int GetProcessId(long window)
    {
        uint processId = 0;
        PInvoke.GetWindowThreadProcessId(Hwnd(window), &processId);
        return (int)processId;
    }

    public string GetClassName(long window)
    {
        var buffer = stackalloc char[257];
        var length = PInvoke.GetClassName(Hwnd(window), buffer, 257);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    public long FindWindow(string className) => (nint)PInvoke.FindWindow(className, null);

    public long GetForegroundWindow() => (nint)PInvoke.GetForegroundWindow();

    public long GetRoot(long window)
    {
        var root = PInvoke.GetAncestor(Hwnd(window), GET_ANCESTOR_FLAGS.GA_ROOT);
        return root.IsNull ? window : (nint)root;
    }

    // upstream tree/service.py:50-68 resolves HWND -> PID -> name with psutil; QueryFullProcessImageName works for
    // elevated processes too (limited information access), and the process list is the fallback.
    public string? GetProcessName(int processId)
    {
        if (processId <= 0) return null;
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (!process.IsNull)
        {
            try
            {
                var buffer = stackalloc char[1024];
                uint size = 1024;
                if (PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &size))
                    return Path.GetFileName(new string(buffer, 0, (int)size));
            }
            finally
            {
                PInvoke.CloseHandle(process);
            }
        }
        try
        {
            using var fallback = System.Diagnostics.Process.GetProcessById(processId);
            return fallback.ProcessName + ".exe";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static HWND Hwnd(long window) => (HWND)(nint)window;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnWindow(HWND window, LPARAM data)
    {
        ((List<long>)GCHandle.FromIntPtr(data).Target!).Add((nint)window);
        return true;
    }
}
