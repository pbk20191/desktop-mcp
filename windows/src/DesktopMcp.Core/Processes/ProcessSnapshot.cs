using System.Runtime.InteropServices;
using Windows.Wdk.System.SystemInformation;
using Windows.Win32.Foundation;
using Windows.Win32.System.WindowsProgramming;

namespace DesktopMcp.Core.Processes;

/// <summary>
/// One process in a snapshot. <paramref name="Name"/> is psutil's name() ("chrome.exe", "System", "System Idle Process")
/// and may be empty; <paramref name="CreateTime"/> is in FILETIME ticks, <paramref name="CpuTime"/> (kernel + user) in
/// 100 ns units, and <paramref name="WorkingSet"/> (psutil's rss) in bytes.
/// </summary>
internal sealed record ProcessEntry(int Pid, string Name, long CreateTime, long CpuTime, long WorkingSet);

/// <summary>
/// Every process from one NtQuerySystemInformation(SystemProcessInformation) call. It needs no per-process handle,
/// so unlike Process.TotalProcessorTime it reads protected and other users' processes, as psutil's fallback does.
/// </summary>
internal static unsafe class ProcessSnapshot
{
    private const uint InitialBufferSize = 1 << 20;
    private const uint BufferSlack = 64 * 1024;   // room for processes started between the size query and the read

    public static Dictionary<int, string> Names() => Take().ToDictionary(p => p.Pid, p => p.Name);

    /// <summary>The processes in PID order; throws when the system call fails.</summary>
    public static List<ProcessEntry> Take()
    {
        var size = InitialBufferSize;
        while (true)
        {
            var buffer = (byte*)NativeMemory.Alloc(size);
            try
            {
                uint needed = 0;
                var status = Windows.Wdk.PInvoke.NtQuerySystemInformation(SYSTEM_INFORMATION_CLASS.SystemProcessInformation, buffer, size, ref needed);
                if (status == NTSTATUS.STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(size, needed) + BufferSlack;
                    continue;
                }
                if (status.Value < 0) throw new InvalidOperationException($"NtQuerySystemInformation failed with NTSTATUS 0x{status.Value:X8}.");
                return Parse(buffer);
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
    }

    private static List<ProcessEntry> Parse(byte* buffer)
    {
        var processes = new List<ProcessEntry>();
        for (var entry = (SYSTEM_PROCESS_INFORMATION*)buffer; ; )
        {
            var pid = (int)(nint)entry->UniqueProcessId;
            // The kernel leaves PID 0's image name empty; psutil calls it "System Idle Process".
            var name = pid == 0 ? "System Idle Process"
                : entry->ImageName.Buffer.Value == null ? ""
                : new string(entry->ImageName.Buffer.Value, 0, entry->ImageName.Length / sizeof(char));
            var times = (ProcessTimes*)&entry->Reserved1;
            processes.Add(new ProcessEntry(pid, name, times->CreateTime, times->KernelTime + times->UserTime, (long)entry->WorkingSetSize));
            if (entry->NextEntryOffset == 0) break;
            entry = (SYSTEM_PROCESS_INFORMATION*)((byte*)entry + entry->NextEntryOffset);
        }
        processes.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        return processes;
    }

    /// <summary>
    /// The undocumented fields that winternl.h, and so the Win32 metadata's SYSTEM_PROCESS_INFORMATION, hides in its
    /// 48-byte Reserved1 (the WDK metadata has no fuller struct): WorkingSetPrivateSize, HardFaultCount,
    /// NumberOfThreadsHighWatermark and CycleTime, then the three times psutil reads.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct ProcessTimes
    {
        [FieldOffset(24)] public long CreateTime;   // FILETIME ticks
        [FieldOffset(32)] public long UserTime;     // 100 ns units
        [FieldOffset(40)] public long KernelTime;
    }
}
