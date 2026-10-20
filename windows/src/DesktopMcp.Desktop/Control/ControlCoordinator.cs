using System.Diagnostics;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Desktop.Overlay;
using DesktopMcp.Desktop.Snapshots;

namespace DesktopMcp.Desktop.Control;

/// <summary>
/// The thread-pool side of desktop control (upstream <c>desktop/control.py</c>, <c>control_worker.py</c>; spec §7
/// "Input-lock safety"; Decisions 4, 12, 14, 16-19). One lock guards the <see cref="ControlStateMachine"/>; the hook never
/// takes it. A heartbeat on the thread pool (never a dispatcher timer) drains the monitor's events, ticks, checks health,
/// faults and recovers, and renews the hook's 1 s suppression deadline only while <c>ai</c> is armed, so a stalled
/// heartbeat lets the deadline lapse and the hook fail open. State changes are applied outside the lock, one at a time in
/// generation order: a change to anything but <c>ai</c> blocks and releases the ledger and hides the indicator.
/// </summary>
public sealed class ControlCoordinator : IDesktopControl
{
    internal static readonly TimeSpan BeatInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan HealthInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StartRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BeatLimit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LoopErrorInterval = TimeSpan.FromSeconds(5);
    private const int DrainLimit = 128;

    private readonly ControlSettings _settings;
    private readonly IInputMonitor _monitor;
    private readonly Func<IControlIndicator> _indicators;
    private readonly Func<bool> _interactiveDesktop;
    private readonly TimeProvider _time;
    private readonly DesktopLog? _log;
    private readonly Action _ensureRuntime;
    private readonly CancellationTokenSource _loopStop = new();
    private readonly LogThrottle _loopErrors;
    private readonly LogThrottle _startErrors = new(long.MaxValue); // a start failure warns once per distinct error

    // The state machine and every field below it, up to the apply gate.
    private readonly Lock _lock = new();
    private readonly ControlStateMachine _machine;
    private readonly Queue<ControlStatus> _changes = new();
    private long _lastQueued;
    private bool _started;        // StartAsync was called
    private bool _running;        // the monitor runs and the machine started
    private bool _stopped;
    private bool _retryStart;
    private long _nextStart;
    private long _nextHealth;
    private long _faultedAt;
    private long _showing;        // the generation whose indicator show is in flight (0 = none)
    private bool _loopStarted;
    private Task _loop = Task.CompletedTask;
    private IControlIndicator _indicator;

    // Applies the queued changes one at a time, in generation order (taken outside _lock, never inside it).
    private readonly Lock _applyGate = new();

    private volatile bool _paused;

    internal ControlCoordinator(ControlSettings settings, IInputMonitor monitor, Func<IControlIndicator> indicators, Func<bool> interactiveDesktop,
        TimeProvider time, DesktopLog? log = null, Action? ensureRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(indicators);
        ArgumentNullException.ThrowIfNull(interactiveDesktop);
        ArgumentNullException.ThrowIfNull(time);
        _settings = settings;
        _monitor = monitor;
        _indicators = indicators;
        _interactiveDesktop = interactiveDesktop;
        _time = time;
        _log = log;
        _ensureRuntime = ensureRuntime ?? (static () => { });
        _machine = new ControlStateMachine(settings, time);
        _loopErrors = new LogThrottle(Ticks(LoopErrorInterval));
        Ledger = new InputLedger(UserHolds);
        Ledger.BlockNew(); // until a call enters ai
        Checkpoint = new CallCheckpoint(this);
        _indicator = indicators();
    }

    /// <summary>
    /// <see cref="DisabledControl"/> when control is off; else the real input monitor and indicator on the shared
    /// dispatcher, <see cref="InteractiveDesktop.IsAvailable"/>, the system clock, and other programs' injected input
    /// counting as the user's (Open question 1). Cheap: nothing starts until <see cref="StartAsync"/>.
    /// </summary>
    public static IDesktopControl Create(ControlSettings settings, DesktopLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled) return new DisabledControl(settings);
        ControlCoordinator? coordinator = null;
        // The hook blocks the ledger on a chord or fail-open; the coordinator exists before the hooks are installed.
        var filter = new PhysicalInputFilter(Stopwatch.GetTimestamp, Stopwatch.Frequency, ForeignInjectedPolicy.Physical,
            () => coordinator?.Ledger.BlockNew());
        var monitor = new InputMonitor(DispatcherHost.Shared, filter, Stopwatch.GetTimestamp);
        coordinator = new ControlCoordinator(settings, monitor,
            () => new ControlIndicator(DispatcherHost.Shared, new LayeredWindowFactory(log), OverlayArt.Shared, Win32Displays.MonitorBounds,
                DesktopSession.CursorPosition, Stopwatch.GetTimestamp, log),
            InteractiveDesktop.IsAvailable, TimeProvider.System, log, AppRuntime.EnsureInitialized);
        return coordinator;
    }

    public ControlSettings Settings => _settings;

    public InputLedger Ledger { get; }

    public IActionCheckpoint Checkpoint { get; }

    /// <summary>
    /// Raised outside the state lock, one change at a time in generation order, on whichever thread applied it (the
    /// heartbeat or a caller). Handlers must be fast and must not block: the next change waits for them. As upstream, the
    /// intermediate states inside one Begin or one beat collapse into the final one (its generation may skip).
    /// </summary>
    public event Action<ControlStatus>? StateChanged;

    /// <summary>Tests: false starts no heartbeat loop; the test drives <see cref="RunOnceAsync"/> (preflight F2).</summary>
    internal bool LoopEnabled { get; init; } = true;

    /// <summary>Tests: the heartbeat was started (or would have been, with <see cref="LoopEnabled"/> false).</summary>
    internal bool LoopStarted
    {
        get
        {
            lock (_lock) return _loopStarted;
        }
    }

    /// <summary>Tests: a paused heartbeat does nothing, as a stalled one would.</summary>
    internal bool HeartbeatPaused
    {
        set => _paused = value;
    }

    public ControlStatus Status()
    {
        ControlStatus status;
        lock (_lock)
        {
            _machine.Tick();
            QueueChangeLocked();
            PublishLocked(_time.GetTimestamp());
            status = _machine.Status;
        }
        ApplyChanges();
        return status;
    }

    public async Task<ControlLease> BeginCallAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var filter = _monitor.Filter;
        BeginResult begun;
        try
        {
            lock (_lock)
            {
                try
                {
                    // The monitor's flags are copied in first, so a key pressed since the last beat is seen (preflight F15).
                    begun = _machine.Begin(filter.Held, filter.TakeoverPending);
                }
                finally
                {
                    QueueChangeLocked(); // Begin's own tick may have changed the state, even when it rejects
                    PublishLocked(_time.GetTimestamp());
                }
            }
        }
        finally
        {
            ApplyChanges();
        }

        // The ledger is enabled only after the older changes were applied, so a release still in progress cannot block it again.
        var lease = new ControlLease(begun.Token, EndCall);
        try
        {
            ControlStatus? rejected = null;
            var show = false;
            lock (_lock)
            {
                if (_machine.Status.Generation == begun.Token)
                {
                    try
                    {
                        Ledger.Enable();
                        // The hook blocks the ledger without this lock on a chord or fail-open; one that landed during Enable
                        // must leave it blocked (upstream arm_visible re-reads the hook's flags). The fence orders the
                        // enabling write before the flag reads, as the hook writes its flag before blocking.
                        Interlocked.MemoryBarrier();
                        if (filter.TakeoverPending || filter.FailOpenRequested) Ledger.BlockNew();
                    }
                    catch (InvalidOperationException e)
                    {
                        FaultLocked(_time.GetTimestamp(), "AI input is still held after a release", e);
                        QueueChangeLocked();
                        PublishLocked(_time.GetTimestamp());
                        rejected = _machine.Status;
                    }
                    // Entering ai shows the indicator; so does joining an unarmed ai with no show in flight (an earlier
                    // show failed), rather than leaving every call preempted until the lease runs out.
                    if (rejected is null && (begun.NeedsIndicator || (!_machine.Armed && _showing != begun.Token)))
                    {
                        show = true;
                        _showing = begun.Token;
                    }
                }
            }
            if (rejected is not null)
            {
                ApplyChanges();
                throw new ControlRejectedException(ControlCodes.ControlUnavailable, rejected);
            }

            if (show)
            {
                try
                {
                    await ShowIndicatorAsync(begun.Token, ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_showing == begun.Token) _showing = 0;
                    }
                }
                lock (_lock)
                {
                    // Only after the indicator is visible (Decisions 15), with the monitor's live flags and stamp: refused when
                    // input, a held key or a takeover came since entering ai.
                    _machine.PhysicalHeld = filter.Held;
                    _machine.TakeoverPending = filter.TakeoverPending;
                    _machine.Arm(begun.Token, filter.LastPhysical);
                    PublishLocked(_time.GetTimestamp(), renew: true);
                }
            }
            return lease;
        }
        catch
        {
            lease.Dispose(); // ends the call
            throw;
        }
    }

    public ControlRejectedException Interrupted(Exception interruption, ControlLease? lease) =>
        ControlErrors.Interrupted(interruption, lease, Status);

    /// <summary>
    /// Starts the monitor (Decisions 14). With no interactive desktop or a failed monitor start, control stays
    /// <c>unavailable</c> and the heartbeat retries every 5 s; a missing Windows App SDK runtime
    /// (<see cref="InvalidOperationException"/> from <see cref="AppRuntime"/>) is not retried. Then the heartbeat starts.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_started || _stopped) return;
            _started = true;
            _retryStart = true;
        }
        try
        {
            await TryStartAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log(DesktopLogLevel.Warning, "Desktop control start was cancelled; the heartbeat retries in 5 s.", null);
            throw;
        }
        finally
        {
            // A cancelled or failed first attempt still leaves the heartbeat running, which retries the start.
            StartLoop();
        }
    }

    private void StartLoop()
    {
        lock (_lock)
        {
            if (_stopped || _loopStarted) return;
            _loopStarted = true;
            if (!LoopEnabled) return;
            var token = _loopStop.Token;
            _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Stops for good: <c>unavailable</c>, suppression off, new presses refused, the heartbeat and the monitor stopped,
    /// the indicator disposed, held AI input released (errors logged), then <see cref="StateChanged"/>.
    /// </summary>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            if (_stopped) return;
            _stopped = true;
            _machine.Stop();
            QueueChangeLocked();
            PublishLocked(_time.GetTimestamp());
        }
        Ledger.BlockNew();
        _loopStop.Cancel();
        Task loop;
        lock (_lock) loop = _loop;
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log(DesktopLogLevel.Error, "The control heartbeat failed while stopping.", e);
        }
        lock (_lock) _running = false;
        try
        {
            await _monitor.StopAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log(DesktopLogLevel.Error, "The input monitor failed to stop.", e);
        }
        try
        {
            await Volatile.Read(ref _indicator).DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log(DesktopLogLevel.Error, "The AI control indicator failed to close.", e);
        }
        try
        {
            Ledger.ReleaseAll();
        }
        catch (Exception e)
        {
            Log(DesktopLogLevel.Error, "Releasing AI input failed while stopping.", e);
        }
        ApplyChanges(raiseOnly: true);
        _loopStop.Dispose(); // the loop has ended, and a stopped coordinator never reads the token again
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// One heartbeat iteration (<c>control.py:450-574</c>): retry a failed start when due; then, only if the lock is
    /// free at once (else skip this round, <c>refresh_deadline</c>), copy the monitor's flags, drain up to 128 events, tick,
    /// fault on a fail-open request, check health every 250 ms (faulting or recovering), and publish the suppression and
    /// state to the hook; then apply the changes outside the lock.
    /// </summary>
    internal async Task RunOnceAsync()
    {
        if (_paused) return;
        bool retry;
        lock (_lock) retry = !_stopped && !_running && _retryStart && _time.GetTimestamp() >= _nextStart;
        if (retry) await TryStartAsync(_loopStop.Token).ConfigureAwait(false);

        if (!_lock.TryEnter()) return;
        IControlIndicator? retired;
        try
        {
            retired = BeatLocked();
        }
        finally
        {
            _lock.Exit();
        }
        ApplyChanges();
        if (retired is not null) _ = DisposeRetiredAsync(retired);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await Task.Delay(BeatInterval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await RunOnceAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A repeating failure logs once per distinct error, or again after 5 s.
                var level = _loopErrors.Allow(LogThrottle.Key(e), _time.GetTimestamp()) ? DesktopLogLevel.Error : DesktopLogLevel.Debug;
                Log(level, "The control heartbeat failed.", e);
            }
        }
    }

    private async Task TryStartAsync(CancellationToken ct)
    {
        lock (_lock) _nextStart = _time.GetTimestamp() + Ticks(StartRetryInterval);
        if (!Interactive())
        {
            Log(DesktopLogLevel.Debug, "No interactive desktop: desktop control stays unavailable and retries in 5 s.", null);
            return;
        }
        try
        {
            _ensureRuntime();
        }
        catch (InvalidOperationException e)
        {
            lock (_lock) _retryStart = false;
            Log(DesktopLogLevel.Error, "Desktop control is unavailable: the Windows App SDK runtime could not start.", e);
            return;
        }
        catch (Exception e)
        {
            Log(DesktopLogLevel.Warning, "Desktop control could not start the Windows App SDK runtime; retrying in 5 s.", e);
            return;
        }
        try
        {
            await _monitor.StartAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var level = _startErrors.Allow(LogThrottle.Key(e), _time.GetTimestamp()) ? DesktopLogLevel.Warning : DesktopLogLevel.Debug;
            Log(level, "The input monitor failed to start; desktop control stays unavailable and retries in 5 s.", e);
            return;
        }
        _startErrors.Reset();

        bool stale;
        lock (_lock)
        {
            stale = _stopped;
            if (!stale)
            {
                _running = true;
                _machine.Start(_monitor.Filter.LastPhysical);
                try
                {
                    Ledger.Enable();
                }
                catch (InvalidOperationException e)
                {
                    Log(DesktopLogLevel.Warning, "AI input is still held at control start.", e);
                }
                _nextHealth = 0;
                QueueChangeLocked();
                PublishLocked(_time.GetTimestamp());
            }
        }
        if (stale)
        {
            // A stop came while the monitor was starting; it may have stopped the monitor before this start finished.
            try
            {
                await _monitor.StopAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(DesktopLogLevel.Error, "The input monitor failed to stop after a start that a stop overtook.", e);
            }
            return;
        }
        ApplyChanges();
    }

    // Returns an indicator replaced by recovery, to dispose outside the lock.
    private IControlIndicator? BeatLocked()
    {
        if (!_running || _stopped) return null;
        var now = _time.GetTimestamp();
        var filter = _monitor.Filter;
        _machine.PhysicalHeld = filter.Held;
        _machine.TakeoverPending = filter.TakeoverPending;
        for (var i = 0; i < DrainLimit && filter.Events.TryRead(out var e); i++)
        {
            _machine.Handle(e);
            // The machine cleared its copy; clear the monitor's too, or the next copy sets it again (preflight F3).
            if (e.Kind == PhysicalEventKind.Hotkey) filter.ClearTakeover();
        }
        _machine.Tick();
        if (filter.FailOpenRequested) FaultLocked(now, "The input hook failed open.", null);
        IControlIndicator? retired = null;
        if (now >= _nextHealth)
        {
            _nextHealth = now + Ticks(HealthInterval);
            retired = HealthLocked(now);
        }
        QueueChangeLocked();
        PublishLocked(now, renew: true);
        return retired;
    }

    // control.py:450-499: the session and input desktop, the indicator and the dispatcher's beat; recovery after a rehook.
    private IControlIndicator? HealthLocked(long now)
    {
        var interactive = Interactive();
        var beat = now - _monitor.LastBeat <= Ticks(BeatLimit);
        IControlIndicator? retired = null;
        if (_machine.Faulted && interactive && beat && !_indicator.Healthy)
        {
            // An indicator failure is sticky: recovery starts over with a new one (the old one was hidden by the fault).
            try
            {
                var fresh = _indicators();
                retired = _indicator;
                Volatile.Write(ref _indicator, fresh);
            }
            catch (Exception e)
            {
                Log(DesktopLogLevel.Error, "The AI control indicator could not be recreated.", e);
            }
        }
        var indicatorHealthy = _indicator.Healthy;
        if (!(interactive && beat && indicatorHealthy))
        {
            FaultLocked(now, !interactive ? "The interactive desktop is not available (locked, disconnected or a secure desktop)."
                : !beat ? "The input monitor's dispatcher stopped beating."
                : "The AI control indicator is unhealthy.", null);
            return retired;
        }
        if (_machine.Faulted && _monitor.LastRehook > _faultedAt) RecoverLocked();
        return retired;
    }

    private void RecoverLocked()
    {
        try
        {
            Ledger.ReleaseAll();
        }
        catch (Exception e)
        {
            Log(DesktopLogLevel.Warning, "Releasing AI input failed; desktop control stays unavailable.", e);
            return;
        }
        if (Ledger.Pending.Count > 0) return;
        try
        {
            Ledger.Enable();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        // Before suppression can resume: the hook passes nothing through by request, and no stale chord stays pending.
        _monitor.Filter.ClearFailOpen();
        _monitor.Filter.ClearTakeover();
        _machine.Recover();
        Log(DesktopLogLevel.Warning, "Desktop control recovered.", null);
    }

    private void FaultLocked(long now, string reason, Exception? error)
    {
        if (!_running || _stopped || _machine.Faulted) return;
        _machine.Fault();
        _faultedAt = now;
        _monitor.RequestRehook();
        Log(DesktopLogLevel.Warning, $"Desktop control is unavailable: {reason}", error);
    }

    private async Task ShowIndicatorAsync(long generation, CancellationToken ct)
    {
        var indicator = Volatile.Read(ref _indicator);
        try
        {
            // The indicator bounds its dispatcher part by 3 s itself; the render waits for the graphics slot with the
            // call's token (preflight F19).
            await indicator.ShowAsync(generation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            ControlStatus status;
            lock (_lock)
            {
                // Only a broken indicator faults control; a render failure or timeout rejects this call alone. A stale
                // failure (the indicator was replaced, or the state moved on) never faults control that recovered since.
                if (!indicator.Healthy && ReferenceEquals(indicator, _indicator) && _machine.Status.Generation == generation)
                    FaultLocked(_time.GetTimestamp(), "The AI control indicator failed to show.", e);
                else Log(DesktopLogLevel.Warning, "The AI control indicator could not be shown.", e);
                QueueChangeLocked();
                PublishLocked(_time.GetTimestamp());
                status = _machine.Status;
            }
            ApplyChanges();
            throw new ControlRejectedException(ControlCodes.ControlUnavailable, status);
        }
    }

    private void EndCall(ControlLease lease)
    {
        lock (_lock)
        {
            _machine.End(lease.Token);
            QueueChangeLocked();
            PublishLocked(_time.GetTimestamp());
        }
        ApplyChanges();
    }

    // Queues the current status when its generation moved since the last one queued (a Begin that rejected may have
    // changed the state through its own tick; nothing is lost).
    private void QueueChangeLocked()
    {
        var status = _machine.Status;
        if (status.Generation == _lastQueued) return;
        _lastQueued = status.Generation;
        _changes.Enqueue(status);
    }

    // Publishes the state to the hook and turns suppression off when armed ai is not wanted. Only the heartbeat and the
    // arming of a call (renew) set the 1 s deadline, so a stalled heartbeat lets it lapse whatever else calls in. A chord or
    // fail-open that raced the renewal is honoured: the hook writes its flag before turning suppression off, so reading the
    // flags after our write sees it.
    private void PublishLocked(long now, bool renew = false)
    {
        var filter = _monitor.Filter;
        filter.PublishState(_machine.Status.State, _machine.LastInput, _settings.IdleTimeoutSeconds);
        if (!_running || _stopped || !_machine.SuppressionWanted || filter.TakeoverPending || filter.FailOpenRequested)
        {
            filter.SetSuppression(false, 0);
            return;
        }
        if (!renew) return;
        filter.SetSuppression(true, now + Ticks(SuppressionWindow));
        Interlocked.MemoryBarrier();
        if (filter.TakeoverPending || filter.FailOpenRequested) filter.SetSuppression(false, 0);
    }

    private void ApplyChanges(bool raiseOnly = false)
    {
        lock (_applyGate)
        {
            while (true)
            {
                ControlStatus? next;
                lock (_lock)
                {
                    if (!_changes.TryDequeue(out next)) return;
                }
                Apply(next, raiseOnly);
            }
        }
    }

    private void Apply(ControlStatus status, bool raiseOnly)
    {
        if (!raiseOnly && status.State != ControlState.Ai)
        {
            Ledger.BlockNew();
            try
            {
                Ledger.ReleaseAll();
            }
            catch (Exception e)
            {
                // control.py:188-200: input that cannot be released makes control unavailable.
                lock (_lock)
                {
                    FaultLocked(_time.GetTimestamp(), "Releasing AI input failed.", e);
                    QueueChangeLocked();
                    PublishLocked(_time.GetTimestamp());
                }
            }
            try
            {
                Volatile.Read(ref _indicator).Hide(status.Generation);
            }
            catch (Exception e)
            {
                Log(DesktopLogLevel.Error, "Hiding the AI control indicator failed.", e);
            }
        }
        try
        {
            StateChanged?.Invoke(status);
        }
        catch (Exception e)
        {
            Log(DesktopLogLevel.Error, "A control state change handler failed.", e);
        }
    }

    private async Task DisposeRetiredAsync(IControlIndicator indicator)
    {
        try
        {
            await indicator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log(DesktopLogLevel.Error, "The replaced AI control indicator failed to close.", e);
        }
    }

    // The ledger leaves alone a key or button whose physical down reached the system (Decisions 19; both names of Ctrl and Shift).
    private bool UserHolds(string name)
    {
        var filter = _monitor.Filter;
        foreach (var vk in LedgerNames.Keys(name))
            if (filter.KeyDelivered(vk)) return true;
        return LedgerNames.Button(name) is { } button && filter.ButtonDelivered(button);
    }

    private bool Interactive()
    {
        try
        {
            return _interactiveDesktop();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private long Ticks(TimeSpan span) => (long)Math.Round(span.TotalSeconds * _time.TimestampFrequency);

    private void Log(DesktopLogLevel level, string message, Exception? error)
    {
        try
        {
            _log?.Invoke(level, message, error);
        }
        catch (Exception)
        {
            // A sink must not throw; never let one break control.
        }
    }

    private sealed class CallCheckpoint(ControlCoordinator owner) : IActionCheckpoint
    {
        /// <summary>Nothing outside a call; else <c>CONTROL_PREEMPTED</c> unless the call still owns armed <c>ai</c>.</summary>
        public void Checkpoint()
        {
            if (ControlCall.Current is not { } lease) return;
            var filter = owner._monitor.Filter;
            lock (owner._lock)
            {
                // The hook's live flags, not the heartbeat's copy (control.py:299-307): a chord, a fail-open or a lapsed
                // deadline between beats preempts at once.
                owner._machine.TakeoverPending = filter.TakeoverPending;
                owner._machine.Checkpoint(lease.Token, lease.ExecutedSteps, lease.Closed);
                if (filter.FailOpenRequested || !filter.Suppressing || owner._time.GetTimestamp() > filter.Deadline)
                    throw new ControlRejectedException(ControlCodes.ControlPreempted, owner._machine.Status, lease.ExecutedSteps);
            }
        }

        public void RecordStep() => ControlCall.Current?.RecordStep();
    }
}

/// <summary>
/// Lets a repeating log entry through once per distinct key, or again once <c>interval</c> timestamp ticks have passed
/// (<see cref="long.MaxValue"/>: once per distinct key until <see cref="Reset"/>). Thread-safe.
/// </summary>
internal sealed class LogThrottle(long interval)
{
    private readonly Lock _gate = new();
    private string? _key;
    private long _at;

    public static string Key(Exception e) => e.GetType().FullName + ": " + e.Message;

    public bool Allow(string key, long now)
    {
        lock (_gate)
        {
            if (key == _key && now - _at < interval) return false;
            _key = key;
            _at = now;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate) _key = null;
    }
}
