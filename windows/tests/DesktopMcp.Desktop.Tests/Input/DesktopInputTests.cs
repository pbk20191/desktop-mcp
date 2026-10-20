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

    private sealed class InputRig
    {
        private readonly List<(InjectedInput Input, TimeSpan At)> _log = [];
        private readonly Action<int>? _onDelay;
        private TimeSpan _now;
        private ScreenPoint _cursor;

        public InputRig(ScreenPoint cursor, uint doubleClickTimeMs = 500, Action<int>? onDelay = null, string? clipboard = null,
            bool onlyNonText = false)
        {
            _cursor = cursor;
            _onDelay = onDelay;
            Clipboard = new FakeClipboard { Text = clipboard };
            Input = new DesktopInput(Ledger, Clipboard, NoCheckpoint.Instance, new FakeInjector(this), () => _cursor, () => Screen,
                DelayAsync, () => doubleClickTimeMs, () => onlyNonText);
        }

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
                foreach (var input in inputs)
                {
                    rig._log.Add((input, rig._now));
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
