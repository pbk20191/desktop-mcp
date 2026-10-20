using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// A <see cref="TestWindow"/> the test thread makes the foreground window (AttachThreadInput + SetForegroundWindow,
/// verified with GetForegroundWindow), so injected keyboard input reaches it. If Windows refuses, nothing is sent:
/// the constructor throws and the window is destroyed.
/// </summary>
internal sealed class ActiveTestWindow : IDisposable
{
    public ActiveTestWindow(int left = 120, int top = 120, int width = 420, int height = 260)
    {
        Window = new TestWindow(left, top, width, height, activate: true);
        try
        {
            TestForeground.Activate(Window.Handle);
        }
        catch
        {
            Window.Dispose();
            throw;
        }
    }

    public TestWindow Window { get; }

    public nint Handle => Window.Handle;

    public void Dispose() => Window.Dispose();
}

/// <summary>Foreground activation and window geometry for the guarded desktop tests.</summary>
internal static partial class TestForeground
{
    public const string NotForeground = "The test window could not be made the foreground window; no input was sent.";
    private static readonly TimeSpan ActivationLimit = TimeSpan.FromSeconds(2);

    public static void Activate(nint window)
    {
        var self = GetCurrentThreadId();
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var windowThread = GetWindowThreadProcessId(window, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true);
        var attachedWindow = windowThread != 0 && windowThread != self && windowThread != foregroundThread
            && AttachThreadInput(self, windowThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attachedWindow) AttachThreadInput(self, windowThread, false);
            if (attachedForeground) AttachThreadInput(self, foregroundThread, false);
        }

        var deadline = DateTime.UtcNow + ActivationLimit;
        while (GetForegroundWindow() != window && DateTime.UtcNow < deadline) Thread.Sleep(20);
        if (GetForegroundWindow() != window) throw new InvalidOperationException(NotForeground);
    }

    /// <summary>The window's client area in screen pixels (exclusive right and bottom).</summary>
    public static ScreenRect ClientRect(nint window)
    {
        if (!GetClientRect(window, out var client)) throw new InvalidOperationException($"GetClientRect failed: {Marshal.GetLastPInvokeError()}");
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) throw new InvalidOperationException("ClientToScreen failed.");
        return new ScreenRect(origin.X + client.Left, origin.Y + client.Top, origin.X + client.Right, origin.Y + client.Bottom);
    }

    /// <summary>The centre of a window (a child control or a top-level window) in screen pixels.</summary>
    public static ScreenPoint Center(nint window)
    {
        if (!GetWindowRect(window, out var rect)) throw new InvalidOperationException($"GetWindowRect failed: {Marshal.GetLastPInvokeError()}");
        return new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom).Center;
    }

    public static ScreenPoint Cursor()
    {
        if (!GetCursorPos(out var point)) throw new InvalidOperationException($"GetCursorPos failed: {Marshal.GetLastPInvokeError()}");
        return new ScreenPoint(point.X, point.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref Point point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);
}
