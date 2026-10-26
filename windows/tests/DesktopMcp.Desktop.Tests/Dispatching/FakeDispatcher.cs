using DesktopMcp.Desktop.Dispatching;

namespace DesktopMcp.Desktop.Tests.Dispatching;

/// <summary>
/// An <see cref="IDesktopDispatcher"/> that runs work inline on the caller's thread and records timers instead of
/// starting them: a test drives a timer by invoking its recorded tick (<see cref="Timers"/>, <see cref="TickAll"/>).
/// </summary>
internal sealed class FakeDispatcher : IDesktopDispatcher
{
    private readonly List<FakeTimer> _timers = [];

    public bool HasThreadAccess => true;
    public int ManagedThreadId => Environment.CurrentManagedThreadId;
    public int Disposals { get; private set; }
    public IReadOnlyList<FakeTimer> Timers => _timers;

    public event Action<Exception>? UnhandledException;

    public Task InvokeAsync(Action action)
    {
        try { action(); return Task.CompletedTask; }
        catch (Exception e) { return Task.FromException(e); }
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        try { return Task.FromResult(func()); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }

    public Task InvokeAsync(Func<Task> func)
    {
        try { return func(); }
        catch (Exception e) { return Task.FromException(e); }
    }

    public Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick)
    {
        var timer = new FakeTimer(interval, () => TimerTicks.Guard(tick, e => UnhandledException?.Invoke(e)));
        _timers.Add(timer);
        return Task.FromResult<IDisposable>(timer);
    }

    /// <summary>Runs one tick of every running timer.</summary>
    public void TickAll()
    {
        foreach (var timer in _timers.ToArray()) timer.Tick();
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }

    internal sealed class FakeTimer(TimeSpan interval, Action tick) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;
        public bool Stopped { get; private set; }
        /// <summary>Runs the tick, as the dispatcher would; a stopped timer does nothing.</summary>
        public void Tick()
        {
            if (!Stopped) tick();
        }
        public void Dispose() => Stopped = true;
    }
}
