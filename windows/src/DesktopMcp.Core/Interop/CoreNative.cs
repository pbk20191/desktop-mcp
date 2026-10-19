using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopMcp.Core.Interop;

/// <summary>Hand-declared Win32 entry points for Core. Layouts assume a 64-bit process (win-x64, win-arm64).</summary>
internal static unsafe partial class CoreNative
{
    // kernel32: console control events (the PowerShell graceful stop)
    internal const uint CtrlBreakEvent = 1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    // kernel32: Job Objects (spec §5)
    internal const uint JobObjectLimitKillOnJobClose = 0x2000;
    internal const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    internal static partial nint CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(nint job, int infoClass, JobObjectExtendedLimitInformation* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    // kernel32: final path names (FileSystem path resolution, like Python's os.path.realpath)
    internal const uint FileShareAll = 0x7;                      // read | write | delete
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;    // required to open a directory

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint bufferLength, uint flags);

    // ntdll: the system process snapshot psutil falls back to when it cannot open a process
    internal const int SystemProcessInformationClass = 5;
    internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        public ushort Length;           // bytes, without a terminator
        public ushort MaximumLength;
        public char* Buffer;
    }

    /// <summary>The leading fields of SYSTEM_PROCESS_INFORMATION, up to WorkingSetSize.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;         // FILETIME ticks
        public long UserTime;           // 100 ns units
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public nuint UniqueProcessKey;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
    }

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    // kernel32: opening and terminating a process (psutil's terminate/kill)
    internal const uint ProcessTerminate = 0x0001;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint StillActive = 259;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(nint process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(nint process, uint exitCode);

    // kernel32: the system code pages behind Python's mbcs and oem codecs
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetACP();

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetOEMCP();
}
