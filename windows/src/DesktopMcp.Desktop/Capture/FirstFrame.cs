namespace DesktopMcp.Desktop.Capture;

/// <summary>
/// Hands the first frame a capture delivers to one waiter and disposes every other frame (P0 follow-up "dispose a late
/// frame on timeout or cancel"). Frames arrive on the frame pool's thread; once the waiter gives up, a frame that is
/// already set or arrives later is disposed here, so none is leaked whichever side gets there first.
/// </summary>
internal sealed class FirstFrame<T> where T : class, IDisposable
{
    private readonly TaskCompletionSource<T> _frame = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Takes a frame (from the FrameArrived handler); a frame nobody can use any more is disposed.</summary>
    public void Offer(Func<T?> take)
    {
        try
        {
            var frame = take();
            if (frame is not null && !_frame.TrySetResult(frame)) frame.Dispose();
        }
        catch (Exception e)
        {
            _frame.TrySetException(e);
        }
    }

    /// <summary>Waits for the first frame; on timeout (<see cref="TimeoutException"/>) or cancellation no frame is kept.</summary>
    public async Task<T> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _frame.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Close the slot: a later Offer then fails TrySetResult and disposes its frame; a frame that won the race is ours.
            // (A failed read leaves the slot faulted, and there is nothing to dispose.)
            if (!_frame.TrySetCanceled() && _frame.Task.IsCompletedSuccessfully) _frame.Task.Result.Dispose();
            throw;
        }
    }
}
