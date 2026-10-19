using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
        if (pid is { } id)
        {
            // psutil.Process(pid) raises this ValueError, which upstream does not catch.
            if (id < 0) throw new ArgumentException($"pid must be a positive integer (got {id})");
            if (id > int.MaxValue) return $"No process with PID {id} found.";
            Process process;
            try { process = Process.GetProcessById((int)id); }
            catch (ArgumentException) { return $"No process with PID {id} found."; }
            using (process)
            {
                var processName = ProcessSnapshot.Names().GetValueOrDefault((int)id) ?? process.ProcessName;
                try { process.Kill(); }
                catch (Win32Exception) { return $"Access denied to kill PID {id}. Try running as administrator."; }
                catch (InvalidOperationException) { return $"No process with PID {id} found."; }
                killed.Add($"{processName} (PID {id})");
            }
        }
        else
        {
            foreach (var (processId, processName) in ProcessSnapshot.Names().OrderBy(p => p.Key))
            {
                if (!processName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var process = Process.GetProcessById(processId);
                    process.Kill();
                    killed.Add($"{processName} (PID {processId})");
                }
                catch (Exception e) when (e is ArgumentException or Win32Exception or InvalidOperationException)
                {
                }
            }
        }
        if (killed.Count == 0) return $"No process matching \"{name}\" found or access denied.";
        return $"{(force ? "Force killed" : "Terminated")}: {string.Join(", ", killed)}";
    }

    // Two CPU-time samples CpuSampleInterval apart; like psutil, a share of one CPU (can exceed 100).
    private static async Task<IReadOnlyList<ProcessRow>> SampleAsync(CancellationToken cancellationToken)
    {
        var first = CpuTimes();
        var clock = Stopwatch.StartNew();
        await Task.Delay(CpuSampleInterval, cancellationToken).ConfigureAwait(false);
        var second = CpuTimes();
        var elapsed = clock.Elapsed.TotalSeconds;
        var names = ProcessSnapshot.Names();
        var rows = new List<ProcessRow>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!names.TryGetValue(process.Id, out var name)) continue;
                var cpu = first.TryGetValue(process.Id, out var before) && second.TryGetValue(process.Id, out var after)
                    ? Math.Max(0, (after - before).TotalSeconds / elapsed * 100)
                    : 0;
                long workingSet;
                try { workingSet = process.WorkingSet64; }
                catch (InvalidOperationException) { continue; }
                // Python's round(rss / 2**20, 1); sorting uses the rounded value, as upstream does.
                var memory = double.Parse(PyText.Fixed(workingSet / (1024.0 * 1024.0), 1), CultureInfo.InvariantCulture);
                rows.Add(new ProcessRow(process.Id, name, cpu, memory));
            }
        }
        return rows;
    }

    private static Dictionary<int, TimeSpan> CpuTimes()
    {
        var times = new Dictionary<int, TimeSpan>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { times[process.Id] = process.TotalProcessorTime; }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return times;
    }
}
