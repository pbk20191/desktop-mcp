using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;

namespace DesktopMcp.Desktop.Snapshots;

/// <summary>The cursor position and whether a desktop the user sees is available (spec §5).</summary>
internal static unsafe class DesktopSession
{
    /// <summary>The input desktop is not the user's: the lock screen or a secure desktop (Decisions 18).</summary>
    internal const string DesktopUnavailable =
        "The desktop is not available: the screen is locked or a secure desktop (such as a UAC prompt) is showing.";

    /// <summary>The server runs in session 0 (Decisions 18).</summary>
    internal const string NoInteractiveSession =
        "No interactive desktop: the server runs in session 0 (as a service). Run it in a signed-in user's session to use the desktop tools.";

    // A desktop name is at most a few dozen characters; "Default", "Winlogon" and "Screen-saver" are the ones that matter.
    private const int NameChars = 64;

    /// <summary>upstream uia/core.py:176-184 GetCursorPos.</summary>
    public static ScreenPoint CursorPosition()
    {
        PInvoke.GetCursorPos(out var point);
        return new ScreenPoint(point.X, point.Y);
    }

    /// <summary>
    /// Throws a <see cref="DesktopException"/> when there is no interactive session (session 0, a service) or the input
    /// desktop is not the user's "Default" desktop (the lock screen or a UAC prompt) (Decisions 18).
    /// </summary>
    public static void ThrowIfUnavailable()
    {
        if (!PInvoke.ProcessIdToSessionId((uint)Environment.ProcessId, out var session) || session == 0)
            throw new DesktopException(NoInteractiveSession);
        if (!IsInputDesktopDefault()) throw new DesktopException(DesktopUnavailable);
    }

    /// <summary>
    /// Whether the input desktop is the user's "Default" desktop: false on the lock screen or a secure desktop (a UAC
    /// prompt), or when the input desktop cannot be opened. A desktop whose name cannot be read counts as the user's unless
    /// <paramref name="strict"/> (the control's check, <c>control_win32.py:239-252</c>, which fails safe). Never throws.
    /// Shared by <see cref="ThrowIfUnavailable"/> and the control's interactive-desktop check (P4 preflight F12).
    /// </summary>
    public static bool IsInputDesktopDefault(bool strict = false)
    {
        try
        {
            var desktop = PInvoke.OpenInputDesktop(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);
            if (desktop.IsNull) return false;
            try
            {
                var name = stackalloc char[NameChars];
                uint needed;
                var read = PInvoke.GetUserObjectInformation((HANDLE)(nint)desktop.Value, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, name, NameChars * sizeof(char), &needed);
                return IsDefaultName(read ? NameOf(name, needed) : null, strict);
            }
            finally
            {
                PInvoke.CloseDesktop(desktop);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The rule over the input desktop's name; null when it could not be read.</summary>
    internal static bool IsDefaultName(string? name, bool strict) =>
        name is null ? !strict : name.Equals("Default", StringComparison.OrdinalIgnoreCase);

    // needed is the name's size in bytes, its terminator included; never read past the buffer or the terminator.
    private static string NameOf(char* name, uint needed)
    {
        var length = (int)Math.Min(needed / sizeof(char), (uint)NameChars);
        var span = new ReadOnlySpan<char>(name, length);
        var end = span.IndexOf('\0');
        return (end < 0 ? span : span[..end]).ToString();
    }
}
