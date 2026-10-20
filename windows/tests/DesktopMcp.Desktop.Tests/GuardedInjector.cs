using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// The only injector a desktop test may use (Decisions 11). Before every Send it checks that the foreground window
/// belongs to the test window's thread and that every mouse event lands on the test window (absolute events at their
/// denormalised point, wheel and button events without coordinates at the cursor; relative moves are refused). If any
/// check fails it throws and forwards nothing, so a test can never type or click into the user's applications.
/// </summary>
internal sealed partial class GuardedInjector : IInputInjector
{
    private const uint GaRoot = 2;
    private readonly IInputInjector _inner;
    private readonly nint _window;
    private readonly uint _thread;

    public GuardedInjector(IInputInjector inner, nint window)
    {
        _inner = inner;
        _window = window;
        _thread = window == 0 ? 0 : TestForeground.GetWindowThreadProcessId(window, out _);
        if (_thread == 0) throw new ArgumentException("The guarded window does not exist.", nameof(window));
    }

    public void Send(IReadOnlyList<InjectedInput> inputs)
    {
        var reason = Refusal(inputs);
        if (reason is not null) throw new InvalidOperationException($"Test input guard: {reason}; nothing was sent.");
        _inner.Send(inputs);
    }

    private string? Refusal(IReadOnlyList<InjectedInput> inputs)
    {
        if (!IsWindow(_window)) return "the test window no longer exists";
        var foreground = TestForeground.GetForegroundWindow();
        if (foreground == 0) return "there is no foreground window";
        var foregroundThread = TestForeground.GetWindowThreadProcessId(foreground, out _);
        if (foregroundThread != _thread)
            return $"the foreground window 0x{foreground:X} belongs to thread {foregroundThread}, not the test window's thread {_thread}";

        var screen = VirtualScreen();
        ScreenPoint? cursor = null; // where an earlier absolute event of this batch leaves the cursor
        foreach (var input in inputs)
        {
            if (input.IsKeyboard) continue;
            ScreenPoint point;
            if ((input.Mouse & MouseFlags.Absolute) != 0)
            {
                point = new ScreenPoint(
                    screen.Left + (int)Math.Round((double)input.X * Math.Max(screen.Width - 1, 1) / 65535),
                    screen.Top + (int)Math.Round((double)input.Y * Math.Max(screen.Height - 1, 1) / 65535));
                cursor = point;
            }
            else if ((input.Mouse & MouseFlags.Move) != 0)
            {
                return "a relative mouse move cannot be checked";
            }
            else
            {
                point = cursor ?? TestForeground.Cursor();
            }

            var root = GetAncestor(WindowFromPoint(new TestForeground.Point { X = point.X, Y = point.Y }), GaRoot);
            if (root != _window)
                return $"the mouse event at ({point.X},{point.Y}) would land on window 0x{root:X}, not the test window 0x{_window:X}";
        }
        return null;
    }

    private static ScreenRect VirtualScreen()
    {
        var left = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        var top = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        return new ScreenRect(left, top, left + GetSystemMetrics(78), top + GetSystemMetrics(79));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(TestForeground.Point point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
