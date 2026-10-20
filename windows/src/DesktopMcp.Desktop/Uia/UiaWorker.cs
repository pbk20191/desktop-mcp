using System.Collections.Concurrent;
using System.Globalization;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Uia;

/// <summary>
/// The UIA hang breaker (P0 follow-up, Decisions 2). Jobs run one at a time on a dedicated MTA thread that owns a
/// session (the UIA client). A job that misses its deadline gives a <see cref="UiaTimeoutException"/>; its thread is
/// abandoned (it stays blocked in the hung application until the call returns) and the next job gets a new thread
/// and a new session. A cancelled wait abandons the thread the same way but throws <see cref="OperationCanceledException"/>,
/// so callers block nothing for it (preflight F5). When <see cref="MaxStuckWorkers"/> abandoned threads are still blocked,
/// jobs fail at once with <see cref="UiaUnavailableException"/>.
/// </summary>
/// <remarks>
/// <para>Each job gets a token that is cancelled when its deadline passes or its caller cancels (preflight F4): a walk on an
/// abandoned thread checks it at each step and stops, instead of racing the next walk for the shared budget and ids.</para>
/// <para>COM release: the generated interfaces (CsWin32 over <c>[GeneratedComInterface]</c>) come back as shared
/// <c>ComObject</c> wrappers, on which <c>ComObject.FinalRelease</c> does nothing (it releases unique instances only, and
/// the generated marshallers never create those), so a COM object is released when its wrapper is finalized. A walk
/// creates thousands of them (about 1 KB managed and 3 KB native each, measured in P2 Task 10), and the managed side alone
/// does not make the GC run soon. So a job that allocated <see cref="CollectAfterAllocatedBytes"/> or more on the worker
/// thread is followed by a blocking gen1 collection on that thread, which queues the finalizers of the job's (young)
/// wrappers without a full gen2 pause of the whole server. It runs after the job's result is delivered, so it does not
/// count against that job's deadline, but it delays the next job on the thread. Probes allocate far less and skip it.</para>
/// </remarks>
internal sealed class UiaWorker<TSession> : IDisposable where TSession : class
{
    public const int MaxStuckWorkers = 4;

    /// <summary>About a thousand UIA wrappers: the managed allocation after which a job is followed by a collection.</summary>
    public const long CollectAfterAllocatedBytes = 1 << 20;

    // Task.WaitAsync's limit (uint.MaxValue - 1 ms, as for timers).
    private const double MaxDeadlineMs = uint.MaxValue - 1.0;

    private readonly Func<TSession> _createSession;
    private readonly TimeProvider _time;
    private readonly long _collectAfterBytes;
    private readonly Action _collect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private Worker? _worker;
    private bool _disposed;
    private int _stuck;

    /// <param name="createSession">Creates the session on the worker thread.</param>
    /// <param name="time">Times the deadlines (<see cref="TimeProvider.System"/> by default; a test passes its own).</param>
    public UiaWorker(Func<TSession> createSession, TimeProvider? time = null)
        : this(createSession, time ?? TimeProvider.System, CollectAfterAllocatedBytes,
            static () => GC.Collect(1, GCCollectionMode.Forced, blocking: true))
    {
    }

    /// <summary>A test passes its own threshold and collection.</summary>
    internal UiaWorker(Func<TSession> createSession, TimeProvider time, long collectAfterBytes, Action collect)
    {
        _createSession = createSession;
        _time = time;
        _collectAfterBytes = collectAfterBytes;
        _collect = collect;
    }

    /// <summary>Abandoned threads whose job has not returned yet.</summary>
    public int StuckWorkers => Volatile.Read(ref _stuck);

    /// <summary>
    /// Runs <paramref name="job"/> on the worker thread, after the jobs before it, and waits up to
    /// <paramref name="deadline"/> for it. The job's token is cancelled when the deadline passes or
    /// <paramref name="cancellationToken"/> is cancelled while the job runs; either abandons the thread.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<TSession, CancellationToken, T> job, TimeSpan deadline, CancellationToken cancellationToken)
    {
        // Validated before anything is posted: a bad deadline must not look like a hang and abandon a healthy thread.
        if ((deadline < TimeSpan.Zero && deadline != Timeout.InfiniteTimeSpan) || deadline.TotalMilliseconds > MaxDeadlineMs)
            throw new ArgumentOutOfRangeException(nameof(deadline), deadline, "The deadline must be non-negative or infinite.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stuck = StuckWorkers;
            if (stuck >= MaxStuckWorkers)
                throw new UiaUnavailableException(
                    $"UI Automation is not responding: {stuck} calls are still blocked in other applications.");
            Worker worker;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                worker = _worker ??= new Worker(this);
            }

            var jobCancellation = new CancellationTokenSource();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            // The deadline starts before the job is posted, so it covers the whole time the job holds the thread.
            var waiting = completion.Task.WaitAsync(deadline, _time, cancellationToken);
            worker.Post(() =>
            {
                // Abandoned before it started (a timeout or cancellation raced the post): do not run it at all.
                if (jobCancellation.IsCancellationRequested)
                {
                    completion.TrySetCanceled(jobCancellation.Token);
                    return;
                }
                try { completion.TrySetResult(job(worker.Session, jobCancellation.Token)); }
                catch (Exception e) { completion.TrySetException(e); }
            });

            try
            {
                await waiting.ConfigureAwait(false);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException && !completion.Task.IsCompleted)
            {
                // The job is still running or not started: give up its thread. The token is cancelled first, so a job that
                // has not started yet never runs; it is left undisposed, since the job may still read it. A callback the job
                // registered must not replace the timeout or the cancellation.
                try { jobCancellation.Cancel(); }
                catch (Exception) { }
                Abandon(worker);
                cancellationToken.ThrowIfCancellationRequested();
                throw new UiaTimeoutException(
                    $"UI Automation did not answer within {deadline.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.");
            }
            catch (Exception) when (completion.Task.IsCompleted)
            {
                // The job finished (perhaps just after the deadline): its own result or exception is returned below.
            }
            jobCancellation.Dispose();
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Accepts no more jobs. A running job finishes on its thread, which then ends.</summary>
    public void Dispose()
    {
        Worker? worker;
        lock (_lock)
        {
            _disposed = true;
            worker = _worker;
            _worker = null;
        }
        worker?.Close(abandoned: false);
    }

    private void Abandon(Worker worker)
    {
        lock (_lock)
        {
            if (_worker == worker) _worker = null;
        }
        // Counted before the close, so the thread's decrement (when the job returns) never comes first.
        Interlocked.Increment(ref _stuck);
        if (!worker.Close(abandoned: true)) Interlocked.Decrement(ref _stuck);
    }

    private sealed class Worker
    {
        private readonly UiaWorker<TSession> _owner;
        private readonly BlockingCollection<Action> _queue = [];
        private TSession? _session;
        private int _closed;
        private volatile bool _abandoned;

        public Worker(UiaWorker<TSession> owner)
        {
            _owner = owner;
            var thread = new Thread(Run) { IsBackground = true, Name = "desktop-mcp UIA" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        /// <summary>The session, created on this thread by the first job that needs it (again after a failed creation).</summary>
        public TSession Session => _session ??= _owner._createSession();

        public void Post(Action work)
        {
            try
            {
                _queue.Add(work);
            }
            catch (InvalidOperationException)
            {
                // Closed by Dispose after the job took the worker.
                throw new ObjectDisposedException(nameof(UiaWorker<TSession>));
            }
        }

        /// <summary>Ends the thread after its current job. False when it was already closed.</summary>
        public bool Close(bool abandoned)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return false;
            _abandoned = abandoned;
            _queue.CompleteAdding();
            return true;
        }

        private void Run()
        {
            try
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    work();
                    if (GC.GetAllocatedBytesForCurrentThread() - allocated >= _owner._collectAfterBytes) _owner._collect();
                }
            }
            finally
            {
                if (_abandoned) Interlocked.Decrement(ref _owner._stuck);
            }
        }
    }
}

/// <summary>
/// Windows and processes that made a UIA call time out are skipped for <c>duration</c> (about 90 s, Decisions 2), so a
/// hung application costs one deadline, not one per snapshot. A process id of 0 (unknown) blocks the window only.
/// </summary>
internal sealed class HangBlockList(TimeProvider time, TimeSpan duration)
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(90);

    private readonly Lock _gate = new();
    private readonly Dictionary<long, DateTimeOffset> _windows = [];
    private readonly Dictionary<int, DateTimeOffset> _processes = [];

    public void Block(long window, int processId)
    {
        var now = time.GetUtcNow();
        var until = now + duration;
        lock (_gate)
        {
            // Expired entries go here too, not only on lookup, so the list stays as small as the live blocks.
            Prune(_windows, now);
            Prune(_processes, now);
            _windows[window] = until;
            if (processId > 0) _processes[processId] = until;
        }
    }

    public bool IsBlocked(long window, int processId)
    {
        var now = time.GetUtcNow();
        lock (_gate)
            return Active(_windows, window, now) || (processId > 0 && Active(_processes, processId, now));
    }

    /// <summary>Live entries (windows plus processes), for the tests.</summary>
    internal int Count
    {
        get { lock (_gate) return _windows.Count + _processes.Count; }
    }

    private static void Prune<TKey>(Dictionary<TKey, DateTimeOffset> entries, DateTimeOffset now) where TKey : notnull
    {
        foreach (var (key, until) in entries)
            if (until <= now) entries.Remove(key);
    }

    private static bool Active<TKey>(Dictionary<TKey, DateTimeOffset> entries, TKey key, DateTimeOffset now) where TKey : notnull
    {
        if (!entries.TryGetValue(key, out var until)) return false;
        if (until > now) return true;
        entries.Remove(key);
        return false;
    }
}
