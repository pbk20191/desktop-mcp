using DesktopMcp.Desktop.Snapshots;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RemoteDesktop;

namespace DesktopMcp.Desktop.Control;

/// <summary>
/// Whether the user can see and drive this session now (<c>control_win32.py:221-253</c>): the session's connect state is
/// <c>WTSActive</c> (not disconnected, not session 0) and the input desktop is <c>Default</c> (not the lock screen or a UAC
/// prompt; the check <see cref="DesktopSession"/> shares, P4 preflight F12). The coordinator's health check calls it.
/// </summary>
internal static unsafe class InteractiveDesktop
{
    /// <summary>True only when the session is active and the input desktop is the user's. Never throws.</summary>
    public static bool IsAvailable() => IsAvailable(SessionIsActive, DesktopSession.IsInputDesktopDefault);

    /// <summary>The rule over its two checks; a check that throws counts as unavailable.</summary>
    internal static bool IsAvailable(Func<bool> sessionActive, Func<bool> inputDesktopDefault)
    {
        try
        {
            return sessionActive() && inputDesktopDefault();
        }
        catch (Exception)
        {
            return false;
        }
    }

    // WTS_CURRENT_SESSION, WTSConnectState: the buffer holds a WTS_CONNECTSTATE_CLASS (an int); WTSActive is 0.
    private static bool SessionIsActive()
    {
        PWSTR buffer;
        uint length;
        if (!PInvoke.WTSQuerySessionInformation(HANDLE.WTS_CURRENT_SERVER_HANDLE, PInvoke.WTS_CURRENT_SESSION, WTS_INFO_CLASS.WTSConnectState, &buffer, &length))
            return false;
        try
        {
            return buffer.Value is not null && length >= sizeof(int)
                && *(int*)buffer.Value == (int)WTS_CONNECTSTATE_CLASS.WTSActive;
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer.Value);
        }
    }
}
