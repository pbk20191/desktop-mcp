namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// A clock for the gate-budget tests: the first timestamp read is time zero, every later read is <c>hold</c> after it (the
/// time the call spent waiting at the gate). Each timer's due time is recorded and the timer fires at once, so the call
/// under test ends without a real sleep.
/// </summary>
internal sealed class BudgetTime(TimeSpan hold) : TimeProvider
{
    private int _reads;

    public List<TimeSpan> DueTimes { get; } = [];

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Increment(ref _reads) == 1 ? 0 : (long)hold.TotalMilliseconds;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (DueTimes) DueTimes.Add(dueTime);
        callback(state);
        return System.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
}
