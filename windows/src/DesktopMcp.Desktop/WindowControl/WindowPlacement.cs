using System.ComponentModel;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopMcp.Desktop.WindowControl;

/// <summary>
/// App resize (Decisions 32; upstream desktop/service.py:444-476 calls MoveWindow synchronously): the window must answer
/// the WM_NULL check, then SetWindowPos with SWP_ASYNCWINDOWPOS, which cannot block on the window's thread. A failed call
/// is an error (upstream reported success).
/// </summary>
public sealed class WindowPlacement
{
    internal const uint ResizeFlags = (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
        | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);

    private readonly IWindowSystem _windows;
    private readonly IForegroundCalls _calls;

    public WindowPlacement() : this(new Win32WindowSystem(), new Win32ForegroundCalls())
    {
    }

    internal WindowPlacement(IWindowSystem windows, IForegroundCalls calls)
    {
        _windows = windows;
        _calls = calls;
    }

    /// <summary>Moves and sizes the window to <paramref name="target"/>; throws <see cref="DesktopException"/> with the result text.</summary>
    public Task ResizeAsync(long window, string name, ScreenRect target, CancellationToken ct) =>
        // The WM_NULL check waits up to 250 ms; keep it off the caller's thread.
        Task.Run(() =>
        {
            if (_windows.CheckResponding(window) != WindowResponse.Responding) throw new DesktopException($"{name} is not responding");
            ct.ThrowIfCancellationRequested();
            if (!_calls.SetWindowPos(window, 0, target.Left, target.Top, target.Width, target.Height, ResizeFlags, out var error))
                throw new DesktopException($"Failed to resize {name}: {new Win32Exception(error).Message}");
        }, ct);
}
