using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Snapshots;
using Windows.Win32;

namespace DesktopMcp.Desktop.Input;

/// <summary>Upstream's controller checkpoint_current / record_step_current around every input step.</summary>
public interface IActionCheckpoint
{
    void Checkpoint();

    void RecordStep();
}

/// <summary>P3's checkpoint: no controller yet, so both calls do nothing.</summary>
public sealed class NoCheckpoint : IActionCheckpoint
{
    private NoCheckpoint()
    {
    }

    public static NoCheckpoint Instance { get; } = new();

    public void Checkpoint()
    {
    }

    public void RecordStep()
    {
    }
}

public enum CaretPosition { Idle, Start, End }

/// <summary>Where a drag started and the duration it used (upstream drag's "start" and "duration").</summary>
public sealed record DragResult(ScreenPoint Start, double? Duration);

/// <summary>Upstream's waits, with their sources.</summary>
public static class InputTiming
{
    /// <summary>uia/core.py:34 OPERATION_WAIT_TIME.</summary>
    public static readonly TimeSpan OperationWait = TimeSpan.FromMilliseconds(500);

    /// <summary>uia/core.py:289: the button is held this long between down and up.</summary>
    public static readonly TimeSpan ButtonHold = TimeSpan.FromMilliseconds(50);

    /// <summary>Decisions 8: the pause between moving onto a target and pressing the button.</summary>
    public static readonly TimeSpan PressAfterMove = TimeSpan.FromMilliseconds(50);

    /// <summary>desktop/service.py:986.</summary>
    public static readonly TimeSpan MoveStep = TimeSpan.FromMilliseconds(3);

    /// <summary>desktop/service.py:943.</summary>
    public static readonly TimeSpan DragSettle = TimeSpan.FromMilliseconds(500);

    /// <summary>desktop/service.py:952-953 (PressMouse/ReleaseMouse waitTime).</summary>
    public static readonly TimeSpan DragButtonWait = TimeSpan.FromMilliseconds(50);

    /// <summary>uia/core.py:528 (WheelUp/WheelDown interval).</summary>
    public static readonly TimeSpan WheelInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>SendKeys(..., waitTime=0.05) in Type and Shortcut's helpers.</summary>
    public static readonly TimeSpan KeyWait = TimeSpan.FromMilliseconds(50);

    /// <summary>desktop/service.py:809: per-character SendKeys interval.</summary>
    public static readonly TimeSpan TypeInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>desktop/service.py:1000 (and SendKeys' default interval).</summary>
    public static readonly TimeSpan ShortcutInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>desktop/service.py:792: the pause before clearing.</summary>
    public static readonly TimeSpan ClearSettle = TimeSpan.FromMilliseconds(500);

    /// <summary>desktop/service.py:830: lets the clipboard write settle before Ctrl+V.</summary>
    public static readonly TimeSpan PasteSettle = TimeSpan.FromMilliseconds(50);

    /// <summary>desktop/service.py:766 _LONG_TEXT_PASTE_THRESHOLD, in code points.</summary>
    public const int PasteThreshold = 20;

    /// <summary>desktop/service.py:1020: Click(x, y, waitTime=0.2).</summary>
    public static readonly TimeSpan MultiSelectClickWait = TimeSpan.FromMilliseconds(200);

    /// <summary>desktop/service.py:1022.</summary>
    public static readonly TimeSpan MultiSelectSettle = TimeSpan.FromMilliseconds(500);

    /// <summary>desktop/service.py:876: PressKey/ReleaseKey waitTime for held modifiers.</summary>
    public static readonly TimeSpan KeyHoldWait = TimeSpan.FromMilliseconds(50);
}

/// <summary>Upstream's Desktop.click/type/scroll/drag/move/shortcut/multi_select (desktop/service.py:722-1031).</summary>
public interface IDesktopInput
{
    Task ClickAsync(ScreenPoint point, MouseButton button, long clicks, CancellationToken ct);

    Task MoveAsync(ScreenPoint to, CancellationToken ct);

    Task<DragResult> DragAsync(ScreenPoint? from, ScreenPoint to, double? duration, CancellationToken ct);

    /// <summary>Upstream's text for an invalid type/direction combination, or null.</summary>
    Task<string?> ScrollAsync(ScreenPoint? at, string type, string direction, long wheelTimes, CancellationToken ct);

    Task PressKeysAsync(IReadOnlyList<KeyToken> tokens, TimeSpan interval, TimeSpan waitTime, CancellationToken ct);

    Task TypeAsync(ScreenPoint at, string text, CaretPosition caret, bool clear, bool pressEnter, Func<CancellationToken, Task>? afterClick,
        CancellationToken ct);

    Task<IReadOnlyList<ScreenPoint>> MultiSelectAsync(bool pressCtrl, int count, Func<int, CancellationToken, Task<ScreenPoint>> target,
        CancellationToken ct);

    ScreenPoint Cursor { get; }
}

/// <summary>
/// The input primitives over <see cref="IInputInjector"/>. Every step is: checkpoint, cancellation check, send, record,
/// then its wait (with the call's token). Every key or button held across a wait is pressed through
/// <see cref="InputLedger"/> and released in a finally; releases and their waits never take the token.
/// </summary>
public sealed class DesktopInput : IDesktopInput
{
    private const string MouseLeft = "mouse:left";
    private const string KeyShift = "key:shift";
    private const string KeyCtrl = "key:ctrl";
    private const int WheelDelta = 120;

    private static readonly KeyToken[] Home = [new(KeyTables.VkHome, false), new(KeyTables.VkHome, true)];
    private static readonly KeyToken[] End = [new(KeyTables.VkEnd, false), new(KeyTables.VkEnd, true)];
    private static readonly KeyToken[] Back = [new(KeyTables.VkBack, false), new(KeyTables.VkBack, true)];
    private static readonly KeyToken[] Enter = [new(KeyTables.VkReturn, false), new(KeyTables.VkReturn, true)];
    private static readonly KeyToken[] SelectAll = Chord(KeyTables.VkA);
    private static readonly KeyToken[] Paste = Chord(KeyTables.VkV);

    private readonly InputLedger _ledger;
    private readonly IClipboard _clipboard;
    private readonly IActionCheckpoint _checkpoint;
    private readonly IInputInjector _injector;
    private readonly Func<ScreenPoint> _cursor;
    private readonly Func<ScreenRect> _virtualScreen;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<uint> _doubleClickTimeMs;
    private readonly Func<bool> _clipboardHoldsOnlyNonText;

    public DesktopInput(InputLedger ledger, IClipboard clipboard, IActionCheckpoint checkpoint)
        : this(ledger, clipboard, checkpoint, new SendInputInjector())
    {
    }

    /// <summary>The real desktop through another injector (the desktop tests' guard).</summary>
    internal DesktopInput(InputLedger ledger, IClipboard clipboard, IActionCheckpoint checkpoint, IInputInjector injector)
        : this(ledger, clipboard, checkpoint, injector, DesktopSession.CursorPosition, new Win32Displays().GetVirtualScreen,
            static (delay, ct) => Task.Delay(delay, ct), static () => PInvoke.GetDoubleClickTime(), ClipboardFormats.HoldsOnlyNonText)
    {
    }

    internal DesktopInput(InputLedger ledger, IClipboard clipboard, IActionCheckpoint checkpoint, IInputInjector injector,
        Func<ScreenPoint> cursor, Func<ScreenRect> virtualScreen, Func<TimeSpan, CancellationToken, Task> delay, Func<uint> doubleClickTimeMs,
        Func<bool> clipboardHoldsOnlyNonText)
    {
        _ledger = ledger;
        _clipboard = clipboard;
        _checkpoint = checkpoint;
        _injector = injector;
        _cursor = cursor;
        _virtualScreen = virtualScreen;
        _delay = delay;
        _doubleClickTimeMs = doubleClickTimeMs;
        _clipboardHoldsOnlyNonText = clipboardHoldsOnlyNonText;
    }

    public ScreenPoint Cursor => _cursor();

    /// <summary>desktop/service.py:722-755 over uia/core.py:280-319, with the press-after-move pause (Decisions 8).</summary>
    public Task ClickAsync(ScreenPoint point, MouseButton button, long clicks, CancellationToken ct) =>
        ClickCoreAsync(point, button, clicks, InputTiming.OperationWait, ct);

    /// <summary>desktop/service.py:976-986.</summary>
    public async Task MoveAsync(ScreenPoint to, CancellationToken ct)
    {
        _checkpoint.Checkpoint();
        foreach (var point in PointerPath.MoveSteps(_cursor(), to))
            await StepAsync(() => SendMouse(MouseFlags.AbsoluteMove, point), InputTiming.MoveStep, ct).ConfigureAwait(false);
    }

    /// <summary>desktop/service.py:930-974.</summary>
    public async Task<DragResult> DragAsync(ScreenPoint? from, ScreenPoint to, double? duration, CancellationToken ct)
    {
        _checkpoint.Checkpoint();
        await WaitAsync(InputTiming.DragSettle, ct).ConfigureAwait(false);
        _checkpoint.Checkpoint();
        var start = from ?? _cursor();
        ct.ThrowIfCancellationRequested();
        _ledger.Press(MouseLeft,
            () => Send([Mouse(MouseFlags.AbsoluteMove, start), Mouse(MouseFlags.AbsoluteMove | MouseFlags.LeftDown, start)]),
            () => SendMouse(MouseFlags.AbsoluteMove | MouseFlags.LeftUp, _cursor()));
        Exception? primary = null;
        try
        {
            await WaitAsync(InputTiming.DragButtonWait, ct).ConfigureAwait(false);
            _checkpoint.RecordStep();
            var (points, interval) = PointerPath.DragSteps(start, to, duration);
            foreach (var point in points)
                await StepAsync(() => SendMouse(MouseFlags.AbsoluteMove, point), interval, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            primary = e;
            throw;
        }
        finally
        {
            await ReleaseAsync([MouseLeft], InputTiming.DragButtonWait, primary).ConfigureAwait(false);
        }
        return new DragResult(start, duration);
    }

    /// <summary>desktop/service.py:844-904; the combination is validated before anything moves.</summary>
    public async Task<string?> ScrollAsync(ScreenPoint? at, string type, string direction, long wheelTimes, CancellationToken ct)
    {
        bool horizontal;
        int delta;
        switch (type)
        {
            case "vertical":
                horizontal = false;
                switch (direction)
                {
                    case "up": delta = WheelDelta; break;
                    case "down": delta = -WheelDelta; break;
                    default: return "Invalid direction. Use \"up\" or \"down\".";
                }
                break;
            case "horizontal":
                horizontal = true;
                switch (direction)
                {
                    case "left": delta = WheelDelta; break;
                    case "right": delta = -WheelDelta; break;
                    default: return "Invalid direction. Use \"left\" or \"right\".";
                }
                break;
            default:
                return "Invalid type. Use \"horizontal\" or \"vertical\".";
        }

        _checkpoint.Checkpoint();
        if (at is { } point) await MoveAsync(point, ct).ConfigureAwait(false);
        if (!horizontal)
        {
            await WheelAsync(delta, wheelTimes, ct).ConfigureAwait(false);
            return null;
        }

        ct.ThrowIfCancellationRequested();
        _ledger.Press(KeyShift, () => SendKey(KeyTables.VkShift, false), () => SendKey(KeyTables.VkShift, true));
        Exception? primary = null;
        try
        {
            await WaitAsync(InputTiming.KeyHoldWait, ct).ConfigureAwait(false);
            await WheelAsync(delta, wheelTimes, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            primary = e;
            throw;
        }
        finally
        {
            await ReleaseAsync([KeyShift], InputTiming.KeyHoldWait, primary).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Upstream's SendKeys (uia/core.py:1702-1744) through <see cref="SendKeysPlan"/>. Each virtual-key down is held
    /// through the ledger as <c>key:vk:&lt;hex&gt;</c> until its own up; whatever is still held is released in reverse.
    /// </summary>
    public async Task PressKeysAsync(IReadOnlyList<KeyToken> tokens, TimeSpan interval, TimeSpan waitTime, CancellationToken ct)
    {
        var held = new List<string>();
        Exception? primary = null;
        try
        {
            foreach (var step in SendKeysPlan.Build(tokens, interval, waitTime))
            {
                _checkpoint.Checkpoint();
                ct.ThrowIfCancellationRequested();
                if (step.Inputs is [{ VirtualKey: not 0 } key])
                {
                    var name = $"key:vk:{key.VirtualKey:x2}";
                    if (!key.Up && !held.Contains(name))
                    {
                        _ledger.Press(name, () => SendKey(key.VirtualKey, false), () => SendKey(key.VirtualKey, true));
                        held.Add(name);
                    }
                    else if (key.Up && held.Remove(name))
                    {
                        _ledger.Release(name);
                    }
                    else
                    {
                        Send([InjectedInput.Keyboard(key)]);
                    }
                }
                else
                {
                    var batch = new InjectedInput[step.Inputs.Count];
                    for (var i = 0; i < batch.Length; i++) batch[i] = InjectedInput.Keyboard(step.Inputs[i]);
                    Send(batch);
                }
                _checkpoint.RecordStep();
                await WaitAsync(step.PauseAfter, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            primary = e;
            throw;
        }
        finally
        {
            held.Reverse();
            await ReleaseAsync(held, SendKeysPlan.HotkeyInterval, primary).ConfigureAwait(false);
        }
    }

    /// <summary>desktop/service.py:767-814, with the paste path of 816-842 and the non-text clipboard rule (Decisions 21).</summary>
    public async Task TypeAsync(ScreenPoint at, string text, CaretPosition caret, bool clear, bool pressEnter,
        Func<CancellationToken, Task>? afterClick, CancellationToken ct)
    {
        await ClickAsync(at, MouseButton.Left, 1, ct).ConfigureAwait(false);
        if (afterClick is not null) await afterClick(ct).ConfigureAwait(false);
        if (caret == CaretPosition.Start) await PressKeysAsync(Home, InputTiming.ShortcutInterval, InputTiming.KeyWait, ct).ConfigureAwait(false);
        else if (caret == CaretPosition.End) await PressKeysAsync(End, InputTiming.ShortcutInterval, InputTiming.KeyWait, ct).ConfigureAwait(false);
        if (clear)
        {
            await WaitAsync(InputTiming.ClearSettle, ct).ConfigureAwait(false);
            await PressKeysAsync(SelectAll, InputTiming.ShortcutInterval, InputTiming.KeyWait, ct).ConfigureAwait(false);
            await PressKeysAsync(Back, InputTiming.ShortcutInterval, InputTiming.KeyWait, ct).ConfigureAwait(false);
        }

        if (PyText.CodePointLength(text) >= InputTiming.PasteThreshold && text.AsSpan().IndexOfAny("\n\t{}") < 0
            && !_clipboardHoldsOnlyNonText())
        {
            await PasteAsync(text, ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var entry in TextKeys.PerCharacter(text))
            {
                if (entry.Count == 0)
                {
                    _checkpoint.Checkpoint();
                    ct.ThrowIfCancellationRequested();
                    _checkpoint.RecordStep();
                    continue;
                }
                await PressKeysAsync(entry, InputTiming.TypeInterval, InputTiming.KeyWait, ct).ConfigureAwait(false);
            }
        }

        if (pressEnter) await PressKeysAsync(Enter, InputTiming.ShortcutInterval, InputTiming.KeyWait, ct).ConfigureAwait(false);
    }

    /// <summary>desktop/service.py:1003-1025; the targets are resolved one at a time, just before each click.</summary>
    public async Task<IReadOnlyList<ScreenPoint>> MultiSelectAsync(bool pressCtrl, int count,
        Func<int, CancellationToken, Task<ScreenPoint>> target, CancellationToken ct)
    {
        _checkpoint.Checkpoint();
        var points = new List<ScreenPoint>();
        var holding = false;
        Exception? primary = null;
        try
        {
            if (pressCtrl)
            {
                ct.ThrowIfCancellationRequested();
                _ledger.Press(KeyCtrl, () => SendKey(KeyTables.VkControl, false), () => SendKey(KeyTables.VkControl, true));
                holding = true;
                await WaitAsync(InputTiming.KeyHoldWait, ct).ConfigureAwait(false);
            }
            for (var i = 0; i < count; i++)
            {
                _checkpoint.Checkpoint();
                ct.ThrowIfCancellationRequested();
                var point = await target(i, ct).ConfigureAwait(false);
                points.Add(point);
                await ClickCoreAsync(point, MouseButton.Left, 1, InputTiming.MultiSelectClickWait, ct).ConfigureAwait(false);
                await WaitAsync(InputTiming.MultiSelectSettle, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            primary = e;
            throw;
        }
        finally
        {
            if (holding) await ReleaseAsync([KeyCtrl], InputTiming.KeyHoldWait, primary).ConfigureAwait(false);
        }
        return points;
    }

    /// <summary>
    /// Each click: move and pause when the cursor is not on the point yet, the button down through the ledger, the hold,
    /// the up, then the wait (left double clicks wait half the double-click time between clicks).
    /// </summary>
    private async Task ClickCoreAsync(ScreenPoint point, MouseButton button, long clicks, TimeSpan lastWait, CancellationToken ct)
    {
        if (clicks == 0)
        {
            _checkpoint.Checkpoint();
            ct.ThrowIfCancellationRequested();
            SendMouse(MouseFlags.AbsoluteMove, point);
            return;
        }

        var (down, up, name) = button switch
        {
            MouseButton.Right => (MouseFlags.RightDown, MouseFlags.RightUp, "mouse:right"),
            MouseButton.Middle => (MouseFlags.MiddleDown, MouseFlags.MiddleUp, "mouse:middle"),
            _ => (MouseFlags.LeftDown, MouseFlags.LeftUp, MouseLeft),
        };
        var between = button == MouseButton.Left && clicks >= 2 ? TimeSpan.FromMilliseconds(_doubleClickTimeMs() / 2.0) : lastWait;
        for (long i = 0; i < clicks; i++)
        {
            _checkpoint.Checkpoint();
            ct.ThrowIfCancellationRequested();
            if (_cursor() != point)
            {
                SendMouse(MouseFlags.AbsoluteMove, point);
                await WaitAsync(InputTiming.PressAfterMove, ct).ConfigureAwait(false);
            }
            _ledger.Press(name, () => SendMouse(MouseFlags.AbsoluteMove | down, point), () => SendMouse(MouseFlags.AbsoluteMove | up, point));
            Exception? primary = null;
            try
            {
                await WaitAsync(InputTiming.ButtonHold, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                primary = e;
                throw;
            }
            finally
            {
                await ReleaseAsync([name], TimeSpan.Zero, primary).ConfigureAwait(false);
            }
            _checkpoint.RecordStep();
            await WaitAsync(i < clicks - 1 ? between : lastWait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>uia/core.py:516-544: one notch, the interval, then the operation wait, per tick.</summary>
    private async Task WheelAsync(int delta, long wheelTimes, CancellationToken ct)
    {
        for (long i = 0; i < wheelTimes; i++)
        {
            await StepAsync(() => Send([InjectedInput.Wheel(delta)]), InputTiming.WheelInterval, ct).ConfigureAwait(false);
            await WaitAsync(InputTiming.OperationWait, ct).ConfigureAwait(false);
        }
    }

    private async Task StepAsync(Action send, TimeSpan wait, CancellationToken ct)
    {
        _checkpoint.Checkpoint();
        ct.ThrowIfCancellationRequested();
        send();
        _checkpoint.RecordStep();
        await WaitAsync(wait, ct).ConfigureAwait(false);
    }

    private async Task PasteAsync(string text, CancellationToken ct)
    {
        _checkpoint.Checkpoint();
        string? prior = null;
        try
        {
            prior = await _clipboard.GetTextAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // upstream: a clipboard that cannot be read is not restored
        }
        await _clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
        try
        {
            await WaitAsync(InputTiming.PasteSettle, ct).ConfigureAwait(false);
            await PressKeysAsync(Paste, InputTiming.ShortcutInterval, InputTiming.KeyWait, ct).ConfigureAwait(false);
        }
        finally
        {
            // Do not overwrite a clipboard value the user changed meanwhile.
            if (prior is not null)
            {
                try
                {
                    if (await _clipboard.GetTextAsync(CancellationToken.None).ConfigureAwait(false) == text)
                        await _clipboard.SetTextAsync(prior, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // upstream swallows a failed restore
                }
            }
        }
    }

    /// <summary>
    /// Releases every name in order, each followed by its wait (never cancelled). One failed release does not stop the
    /// rest; the error that ended the held section stays primary, else the first release error is thrown (as
    /// <see cref="InputLedger.ReleaseAll"/>).
    /// </summary>
    private async Task ReleaseAsync(IReadOnlyList<string> names, TimeSpan wait, Exception? primary)
    {
        Exception? first = null;
        foreach (var name in names)
        {
            try
            {
                _ledger.Release(name);
            }
            catch (Exception e)
            {
                first ??= e;
            }
            await WaitAsync(wait, CancellationToken.None).ConfigureAwait(false);
        }
        if (primary is null && first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }

    private Task WaitAsync(TimeSpan wait, CancellationToken ct) => wait > TimeSpan.Zero ? _delay(wait, ct) : Task.CompletedTask;

    private void Send(IReadOnlyList<InjectedInput> inputs) => _injector.Send(inputs);

    private void SendMouse(MouseFlags flags, ScreenPoint point) => Send([Mouse(flags, point)]);

    private void SendKey(ushort virtualKey, bool up) => Send([InjectedInput.Keyboard(new KeyInput(virtualKey, '\0', up))]);

    private InjectedInput Mouse(MouseFlags flags, ScreenPoint point) =>
        InjectedInput.MouseAt(flags, MouseCoordinates.Normalize(point, _virtualScreen()));

    private static KeyToken[] Chord(ushort key) =>
        [new(KeyTables.VkControl, false), new(key, false), new(key, true), new(KeyTables.VkControl, true)];
}
