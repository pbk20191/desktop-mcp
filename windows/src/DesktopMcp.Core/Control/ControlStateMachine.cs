using DesktopMcp.Core.Config;

namespace DesktopMcp.Core.Control;

public enum PhysicalEventKind { Key, Pointer, RawMotion, ForeignMotion, Hotkey }

/// <summary>
/// One input event from the monitor. <see cref="Timestamp"/> is in <see cref="TimeProvider.GetTimestamp"/> units;
/// <see cref="Device"/>, <see cref="Dx"/> and <see cref="Dy"/> describe motion (raw device units for
/// <see cref="PhysicalEventKind.RawMotion"/>, screen pixels for <see cref="PhysicalEventKind.ForeignMotion"/>).
/// </summary>
public readonly record struct PhysicalEvent(PhysicalEventKind Kind, long Timestamp, long Device = 0, int Dx = 0, int Dy = 0);

/// <summary>
/// <see cref="Token"/> is the call's generation (0 when control is disabled); <see cref="Changed"/> is the new status
/// when the call moved the state; <see cref="NeedsIndicator"/> is true when it entered <c>ai</c> from <c>ready</c>.
/// </summary>
public readonly record struct BeginResult(long Token, ControlStatus? Changed, bool NeedsIndicator);

/// <summary>
/// Desktop ownership (upstream <c>desktop/control.py</c>; spec §7). Pure and single-threaded: the coordinator
/// serialises every call under its lock. Methods returning <see cref="ControlStatus"/>? return the new status when the
/// state changed, else null.
/// </summary>
public sealed class ControlStateMachine(ControlSettings settings, TimeProvider time)
{
    public const double LeaseSeconds = 15;
    public const double MotionPauseSeconds = 0.5;

    private ControlState _state = ControlState.Unavailable;
    private long _generation;
    private bool _started;
    private bool _stopped;
    private bool _faulted;
    private bool _armed;
    // Kept beside Arm's stamp check: it also catches an event stamped before entering ai but handled after it
    // (it reached the system unswallowed while ai was not armed).
    private bool _inputSinceEntry;
    private int _activeCalls;
    private long _leaseUntil;
    private long _lastUser;
    private long _lastPhysical;
    private long _aiSince;
    private MotionPath _raw;
    private MotionPath _foreign;

    /// <summary>
    /// True while a physical key or button is down. The coordinator copies it in from the monitor before each call
    /// (the heartbeat before <see cref="Handle"/>/<see cref="Tick"/>, and <see cref="Begin(bool, bool)"/> takes it).
    /// </summary>
    public bool PhysicalHeld { get; set; }

    /// <summary>
    /// The monitor's takeover-chord flag, copied in before each call. Reported as <c>user</c> until the queued
    /// <see cref="PhysicalEventKind.Hotkey"/> event is handled, which clears it here; the coordinator must then clear
    /// the monitor's flag too (<c>ClearTakeover</c>), or the next copy sets it again and the state never returns to
    /// <c>ready</c>.
    /// </summary>
    public bool TakeoverPending { get; set; }

    public ControlState RawState => _state;

    public bool Armed => _armed;

    public bool Faulted => _faulted;

    /// <summary>The later of the last user activity and the last physical event (timestamp units, 0 = none).</summary>
    public long LastInput => Math.Max(_lastUser, _lastPhysical);

    /// <summary>True when the hook should swallow physical input: armed <c>ai</c>, no fault, no takeover pending.</summary>
    public bool SuppressionWanted =>
        settings.Enabled && Operational && _state == ControlState.Ai && _armed && !TakeoverPending;

    /// <summary>The reported status (never ticks).</summary>
    public ControlStatus Status => Snapshot(time.GetTimestamp());

    private bool Operational => _started && !_stopped && !_faulted;

    /// <summary>
    /// Starts ownership once the monitor runs: <c>user</c> when <paramref name="lastPhysical"/> is within the idle
    /// timeout, else <c>ready</c> (<c>control.py:375-386</c>). Null when disabled, already started or stopped.
    /// </summary>
    public ControlStatus? Start(long lastPhysical)
    {
        if (!settings.Enabled || _started || _stopped) return null;
        _started = true;
        var now = time.GetTimestamp();
        _lastPhysical = Math.Max(_lastPhysical, lastPhysical);
        if (_faulted) return null; // Recover decides the state
        var recent = Recent(now);
        if (recent) _lastUser = _lastPhysical;
        return Set(recent ? ControlState.User : ControlState.Ready, now) ? Snapshot(now) : null;
    }

    /// <summary>
    /// Handles one queued monitor event (<c>control.py:517-559</c>): records its stamp as the last physical event
    /// (except zero-length foreign motion, which is not input), ticks, then applies the event.
    /// </summary>
    public ControlStatus? Handle(PhysicalEvent e)
    {
        if (!settings.Enabled) return null;
        var now = time.GetTimestamp();
        var motion = e.Kind is PhysicalEventKind.RawMotion or PhysicalEventKind.ForeignMotion;
        if (e.Kind == PhysicalEventKind.ForeignMotion && e.Dx == 0 && e.Dy == 0)
            return TickCore(now) ? Snapshot(now) : null;

        _lastPhysical = Math.Max(_lastPhysical, e.Timestamp);
        var changed = TickCore(now);
        _inputSinceEntry = true;
        if (e.Kind == PhysicalEventKind.Hotkey) TakeoverPending = false;
        if (!Operational) return changed ? Snapshot(now) : null;

        if (e.Kind == PhysicalEventKind.Hotkey
            || _state is ControlState.Ready or ControlState.User
            || (_state == ControlState.Ai && !_armed))
        {
            _lastUser = now;
            changed |= Set(ControlState.User, now);
        }
        else if (motion && _state == ControlState.Ai && (e.Dx != 0 || e.Dy != 0) && e.Timestamp >= _aiSince)
        {
            // Sum path length, not displacement (Decisions 24): raw device units and foreign pixels separately.
            ref var path = ref e.Kind == PhysicalEventKind.RawMotion ? ref _raw : ref _foreign;
            var threshold = e.Kind == PhysicalEventKind.RawMotion ? settings.MouseTakeoverUnits : settings.MouseTakeoverPixels;
            if (!path.Started || path.Device != e.Device || e.Timestamp - path.LastMove >= Ticks(MotionPauseSeconds))
                path = new MotionPath { Started = true, Device = e.Device };
            path.LastMove = e.Timestamp;
            path.Distance += double.Hypot(e.Dx, e.Dy);
            if (path.Distance >= threshold)
            {
                _lastUser = now;
                changed |= Set(ControlState.User, now);
            }
        }
        return changed ? Snapshot(now) : null;
    }

    /// <summary>Applies the idle timeout and the <c>ai</c> lease (<c>control.py:230-249</c>).</summary>
    public ControlStatus? Tick()
    {
        if (!settings.Enabled) return null;
        var now = time.GetTimestamp();
        return TickCore(now) ? Snapshot(now) : null;
    }

    /// <summary>
    /// <see cref="Begin()"/> after copying the monitor's flags in, so a key pressed since the last heartbeat is seen
    /// (<c>PHYSICAL_INPUT_HELD</c>). The coordinator uses this form.
    /// </summary>
    public BeginResult Begin(bool physicalHeld, bool takeoverPending)
    {
        PhysicalHeld = physicalHeld;
        TakeoverPending = takeoverPending;
        return Begin();
    }

    /// <summary>
    /// Opens a gated call (<c>control.py:251-293</c>) using the <see cref="PhysicalHeld"/> and
    /// <see cref="TakeoverPending"/> values last copied in. Throws <see cref="ControlRejectedException"/> when the
    /// call may not act. Its tick can change the state even when it throws (compare <see cref="ControlStatus.Generation"/>).
    /// </summary>
    public BeginResult Begin()
    {
        if (!settings.Enabled) return new BeginResult(0, null, false);
        var now = time.GetTimestamp();
        var ticked = TickCore(now);
        if (PhysicalHeld) throw new ControlRejectedException(ControlCodes.PhysicalInputHeld, Snapshot(now));
        if (!Operational || TakeoverPending || _state is not (ControlState.Ready or ControlState.Ai))
        {
            var status = Snapshot(now);
            throw new ControlRejectedException(ControlCodes.ForState(status.State), status);
        }
        var entered = Set(ControlState.Ai, now);
        _activeCalls++;
        return new BeginResult(_generation, entered || ticked ? Snapshot(now) : null, entered);
    }

    /// <summary><see cref="Arm(long, long)"/> without the monitor's stamp: only events already handled count.</summary>
    public bool Arm(long generation) => Arm(generation, 0);

    /// <summary>
    /// Arms suppression once the indicator is visible (Decisions 15): only for the current <c>ai</c> generation and
    /// only when no physical event, held input, takeover or fault happened since entering <c>ai</c>.
    /// <paramref name="lastPhysical"/> is the monitor's latest event stamp (upstream <c>arm_visible</c> compares the
    /// hook-written stamp), so an event not yet drained into <see cref="Handle"/> also refuses. A stamp at or after the
    /// entry instant counts as since entry, as for motion (preflight F4). The coordinator uses this form.
    /// </summary>
    public bool Arm(long generation, long lastPhysical)
    {
        if (!settings.Enabled || !Operational || _state != ControlState.Ai || _generation != generation
            || _inputSinceEntry || lastPhysical >= _aiSince || PhysicalHeld || TakeoverPending)
            return false;
        _armed = true;
        return true;
    }

    /// <summary>
    /// Throws <c>CONTROL_PREEMPTED</c> with the current status and <paramref name="executedSteps"/> unless the call
    /// still owns armed <c>ai</c> (<c>control.py:295-311</c>). Does nothing when control is disabled.
    /// </summary>
    public void Checkpoint(long token, long executedSteps, bool requestClosed)
    {
        if (!settings.Enabled) return;
        if (requestClosed || !Operational || _state != ControlState.Ai || _generation != token || TakeoverPending || !_armed)
            throw new ControlRejectedException(ControlCodes.ControlPreempted, Status, executedSteps);
    }

    /// <summary>
    /// Closes a call; when none remain in <c>ai</c>, the lease ends <see cref="LeaseSeconds"/> from now. The token is
    /// ignored, as upstream's <c>end_call</c> ignores it: every call counts toward the active calls whatever its generation.
    /// </summary>
    public void End(long token)
    {
        if (!settings.Enabled) return;
        _activeCalls = Math.Max(0, _activeCalls - 1);
        if (_state == ControlState.Ai && _activeCalls == 0) _leaseUntil = time.GetTimestamp() + Ticks(LeaseSeconds);
    }

    /// <summary>Moves to <c>unavailable</c> until <see cref="Recover"/>.</summary>
    public ControlStatus? Fault()
    {
        if (!settings.Enabled || _stopped) return null;
        _faulted = true;
        var now = time.GetTimestamp();
        return Set(ControlState.Unavailable, now) ? Snapshot(now) : null;
    }

    /// <summary>Leaves a fault for <c>user</c> with the last input now (<c>control.py:485-495</c>).</summary>
    public ControlStatus? Recover()
    {
        if (!settings.Enabled || _stopped || !_faulted) return null;
        _faulted = false;
        TakeoverPending = false; // upstream _recover_after_rehook clears the fast-takeover flags
        if (!_started) return null;
        var now = time.GetTimestamp();
        _lastUser = now;
        return Set(ControlState.User, now) ? Snapshot(now) : null;
    }

    /// <summary>Moves to <c>unavailable</c> for good; later <see cref="Start"/> and <see cref="Recover"/> return null.</summary>
    public ControlStatus? Stop()
    {
        if (!settings.Enabled || _stopped) return null;
        _stopped = true;
        var now = time.GetTimestamp();
        return Set(ControlState.Unavailable, now) ? Snapshot(now) : null;
    }

    private bool TickCore(long now)
    {
        if (!Operational) return false;
        var recent = Recent(now);
        if (_state == ControlState.Ready && recent)
        {
            _lastUser = _lastPhysical;
            return Set(ControlState.User, now);
        }
        if (_state == ControlState.User && !PhysicalHeld && now - LastInput >= Ticks(settings.IdleTimeoutSeconds))
            return Set(ControlState.Ready, now);
        if (_state == ControlState.Ai && _activeCalls == 0 && now >= _leaseUntil)
        {
            if (recent) _lastUser = _lastPhysical;
            return Set(recent ? ControlState.User : ControlState.Ready, now);
        }
        return false;
    }

    // Zero means no input has been observed, even with a timeout longer than uptime (control.py:231).
    private bool Recent(long now) => _lastPhysical > 0 && now - _lastPhysical < Ticks(settings.IdleTimeoutSeconds);

    private bool Set(ControlState state, long now)
    {
        if (state == _state) return false;
        _state = state;
        _generation++;
        _armed = false;
        _inputSinceEntry = false;
        _raw = default;
        _foreign = default;
        if (state == ControlState.Ai) _aiSince = now;
        return true;
    }

    private ControlStatus Snapshot(long now)
    {
        var state = !settings.Enabled ? ControlState.Disabled
            : !Operational || _state == ControlState.Unavailable ? ControlState.Unavailable
            : TakeoverPending ? ControlState.User
            : _state;
        var wait = state == ControlState.User
            ? Math.Max(0, (LastInput - now) / (double)time.TimestampFrequency + settings.IdleTimeoutSeconds)
            : 0;
        return new ControlStatus(state, _generation, wait, settings.Enabled, settings.Policy, settings.IdleTimeoutSeconds);
    }

    private long Ticks(double seconds) => (long)Math.Round(seconds * time.TimestampFrequency);

    private struct MotionPath
    {
        public bool Started;
        public long Device;
        public long LastMove;
        public double Distance;
    }
}
