using System.ComponentModel;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Threading;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.WindowControl;

namespace DesktopMcp.Desktop.Apps;

/// <summary>An App result text; <see cref="IsError"/> makes the tool an <c>isError</c> result (upstream raised).</summary>
public sealed record AppResult(string Text, bool IsError);

/// <summary>The App tool's four modes (upstream desktop/service.py:415-584, tools/app.py).</summary>
public interface IAppService
{
    Task<AppResult> SwitchAsync(string name, CancellationToken ct);

    Task<AppResult> ResizeAsync(string? name, (int X, int Y)? loc, (int W, int H)? size, CancellationToken ct);

    Task<AppResult> LaunchAsync(string name, CancellationToken ct);

    Task<AppResult> LaunchExecutableAsync(string executable, IReadOnlyList<string> args, string? cwd, CancellationToken ct);
}

/// <summary>
/// The App tool's modes. Switch and resize: windows come from the window catalog's scan and never replace the snapshot
/// store (P3 Decisions 24); the name is matched with <see cref="Fuzzy.ExtractOne"/> at cutoff 70 (Decisions 30), and of
/// several windows with one name the first wins, the active window first and then z-order (Decisions 29). Launch: the
/// AppsFolder entries, packaged activation or ShellExecuteEx, then the window waits (Decisions 31); launch_executable:
/// CreateProcess of the resolved path (Decisions 34). Launched apps die with the server, except packaged ones (Open
/// question 1's ruling).
/// </summary>
public sealed class AppService : IAppService
{
    /// <summary>upstream desktop/service.py:438 score_cutoff.</summary>
    public const int MatchCutoff = 70;

    /// <summary>How long one activation or ShellExecuteEx may take before it is abandoned (preflight F24).</summary>
    public static readonly TimeSpan LaunchLimit = TimeSpan.FromSeconds(10);

    /// <summary>Each launch window wait: upstream's <c>Exists(maxSearchSeconds=10)</c>.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>upstream uia/core.py:31 SEARCH_INTERVAL.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IWindowScanner _windows;
    private readonly IForeground _foreground;
    private readonly Func<long, string, ScreenRect, CancellationToken, Task> _resize;
    private readonly IAppCatalog _catalog;
    private readonly IAppLauncher _launcher;
    private readonly IWindowSystem _system;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;

    public AppService(DesktopMachine machine, IForeground foreground)
        : this(machine.Catalog, foreground, new WindowPlacement().ResizeAsync, new AppCatalog(), new AppLauncher(), machine.Windows,
            (delay, ct) => Task.Delay(delay, ct), TimeProvider.System)
    {
    }

    /// <param name="resize">Moves and sizes a window (<see cref="WindowPlacement.ResizeAsync"/>); throws <see cref="DesktopException"/> with the result text.</param>
    /// <param name="catalog">The Start menu apps.</param>
    /// <param name="launcher">Starts apps; its shell calls run on an STA thread bounded by <see cref="LaunchLimit"/>.</param>
    /// <param name="system">The top-level windows the launch waits read.</param>
    /// <param name="delay">The wait between polls.</param>
    /// <param name="time">The clock of the waits and of <see cref="LaunchLimit"/>.</param>
    internal AppService(IWindowScanner windows, IForeground foreground, Func<long, string, ScreenRect, CancellationToken, Task> resize,
        IAppCatalog catalog, IAppLauncher launcher, IWindowSystem system, Func<TimeSpan, CancellationToken, Task> delay, TimeProvider time)
    {
        _windows = windows;
        _foreground = foreground;
        _resize = resize;
        _catalog = catalog;
        _launcher = launcher;
        _system = system;
        _delay = delay;
        _time = time;
    }

    /// <summary>
    /// upstream desktop/service.py:559-584 switch_app with Decisions 33: the switch is Decisions 7's bounded sequence and
    /// its 1 s handle check; every failure is an error, an exception gives <c>Error switching app: message</c>.
    /// </summary>
    public async Task<AppResult> SwitchAsync(string name, CancellationToken ct)
    {
        try
        {
            var (window, error) = await FindAsync(name, ct).ConfigureAwait(false);
            if (window is null) return new AppResult(error!, true);
            var result = await _foreground.SwitchToAsync(window.Handle, ct).ConfigureAwait(false);
            if (!result.Succeeded) return new AppResult($"Failed to bring {window.Name} to the foreground within 1 second.", true);
            return new AppResult(result.WasMinimized
                ? $"Restored {TreeConfig.Title(window.Name)} from minimized and switched to it."
                : $"Switched to {TreeConfig.Title(window.Name)} window.", false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new AppResult($"Error switching app: {e.Message}", true);
        }
    }

    /// <summary>
    /// upstream desktop/service.py:444-476 resize_app with Decisions 24 and 32: without a name, the current foreground
    /// window; a missing location or size keeps the window's; every text is a normal result, as upstream returned them.
    /// </summary>
    public async Task<AppResult> ResizeAsync(string? name, (int X, int Y)? loc, (int W, int H)? size, CancellationToken ct)
    {
        WindowInfo? window;
        if (name is not null)
        {
            (window, var error) = await FindAsync(name, ct).ConfigureAwait(false);
            if (window is null) return new AppResult(error!, false);
        }
        else
        {
            window = await _windows.ScanActiveAsync(ct).ConfigureAwait(false);
            if (window is null) return new AppResult("No active window found", false);
        }

        if (window.Status == WindowStatus.Minimized) return new AppResult($"{window.Name} is minimized", false);
        if (window.Status == WindowStatus.Maximized) return new AppResult($"{window.Name} is maximized", false);
        var (x, y) = loc ?? (window.Bounds.Left, window.Bounds.Top);
        var (width, height) = size ?? (window.Bounds.Width, window.Bounds.Height);
        try
        {
            await _resize(window.Handle, window.Name, new ScreenRect(x, y, unchecked(x + width), unchecked(y + height)), ct).ConfigureAwait(false);
        }
        catch (DesktopException e)
        {
            return new AppResult(e.Message, false);
        }
        return new AppResult($"{window.Name} resized to {width}x{height} at {x},{y}.", false);
    }

    /// <summary>
    /// upstream desktop/service.py:519-557 launch_app and 478-507 app("launch") with Decisions 31: the AppsFolder entries
    /// by lower-cased display name (the first of a name wins), <see cref="Fuzzy.ExtractOne"/> at cutoff 70, then a
    /// packaged app is activated and anything else goes through ShellExecuteEx, each on an STA thread abandoned after
    /// <see cref="LaunchLimit"/>. Then up to 10 s for a visible top-level window of the process (when its id is known),
    /// then up to 10 s for a visible top-level window whose title contains <paramref name="name"/>, polling every 0.5 s.
    /// Every text is a normal result, as upstream returned them.
    /// </summary>
    public async Task<AppResult> LaunchAsync(string name, CancellationToken ct)
    {
        var apps = await _catalog.ListAsync(ct).ConfigureAwait(false);
        var parsingNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var app in apps)
        {
            // upstream keeps only rows with both a Name and an AppID.
            if (app.Name.Length == 0 || app.ParsingName.Length == 0) continue;
            var key = app.Name.ToLowerInvariant();
            if (parsingNames.TryAdd(key, app.ParsingName)) names.Add(key);
        }
        var title = TreeConfig.Title(name);
        if (Fuzzy.ExtractOne(name, names, MatchCutoff) is not { } match) return new AppResult($"{title} not found in start menu.", false);

        var parsingName = parsingNames[match.Choice];
        int? processId;
        try
        {
            processId = await StaThread.RunAsync(
                    () => AppLauncher.IsPackaged(parsingName) ? _launcher.ActivatePackaged(parsingName) : _launcher.StartFromShell(parsingName), ct)
                .WaitAsync(LaunchLimit, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new AppResult($"Failed to launch {title}: it did not start within 10 s.", false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new AppResult($"Failed to launch {title}: {e.Message}", false);
        }

        var launched = processId is > 0 and var pid
            && await PollAsync(() => AnyVisibleWindow(window => _system.GetProcessId(window) == pid), ct).ConfigureAwait(false);
        if (!launched)
            launched = await PollAsync(() => AnyVisibleWindow(window => _system.GetTitle(window).Contains(name, StringComparison.OrdinalIgnoreCase)), ct)
                .ConfigureAwait(false);
        return new AppResult(launched ? $"{title} launched." : $"Launching {title} sent, but window not detected yet.", false);
    }

    /// <summary>
    /// upstream tools/app.py:24-71 _launch_executable with Decisions 34: <c>~</c> expanded, the executable and working
    /// directory resolved as Python's Path.resolve(), the argv joined as list2cmdline, and the result as json.dumps(indent=2).
    /// Path and CreateProcess failures are errors (upstream raised ValueError or OSError). The tool has checked args first.
    /// </summary>
    public Task<AppResult> LaunchExecutableAsync(string executable, IReadOnlyList<string> args, string? cwd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = PathResolver.Resolve(ConfigFile.ExpandUser(executable, home));
        if (!File.Exists(path)) return Task.FromResult(new AppResult($"Executable does not exist: {path}", true));
        string? directory = null;
        if (cwd is not null)
        {
            directory = PathResolver.Resolve(ConfigFile.ExpandUser(cwd, home));
            if (!Directory.Exists(directory)) return Task.FromResult(new AppResult($"Working directory does not exist: {directory}", true));
        }

        int processId;
        try
        {
            processId = _launcher.StartExecutable(CommandLine.Join([path, .. args]), directory);
        }
        catch (Win32Exception e)
        {
            return Task.FromResult(new AppResult($"[WinError {e.NativeErrorCode}] {PythonOSErrorText(e.Message)}", true));
        }
        return Task.FromResult(new AppResult(PyJson.Dumps(
        [
            new("pid", (long)processId),
            new("executable", path),
            new("args", args),
            new("cwd", directory),
        ]), false));
    }

    // CPython's PyErr_SetFromWindowsErr trims trailing whitespace and dots from the system message.
    private static string PythonOSErrorText(string message)
    {
        var length = message.Length;
        while (length > 0 && (message[length - 1] <= ' ' || message[length - 1] == '.')) length--;
        return message[..length];
    }

    private bool AnyVisibleWindow(Func<long, bool> match)
    {
        foreach (var window in _system.EnumerateTopLevel())
            if (_system.IsVisible(window) && match(window)) return true;
        return false;
    }

    /// <summary>Checks now and every <see cref="PollInterval"/> until the condition holds or <see cref="WaitLimit"/> has passed.</summary>
    private async Task<bool> PollAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + WaitLimit;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (condition()) return true;
            if (_time.GetUtcNow() >= deadline) return false;
            await _delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    // upstream desktop/service.py:415-442 _find_window_by_name, reading a fresh scan (Decisions 24) where upstream
    // refreshed its desktop state, and keeping the first window of each name (Decisions 29).
    private async Task<(WindowInfo? Window, string? Error)> FindAsync(string name, CancellationToken ct)
    {
        var scan = await _windows.ScanAsync(ct).ConfigureAwait(false);
        var byName = new Dictionary<string, WindowInfo>(StringComparer.Ordinal);
        var names = new List<string>();
        var seen = new HashSet<long>();
        foreach (var window in scan.Active is { } active ? [active, .. scan.Windows] : scan.Windows)
        {
            if (!seen.Add(window.Handle)) continue;
            if (byName.TryAdd(window.Name, window)) names.Add(window.Name);
        }
        if (names.Count == 0) return (null, "No windows found on the desktop.");
        return Fuzzy.ExtractOne(name, names, MatchCutoff) is { } match
            ? (byName[match.Choice], null)
            : (null, $"Application {TreeConfig.Title(name)} not found.");
    }
}
