using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.Apps;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Desktop.Waiting;
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

    /// <summary>When set, <see cref="PointError"/> applies only to this point.</summary>
    public ScreenPoint? PointErrorAt { get; set; }

    /// <summary>The window a checked point lands on (0: no window); it is named <c>Window</c>.</summary>
    public long PointWindow { get; set; } = 2;

    /// <summary>Labels in range whose locate fails as stale (<c>button "OK"</c> in <c>Notepad</c>).</summary>
    public HashSet<long> StaleLabels { get; } = [];

    /// <summary>When set, the read-back returns the text of the latest Type call instead of <see cref="ReadBack"/>.</summary>
    public bool ReadBackEchoesTypedText { get; set; }

    /// <summary>Each Type call's caret and Enter as <c>Caret PressEnter</c>.</summary>
    public IReadOnlyList<string> TypeOptions
    {
        get
        {
            lock (_typeOptions) return [.. _typeOptions];
        }
    }

    private readonly List<string> _typeOptions = [];
    private string? _lastTyped;

    /// <summary>What the read-back returns (null: skipped or unreadable).</summary>
    public string? ReadBack { get; set; }

    /// <summary>When set, <see cref="IForeground.EnsureKeyboardTargetAsync"/> throws a <see cref="DesktopException"/> with this text.</summary>
    public string? ForegroundFails { get; set; }

    /// <summary>When set, <see cref="ForegroundFails"/> applies only to this window.</summary>
    public long? ForegroundFailsFor { get; set; }

    /// <summary>When set, <see cref="IActionTargets.CheckForegroundInput"/> throws a <see cref="DesktopException"/> with this text.</summary>
    public string? ForegroundInputError { get; set; }

    public int ReadBackCalls => Volatile.Read(ref _readBackCalls);

    /// <summary>Each keyboard-target check as <c>window name</c>.</summary>
    public IReadOnlyList<string> ForegroundChecks
    {
        get
        {
            lock (_foregroundChecks) return [.. _foregroundChecks];
        }
    }

    private int _readBackCalls;
    private readonly List<string> _foregroundChecks = [];

    /// <summary>What every <see cref="IAppService"/> call returns.</summary>
    public AppResult AppResult { get; set; } = new("ok", false);

    /// <summary>
    /// Each App service call as <c>Launch name</c>, <c>Switch name</c>, <c>Resize name (x, y)|none (w, h)|none</c> or
    /// <c>LaunchExecutable executable [args] cwd</c>.
    /// </summary>
    public IReadOnlyList<string> AppCalls
    {
        get
        {
            lock (_appCalls) return [.. _appCalls];
        }
    }

    private readonly List<string> _appCalls = [];

    /// <summary>
    /// What each WaitFor poll returns, in order; the last entry repeats once the list is used up. A null detail is a
    /// transient failure (Decisions 28).
    /// </summary>
    public IReadOnlyList<(bool Matched, string? Detail)> WaitResults { get; set; } = [(true, "matched")];

    /// <summary>Each WaitFor poll as <c>condition text window</c> (<c>-</c> for null).</summary>
    public IReadOnlyList<string> WaitCalls
    {
        get
        {
            lock (_waitCalls) return [.. _waitCalls];
        }
    }

    private readonly List<string> _waitCalls = [];

    /// <summary>How many times WaitFor checked the desktop session.</summary>
    public int SessionChecks => Volatile.Read(ref _sessionChecks);

    private int _sessionChecks;

    /// <summary>When set, the desktop session checks (WaitFor's and the input tools') throw a <see cref="DesktopException"/> with this text.</summary>
    public string? SessionError { get; set; }

    public FakeInput Input { get; }

    public FakeTargets Targets { get; }

    public Task FirstCallStarted => _firstCallStarted.Task;

    public void ReleaseFirstCall() => _firstCallReleased.TrySetResult();

    public ToolServices Services() => ToolServices.CreateDefault() with
    {
        Input = Input,
        Targets = Targets,
        Foreground = new FakeForeground(this),
        ReadBack = new FakeReadBack(this),
        Apps = new FakeApps(this),
        WaitConditions = new FakeWaitConditions(this),
        Ledger = new InputLedger(),
        InputLock = new DesktopInputLock(),
        Time = new AutoAdvanceTime(),
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
            lock (desktop._typeOptions) desktop._typeOptions.Add($"{caret} {pressEnter}");
            try
            {
                if (afterClick is not null) await afterClick(ct);
            }
            catch
            {
                await RecordAsync($"Type {P(at)} click-only", ct);
                throw;
            }
            Volatile.Write(ref desktop._lastTyped, text);
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
            if (!desktop.Labels.TryGetValue(label, out var point)) throw new ArgumentException(LabelErrors.NotFound(label));
            if (desktop.StaleLabels.Contains(label)) throw new DesktopException(LabelErrors.Stale(label, "button", "OK", "Notepad"));
            return Task.FromResult(new ActionTarget(point, 1, "Window"));
        }

        public ActionTarget Point(ScreenPoint point)
        {
            Record($"Point ({point.X},{point.Y})");
            if (desktop.PointError is { } error && (desktop.PointErrorAt is null || desktop.PointErrorAt == point)) throw new ArgumentException(error);
            return new ActionTarget(point, desktop.PointWindow, desktop.PointWindow == 0 ? "" : "Window");
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

        /// <summary>How many times an input tool checked the desktop session (throws <see cref="SessionError"/> when set).</summary>
        public int SessionChecks => Volatile.Read(ref _sessionChecks);

        private int _sessionChecks;

        public void CheckSession()
        {
            Interlocked.Increment(ref _sessionChecks);
            if (desktop.SessionError is { } error) throw new DesktopException(error);
        }

        public int ForegroundInputChecks => Volatile.Read(ref _foregroundInputChecks);

        private int _foregroundInputChecks;

        public void CheckForegroundInput()
        {
            Interlocked.Increment(ref _foregroundInputChecks);
            if (desktop.ForegroundInputError is { } error) throw new DesktopException(error);
        }
    }

    internal sealed class FakeForeground(FakeDesktop desktop) : IForeground
    {
        public Task<ForegroundResult> SwitchToAsync(long window, CancellationToken ct) => Task.FromResult(new ForegroundResult(true, false));

        public Task EnsureKeyboardTargetAsync(long window, string windowName, CancellationToken ct)
        {
            lock (desktop._foregroundChecks) desktop._foregroundChecks.Add($"{window} {windowName}");
            return desktop.ForegroundFails is { } error && (desktop.ForegroundFailsFor is null || desktop.ForegroundFailsFor == window)
                ? Task.FromException(new DesktopException(error)) : Task.CompletedTask;
        }
    }

    internal sealed class FakeApps(FakeDesktop desktop) : IAppService
    {
        public Task<AppResult> SwitchAsync(string name, CancellationToken ct) => Record($"Switch {name}");

        public Task<AppResult> ResizeAsync(string? name, (int X, int Y)? loc, (int W, int H)? size, CancellationToken ct) =>
            Record($"Resize {name} {loc?.ToString() ?? "none"} {size?.ToString() ?? "none"}");

        public Task<AppResult> LaunchAsync(string name, CancellationToken ct) => Record($"Launch {name}");

        public Task<AppResult> LaunchExecutableAsync(string executable, IReadOnlyList<string> args, string? cwd, CancellationToken ct) =>
            Record($"LaunchExecutable {executable} [{string.Join(", ", args)}] {cwd}");

        private Task<AppResult> Record(string call)
        {
            lock (desktop._appCalls) desktop._appCalls.Add(call);
            return Task.FromResult(desktop.AppResult);
        }
    }

    internal sealed class FakeWaitConditions(FakeDesktop desktop) : IWaitConditions
    {
        public void EnsureSession()
        {
            Interlocked.Increment(ref desktop._sessionChecks);
            if (desktop.SessionError is { } error) throw new DesktopException(error);
        }

        public Task<(bool Matched, string? Detail)> CheckAsync(WaitCondition condition, string? text, string? windowName, CancellationToken ct)
        {
            int poll;
            lock (desktop._waitCalls)
            {
                poll = desktop._waitCalls.Count;
                desktop._waitCalls.Add($"{WaitConditions.Name(condition)} {text ?? "-"} {windowName ?? "-"}");
            }
            var results = desktop.WaitResults;
            return Task.FromResult(results[Math.Min(poll, results.Count - 1)]);
        }
    }

    internal sealed class FakeReadBack(FakeDesktop desktop) : ITypeReadBack
    {
        public Task<string?> ReadFocusedValueAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref desktop._readBackCalls);
            return Task.FromResult(desktop.ReadBackEchoesTypedText ? Volatile.Read(ref desktop._lastTyped) : desktop.ReadBack);
        }
    }
}
