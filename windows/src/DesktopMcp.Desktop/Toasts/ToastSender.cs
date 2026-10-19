using System.Security;
using Windows.UI.Notifications;
using XmlDocument = Windows.Data.Xml.Dom.XmlDocument;

namespace DesktopMcp.Desktop.Toasts;

/// <summary>
/// spec §8: CsWinRT ToastNotificationManager. app_id takes any AUMID, which AppNotificationManager cannot do. Never reads
/// notifier.Setting, which throws for unpackaged callers. Needs no Windows App SDK. The WinRT call runs on the thread
/// pool, bounded by <see cref="Timeout"/> (NativeAOT can block a WinRT call instead of failing it); a call that times
/// out is abandoned, not stopped.
/// </summary>
public sealed class ToastSender : IToastSender
{
    /// <summary>Windows PowerShell's AUMID, registered on every Windows install (probe and tests).</summary>
    public const string WindowsPowerShellAppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    private readonly Action<string, string> _show;

    public ToastSender()
        : this(ShowWithWinRt)
    {
    }

    /// <summary>Tests replace the WinRT call (toast XML, AUMID) with a fake.</summary>
    internal ToastSender(Action<string, string> show) => _show = show;

    /// <summary>How long the WinRT call may take before <see cref="Show"/> throws <see cref="TimeoutException"/>.</summary>
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public void Show(string title, string message, string appId)
    {
        var xml = BuildXml(title, message);
        try
        {
            Task.Run(() => _show(xml, appId)).WaitAsync(Timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The toast notifier did not respond within {Timeout.TotalSeconds:0.###} s.");
        }
    }

    private static void ShowWithWinRt(string xml, string appId)
    {
        var document = new XmlDocument();
        document.LoadXml(xml);
        ToastNotificationManager.CreateToastNotifier(appId).Show(new ToastNotification(document));
    }

    // upstream's template (notifications/service.py:30-44) with ps_quote_for_xml's escaping.
    internal static string BuildXml(string title, string message) =>
        "<toast><visual><binding template=\"ToastGeneric\">" +
        $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(message)}</text>" +
        "</binding></visual></toast>";
}
