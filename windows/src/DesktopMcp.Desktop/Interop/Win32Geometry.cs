using DesktopMcp.Core.Snapshots;
using Windows.Win32.Foundation;

namespace DesktopMcp.Desktop.Interop;

/// <summary>The one conversion from Win32 RECTs to <see cref="ScreenRect"/> (displays, UIA bounds).</summary>
internal static class Win32Geometry
{
    /// <summary>A RECT's right and bottom are exclusive, like <see cref="ScreenRect"/>'s, so the fields map one to one.</summary>
    internal static ScreenRect ToScreenRect(this RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);
}
