using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Interop;

/// <summary>
/// Hand-declared Win32 entry points used before CsWin32 is introduced (P2). Layouts assume a
/// 64-bit process (win-x64, win-arm64).
/// </summary>
internal static unsafe partial class Native
{
    // kernelbase: OS Dynamic Dependencies (appmodel.h, Windows 11 22H2+)
    [Flags]
    internal enum PackageDependencyProcessorArchitectures
    {
        None = 0,
        Neutral = 0x1,
        X86 = 0x2,
        X64 = 0x4,
        Arm = 0x8,
        Arm64 = 0x10,
        X86OnArm64 = 0x20,
    }

    internal const int PackageDependencyLifetimeKindProcess = 0;
    internal const int CreatePackageDependencyOptionsNone = 0;
    internal const int AddPackageDependencyOptionsNone = 0;
    internal const int PackageDependencyRankDefault = 0;

    [LibraryImport("kernelbase.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int TryCreatePackageDependency(
        nint user,
        string packageFamilyName,
        ulong minVersion,
        PackageDependencyProcessorArchitectures packageDependencyProcessorArchitectures,
        int lifetimeKind,
        string? lifetimeArtifact,
        int options,
        out nint packageDependencyId);

    [LibraryImport("kernelbase.dll")]
    internal static partial int AddPackageDependency(
        nint packageDependencyId,
        int rank,
        int options,
        out nint packageDependencyContext,
        out nint packageFullName);

    // kernel32: frees the strings the Dynamic Dependencies API allocates on the process heap
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetProcessHeap();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HeapFree(nint heap, uint flags, nint memory);

    // user32: monitors
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    internal const uint MonitorDefaultToPrimary = 1;

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(Point point, uint flags);
}
