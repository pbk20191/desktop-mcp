using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// A top-level window the desktop tests own, so they never read or touch the user's applications: a dialog-class
/// window with minimize and maximize boxes, a "Press me" button and an edit holding "hello world". It runs its own
/// message loop on an STA thread and is destroyed on Dispose. It is shown without activation, so it does not take the
/// keyboard focus from the user's foreground window.
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
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly Thread _thread;
    private readonly TaskCompletionSource<(nint Window, uint ThreadId)> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestWindow(int left = 120, int top = 120, int width = 420, int height = 260)
    {
        TestDpi.EnsurePerMonitorV2();
        Title = $"desktop-mcp test {Guid.NewGuid():N}";
        _thread = new Thread(() => Run(left, top, width, height)) { IsBackground = true, Name = "test window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        try
        {
            (Handle, ThreadId) = _created.Task.WaitAsync(WaitLimit).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            // The thread destroys the window itself if it creates it after this.
            _created.TrySetCanceled();
            throw;
        }
    }

    public string Title { get; }
    public nint Handle { get; }
    private uint ThreadId { get; }

    public void Dispose()
    {
        PostThreadMessageW(ThreadId, WmApp, 0, 0);
        _thread.Join(WaitLimit);
    }

    private void Run(int left, int top, int width, int height)
    {
        nint window = 0;
        try
        {
            var module = GetModuleHandleW(null);
            window = CreateWindowExW(0, "#32770", Title, WsOverlappedWindow, left, top, width, height, 0, 0, module, 0);
            if (window == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
            CreateWindowExW(0, "BUTTON", ButtonText, WsChild | WsVisible, 20, 20, 120, 32, window, 1, module, 0);
            CreateWindowExW(WsExClientEdge, "EDIT", EditText, WsChild | WsVisible, 20, 70, 300, 28, window, 2, module, 0);
            ShowWindow(window, SwShowNoActivate);
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
            if (window != 0) DestroyWindow(window);
        }
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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);
}
