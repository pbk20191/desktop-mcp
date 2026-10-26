using Windows.ApplicationModel.DataTransfer;
using WinRtSystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace DesktopMcp.Desktop.ClipboardAccess;

/// <summary>
/// WinRT clipboard calls; run them on an STA thread. The API is in the OS SDK projection, not the Windows App SDK,
/// so it needs no <see cref="AppRuntime"/> initialisation.
/// </summary>
internal static class WinRtClipboard
{
    public static string? GetText()
    {
        var view = WinRtSystemClipboard.GetContent();
        return view.Contains(StandardDataFormats.Text) ? view.GetTextAsync().AsTask().GetAwaiter().GetResult() : null;
    }

    public static void SetText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        WinRtSystemClipboard.SetContent(package);
        // Render the data now, so the text stays on the clipboard after this process exits.
        WinRtSystemClipboard.Flush();
    }
}
