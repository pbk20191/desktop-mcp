using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tree;

internal interface ITreeCapture
{
    /// <summary>Walks the active window first, then <paramref name="others"/>, into one tree (upstream tree/service.py:113-224).</summary>
    Task<TreeSnapshot> CaptureAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, CancellationToken cancellationToken);
}

/// <summary>Walks one window; throws on failure and gives a timeout exception when the hang breaker fires.</summary>
internal interface IWindowWalker
{
    Task<WindowWalk> WalkAsync(long window, bool isBrowser, ScreenRect screen, ElementBudget budget, Func<int> nextId, CancellationToken cancellationToken);
}

internal sealed class UiaWindowWalker(UiaWorker<UiaSession> worker) : IWindowWalker
{
    /// <summary>The hang breaker's deadline for one window walk (Decisions 2).</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public Task<WindowWalk> WalkAsync(
        long window, bool isBrowser, ScreenRect screen, ElementBudget budget, Func<int> nextId, CancellationToken cancellationToken) =>
        // The job's token is cancelled when the deadline passes or the caller cancels, so an abandoned walk stops at its
        // next element instead of racing the next window's walk for the budget and the ids.
        worker.RunAsync((session, token) => UiaJob.Run("UI Automation window walk", () =>
        {
            var source = new UiaTreeSource(session);
            var root = source.ReadWindow(window);
            // upstream tree/service.py:939-941 strips the title before app_name_correction.
            var name = TreeConfig.WindowName(root.Name.Trim(), root.ClassName);
            return new TreeWalker(source, budget, nextId).Walk(root, new WindowContext(name, isBrowser, root.Bounds, screen), token);
        }), Deadline, cancellationToken);
}

/// <summary>
/// upstream tree/service.py:113-305: excluded processes are skipped before any UIA call, each window is retried three
/// times after 0.5, 1 and 2 s, and the budget stops the capture (the tree is then marked truncated).
/// </summary>
/// <remarks>
/// Decisions 2: the hang breaker's deadline (<see cref="UiaTimeoutException"/>) blocks the window and its process and is
/// not retried; the block is checked before every attempt, so the process's other windows are not walked into the same
/// hang. UIA's own timeout is not retried and blocks nothing; a dead element is retried only while the window still
/// exists; cancellation ends the capture and blocks nothing. Each attempt walks under its own slice of the budget, merged
/// only when it succeeds, so a walk that failed or timed out contributes nothing, and an abandoned walk still running on
/// its thread never touches the capture's budget. Ids come from an atomic counter that keeps them unique across both.
/// </remarks>
internal sealed class TreeService(
    IWindowSystem windows, IWindowWalker walker, HangBlockList blocks, Func<string, string?> environment,
    Func<TimeSpan, CancellationToken, Task> delay) : ITreeCapture
{
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    public async Task<TreeSnapshot> CaptureAsync(long? active, IReadOnlyList<long> others, ScreenRect screen, CancellationToken cancellationToken)
    {
        var budget = new ElementBudget(SnapshotSettings.MaxTreeElements(environment(SnapshotSettings.MaxTreeElementsVariable)));
        var excluded = SnapshotSettings.ExcludedProcesses(environment(SnapshotSettings.ExcludeProcessesVariable));
        var targets = new List<(long Window, int ProcessId, bool IsBrowser)>();
        var seen = new HashSet<long>();
        foreach (var window in active is { } first ? [first, .. others] : others)
        {
            // The active window may also be among the others, and a window listed twice is walked once.
            if (!seen.Add(window)) continue;
            var processId = windows.GetProcessId(window);
            var processName = windows.GetProcessName(processId);
            if (processName is not null && excluded.Contains(processName.ToLowerInvariant())) continue;
            if (blocks.IsBlocked(window, processId)) continue;
            // upstream tree/service.py:259-260: with a focused window, the desktop icons (Progman) are not walked.
            if (active is not null && windows.GetClassName(window) == "Progman") continue;
            targets.Add((window, processId, TreeConfig.IsBrowser(processName)));
        }

        var ids = new IdCounter();
        var walks = new List<WindowWalk>();
        foreach (var (window, processId, isBrowser) in targets)
        {
            // upstream tree/service.py:268-275: a spent budget skips the remaining windows (the budget is marked truncated).
            if (budget.Exhausted) break;
            if (await WalkAsync(window, processId, isBrowser, screen, budget, ids.Next, cancellationToken).ConfigureAwait(false) is { } walk)
                walks.Add(walk);
        }
        return Assemble(walks, budget);
    }

    // upstream tree/service.py:276-303.
    private async Task<WindowWalk?> WalkAsync(
        long window, int processId, bool isBrowser, ScreenRect screen, ElementBudget budget, Func<int> nextId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            // A cancelled capture walks nothing more: the hang breaker would abandon its thread for a job that never ran.
            cancellationToken.ThrowIfCancellationRequested();
            // Another window of this process may have timed out since the targets were listed (or during a retry delay).
            if (blocks.IsBlocked(window, processId)) return null;
            // Upstream counts a failed attempt's elements against the capture; this port does not, on purpose (see remarks).
            var slice = budget.Slice();
            try
            {
                var walk = await walker.WalkAsync(window, isBrowser, screen, slice, nextId, cancellationToken).ConfigureAwait(false);
                budget.Absorb(slice);
                return walk;
            }
            catch (UiaTimeoutException)
            {
                blocks.Block(window, processId);
                return null;
            }
            catch (Exception e) when (ComErrors.IsTimeout(e))
            {
                return null;
            }
            catch (Exception e) when (ComErrors.IsDeadElement(e) && !windows.IsWindow(window))
            {
                // The window is gone: retrying cannot bring it back.
                return null;
            }
            catch (Exception e) when (e is not (OperationCanceledException or UiaUnavailableException or ObjectDisposedException))
            {
                // Retried (a dead element too, while the window exists); after the last retry the window is left out.
                if (attempt == RetryDelays.Length) return null;
            }
            await delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>upstream tree/service.py:181-224: the desktop root over the windows, then the semantic tree cleaned up.</summary>
    internal static TreeSnapshot Assemble(IReadOnlyList<WindowWalk> walks, ElementBudget budget)
    {
        var interactive = walks.SelectMany(walk => walk.Interactive).ToArray();
        var scrollable = walks.SelectMany(walk => walk.Scrollable).ToArray();
        var root = new SemanticNode(SemanticKind.Desktop, "Desktop", "Desktop");
        root.Children.AddRange(walks.Select(walk => walk.Window));
        SemanticTree.DropElements(root, interactive.Select(e => e.Id).Concat(scrollable.Select(e => e.Id)).ToHashSet());
        SemanticTree.PruneStructural(root);
        return new TreeSnapshot(interactive, scrollable, root, budget.Truncated, budget.Limit);
    }

    /// <summary>Element ids from 0, safe to take from an abandoned walk's thread while the next walk runs.</summary>
    private sealed class IdCounter
    {
        private int _next;

        public int Next() => Interlocked.Increment(ref _next) - 1;
    }
}
