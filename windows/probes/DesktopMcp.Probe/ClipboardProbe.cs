using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Threading;

namespace DesktopMcp.Probe;

/// <summary>
/// Clipboard paths under NativeAOT: WinRT on an STA thread, WinRT on the thread pool (MTA), Win32, and SystemClipboard.
/// Changes the user's clipboard text and restores it in <c>finally</c>.
/// </summary>
internal static class ClipboardProbe
{
    public static async Task<bool> RunAsync()
    {
        var clipboard = new SystemClipboard();
        var saved = await clipboard.GetTextAsync(CancellationToken.None);
        Console.WriteLine($"clipboard: saved {saved?.Length ?? 0} chars through {clipboard.LastPath}");
        try
        {
            // Win32 first: under NativeAOT, WinRT on an MTA thread blocks, and Win32 calls after it time out too.
            var win32 = await CheckAsync("Win32", () => Task.Run(() => { Win32Clipboard.SetText("probe-win32"); return Win32Clipboard.GetText(); }), "probe-win32");
            await CheckAsync("WinRT on STA", () => StaThread.RunAsync(() => { WinRtClipboard.SetText("probe-winrt-sta"); return WinRtClipboard.GetText(); }, CancellationToken.None), "probe-winrt-sta");
            await CheckAsync("WinRT on MTA", () => Task.Run(() => { WinRtClipboard.SetText("probe-winrt-mta"); return WinRtClipboard.GetText(); }), "probe-winrt-mta");
            await clipboard.SetTextAsync("probe-system", CancellationToken.None);
            var system = await clipboard.GetTextAsync(CancellationToken.None) == "probe-system";
            Console.WriteLine($"clipboard: SystemClipboard {(system ? "OK" : "FAIL")} through {clipboard.LastPath}{(clipboard.LastWinRtError is { } error ? $" (WinRT: {error})" : "")}");
            return system && win32;
        }
        finally
        {
            if (saved is not null) await clipboard.SetTextAsync(saved, CancellationToken.None);
        }
    }

    // A path that blocks (WinRT on an MTA thread does under NativeAOT) reports a TimeoutException instead of hanging the probe.
    private static async Task<bool> CheckAsync(string name, Func<Task<string?>> roundTrip, string expected)
    {
        try
        {
            var text = await roundTrip().WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"clipboard: {name}: {(text == expected ? "OK" : $"FAIL (read {text ?? "null"})")}");
            return text == expected;
        }
        catch (Exception e)
        {
            Console.WriteLine($"clipboard: {name}: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            return false;
        }
    }
}
