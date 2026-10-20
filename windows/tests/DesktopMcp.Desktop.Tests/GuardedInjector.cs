using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// The only injector a desktop test may use (Decisions 11). Before every Send it checks that the foreground window
/// belongs to the test window's thread and that every mouse event lands on the test window (absolute events at their
/// denormalised point, wheel and button events without coordinates at the cursor; relative moves are refused). If any
/// check fails it throws and forwards nothing, so a test can never type or click into the user's applications.
/// The one exception keeps the user's machine from being left with a key or button down: a batch made only of ups that
/// match downs this guard forwarded is always forwarded, with mouse ups stripped of their move (so they release at the
/// current cursor and never move it).
/// </summary>
internal sealed partial class GuardedInjector : IInputInjector
{
    private const uint GaRoot = 2;
    private const MouseFlags ButtonDowns = MouseFlags.LeftDown | MouseFlags.RightDown | MouseFlags.MiddleDown;
    private const MouseFlags ButtonUps = MouseFlags.LeftUp | MouseFlags.RightUp | MouseFlags.MiddleUp;

    private readonly IInputInjector _inner;
    private readonly nint _window;
    private readonly uint _thread;
    private readonly Func<nint> _foreground;
    private readonly Func<nint, uint> _threadOf;
    private readonly Func<nint, bool> _isWindow;
    private readonly Func<ScreenPoint, nint> _rootAt;
    private readonly Func<ScreenRect> _virtualScreen;
    private readonly Func<ScreenPoint> _cursor;
    private readonly HashSet<ushort> _keysDown = [];
    private MouseFlags _buttonsDown;

    public GuardedInjector(IInputInjector inner, nint window)
        : this(inner, window, TestForeground.GetForegroundWindow, w => TestForeground.GetWindowThreadProcessId(w, out _), IsWindow,
            p => GetAncestor(WindowFromPoint(new TestForeground.Point { X = p.X, Y = p.Y }), GaRoot), VirtualScreen, TestForeground.Cursor)
    {
    }

    internal GuardedInjector(IInputInjector inner, nint window, Func<nint> foreground, Func<nint, uint> threadOf, Func<nint, bool> isWindow,
        Func<ScreenPoint, nint> rootAt, Func<ScreenRect> virtualScreen, Func<ScreenPoint> cursor)
    {
        _inner = inner;
        _window = window;
        _foreground = foreground;
        _threadOf = threadOf;
        _isWindow = isWindow;
        _rootAt = rootAt;
        _virtualScreen = virtualScreen;
        _cursor = cursor;
        _thread = window == 0 ? 0 : threadOf(window);
        if (_thread == 0) throw new ArgumentException("The guarded window does not exist.", nameof(window));
    }

    public void Send(IReadOnlyList<InjectedInput> inputs)
    {
        var reason = Refusal(inputs);
        if (reason is null)
        {
            _inner.Send(inputs);
        }
        else if (IsMatchedRelease(inputs))
        {
            _inner.Send([.. inputs.Select(i => i.IsKeyboard ? i : new InjectedInput(false, i.Mouse & ButtonUps, 0, 0, 0, default))]);
        }
        else
        {
            throw new InvalidOperationException($"Test input guard: {reason}; nothing was sent.");
        }
        Track(inputs);
    }

    private bool IsMatchedRelease(IReadOnlyList<InjectedInput> inputs)
    {
        if (inputs.Count == 0) return false;
        foreach (var input in inputs)
        {
            if (input.IsKeyboard)
            {
                if (!input.Key.Up || input.Key.VirtualKey == 0 || !_keysDown.Contains(input.Key.VirtualKey)) return false;
            }
            else
            {
                var ups = input.Mouse & ButtonUps;
                var other = input.Mouse & ~(ButtonUps | MouseFlags.AbsoluteMove);
                if (ups == 0 || other != 0 || (Downs(ups) & ~_buttonsDown) != 0) return false;
            }
        }
        return true;
    }

    private void Track(IReadOnlyList<InjectedInput> inputs)
    {
        foreach (var input in inputs)
        {
            if (input.IsKeyboard)
            {
                if (input.Key.VirtualKey == 0) continue;
                if (input.Key.Up) _keysDown.Remove(input.Key.VirtualKey);
                else _keysDown.Add(input.Key.VirtualKey);
            }
            else
            {
                _buttonsDown |= input.Mouse & ButtonDowns;
                _buttonsDown &= ~Downs(input.Mouse & ButtonUps);
            }
        }
    }

    // Each button's up flag is its down flag shifted left by one (LeftDown 0x2 / LeftUp 0x4, and so on).
    private static MouseFlags Downs(MouseFlags ups) => (MouseFlags)((uint)ups >> 1) & ButtonDowns;

    private string? Refusal(IReadOnlyList<InjectedInput> inputs)
    {
        if (!_isWindow(_window)) return "the test window no longer exists";
        var foreground = _foreground();
        if (foreground == 0) return "there is no foreground window";
        var foregroundThread = _threadOf(foreground);
        if (foregroundThread != _thread)
            return $"the foreground window 0x{foreground:X} belongs to thread {foregroundThread}, not the test window's thread {_thread}";

        var screen = _virtualScreen();
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
                point = cursor ?? _cursor();
            }

            var root = _rootAt(point);
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
