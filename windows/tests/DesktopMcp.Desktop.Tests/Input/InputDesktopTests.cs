using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Tests.Input;

/// <summary>
/// Real input into the tests' own windows. Every event goes through <see cref="GuardedInjector"/>, which refuses (sending
/// nothing) unless the test window is the foreground window and every mouse event lands on it. Run with hands off the
/// mouse and keyboard: the test windows take the foreground.
/// </summary>
[Trait("Category", "Desktop")]
public partial class InputDesktopTests
{
    private static DesktopInput Guarded(nint window, IClipboard? clipboard = null) =>
        new(new InputLedger(), clipboard ?? new SystemClipboard(), NoCheckpoint.Instance, new GuardedInjector(new SendInputInjector(), window));

    [Fact]
    public async Task InjectedMouseEventsCarryTheSignature()
    {
        using var target = new ActiveTestWindow();
        var input = Guarded(target.Handle);
        var centre = TestForeground.ClientRect(target.Handle).Center;
        IReadOnlyList<nuint> recorded;
        using (var hook = new MouseHookRecorder())
        {
            await input.MoveAsync(centre, default);
            await Task.Delay(200); // let the hook thread see the last event
            recorded = hook.InjectedExtraInfo;
        }
        Assert.NotEmpty(recorded);
        Assert.All(recorded, extra => Assert.Equal((nuint)InputSignature.ExtraInfo, extra));
    }

    [Fact]
    public async Task ClickPressesTheTestWindowsButton()
    {
        using var target = new ActiveTestWindow();
        var input = Guarded(target.Handle);
        await input.ClickAsync(TestForeground.Center(target.Window.ButtonHandle), MouseButton.Left, 1, default);
        Assert.Equal(1, target.Window.ButtonClicks);
    }

    [Fact]
    public async Task TypeWritesIntoTheTestWindowsEdit()
    {
        using var target = new ActiveTestWindow();
        var clipboard = new SystemClipboard();
        var input = Guarded(target.Handle, clipboard);
        var edit = TestForeground.Center(target.Window.EditHandle);

        await input.TypeAsync(edit, "héllo \U0001F600", CaretPosition.Idle, clear: true, pressEnter: false, null, default);
        Assert.Equal("héllo \U0001F600", target.Window.ReadEditText());

        const string Long = "abcdefghijklmnopqrstuvwxy"; // 25 characters: the paste path
        var before = await clipboard.GetTextAsync(default);
        await input.TypeAsync(edit, Long, CaretPosition.Idle, clear: true, pressEnter: false, null, default);
        Assert.Equal(Long, target.Window.ReadEditText());
        Assert.Equal(before, await clipboard.GetTextAsync(default));
    }

    [Fact]
    public async Task WinFormsToolStripButtonTakesEveryClick()
    {
        using var target = new WinFormsTarget();
        target.ActivateAndWait();
        var input = Guarded(target.Handle);
        var button = target.ToolStripButtonCenter;
        for (var i = 0; i < 10; i++) await input.ClickAsync(button, MouseButton.Left, 1, default);
        Assert.Equal(10, target.ToolStripClicks);
    }

    [Fact]
    public async Task CoordinatesLandOnTheTargetPixel()
    {
        using var target = new ActiveTestWindow();
        var input = Guarded(target.Handle);
        var client = TestForeground.ClientRect(target.Handle);
        ScreenPoint[] corners =
        [
            new(client.Left + 2, client.Top + 2), new(client.Right - 3, client.Top + 2),
            new(client.Left + 2, client.Bottom - 3), new(client.Right - 3, client.Bottom - 3),
        ];
        foreach (var corner in corners)
        {
            await input.MoveAsync(corner, default);
            Assert.Equal(corner, TestForeground.Cursor());
        }
    }

    /// <summary>A WH_MOUSE_LL hook on its own thread with a message loop (as probes/DesktopMcp.Probe/HookProbe.cs).</summary>
    private sealed partial class MouseHookRecorder : IDisposable
    {
        private const int WhMouseLl = 14;
        private const uint LlmhfInjected = 0x01;
        private const uint WmQuit = 0x0012;
        private static readonly ConcurrentQueue<nuint> s_extraInfo = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource<uint> _installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly uint _threadId;

        public MouseHookRecorder()
        {
            s_extraInfo.Clear();
            _thread = new Thread(Run) { IsBackground = true, Name = "mouse hook" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _threadId = _installed.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }

        public IReadOnlyList<nuint> InjectedExtraInfo => [.. s_extraInfo];

        public void Dispose()
        {
            PostThreadMessageW(_threadId, WmQuit, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(10));
        }

        private unsafe void Run()
        {
            var hook = SetWindowsHookExW(WhMouseLl, &MouseProc, GetModuleHandleW(null), 0);
            if (hook == 0)
            {
                _installed.TrySetException(new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastPInvokeError()}"));
                return;
            }
            try
            {
                _installed.TrySetResult(GetCurrentThreadId());
                while (GetMessageW(out var message, 0, 0, 0) > 0) DispatchMessageW(in message);
            }
            finally
            {
                UnhookWindowsHookEx(hook);
            }
        }

        [UnmanagedCallersOnly]
        private static unsafe nint MouseProc(int code, nint wParam, nint lParam)
        {
            if (code >= 0)
            {
                var info = (MsllHookStruct*)lParam;
                if ((info->Flags & LlmhfInjected) != 0) s_extraInfo.Enqueue(info->ExtraInfo);
            }
            return CallNextHookEx(0, code, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MsllHookStruct
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public nuint ExtraInfo;
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

        [LibraryImport("user32.dll", SetLastError = true)]
        private static unsafe partial nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint threadId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnhookWindowsHookEx(nint hook);

        [LibraryImport("user32.dll")]
        private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        private static partial int GetMessageW(out Msg message, nint window, uint filterMin, uint filterMax);

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
}
