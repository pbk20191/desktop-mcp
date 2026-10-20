using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using DesktopMcp.Core.Processes;

namespace DesktopMcp.Core.Tests;

public class ProcessServiceTests
{
    private static readonly ProcessRow[] Rows =
    [
        new(88, "notepad.exe", 0, 20.0),
        new(4, "System", 0, 0.1),
        new(1234, "chrome.exe", 12.5, 300.5),
        new(40, "chrome.exe", 1.25, 20.0),
    ];

    private readonly ProcessService _processes = new();

    [Fact]
    public void ListSortsByMemoryAndKeepsPidOrderForTies() =>
        Assert.Equal(
            "Processes (4 shown):\n" +
            "  PID  Name         CPU%    Memory\n" +
            "-----  -----------  ------  --------\n" +
            " 1234  chrome.exe   12.5%   300.5 MB\n" +
            "   40  chrome.exe   1.2%    20.0 MB\n" +
            "   88  notepad.exe  0.0%    20.0 MB\n" +
            "    4  System       0.0%    0.1 MB",
            ProcessService.Format(Rows, null, "memory", 20));

    [Fact]
    public void ListByNameAndByCpu()
    {
        Assert.Equal(["chrome.exe", "chrome.exe", "notepad.exe", "System"], Names(ProcessService.Format(Rows, null, "name", 20)));
        Assert.Equal(["chrome.exe", "chrome.exe", "System", "notepad.exe"], Names(ProcessService.Format(Rows, null, "cpu", 20)));
        Assert.Equal(["chrome.exe", "chrome.exe", "notepad.exe", "System"], Names(ProcessService.Format(Rows, null, "bogus", 20)));
    }

    [Fact]
    public void FuzzyNameFilterAndLimitSlicing()
    {
        Assert.Equal(["chrome.exe", "chrome.exe"], Names(ProcessService.Format(Rows, "Chrome", "memory", 20)));
        Assert.Equal(["chrome.exe"], Names(ProcessService.Format(Rows, null, "memory", 1)));
        Assert.Equal(["chrome.exe", "chrome.exe", "notepad.exe"], Names(ProcessService.Format(Rows, null, "memory", -1)));
        Assert.Equal("No processes found.", ProcessService.Format(Rows, null, "memory", 0));
        Assert.Equal("No processes found matching zzz.", ProcessService.Format(Rows, "zzz", "memory", 20));
    }

    [Fact]
    public async Task ListFindsThisProcessByItsImageName()
    {
        var name = ProcessSnapshot.Names()[Environment.ProcessId];
        Assert.EndsWith(".exe", name);
        var text = await _processes.ListAsync(name, "name", 500, CancellationToken.None);
        Assert.StartsWith("Processes (", text);
        Assert.Contains(text.Split('\n'), line => line.TrimStart().StartsWith($"{Environment.ProcessId}  {name}", StringComparison.Ordinal));
    }

    [Fact]
    public void PidsZeroAndFourAreNamedAsPsutilNamesThem()
    {
        var names = ProcessSnapshot.Names();
        Assert.Equal("System Idle Process", names[0]);
        Assert.Equal("System", names[4]);
    }

    [UnelevatedFact]
    public void TheSnapshotReadsCpuTimeOfAProcessThatCannotBeOpened()
    {
        using var system = Process.GetProcessById(4);
        Assert.Throws<Win32Exception>(() => system.TotalProcessorTime);
        Assert.True(ProcessSnapshot.Take().Single(p => p.Pid == 4).CpuTime > 0);
    }

    [Fact]
    public void CpuIsAShareOfOneCpuRoundedLikePsutil()
    {
        Assert.Equal(50.0, ProcessService.CpuPercent(0, 1_250_000, 0.25));
        Assert.Equal(1.2, ProcessService.CpuPercent(1_000, 32_250, 0.25));   // 1.25% rounds half to even
        Assert.Equal(200.0, ProcessService.CpuPercent(0, 5_000_000, 0.25));
        Assert.Equal(0.0, ProcessService.CpuPercent(10, 0, 0.25));
    }

    [Fact]
    public void KillByPid()
    {
        using var child = UniqueChild.Start();
        Assert.Equal($"Terminated: {child.Name} (PID {child.Process.Id})", _processes.Kill(null, child.Process.Id, force: false));
        Assert.True(child.Process.WaitForExit(5000));
        Assert.Equal(15, child.Process.ExitCode);   // psutil's TerminateProcess(handle, SIGTERM)
    }

    [Fact]
    public void ForceKillByName()
    {
        using var child = UniqueChild.Start();
        Assert.Equal($"Force killed: {child.Name} (PID {child.Process.Id})", _processes.Kill(child.Name.ToUpperInvariant(), null, force: true));
        Assert.True(child.Process.WaitForExit(5000));
    }

    [Fact]
    public void KillErrors()
    {
        Assert.Equal("Error: Provide either pid or name parameter for kill mode.", _processes.Kill(null, null, false));
        Assert.Equal("No process with PID 2147483644 found.", _processes.Kill(null, 2147483644, false));
        Assert.Equal("No process matching \"no-such-process-dmcp.exe\" found or access denied.", _processes.Kill("no-such-process-dmcp.exe", null, false));
        Assert.Equal("No process matching \"\" found or access denied.", _processes.Kill("", null, false));
        Assert.Equal("pid must be a positive integer (got -1)", Assert.Throws<ArgumentException>(() => _processes.Kill(null, -1, false)).Message);
    }

    private static string[] Names(string table) =>
        table.Split('\n').Skip(3).Select(line => line.Split("  ", StringSplitOptions.RemoveEmptyEntries)[1].Trim()).ToArray();

    /// <summary>A fact that is skipped when this process is elevated, since elevation can open protected processes.</summary>
    private sealed class UnelevatedFactAttribute : FactAttribute
    {
        public UnelevatedFactAttribute()
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) Skip = "Needs an unelevated process.";
        }
    }

    /// <summary>A copy of ping.exe under a unique name, so killing by name cannot touch anything else.</summary>
    private sealed class UniqueChild : IDisposable
    {
        private readonly string _directory;

        private UniqueChild(string directory, string name, Process process)
        {
            _directory = directory;
            Name = name;
            Process = process;
        }

        public string Name { get; }
        public Process Process { get; }

        public static UniqueChild Start()
        {
            var directory = Path.Combine(Path.GetTempPath(), "dmcp-proc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var name = $"dmcp-test-{Guid.NewGuid().ToString("N")[..8]}.exe";
            var exe = Path.Combine(directory, name);
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
            var process = Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
            return new UniqueChild(directory, name, process);
        }

        public void Dispose()
        {
            if (!Process.HasExited) Process.Kill();
            Process.WaitForExit(5000);
            Process.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
