using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Desktop.WindowControl;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

/// <summary>
/// The act tools' desktop for the always-run tests: input recorded as strings, labels mapped to points, a foreground
/// that always succeeds. Nothing here touches the real desktop.
/// </summary>
internal sealed class FakeDesktop
{
    private readonly TaskCompletionSource _firstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstCallReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeDesktop()
    {
        Input = new FakeInput(this);
        Targets = new FakeTargets(this);
    }

    /// <summary>Label number to the point its target resolves to; any other label is out of range.</summary>
    public Dictionary<long, ScreenPoint> Labels { get; } = [];

    public ScreenPoint Cursor { get; set; } = new(0, 0);

    /// <summary>What <see cref="IDesktopInput.ScrollAsync"/> returns (upstream's invalid combination text), or null.</summary>
    public string? ScrollResult { get; set; }

    /// <summary>When set, the first input call is recorded and then waits for <see cref="ReleaseFirstCall"/>.</summary>
    public bool BlockFirstCall { get; set; }

    /// <summary>When set, <see cref="IActionTargets.Point"/> throws an <see cref="ArgumentException"/> with this text.</summary>
    public string? PointError { get; set; }

    public FakeInput Input { get; }

    public FakeTargets Targets { get; }

    public Task FirstCallStarted => _firstCallStarted.Task;

    public void ReleaseFirstCall() => _firstCallReleased.TrySetResult();

    public ToolServices Services() => ToolServices.CreateDefault() with
    {
        Input = Input,
        Targets = Targets,
        Foreground = new FakeForeground(),
        Ledger = new InputLedger(),
        InputLock = new DesktopInputLock(),
    };

    /// <summary>
    /// Records each call as one string: <c>Click (x,y) Button n</c>, <c>Move (x,y)</c>, <c>Drag (x,y)|cursor (x,y) duration|none</c>,
    /// <c>Scroll (x,y)|cursor type direction n</c>, <c>Keys count intervalms waitms</c>,
    /// <c>Type (x,y) [clear ]text</c> (or <c>Type (x,y) click-only</c> when the after-click hook throws) and
    /// <c>MultiSelect ctrl=bool count</c>.
    /// </summary>
    internal sealed class FakeInput(FakeDesktop desktop) : IDesktopInput
    {
        private readonly Lock _lock = new();
        private readonly List<string> _calls = [];
        private bool _first = true;

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_lock) return [.. _calls];
            }
        }

        public ScreenPoint Cursor => desktop.Cursor;

        public async Task ClickAsync(ScreenPoint point, MouseButton button, long clicks, CancellationToken ct) =>
            await RecordAsync($"Click {P(point)} {button} {clicks}", ct);

        public async Task MoveAsync(ScreenPoint to, CancellationToken ct) => await RecordAsync($"Move {P(to)}", ct);

        public async Task<DragResult> DragAsync(ScreenPoint? from, ScreenPoint to, double? duration, CancellationToken ct)
        {
            await RecordAsync($"Drag {(from is { } start ? P(start) : "cursor")} {P(to)} {duration?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}", ct);
            return new DragResult(from ?? desktop.Cursor, duration);
        }

        public async Task<string?> ScrollAsync(ScreenPoint? at, string type, string direction, long wheelTimes, CancellationToken ct)
        {
            await RecordAsync($"Scroll {(at is { } point ? P(point) : "cursor")} {type} {direction} {wheelTimes}", ct);
            return desktop.ScrollResult;
        }

        public async Task PressKeysAsync(IReadOnlyList<KeyToken> tokens, TimeSpan interval, TimeSpan waitTime, CancellationToken ct) =>
            await RecordAsync($"Keys {tokens.Count} {interval.TotalMilliseconds}ms {waitTime.TotalMilliseconds}ms", ct);

        public async Task TypeAsync(ScreenPoint at, string text, CaretPosition caret, bool clear, bool pressEnter,
            Func<CancellationToken, Task>? afterClick, CancellationToken ct)
        {
            try
            {
                if (afterClick is not null) await afterClick(ct);
            }
            catch
            {
                await RecordAsync($"Type {P(at)} click-only", ct);
                throw;
            }
            await RecordAsync($"Type {P(at)} {(clear ? "clear " : "")}{text}", ct);
        }

        public async Task<IReadOnlyList<ScreenPoint>> MultiSelectAsync(bool pressCtrl, int count,
            Func<int, CancellationToken, Task<ScreenPoint>> target, CancellationToken ct)
        {
            var points = new List<ScreenPoint>();
            for (var i = 0; i < count; i++) points.Add(await target(i, ct));
            await RecordAsync($"MultiSelect ctrl={pressCtrl} {count}", ct);
            return points;
        }

        private static string P(ScreenPoint point) => $"({point.X},{point.Y})";

        private async Task RecordAsync(string call, CancellationToken ct)
        {
            bool block;
            lock (_lock)
            {
                _calls.Add(call);
                block = _first && desktop.BlockFirstCall;
                _first = false;
            }
            if (!block) return;
            desktop._firstCallStarted.TrySetResult();
            await desktop._firstCallReleased.Task.WaitAsync(ct);
        }
    }

    /// <summary>Records each resolve as <c>Label n</c> or <c>Point (x,y)</c>.</summary>
    internal sealed class FakeTargets(FakeDesktop desktop) : IActionTargets
    {
        private readonly Lock _lock = new();
        private readonly List<string> _calls = [];

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_lock) return [.. _calls];
            }
        }

        public Task<ActionTarget> LabelAsync(long label, CancellationToken ct)
        {
            Record($"Label {label}");
            return desktop.Labels.TryGetValue(label, out var point)
                ? Task.FromResult(new ActionTarget(point, 1, "Window"))
                : throw new ArgumentException(LabelErrors.NotFound(label));
        }

        public ActionTarget Point(ScreenPoint point)
        {
            Record($"Point ({point.X},{point.Y})");
            return desktop.PointError is { } error ? throw new ArgumentException(error) : new ActionTarget(point, 0, "");
        }

        private void Record(string call)
        {
            lock (_lock) _calls.Add(call);
        }

        public void CheckLabels(IReadOnlyList<long> labels, string wrapper)
        {
            foreach (var label in labels)
                if (!desktop.Labels.ContainsKey(label)) throw new ArgumentException($"{wrapper}: {LabelErrors.OutOfRange(label)}");
        }

        public void CheckForegroundInput()
        {
        }
    }

    internal sealed class FakeForeground : IForeground
    {
        public Task<ForegroundResult> SwitchToAsync(long window, CancellationToken ct) => Task.FromResult(new ForegroundResult(true, false));

        public Task EnsureKeyboardTargetAsync(long window, string windowName, CancellationToken ct) => Task.CompletedTask;
    }
}
