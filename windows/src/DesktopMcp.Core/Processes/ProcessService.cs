using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DesktopMcp.Core.Interop;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Processes;

public sealed record ProcessRow(int Pid, string Name, double Cpu, double MemoryMb);

/// <summary>upstream process/service.py (Decisions 15).</summary>
public sealed class ProcessService
{
    internal static readonly TimeSpan CpuSampleInterval = TimeSpan.FromMilliseconds(250);

    public async Task<string> ListAsync(string? name, string sortBy, long limit, CancellationToken cancellationToken)
    {
        var rows = await SampleAsync(cancellationToken).ConfigureAwait(false);
        return Format(rows, name, sortBy, limit);
    }

    internal static string Format(IReadOnlyList<ProcessRow> rows, string? name, string sortBy, long limit)
    {
        // psutil.process_iter yields processes in PID order; the sorts below are stable.
        IEnumerable<ProcessRow> processes = rows.OrderBy(r => r.Pid);
        if (!string.IsNullOrEmpty(name))
        {
            var needle = name.ToLowerInvariant();
            processes = processes.Where(p => Fuzzy.PartialRatio(needle, p.Name.ToLowerInvariant()) > 60);
        }
        var sorted = sortBy switch
        {
            "cpu" => processes.OrderByDescending(p => p.Cpu).ToList(),
            "name" => processes.OrderBy(p => p.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList(),
            _ => processes.OrderByDescending(p => p.MemoryMb).ToList(),
        };
        // Python's procs[:limit].
        var count = limit >= 0 ? Math.Min(limit, sorted.Count) : Math.Max(0, sorted.Count + limit);
        var shown = sorted.Take((int)count).ToList();
        if (shown.Count == 0) return $"No processes found{(string.IsNullOrEmpty(name) ? "" : $" matching {name}")}.";
        var table = TableFormatter.Simple(
            ["PID", "Name", "CPU%", "Memory"],
            shown.Select(p => (IReadOnlyList<string>)[p.Pid.ToString(CultureInfo.InvariantCulture), p.Name, $"{PyText.Fixed(p.Cpu, 1)}%", $"{PyText.Fixed(p.MemoryMb, 1)} MB"]).ToList());
        return $"Processes ({shown.Count} shown):\n{table}";
    }

    public string Kill(string? name, long? pid, bool force)
    {
        if (pid is null && name is null) return "Error: Provide either pid or name parameter for kill mode.";
        var killed = new List<string>();
        var snapshot = ProcessSnapshot.Take();
        if (pid is { } id)
        {
            // psutil.Process(pid) raises this ValueError, which upstream does not catch.
            if (id < 0) throw new ArgumentException($"pid must be a positive integer (got {id})");
            var target = id <= int.MaxValue ? snapshot.Find(p => p.Pid == id) : null;
            switch (target is null ? KillOutcome.Gone : Terminate(target))
            {
                case KillOutcome.Gone: return $"No process with PID {id} found.";
                case KillOutcome.Denied: return $"Access denied to kill PID {id}. Try running as administrator.";
            }
            killed.Add($"{target!.Name} (PID {id})");
        }
        else
        {
            // upstream skips processes without a name, so an empty name matches nothing.
            foreach (var process in snapshot)
            {
                if (process.Name.Length == 0 || !process.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (Terminate(process) == KillOutcome.Killed) killed.Add($"{process.Name} (PID {process.Pid})");
            }
        }
        if (killed.Count == 0) return $"No process matching \"{name}\" found or access denied.";
        return $"{(force ? "Force killed" : "Terminated")}: {string.Join(", ", killed)}";
    }

    private enum KillOutcome { Killed, Gone, Denied }

    /// <summary>
    /// psutil's terminate() and kill(): TerminateProcess with exit code 15 (SIGTERM). The process must still be the one
    /// in the snapshot: a different creation time means its PID was reused, so the snapshot's process is gone.
    /// </summary>
    private static KillOutcome Terminate(ProcessEntry process)
    {
        if (process.Pid == 0) return KillOutcome.Denied;   // psutil refuses PID 0 with AccessDenied
        var handle = CoreNative.OpenProcess(CoreNative.ProcessTerminate | CoreNative.ProcessQueryLimitedInformation, false, (uint)process.Pid);
        if (handle == 0) return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? KillOutcome.Denied : KillOutcome.Gone;
        try
        {
            if (!CoreNative.GetProcessTimes(handle, out var created, out _, out _, out _)) return KillOutcome.Denied;
            if (created != process.CreateTime) return KillOutcome.Gone;
            if (CoreNative.GetExitCodeProcess(handle, out var exitCode) && exitCode != CoreNative.StillActive) return KillOutcome.Gone;
            // Like psutil, ERROR_ACCESS_DENIED here means the process is already exiting.
            return CoreNative.TerminateProcess(handle, SigTerm) || Marshal.GetLastPInvokeError() == ErrorAccessDenied
                ? KillOutcome.Killed
                : KillOutcome.Denied;
        }
        finally
        {
            CoreNative.CloseHandle(handle);
        }
    }

    // Two snapshots CpuSampleInterval apart. A process is matched by PID and creation time, so a reused PID reads 0.
    private static async Task<IReadOnlyList<ProcessRow>> SampleAsync(CancellationToken cancellationToken)
    {
        var first = ProcessSnapshot.Take().ToDictionary(p => (p.Pid, p.CreateTime), p => p.CpuTime);
        var clock = Stopwatch.StartNew();
        await Task.Delay(CpuSampleInterval, cancellationToken).ConfigureAwait(false);
        var second = ProcessSnapshot.Take();
        var elapsed = clock.Elapsed.TotalSeconds;
        return second.Select(p => new ProcessRow(
            p.Pid,
            p.Name.Length > 0 ? p.Name : "Unknown",
            first.TryGetValue((p.Pid, p.CreateTime), out var before) ? CpuPercent(before, p.CpuTime, elapsed) : 0,
            // Python's round(rss / 2**20, 1); sorting uses the rounded value, as upstream does.
            Round1(p.WorkingSet / (1024.0 * 1024.0)))).ToList();
    }

    /// <summary>psutil's cpu_percent: a share of one CPU (can exceed 100), rounded to 1 decimal.</summary>
    internal static double CpuPercent(long before, long after, double elapsedSeconds) =>
        Round1(Math.Max(0, (after - before) / 1e7 / elapsedSeconds * 100));

    private static double Round1(double value) => double.Parse(PyText.Fixed(value, 1), CultureInfo.InvariantCulture);

    private const int ErrorAccessDenied = 5;
    private const uint SigTerm = 15;
}
