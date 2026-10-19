using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Interop;

/// <summary>
/// Hand-declared Win32 entry points used before CsWin32 is introduced (P2). Layouts assume a
/// 64-bit process (win-x64, win-arm64).
/// </summary>
internal static unsafe partial class Native
{
    // CoreMessaging: DispatcherQueue
    [StructLayout(LayoutKind.Sequential)]
    internal struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;     // DQTYPE_THREAD_DEDICATED = 1
        public int ApartmentType;  // DQTAT_COM_NONE = 0, DQTAT_COM_ASTA = 1, DQTAT_COM_STA = 2
    }

    [LibraryImport("CoreMessaging.dll")]
    internal static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    // user32: monitors
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    internal const uint MonitorDefaultToPrimary = 1;

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(Point point, uint flags);

    // combase: WinRT activation
    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WindowsCreateString(string sourceString, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    internal static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    internal static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);
}
