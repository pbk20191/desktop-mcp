using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    /// <summary>How long <see cref="IsResponding"/> waits for the window's UI thread (Decisions 38).</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Whether the window's UI thread answers WM_NULL within <see cref="ResponseTimeout"/> (SMTO_ABORTIFHUNG returns at once
    /// for a thread Windows already considers hung). A window that no longer exists counts as responding, so the next UIA
    /// call reports it as a dead element instead of a hang.
    /// </summary>
    public static bool IsResponding(long window)
    {
        nuint result;
        return PInvoke.SendMessageTimeout(Hwnd(window), PInvoke.WM_NULL, default, default,
                   SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK,
                   (uint)ResponseTimeout.TotalMilliseconds, &result) != 0
            || !PInvoke.IsWindow(Hwnd(window));
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
