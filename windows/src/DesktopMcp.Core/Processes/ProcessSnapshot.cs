using System.Runtime.InteropServices;
using DesktopMcp.Core.Interop;

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
                var status = CoreNative.NtQuerySystemInformation(CoreNative.SystemProcessInformationClass, buffer, size, out var needed);
                if (status == CoreNative.StatusInfoLengthMismatch)
                {
                    size = Math.Max(size, needed) + BufferSlack;
                    continue;
                }
                if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation failed with NTSTATUS 0x{status:X8}.");
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
        for (var entry = (CoreNative.SystemProcessInformation*)buffer; ; )
        {
            var pid = (int)entry->UniqueProcessId;
            // The kernel leaves PID 0's image name empty; psutil calls it "System Idle Process".
            var name = pid == 0 ? "System Idle Process"
                : entry->ImageName.Buffer == null ? ""
                : new string(entry->ImageName.Buffer, 0, entry->ImageName.Length / sizeof(char));
            processes.Add(new ProcessEntry(pid, name, entry->CreateTime, entry->KernelTime + entry->UserTime, (long)entry->WorkingSetSize));
            if (entry->NextEntryOffset == 0) break;
            entry = (CoreNative.SystemProcessInformation*)((byte*)entry + entry->NextEntryOffset);
        }
        processes.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        return processes;
    }
}
