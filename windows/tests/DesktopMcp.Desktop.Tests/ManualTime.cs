namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// A clock the test sets by hand. The only fake clock in this project (P2 preflight F18): share it. Moving
/// <see cref="Now"/> forward fires the timers that came due (callbacks run on the setting thread), so a
/// <c>Task.WaitAsync(TimeSpan, TimeProvider, ...)</c> deadline expires exactly when the test says.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Now
    {
        get { lock (_lock) return _now; }
        set
        {
            List<ManualTimer> due;
            lock (_lock)
            {
                _now = value;
                due = _timers.Where(timer => timer.Due <= value).ToList();
            }
            foreach (var timer in due) timer.Fire(value);
        }
    }

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (time._lock)
            {
                time._timers.Remove(this);
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = time._now + dueTime;
                time._timers.Add(this);
            }
            if (dueTime == TimeSpan.Zero) Fire(time.Now);
            return true;
        }

        public void Fire(DateTimeOffset now)
        {
            lock (time._lock)
            {
                if (!time._timers.Contains(this) || Due > now) return;
                if (_period > TimeSpan.Zero) Due += _period;
                else time._timers.Remove(this);
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (time._lock) time._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
