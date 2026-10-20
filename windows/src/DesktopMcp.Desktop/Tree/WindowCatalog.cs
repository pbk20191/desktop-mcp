using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Tree;

/// <summary>The window scan: every walk candidate in z-order, the application windows among them, the focused window.</summary>
internal sealed record WindowScan(IReadOnlyList<long> Candidates, IReadOnlyList<WindowInfo> Windows, WindowInfo? Active)
{
    public static WindowScan Empty { get; } = new([], [], null);
}

internal interface IWindowScanner
{
    Task<WindowScan> ScanAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The focused window as <see cref="ScanAsync"/> reports it (blocked windows skipped, the same retries), without
    /// probing the other windows (P3 Decisions 24: App resize without a name, WaitFor's active_window).
    /// </summary>
    Task<WindowInfo?> ScanActiveAsync(CancellationToken cancellationToken);
}

/// <summary>Reads a top-level window's UIA properties; may throw, and times out through the hang breaker.</summary>
internal interface IWindowProbe
{
    Task<WindowProbe> ProbeAsync(long window, CancellationToken cancellationToken);
}

internal sealed class UiaWindowProbe(UiaWorker<UiaSession> worker) : IWindowProbe
{
    /// <summary>The hang breaker's deadline for one window probe (Decisions 2).</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public Task<WindowProbe> ProbeAsync(long window, CancellationToken cancellationToken) =>
        worker.RunAsync((session, _) => UiaJob.Run("UI Automation window probe", () => new UiaTreeSource(session).ProbeWindow(window)),
            Deadline, cancellationToken);
}

/// <summary>
/// upstream desktop/service.py:1068-1245: get_controls_handles, get_windows and get_active_window. Windows are taken in
/// z-order and Depth is the index in that order (Decisions 3); the focused window comes from Win32 (Decisions 25).
/// </summary>
/// <remarks>
/// Only the hang breaker's deadline (<see cref="UiaTimeoutException"/>) blocks a window and its process; a dead element,
/// UIA's own timeout and any other failure skip the window for this scan only (the focused window's read is retried, as
/// upstream does), and cancellation ends the scan.
/// </remarks>
internal sealed class WindowCatalog(
    IWindowSystem windows, IWindowProbe probe, IVirtualDesktops desktops, HangBlockList blocks, int ownProcessId,
    Func<TimeSpan, CancellationToken, Task> delay) : IWindowScanner
{
    /// <summary>upstream desktop/service.py:1112-1113.</summary>
    public const int ActiveWindowAttempts = 3;
    public static readonly TimeSpan ActiveWindowRetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly string[] ShellClasses = ["Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    public async Task<WindowScan> ScanAsync(CancellationToken cancellationToken)
    {
        var candidates = Candidates();
        var list = new List<WindowInfo>();
        for (var depth = 0; depth < candidates.Count; depth++)
        {
            if ((await DescribeAsync(candidates[depth], depth, applicationOnly: true, cancellationToken).ConfigureAwait(false)).Window is { } window)
                list.Add(window);
        }
        var active = await ActiveAsync(list, cancellationToken).ConfigureAwait(false);
        return new WindowScan(candidates, list, active);
    }

    // Depth is 0: the z-order index would need the whole candidate list, and nothing that reads the active window alone
    // uses it.
    public Task<WindowInfo?> ScanActiveAsync(CancellationToken cancellationToken) => ActiveAsync([], cancellationToken);

    // upstream desktop/service.py:1068-1105: visible windows on this virtual desktop that this server does not own, then
    // the desktop and taskbar windows.
    internal IReadOnlyList<long> Candidates()
    {
        var visible = windows.EnumerateTopLevel()
            .Where(window => windows.IsWindow(window) && windows.IsVisible(window) && windows.GetProcessId(window) != ownProcessId)
            .ToList();
        var result = desktops.OnCurrentDesktop(visible).ToList();
        foreach (var className in ShellClasses)
        {
            var shell = windows.FindWindow(className);
            if (shell != 0 && !result.Contains(shell)) result.Add(shell);
        }
        return result;
    }

    /// <summary>
    /// A described window, or none: <see cref="ProbeFailed"/> tells a transient probe failure (worth another attempt) from
    /// a window that is blocked, timed out or filtered out.
    /// </summary>
    private readonly record struct Description(WindowInfo? Window, bool ProbeFailed);

    // upstream desktop/service.py:1054-1066 and 1192-1245. A window whose probe fails is skipped (upstream drops the
    // whole list on most errors).
    private async Task<Description> DescribeAsync(long window, int depth, bool applicationOnly, CancellationToken cancellationToken)
    {
        var processId = windows.GetProcessId(window);
        if (blocks.IsBlocked(window, processId)) return default;
        // A cancelled scan probes nothing more: the hang breaker would abandon its thread for a job that never ran.
        cancellationToken.ThrowIfCancellationRequested();
        WindowProbe described;
        try
        {
            described = await probe.ProbeAsync(window, cancellationToken).ConfigureAwait(false);
        }
        catch (UiaTimeoutException)
        {
            blocks.Block(window, processId);
            return default;
        }
        catch (Exception e) when (ComErrors.IsTimeout(e))
        {
            // UIA's own timeout: not retried (Decisions 2), and only the hang breaker's deadline blocks.
            return default;
        }
        catch (Exception e) when (e is not (OperationCanceledException or UiaUnavailableException or ObjectDisposedException))
        {
            return new Description(null, ProbeFailed: true);
        }
        var status = Status(window);
        if (applicationOnly)
        {
            // upstream desktop/service.py:1054-1066 is_overlay_window.
            var name = described.Name.Trim();
            if (name.Contains("Overlay", StringComparison.Ordinal) || (name.Length == 0 && !described.HasChildren)) return default;
            if (described.ControlType is not (TreeConfig.Window or TreeConfig.Pane)) return default;
            if (!described.HasWindowPattern || !(described.CanMinimize && described.CanMaximize)) return default;
            if (described.Bounds.IsEmpty && status != WindowStatus.Minimized) return default;
        }
        return new Description(new WindowInfo(described.Name, depth, status, described.Bounds, window, described.ProcessId,
            TreeConfig.IsBrowser(windows.GetProcessName(described.ProcessId))), ProbeFailed: false);
    }

    // upstream desktop/service.py:1115-1172 get_active_window: a null foreground window and a failed read of the focused
    // window are both retried after 100 ms (1162-1168); a blocked or timed-out window is not.
    private async Task<WindowInfo?> ActiveAsync(List<WindowInfo> list, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ActiveWindowAttempts; attempt++)
        {
            var foreground = windows.GetForegroundWindow();
            if (foreground == 0)
            {
                await delay(ActiveWindowRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var root = windows.GetRoot(foreground);
            if (windows.GetClassName(root) == "Progman") return null;
            if (list.FirstOrDefault(window => window.Handle == root) is { } listed) return listed;
            var described = await DescribeAsync(root, depth: 0, applicationOnly: false, cancellationToken).ConfigureAwait(false);
            if (!described.ProbeFailed) return described.Window;
            await delay(ActiveWindowRetryDelay, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    // upstream desktop/service.py:317-325 get_window_status.
    private WindowStatus Status(long window) =>
        windows.IsMinimized(window) ? WindowStatus.Minimized
        : windows.IsMaximized(window) ? WindowStatus.Maximized
        : windows.IsVisible(window) ? WindowStatus.Normal
        : WindowStatus.Hidden;
}
