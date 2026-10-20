using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace DesktopMcp.Desktop.Input;

/// <summary>Integrity levels of this process and of a target process (the UIPI comparison).</summary>
internal static unsafe class ProcessIntegrity
{
    private static readonly Lazy<bool> Elevated = new(ReadCurrentElevated);

    public static bool CurrentIsElevated => Elevated.Value;

    /// <summary>The integrity RID (0x2000 medium, 0x3000 high), or null when the token cannot be read.</summary>
    public static int? IntegrityRid(int processId)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsNull) return null;
        try
        {
            return ReadIntegrity(process);
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    /// <summary>True only when both levels are known and the target's is higher than this process's.</summary>
    public static bool IsHigherThanCurrent(int processId)
    {
        var target = IntegrityRid(processId);
        var current = ReadIntegrity(PInvoke.GetCurrentProcess());
        return target is { } t && current is { } c && t > c;
    }

    private static int? ReadIntegrity(HANDLE process)
    {
        if (!PInvoke.OpenProcessToken(new SafeFileHandle(process, false), TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token)) return null;
        try
        {
            uint needed = 0;
            PInvoke.GetTokenInformation((HANDLE)token.DangerousGetHandle(), TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, null, 0, &needed);
            if (needed == 0 || needed > 4096) return null;
            var buffer = stackalloc byte[(int)needed];
            if (!PInvoke.GetTokenInformation((HANDLE)token.DangerousGetHandle(), TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, needed, &needed))
                return null;
            var sid = ((TOKEN_MANDATORY_LABEL*)buffer)->Label.Sid;
            var count = *PInvoke.GetSidSubAuthorityCount(sid);
            if (count == 0) return null;
            return (int)*PInvoke.GetSidSubAuthority(sid, (uint)(count - 1));
        }
        finally
        {
            token.Dispose();
        }
    }

    private static bool ReadCurrentElevated()
    {
        if (!PInvoke.OpenProcessToken(new SafeFileHandle(PInvoke.GetCurrentProcess(), false), TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token)) return false;
        try
        {
            TOKEN_ELEVATION elevation = default;
            uint size = 0;
            return PInvoke.GetTokenInformation((HANDLE)token.DangerousGetHandle(), TOKEN_INFORMATION_CLASS.TokenElevation, &elevation,
                (uint)sizeof(TOKEN_ELEVATION), &size) && elevation.TokenIsElevated != 0;
        }
        finally
        {
            token.Dispose();
        }
    }
}
