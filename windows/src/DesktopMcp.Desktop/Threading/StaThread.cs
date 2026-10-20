namespace DesktopMcp.Desktop.Threading;

/// <summary>
/// Runs one piece of work on a new single-threaded-apartment thread (WinRT clipboard calls need STA).
/// Cancellation only stops the wait: the work is abandoned, not interrupted, so it may still run to completion
/// (and change state) after the returned task is cancelled. The thread is a background thread, so it never keeps the
/// process alive.
/// </summary>
internal static class StaThread
{
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception e) { completion.SetException(e); }
        })
        {
            IsBackground = true,
            Name = "desktop-mcp STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(cancellationToken);
    }

    public static Task RunAsync(Action work, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            work();
            return true;
        }, cancellationToken);
}
