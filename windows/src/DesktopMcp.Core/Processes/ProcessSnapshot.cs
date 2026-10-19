using DesktopMcp.Core.Interop;

namespace DesktopMcp.Core.Processes;

/// <summary>Image names by PID, as psutil's name() reports them ("chrome.exe", "System", "System Idle Process").</summary>
internal static unsafe class ProcessSnapshot
{
    public static Dictionary<int, string> Names()
    {
        var names = new Dictionary<int, string>();
        var snapshot = CoreNative.CreateToolhelp32Snapshot(CoreNative.Th32csSnapProcess, 0);
        if (snapshot == CoreNative.InvalidHandleValue) return names;
        try
        {
            var entry = new CoreNative.ProcessEntry32W { Size = (uint)sizeof(CoreNative.ProcessEntry32W) };
            for (var ok = CoreNative.Process32First(snapshot, &entry); ok; ok = CoreNative.Process32Next(snapshot, &entry))
            {
                var name = new string(entry.ExeFile);
                // Toolhelp calls PID 0 "[System Process]"; psutil calls it "System Idle Process".
                names[(int)entry.ProcessId] = entry.ProcessId == 0 ? "System Idle Process" : name.Length > 0 ? name : "Unknown";
            }
        }
        finally
        {
            CoreNative.CloseHandle(snapshot);
        }
        return names;
    }
}
