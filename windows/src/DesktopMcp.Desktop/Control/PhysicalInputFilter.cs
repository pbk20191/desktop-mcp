using System.Collections.Frozen;
using System.Threading.Channels;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Control;

/// <summary>
/// How injected input that does not carry <see cref="InputSignature.ExtraInfo"/> is treated. Open question 1: other
/// programs' injection (for example Chrome Remote Desktop) is the user, so the default is <see cref="Physical"/>.
/// </summary>
internal enum ForeignInjectedPolicy { Physical, Ignore }

internal enum HookVerdict { Pass, Swallow }

/// <summary>Virtual-key helpers for the takeover chord (<c>control_win32.py:160, 171-178</c>).</summary>
internal static class VirtualKeys
{
    public const int Backspace = 0x08;

    /// <summary>Shift, Ctrl, Alt (generic codes) and Backspace.</summary>
    public static readonly IReadOnlySet<int> Chord = FrozenSet.Create(0x10, 0x11, 0x12, Backspace);

    /// <summary>Maps the left/right modifier codes the low-level hook reports to the generic Shift, Ctrl and Alt.</summary>
    public static int Normalize(int vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => vk,
    };
}

/// <summary>Raw Input mouse filtering (<c>control_win32.py:195-218</c>).</summary>
internal static class RawMouse
{
    private const uint RimTypeMouse = 0;
    private const ushort MouseMoveAbsolute = 0x01;

    /// <summary>
    /// The device and relative motion of a raw mouse report; null for a non-mouse report, the null device (injected
    /// input and some touchpads: the source cannot be told) or an absolute move.
    /// </summary>
    public static (long Device, int Dx, int Dy)? Relative(uint type, long device, ushort flags, int lastX, int lastY) =>
        type != RimTypeMouse || device == 0 || (flags & MouseMoveAbsolute) != 0 ? null : (device, lastX, lastY);
}

/// <summary>
/// Everything the low-level hook and Raw Input callbacks decide, with no Win32 (<c>control_hooks.py</c>,
/// <c>control_recovery.py</c>; Decisions 18, 24, 25, 29). The entry points and <see cref="SampleMissedReleases"/>
/// run on the dispatcher thread, which alone writes the key and button arrays; the setters and the read-only members
/// may be used from any thread. The hook path takes no lock, never blocks and catches every exception (a caught
/// exception requests fail-open). It does no per-event allocation once the queue's buffer has grown (preflight F18):
/// the bounded channel's deque grows on early writes, bounded by <c>queueCapacity</c>.
/// </summary>
internal sealed class PhysicalInputFilter
{
    private const uint KeyInjected = 0x10;               // LLKHF_INJECTED
    private const uint MouseInjected = 0x01 | 0x02;      // LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED
    private const uint WmMouseMove = 0x200;
    private const uint WmXButtonDown = 0x20B;
    private const uint WmXButtonUp = 0x20C;
    private const double SampleIntervalSeconds = 0.1;
    private const int ReleaseSamples = 5;

    // Hook button ids (1 left, 2 right, 3 middle, 4 XBUTTON1, 5 XBUTTON2) to their virtual keys (control_recovery.py:7).
    private static readonly int[] ButtonVk = [0, 0x01, 0x02, 0x04, 0x05, 0x06];
    private static readonly bool[] ChordKey = BuildChordKeys();

    private readonly Func<long> _now;
    private readonly long _ticksPerSecond;
    private readonly ForeignInjectedPolicy _foreign;
    private readonly Action _blockLedger;
    private readonly Channel<PhysicalEvent> _events;

    // Written only on the dispatcher thread; read elsewhere with Volatile.Read.
    private readonly bool[] _pressed = new bool[256];
    private readonly bool[] _delivered = new bool[256];
    private readonly bool[] _quarantine = new bool[256];
    private readonly bool[] _buttonDown = new bool[6];
    private readonly bool[] _buttonDelivered = new bool[6];
    private int _held;
    private long _pressVersion;

    // Set from any thread.
    private bool _suppress;
    private long _deadline;
    private bool _takeover;
    private bool _failOpen;
    private long _lastPhysical;
    private Published _published = new(ControlState.Unavailable, 0, 0);

    // Dispatcher-thread bookkeeping.
    private int _pendingKey = -1;
    private int _pendingButton;
    private bool _foreignSeen;
    private int _foreignX;
    private int _foreignY;
    private Signature? _signature;
    private int _releaseCount;
    private long _nextSample;

    public PhysicalInputFilter(Func<long> now, long ticksPerSecond, ForeignInjectedPolicy foreign, Action blockLedger, int queueCapacity = 4096)
    {
        _now = now;
        _ticksPerSecond = ticksPerSecond;
        _foreign = foreign;
        _blockLedger = blockLedger;
        _events = Channel.CreateBounded<PhysicalEvent>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public ChannelReader<PhysicalEvent> Events => _events.Reader;

    public bool Suppressing => Volatile.Read(ref _suppress);

    public long Deadline => Volatile.Read(ref _deadline);

    public bool TakeoverPending => Volatile.Read(ref _takeover);

    public bool FailOpenRequested => Volatile.Read(ref _failOpen);

    /// <summary>The last physical event's stamp in <c>now()</c> units (0 = none).</summary>
    public long LastPhysical => Volatile.Read(ref _lastPhysical);

    /// <summary>True while any key or button is recorded down.</summary>
    public bool Held => Volatile.Read(ref _held) > 0;

    public bool KeyDelivered(int vk) => (uint)vk < 256 && Volatile.Read(ref _delivered[vk]);

    public bool ButtonDelivered(int button) => (uint)button < 6 && button > 0 && Volatile.Read(ref _buttonDelivered[button]);

    /// <summary>Any thread: turns swallowing on until <paramref name="deadline"/> (renewed by the heartbeat) or off.</summary>
    public void SetSuppression(bool on, long deadline)
    {
        Volatile.Write(ref _deadline, deadline);
        Volatile.Write(ref _suppress, on);
    }

    /// <summary>Any thread: the coordinator's state and last input, which gate <see cref="SampleMissedReleases"/>.</summary>
    public void PublishState(ControlState state, long lastInput, double idleSeconds) =>
        Volatile.Write(ref _published, new Published(state, lastInput, idleSeconds));

    /// <summary>Any thread: the coordinator handled the chord's <see cref="PhysicalEventKind.Hotkey"/> (preflight F3).</summary>
    public void ClearTakeover() => Volatile.Write(ref _takeover, false);

    /// <summary>Any thread: the coordinator handled a fail-open request.</summary>
    public void ClearFailOpen() => Volatile.Write(ref _failOpen, false);

    /// <summary>The keyboard hook's decision (<c>control_hooks.py:54-100</c>). <paramref name="down"/> covers WM_SYSKEYDOWN.</summary>
    public HookVerdict OnKey(uint vk, bool down, uint flags, nuint extraInfo)
    {
        _pendingKey = -1;
        try
        {
            if ((flags & KeyInjected) != 0 && !ForeignCounts(extraInfo)) return HookVerdict.Pass;
            var now = _now();
            Volatile.Write(ref _lastPhysical, now);
            var index = (int)vk;
            var tracked = vk < 256;
            var wasDown = tracked && _pressed[index];
            if (tracked) SetPressed(index, down);

            if (tracked && _quarantine[index])
            {
                if (!down)
                {
                    Volatile.Write(ref _quarantine[index], false);
                    Queue(PhysicalEventKind.Key, now); // idle starts after the last chord key is released
                }
                return HookVerdict.Swallow;
            }

            if (down && index == VirtualKeys.Backspace && !wasDown && !TakeoverPending && Suppressing && ChordHeld())
            {
                for (var k = 0; k < 256; k++)
                    if (_pressed[k] && ChordKey[k]) Volatile.Write(ref _quarantine[k], true);
                Volatile.Write(ref _takeover, true);
                Volatile.Write(ref _suppress, false); // release first, then tell the coordinator
                _blockLedger();
                Queue(PhysicalEventKind.Hotkey, now);
                return HookVerdict.Swallow;
            }

            Queue(PhysicalEventKind.Key, now);
            var verdict = Decide();
            if (tracked && verdict == HookVerdict.Pass) _pendingKey = index; // only a forwarded event can be delivered
            return verdict;
        }
        catch (Exception)
        {
            RequestFailOpen();
            return HookVerdict.Pass;
        }
    }

    /// <summary>After <c>CallNextHookEx</c> for a passed key: a down is delivered only when the result is 0.</summary>
    public void AfterKeyForwarded(uint vk, bool down, nint nextResult)
    {
        try
        {
            if (_pendingKey != (int)vk || vk >= 256) return;
            _pendingKey = -1;
            if (!down) Volatile.Write(ref _delivered[vk], false);
            else if (nextResult == 0) Volatile.Write(ref _delivered[vk], true);
        }
        catch (Exception)
        {
            RequestFailOpen();
        }
    }

    /// <summary>The mouse hook's decision (<c>control_hooks.py:18-51</c>).</summary>
    public HookVerdict OnMouse(uint message, uint mouseData, int x, int y, uint flags, nuint extraInfo)
    {
        _pendingButton = 0;
        try
        {
            var injected = (flags & MouseInjected) != 0;
            if (injected && !ForeignCounts(extraInfo)) return HookVerdict.Pass;
            var now = _now();
            var kind = PhysicalEventKind.Pointer;
            int dx = 0, dy = 0;
            if (injected)
            {
                // Foreign injection has no raw device; its moves are measured in screen pixels (Decisions 24).
                if (_foreignSeen) { dx = x - _foreignX; dy = y - _foreignY; }
                _foreignSeen = true;
                _foreignX = x;
                _foreignY = y;
                if (message == WmMouseMove) kind = PhysicalEventKind.ForeignMotion;
            }
            // A zero-length foreign move is not input (the state machine ignores it too).
            if (kind != PhysicalEventKind.ForeignMotion || dx != 0 || dy != 0) Volatile.Write(ref _lastPhysical, now);

            var (button, down) = Button(message, mouseData);
            if (button != 0) SetButton(button, down);
            Queue(kind, now, 0, dx, dy);
            var verdict = Decide();
            if (verdict == HookVerdict.Pass) _pendingButton = button;
            return verdict;
        }
        catch (Exception)
        {
            RequestFailOpen();
            return HookVerdict.Pass;
        }
    }

    /// <summary>After <c>CallNextHookEx</c> for a passed mouse event: a button down is delivered only when the result is 0.</summary>
    public void AfterMouseForwarded(uint message, uint mouseData, nint nextResult)
    {
        try
        {
            var (button, down) = Button(message, mouseData);
            if (button == 0 || _pendingButton != button) return;
            _pendingButton = 0;
            if (!down) Volatile.Write(ref _buttonDelivered[button], false);
            else if (nextResult == 0) Volatile.Write(ref _buttonDelivered[button], true);
        }
        catch (Exception)
        {
            RequestFailOpen();
        }
    }

    /// <summary>A WM_INPUT mouse report (<c>control_hooks.py:103-110</c>): only relative motion from a real device counts.</summary>
    public void OnRawMouse(long device, ushort flags, int lastX, int lastY)
    {
        try
        {
            if (RawMouse.Relative(0, device, flags, lastX, lastY) is not { } raw) return;
            var now = _now();
            Volatile.Write(ref _lastPhysical, now);
            Queue(PhysicalEventKind.RawMotion, now, raw.Device, raw.Dx, raw.Dy); // arrival time, not handling time
        }
        catch (Exception)
        {
            RequestFailOpen();
        }
    }

    /// <summary>
    /// The 0.1 s dispatcher tick (Decisions 25; <c>control_recovery.py</c>): while idle outside <c>ai</c> with input
    /// recorded down, five all-up samples at least 0.1 s apart, with no input or state change between them, clear the
    /// recorded keys and buttons with their delivery and quarantine flags. Only the high bit of
    /// <paramref name="keyState"/> means down.
    /// </summary>
    public void SampleMissedReleases(Func<int, short> keyState)
    {
        var now = _now();
        var signature = Snapshot(now);
        if (signature is not { } current)
        {
            ResetSampler();
            return;
        }
        if (_signature != current)
        {
            ResetSampler();
            _signature = current;
        }
        if (now < _nextSample) return;

        var anyDown = false;
        for (var k = 0; k < 256; k++)
            if (_pressed[k] && (keyState(k) & 0x8000) != 0) anyDown = true;
        for (var b = 1; b < 6; b++)
            if (_buttonDown[b] && (keyState(ButtonVk[b]) & 0x8000) != 0) anyDown = true;
        // Spaced from after the queries (control_recovery.py:66-68). The hooks run on this same thread, so no input can
        // change the recorded sets between the queries and the clearing below.
        _nextSample = _now() + Ticks(SampleIntervalSeconds);
        if (anyDown)
        {
            _releaseCount = 0;
            return;
        }
        if (++_releaseCount < ReleaseSamples) return;

        for (var k = 0; k < 256; k++)
        {
            if (!_pressed[k]) continue;
            SetPressed(k, false);
            Volatile.Write(ref _delivered[k], false);
            Volatile.Write(ref _quarantine[k], false);
        }
        for (var b = 1; b < 6; b++)
        {
            if (!_buttonDown[b]) continue;
            SetButton(b, false);
            Volatile.Write(ref _buttonDelivered[b], false);
        }
        ResetSampler();
    }

    private bool ForeignCounts(nuint extraInfo) =>
        extraInfo != InputSignature.ExtraInfo && _foreign == ForeignInjectedPolicy.Physical;

    private HookVerdict Decide()
    {
        if (!Suppressing || FailOpenRequested) return HookVerdict.Pass;
        if (_now() <= Deadline) return HookVerdict.Swallow;
        RequestFailOpen(); // a lapsed deadline never leaves input locked
        return HookVerdict.Pass;
    }

    private void Queue(PhysicalEventKind kind, long now, long device = 0, int dx = 0, int dy = 0)
    {
        if (!_events.Writer.TryWrite(new PhysicalEvent(kind, now, device, dx, dy))) RequestFailOpen();
    }

    private void RequestFailOpen()
    {
        Volatile.Write(ref _failOpen, true);
        Volatile.Write(ref _suppress, false);
        Volatile.Write(ref _deadline, 0);
        try
        {
            _blockLedger();
        }
        catch (Exception)
        {
            // Never escape a hook callback; the request flag stands.
        }
    }

    private bool ChordHeld()
    {
        bool shift = false, ctrl = false, alt = false, backspace = false;
        for (var k = 0; k < 256; k++)
        {
            if (!_pressed[k]) continue;
            switch (VirtualKeys.Normalize(k))
            {
                case 0x10: shift = true; break;
                case 0x11: ctrl = true; break;
                case 0x12: alt = true; break;
                case VirtualKeys.Backspace: backspace = true; break;
            }
        }
        return shift && ctrl && alt && backspace;
    }

    private void SetPressed(int vk, bool down)
    {
        if (_pressed[vk] == down) return;
        Volatile.Write(ref _pressed[vk], down);
        Interlocked.Add(ref _held, down ? 1 : -1);
        _pressVersion++;
    }

    private void SetButton(int button, bool down)
    {
        if (_buttonDown[button] == down) return;
        Volatile.Write(ref _buttonDown[button], down);
        Interlocked.Add(ref _held, down ? 1 : -1);
        _pressVersion++;
    }

    // control_win32.py:181-192: left 1, right 2, middle 3, XBUTTON1 4, XBUTTON2 5.
    private static (int Button, bool Down) Button(uint message, uint mouseData) => message switch
    {
        0x201 => (1, true),
        0x202 => (1, false),
        0x204 => (2, true),
        0x205 => (2, false),
        0x207 => (3, true),
        0x208 => (3, false),
        WmXButtonDown or WmXButtonUp when (mouseData >> 16) is 1 or 2 => ((int)(mouseData >> 16) + 3, message == WmXButtonDown),
        _ => (0, false),
    };

    // control_recovery.py:22-46: sample only while idle outside ai, not failing open, with input recorded down.
    private Signature? Snapshot(long now)
    {
        var published = Volatile.Read(ref _published);
        if (published.State is not (ControlState.User or ControlState.Ready)
            || FailOpenRequested
            || now - published.LastInput < Ticks(published.IdleSeconds)
            || !Held)
            return null;
        return new Signature(published.State, published.LastInput, LastPhysical, _pressVersion);
    }

    private void ResetSampler()
    {
        _signature = null;
        _releaseCount = 0;
        _nextSample = 0;
    }

    private long Ticks(double seconds) => (long)Math.Round(seconds * _ticksPerSecond);

    private static bool[] BuildChordKeys()
    {
        var keys = new bool[256];
        for (var k = 0; k < 256; k++) keys[k] = VirtualKeys.Chord.Contains(VirtualKeys.Normalize(k));
        return keys;
    }

    private sealed record Published(ControlState State, long LastInput, double IdleSeconds);

    private readonly record struct Signature(ControlState State, long LastInput, long LastPhysical, long PressVersion);
}
