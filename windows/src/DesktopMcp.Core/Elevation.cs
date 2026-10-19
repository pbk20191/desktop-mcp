using System.Security.Principal;

namespace DesktopMcp.Core;

/// <summary>upstream is_elevated (desktop/utils.py:47-54, IsUserAnAdmin): true only for an elevated administrator token.</summary>
public static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
