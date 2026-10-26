using Windows.Win32;
using Windows.Win32.System.Ole;

namespace DesktopMcp.Desktop.ClipboardAccess;

/// <summary>What the clipboard holds, without opening it (Decisions 21).</summary>
public static class ClipboardFormats
{
    /// <summary>
    /// True when the clipboard holds data but no Unicode text (an image, files): Type's paste path would replace it with
    /// no way to restore it, so it types per key instead.
    /// </summary>
    public static bool HoldsOnlyNonText() =>
        PInvoke.CountClipboardFormats() > 0 && !PInvoke.IsClipboardFormatAvailable((uint)CLIPBOARD_FORMAT.CF_UNICODETEXT);
}
