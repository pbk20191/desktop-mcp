using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Dispatching;

namespace DesktopMcp.Probe;

/// <summary>
/// Installs WH_MOUSE_LL on the dispatcher thread, injects two signed relative mouse moves
/// (+1 px, -1 px) and checks that the hook saw both, flagged as injected, on the dispatcher thread.
/// </summary>
internal static partial class HookProbe
{
    private const nuint Signature = 0x44_4D_43_50; // "DMCP"
    private const int WhMouseLl = 14;
    private const uint LlmhfInjected = 0x01;
    private static int s_hits, s_wrongThread, s_notInjected, s_dispatcherThreadId;

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public nuint ExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; }

    [LibraryImport("user32.dll", SetLastError = true)] private static unsafe partial nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint threadId);
    [LibraryImport("user32.dll")] private static partial int UnhookWindowsHookEx(nint hook);
    [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static unsafe partial uint SendInput(uint count, Input* inputs, int size);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);

    [UnmanagedCallersOnly]
    private static unsafe nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var info = (MsllHookStruct*)lParam;
            if (info->ExtraInfo == Signature)
            {
                Interlocked.Increment(ref s_hits);
                if (Environment.CurrentManagedThreadId != s_dispatcherThreadId) Interlocked.Increment(ref s_wrongThread);
                if ((info->Flags & LlmhfInjected) == 0) Interlocked.Increment(ref s_notInjected);
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    public static async Task<bool> RunAsync(IDesktopDispatcher dispatcher)
    {
        s_dispatcherThreadId = dispatcher.ManagedThreadId;
        var (hook, error) = await dispatcher.InvokeAsync(InstallHook);
        if (hook == 0) { Console.WriteLine($"hook: SetWindowsHookExW failed ({error})"); return false; }
        try
        {
            var sent = SendSignedMoves();
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (Volatile.Read(ref s_hits) < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
            var ok = sent == 2 && s_hits >= 2 && s_wrongThread == 0 && s_notInjected == 0;
            Console.WriteLine($"hook: sent={sent} hits={s_hits} wrongThread={s_wrongThread} notInjected={s_notInjected} -> {(ok ? "OK" : "FAIL")}");
            return ok;
        }
        finally
        {
            await dispatcher.InvokeAsync(() => UnhookWindowsHookEx(hook));
        }
    }

    // Pointer work lives in synchronous helpers: C# does not allow await in an unsafe context.
    private static unsafe (nint Hook, int Error) InstallHook()
    {
        var hook = SetWindowsHookExW(WhMouseLl, &MouseProc, GetModuleHandleW(null), 0);
        return (hook, hook == 0 ? Marshal.GetLastPInvokeError() : 0);
    }

    private static unsafe uint SendSignedMoves()
    {
        var inputs = stackalloc Input[2];
        inputs[0] = new Input { Type = 0, Mouse = new MouseInput { Dx = 1, Flags = 0x0001, ExtraInfo = Signature } };  // MOUSEEVENTF_MOVE
        inputs[1] = new Input { Type = 0, Mouse = new MouseInput { Dx = -1, Flags = 0x0001, ExtraInfo = Signature } };
        return SendInput(2, inputs, sizeof(Input));
    }
}
