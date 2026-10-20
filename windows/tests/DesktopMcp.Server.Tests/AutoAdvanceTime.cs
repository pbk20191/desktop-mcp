namespace DesktopMcp.Server.Tests;

/// <summary>
/// A clock whose timers fire at once and move the clock forward by their due time (preflight F22), so a
/// <c>Task.Delay(d, time, ct)</c> completes immediately and the elapsed time reads exactly the delays awaited. Nothing
/// here sleeps for real. The callback runs on the thread pool, after <see cref="CreateTimer"/> has returned.
/// </summary>
internal sealed class AutoAdvanceTime : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class Timer(AutoAdvanceTime time, TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            // A periodic timer would fire forever; nothing here uses one, so only the first due time counts.
            if (dueTime == Timeout.InfiniteTimeSpan || Volatile.Read(ref _disposed) != 0) return true;
            if (dueTime > TimeSpan.Zero) Interlocked.Add(ref time._ticks, dueTime.Ticks);
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                if (Volatile.Read(ref _disposed) == 0) callback(state);
            }, null);
            return true;
        }

        public void Dispose() => Volatile.Write(ref _disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
