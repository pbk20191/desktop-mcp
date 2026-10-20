using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// A top-level window the desktop tests own, so they never read or touch the user's applications: a dialog-class
/// window with minimize and maximize boxes, a "Press me" button and an edit holding "hello world". It runs its own
/// message loop on an STA thread and is destroyed on Dispose. It is shown without activation, so it does not take the
/// keyboard focus from the user's foreground window, unless the <c>activate</c> flag shows it with SW_SHOW for
/// <see cref="ActiveTestWindow"/>. The dialog is subclassed to count the button's BN_CLICKED notifications.
/// </summary>
internal sealed partial class TestWindow : IDisposable
{
    public const string ButtonText = "Press me";
    public const string EditText = "hello world";

    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const uint WsExClientEdge = 0x00000200;
    private const uint WmApp = 0x8000;
    private const int SwShowNoActivate = 4;
    private const int SwShow = 5;
    private const uint WmCommand = 0x0111;
    private const int BnClicked = 0;
    private const int GwlpWndProc = -4;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly Thread _thread;
    private readonly TaskCompletionSource<(nint Window, uint ThreadId)> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, TestWindow> Subclassed = new();
    private nint _originalProc;
    private int _buttonClicks;

    public TestWindow(int left = 120, int top = 120, int width = 420, int height = 260, bool activate = false)
    {
        TestDpi.EnsurePerMonitorV2();
        Title = $"desktop-mcp test {Guid.NewGuid():N}";
        _thread = new Thread(() => Run(left, top, width, height, activate)) { IsBackground = true, Name = "test window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        try
        {
            (Handle, ThreadId) = _created.Task.WaitAsync(WaitLimit).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            // The thread destroys the window itself if it creates it after this; if it won the race, it is told to stop.
            if (!_created.TrySetCanceled() && _created.Task.IsCompletedSuccessfully)
                PostThreadMessageW(_created.Task.Result.ThreadId, WmApp, 0, 0);
            throw;
        }
    }

    public string Title { get; }
    public nint Handle { get; }

    /// <summary>The window's thread (the GUI thread <see cref="GuardedInjector"/> and <see cref="ActiveTestWindow"/> check).</summary>
    public uint ThreadId { get; }

    public nint ButtonHandle { get; private set; }

    public nint EditHandle { get; private set; }

    /// <summary>BN_CLICKED notifications the button sent to the dialog.</summary>
    public int ButtonClicks => Volatile.Read(ref _buttonClicks);

    /// <summary>The edit's current text (GetWindowTextW, answered by the window's thread).</summary>
    public string ReadEditText()
    {
        var length = GetWindowTextLengthW(EditHandle);
        var buffer = new char[length + 1];
        var copied = GetWindowTextW(EditHandle, buffer, buffer.Length);
        return new string(buffer, 0, Math.Clamp(copied, 0, length));
    }

    public void Dispose()
    {
        if (!PostThreadMessageW(ThreadId, WmApp, 0, 0))
            throw new InvalidOperationException($"PostThreadMessage failed: {Marshal.GetLastPInvokeError()}");
        if (!_thread.Join(WaitLimit)) throw new TimeoutException("The test window's thread did not end within 10 s.");
    }

    private void Run(int left, int top, int width, int height, bool activate)
    {
        nint window = 0;
        try
        {
            var module = GetModuleHandleW(null);
            window = CreateWindowExW(0, "#32770", Title, WsOverlappedWindow, left, top, width, height, 0, 0, module, 0);
            if (window == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
            ButtonHandle = CreateChild(0, "BUTTON", ButtonText, 20, 20, 120, 32, window, 1, module);
            EditHandle = CreateChild(WsExClientEdge, "EDIT", EditText, 20, 70, 300, 28, window, 2, module);
            Subclass(window);
            ShowWindow(window, activate ? SwShow : SwShowNoActivate);
            if (!_created.TrySetResult((window, GetCurrentThreadId()))) return; // the constructor gave up waiting
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == WmApp && message.Window == 0) break;
                TranslateMessage(in message);
                DispatchMessageW(in message);
            }
        }
        catch (Exception e)
        {
            _created.TrySetException(e);
        }
        finally
        {
            if (window != 0)
            {
                DestroyWindow(window);
                Subclassed.TryRemove(window, out _);
            }
        }
    }

    private unsafe void Subclass(nint window)
    {
        Subclassed[window] = this;
        _originalProc = SetWindowLongPtrW(window, GwlpWndProc, (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WindowProc);
        if (_originalProc == 0) throw new InvalidOperationException($"SetWindowLongPtr failed: {Marshal.GetLastPInvokeError()}");
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (!Subclassed.TryGetValue(window, out var owner)) return DefWindowProcW(window, message, wParam, lParam);
        if (message == WmCommand && (int)((wParam >> 16) & 0xFFFF) == BnClicked && lParam == owner.ButtonHandle && lParam != 0)
            Interlocked.Increment(ref owner._buttonClicks);
        return CallWindowProcW(owner._originalProc, window, message, wParam, lParam);
    }

    private static nint CreateChild(uint exStyle, string className, string text, int x, int y, int width, int height,
        nint parent, nint id, nint module)
    {
        var child = CreateWindowExW(exStyle, className, text, WsChild | WsVisible, x, y, width, height, parent, id, module, 0);
        if (child == 0) throw new InvalidOperationException($"CreateWindowEx({className}) failed: {Marshal.GetLastPInvokeError()}");
        return child;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Msg message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in Msg message);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint CallWindowProcW(nint previous, nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(nint window);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(nint window, [Out] char[] buffer, int maxCount);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);
}
