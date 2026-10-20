using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Threading;

namespace DesktopMcp.Probe;

/// <summary>
/// Root-cause experiment for the WinRT clipboard blocking on an MTA thread under NativeAOT (P0 follow-up, P2 Task 18).
/// Hypothesis: an MTA call reaches the clipboard's apartment-threaded objects through the process's main STA; when the
/// first STA thread of the process has already exited, nothing pumps that apartment and the call waits forever. Each
/// mode must run in a new process, because the outcome depends on what the process did before. Read-only: it only
/// reads the clipboard text.
/// </summary>
internal static class ClipboardMtaProbe
{
    public static async Task<bool> RunAsync(string mode)
    {
        await using var setup = await PrepareAsync(mode);
        if (setup is null)
        {
            Console.Error.WriteLine($"clipboard-mta: unknown mode '{mode}' (fresh | after-sta | with-dispatcher)");
            return false;
        }
        try
        {
            var (apartment, text) = await Task.Run(() => (BootstrapProbe.DescribeApartment(), WinRtClipboard.GetText()))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"clipboard-mta {mode}: WinRT GetText on {apartment}: OK ({text?.Length.ToString() ?? "no"} text)");
            return true;
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"clipboard-mta {mode}: WinRT GetText on the thread pool (MTA) blocked for 5 s");
            return false;
        }
        catch (Exception e)
        {
            Console.WriteLine($"clipboard-mta {mode}: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            return false;
        }
    }

    private static async Task<Setup?> PrepareAsync(string mode)
    {
        switch (mode)
        {
            case "fresh":
                // No STA thread has ever existed in this process.
                return new Setup(null);
            case "after-sta":
                // The first STA thread of the process reads the clipboard and exits, as SystemClipboard's StaThread does.
                var staApartment = await StaThread.RunAsync(() => { _ = WinRtClipboard.GetText(); return BootstrapProbe.DescribeApartment(); },
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Console.WriteLine($"clipboard-mta after-sta: first STA thread was {staApartment} and has exited");
                return new Setup(null);
            case "with-dispatcher":
                // A live main STA with a message loop: the Windows App SDK dispatcher thread.
                var dispatcher = DispatcherQueueThread.Start();
                Console.WriteLine($"clipboard-mta with-dispatcher: dispatcher thread is {await dispatcher.InvokeAsync(BootstrapProbe.DescribeApartment)}");
                return new Setup(dispatcher);
            default:
                return null;
        }
    }

    private sealed class Setup(IAsyncDisposable? resource) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => resource?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
