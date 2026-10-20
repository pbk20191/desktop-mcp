using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Errors;
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
/// App switch and resize. Windows come from the window catalog's scan and never replace the snapshot store (P3
/// Decisions 24); the name is matched with <see cref="Fuzzy.ExtractOne"/> at cutoff 70 (Decisions 30), and of several
/// windows with one name the first wins, the active window first and then z-order (Decisions 29).
/// </summary>
public sealed class AppService : IAppService
{
    /// <summary>upstream desktop/service.py:438 score_cutoff.</summary>
    public const int MatchCutoff = 70;

    private readonly IWindowScanner _windows;
    private readonly IForeground _foreground;
    private readonly Func<long, string, ScreenRect, CancellationToken, Task> _resize;

    public AppService(DesktopMachine machine, IForeground foreground)
        : this(machine.Catalog, foreground, new WindowPlacement().ResizeAsync)
    {
    }

    /// <param name="resize">Moves and sizes a window (<see cref="WindowPlacement.ResizeAsync"/>); throws <see cref="DesktopException"/> with the result text.</param>
    internal AppService(IWindowScanner windows, IForeground foreground, Func<long, string, ScreenRect, CancellationToken, Task> resize)
    {
        _windows = windows;
        _foreground = foreground;
        _resize = resize;
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

    public Task<AppResult> LaunchAsync(string name, CancellationToken ct) => throw new NotSupportedException("not available in this build yet");

    public Task<AppResult> LaunchExecutableAsync(string executable, IReadOnlyList<string> args, string? cwd, CancellationToken ct) =>
        throw new NotSupportedException("not available in this build yet");

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
