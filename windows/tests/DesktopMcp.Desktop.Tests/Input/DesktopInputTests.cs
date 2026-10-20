using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Tests.Input;

public class DesktopInputTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1001, 1001); // normalise: round(p * 65535 / 1000)

    [Fact]
    public async Task ClickSendsMovePauseDownUpWithTheSignatureFlags()
    {
        var rig = new InputRig(cursor: new(0, 0));
        await rig.Input.ClickAsync(new(100, 200), MouseButton.Left, 1, default);
        Assert.Equal([MouseFlags.AbsoluteMove, MouseFlags.AbsoluteMove | MouseFlags.LeftDown, MouseFlags.AbsoluteMove | MouseFlags.LeftUp],
            rig.Sent.Select(i => i.Mouse));
        Assert.All(rig.Sent, i => Assert.Equal((6554, 13107), (i.X, i.Y)));
        Assert.Equal([50, 50, 500], rig.Delays.Select(d => (int)d.TotalMilliseconds));
    }

    [Fact]
    public async Task DoubleClickWaitsHalfTheDoubleClickTimeAndPausesOnlyBeforeTheFirstPress()
    {
        var rig = new InputRig(cursor: new(0, 0), doubleClickTimeMs: 500);
        await rig.Input.ClickAsync(new(10, 10), MouseButton.Left, 2, default);
        Assert.Equal([50, 50, 250, 50, 500], rig.Delays.Select(d => (int)d.TotalMilliseconds));
    }

    [Fact]
    public async Task HoverOnlyMoves()
    {
        var rig = new InputRig(cursor: new(0, 0));
        await rig.Input.ClickAsync(new(10, 10), MouseButton.Right, 0, default);
        Assert.Equal([MouseFlags.AbsoluteMove], rig.Sent.Select(i => i.Mouse));
    }

    [Fact]
    public async Task DragReleasesTheButtonWhenCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var rig = new InputRig(cursor: new(0, 0), onDelay: count => { if (count == 4) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Input.DragAsync(null, new(500, 0), null, cancel.Token));
        Assert.Equal(MouseFlags.AbsoluteMove | MouseFlags.LeftUp, rig.Sent[^1].Mouse);
        Assert.Empty(rig.Ledger.Pending);
    }

    [Fact]
    public async Task ScrollHorizontalHoldsShiftAroundTheWheel()
    {
        var rig = new InputRig(cursor: new(0, 0));
        Assert.Null(await rig.Input.ScrollAsync(null, "horizontal", "right", 2, default));
        Assert.Equal(new KeyInput(0x10, '\0', false), rig.Sent[0].Key);
        Assert.Equal([-120, -120], rig.Sent.Where(i => i.Mouse == MouseFlags.Wheel).Select(i => i.WheelDelta));
        Assert.Equal(new KeyInput(0x10, '\0', true), rig.Sent[^1].Key);
    }

    [Theory]
    [InlineData("vertical", "left", "Invalid direction. Use \"up\" or \"down\".")]
    [InlineData("horizontal", "up", "Invalid direction. Use \"left\" or \"right\".")]
    public async Task ScrollInvalidCombinationsReturnUpstreamsText(string type, string direction, string text) =>
        Assert.Equal(text, await new InputRig(cursor: new(0, 0)).Input.ScrollAsync(null, type, direction, 1, default));

    [Fact]
    public async Task HugeWheelCountsStopAtCancellation()
    {
        using var cancel = new CancellationTokenSource();
        var rig = new InputRig(cursor: new(0, 0), onDelay: count => { if (count == 6) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Input.ScrollAsync(null, "vertical", "down", 1_000_000, cancel.Token));
        Assert.InRange(rig.Sent.Count, 1, 4);
    }

    [Fact]
    public async Task ShortcutCancelledMidwayReleasesTheHeldModifiers()
    {
        using var cancel = new CancellationTokenSource();
        var rig = new InputRig(cursor: new(0, 0), onDelay: count => { if (count == 2) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Input.PressKeysAsync(ShortcutParser.Parse("ctrl+shift+s"), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(500), cancel.Token));
        Assert.Equal([new KeyInput(0x11, '\0', false), new KeyInput(0x10, '\0', false), new KeyInput(0x10, '\0', true), new KeyInput(0x11, '\0', true)],
            rig.Sent.Select(i => i.Key));
        Assert.Empty(rig.Ledger.Pending);
    }

    [Fact]
    public async Task TypePastesLongPlainTextAndRestoresTheClipboard()
    {
        var rig = new InputRig(cursor: new(0, 0), clipboard: "before");
        await rig.Input.TypeAsync(new(5, 5), "twenty characters ok", CaretPosition.Idle, false, false, null, default);
        Assert.Contains(new KeyInput(0x56, '\0', false), rig.Sent.Select(i => i.Key)); // Ctrl+V
        Assert.Equal("before", rig.Clipboard.Text);
    }

    [Fact]
    public async Task TypeDoesNotPasteWhenTheClipboardHoldsOnlyNonText()
    {
        var rig = new InputRig(cursor: new(0, 0), clipboard: null, onlyNonText: true);
        await rig.Input.TypeAsync(new(5, 5), "twenty characters ok", CaretPosition.Idle, false, false, null, default);
        Assert.Equal(20, rig.Sent.Count(i => i.IsKeyboard && i.Key.VirtualKey == 0 && !i.Key.Up));
        Assert.Equal(0, rig.Clipboard.Writes);
    }

    [Fact]
    public async Task TypeRunsTheAfterClickHookBeforeAnyKey()
    {
        var rig = new InputRig(cursor: new(0, 0));
        var sentAtHook = -1;
        await rig.Input.TypeAsync(new(5, 5), "x", CaretPosition.End, true, true, _ => { sentAtHook = rig.Sent.Count; return Task.CompletedTask; }, default);
        Assert.Equal(3, sentAtHook); // move, down, up
        Assert.Equal(new KeyInput(0x0D, '\0', true), rig.Sent[^1].Key); // press_enter
    }

    [Fact]
    public async Task MultiSelectReleasesCtrlWhenATargetFails()
    {
        var rig = new InputRig(cursor: new(0, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Input.MultiSelectAsync(true, 3,
            (i, _) => i == 1 ? throw new InvalidOperationException("stale") : Task.FromResult(new ScreenPoint(5, 5)), default));
        Assert.Equal(new KeyInput(0x11, '\0', true), rig.Sent[^1].Key);
        Assert.Empty(rig.Ledger.Pending);
    }

    private static KeyInput Down(ushort vk) => new(vk, '\0', false);

    private static KeyInput Up(ushort vk) => new(vk, '\0', true);

    private static (int X, int Y) At(int x, int y) => MouseCoordinates.Normalize(new(x, y), Screen);

    [Fact]
    public async Task HoverRecordsNoStep()
    {
        var rig = new InputRig(cursor: new(0, 0));
        await rig.Input.ClickAsync(new(10, 10), MouseButton.Left, 0, default);
        Assert.Equal((1, 0), (rig.Checkpoint.Checkpoints, rig.Checkpoint.Records));
    }

    [Fact]
    public async Task ASecondClickAtTheCursorSkipsTheMoveAndThePause()
    {
        var rig = new InputRig(cursor: new(0, 0));
        await rig.Input.ClickAsync(new(10, 10), MouseButton.Left, 1, default);
        var (sent, delays) = (rig.Sent.Count, rig.Delays.Count);
        await rig.Input.ClickAsync(new(10, 10), MouseButton.Left, 1, default);
        Assert.Equal([MouseFlags.AbsoluteMove | MouseFlags.LeftDown, MouseFlags.AbsoluteMove | MouseFlags.LeftUp],
            rig.Sent.Skip(sent).Select(i => i.Mouse));
        Assert.Equal([50, 500], rig.DelayMs.Skip(delays));
    }

    [Fact]
    public async Task DragPressesMovesAndReleasesWithUpstreamsWaits()
    {
        var rig = new InputRig(cursor: new(0, 0));
        var result = await rig.Input.DragAsync(null, new(40, 0), null, default);
        Assert.Equal(new DragResult(new(0, 0), null), result);
        Assert.Equal(
            [(MouseFlags.AbsoluteMove, At(0, 0)), (MouseFlags.AbsoluteMove | MouseFlags.LeftDown, At(0, 0)), (MouseFlags.AbsoluteMove, At(20, 0)),
                (MouseFlags.AbsoluteMove, At(40, 0)), (MouseFlags.AbsoluteMove | MouseFlags.LeftUp, At(40, 0))],
            rig.Sent.Select(i => (i.Mouse, (i.X, i.Y))));
        Assert.Equal([500, 50, 10, 10, 50], rig.DelayMs);
        Assert.Empty(rig.Ledger.Pending);
    }

    [Fact]
    public async Task ScrollAtAPointMovesThereFirst()
    {
        var rig = new InputRig(cursor: new(0, 0));
        Assert.Null(await rig.Input.ScrollAsync(new(10, 0), "vertical", "up", 1, default));
        Assert.Equal([(MouseFlags.AbsoluteMove, At(10, 0), 0), (MouseFlags.Wheel, (0, 0), 120)],
            rig.Sent.Select(i => (i.Mouse, (i.X, i.Y), i.WheelDelta)));
        Assert.Equal([3, 50, 500], rig.DelayMs);
    }

    [Fact]
    public async Task TypeMovesTheCaretToTheStartThenClears()
    {
        var rig = new InputRig(cursor: new(0, 0));
        await rig.Input.TypeAsync(new(5, 5), "", CaretPosition.Start, true, false, null, default);
        Assert.Equal([Down(0x24), Up(0x24), Down(0x11), Down(0x41), Up(0x41), Up(0x11), Down(0x08), Up(0x08)], rig.Keys);
        // the click (pause, hold, wait), Home, the clear settle, Ctrl+A, Back
        Assert.Equal([50, 50, 500, 10, 60, 500, 10, 10, 10, 60, 10, 60], rig.DelayMs);
    }

    [Theory]
    [InlineData("nineteen characters", false)]           // 19 code points
    [InlineData("twenty characters ok", true)]           // 20
    [InlineData("eighteen chars ok \U0001F600", false)]  // 19 code points, 20 UTF-16 units
    [InlineData("nineteen chars ok! \U0001F600", true)]  // 20 code points, 21 UTF-16 units
    [InlineData("twenty five chars {brace}", false)]
    [InlineData("twenty five chars\nnewline", false)]
    [InlineData("twenty five chars\ttabbed", false)]
    public async Task PasteNeedsTwentyCodePointsOfPlainText(string text, bool pastes)
    {
        var rig = new InputRig(cursor: new(0, 0), clipboard: "before");
        await rig.Input.TypeAsync(new(5, 5), text, CaretPosition.Idle, false, false, null, default);
        Assert.Equal(pastes, rig.Keys.Contains(Down(0x56)));
        Assert.Equal(pastes ? 2 : 0, rig.Clipboard.Writes);
    }

    [Fact]
    public async Task PasteLeavesAClipboardTheUserChanged()
    {
        InputRig? rig = null;
        rig = new InputRig(cursor: new(0, 0), clipboard: "before", onSend: i => { if (i.IsKeyboard && i.Key == Down(0x56)) rig!.Clipboard.Text = "user"; });
        await rig.Input.TypeAsync(new(5, 5), "twenty characters ok", CaretPosition.Idle, false, false, null, default);
        Assert.Equal("user", rig.Clipboard.Text);
        Assert.Equal(1, rig.Clipboard.Writes);
    }

    [Fact]
    public async Task AFailedReleaseStillReleasesTheRestAndKeepsTheCancellation()
    {
        using var cancel = new CancellationTokenSource();
        var rig = new InputRig(cursor: new(0, 0), onDelay: count => { if (count == 2) cancel.Cancel(); },
            failSend: i => i.IsKeyboard && i.Key == Up(0x10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.Input.PressKeysAsync(ShortcutParser.Parse("ctrl+shift+s"), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(500), cancel.Token));
        Assert.Equal([Down(0x11), Down(0x10), Up(0x11)], rig.Keys);
        Assert.Equal(["key:vk:10"], rig.Ledger.Pending); // a failed up keeps its entry for shutdown
    }

    [Fact]
    public async Task AClickCancelledDuringTheHoldKeepsTheCancellationWhenTheUpFails()
    {
        using var cancel = new CancellationTokenSource();
        var rig = new InputRig(cursor: new(0, 0), onDelay: count => { if (count == 2) cancel.Cancel(); },
            failSend: i => (i.Mouse & MouseFlags.LeftUp) != 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Input.ClickAsync(new(10, 10), MouseButton.Left, 1, cancel.Token));
    }

    [Fact]
    public async Task AFailedReleaseWithNoOtherErrorIsReported()
    {
        var rig = new InputRig(cursor: new(0, 0), failSend: i => i.IsKeyboard && i.Key == Up(0x10));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Input.ScrollAsync(null, "horizontal", "left", 1, default));
        Assert.Equal("send failed", error.Message);
    }

    private sealed class InputRig
    {
        private readonly List<(InjectedInput Input, TimeSpan At)> _log = [];
        private readonly Action<int>? _onDelay;
        private TimeSpan _now;
        private ScreenPoint _cursor;

        public InputRig(ScreenPoint cursor, uint doubleClickTimeMs = 500, Action<int>? onDelay = null, string? clipboard = null,
            bool onlyNonText = false, Func<InjectedInput, bool>? failSend = null, Action<InjectedInput>? onSend = null)
        {
            _cursor = cursor;
            _onDelay = onDelay;
            _failSend = failSend;
            _onSend = onSend;
            Clipboard = new FakeClipboard { Text = clipboard };
            Input = new DesktopInput(Ledger, Clipboard, Checkpoint, new FakeInjector(this), () => _cursor, () => Screen,
                DelayAsync, () => doubleClickTimeMs, () => onlyNonText);
        }

        private readonly Func<InjectedInput, bool>? _failSend;
        private readonly Action<InjectedInput>? _onSend;

        public CountingCheckpoint Checkpoint { get; } = new();

        public IReadOnlyList<int> DelayMs => [.. Delays.Select(d => (int)d.TotalMilliseconds)];

        public IReadOnlyList<KeyInput> Keys => [.. Sent.Where(i => i.IsKeyboard).Select(i => i.Key)];

        public InputLedger Ledger { get; } = new();

        public FakeClipboard Clipboard { get; }

        public DesktopInput Input { get; }

        public List<TimeSpan> Delays { get; } = [];

        public IReadOnlyList<InjectedInput> Sent => [.. _log.Select(entry => entry.Input)];

        private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            _now += delay;
            _onDelay?.Invoke(Delays.Count);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private sealed class FakeInjector(InputRig rig) : IInputInjector
        {
            public void Send(IReadOnlyList<InjectedInput> inputs)
            {
                if (rig._failSend is { } fail && inputs.Any(fail)) throw new InvalidOperationException("send failed");
                foreach (var input in inputs)
                {
                    rig._log.Add((input, rig._now));
                    rig._onSend?.Invoke(input);
                    if (!input.IsKeyboard && (input.Mouse & MouseFlags.Absolute) != 0)
                    {
                        rig._cursor = new ScreenPoint(
                            Screen.Left + (int)Math.Round((double)input.X * (Screen.Width - 1) / 65535),
                            Screen.Top + (int)Math.Round((double)input.Y * (Screen.Height - 1) / 65535));
                    }
                }
            }
        }
    }

    private sealed class CountingCheckpoint : IActionCheckpoint
    {
        public int Checkpoints { get; private set; }

        public int Records { get; private set; }

        public void Checkpoint() => Checkpoints++;

        public void RecordStep() => Records++;
    }

    private sealed class FakeClipboard : IClipboard
    {
        public string? Text { get; set; }

        public int Writes { get; private set; }

        public Task<string?> GetTextAsync(CancellationToken cancellationToken) => Task.FromResult(Text);

        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            Writes++;
            return Task.CompletedTask;
        }
    }
}
