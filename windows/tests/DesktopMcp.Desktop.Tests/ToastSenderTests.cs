using DesktopMcp.Desktop.Toasts;

namespace DesktopMcp.Desktop.Tests;

public sealed class ToastSenderTests : IDisposable
{
    // Releases the fake that blocks, so no test leaves a thread waiting.
    private readonly ManualResetEventSlim _release = new();

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }

    [Fact]
    public void TitleAndMessageAreXmlEscaped() =>
        Assert.Equal(
            "<toast><visual><binding template=\"ToastGeneric\"><text>a &lt;b&gt; &amp; &quot;c&quot; &apos;d&apos;</text><text>m</text></binding></visual></toast>",
            ToastSender.BuildXml("a <b> & \"c\" 'd'", "m"));

    [Fact]
    public void ShowPassesTheXmlAndAppIdToWinRt()
    {
        var shown = new List<(string Xml, string AppId)>();
        new ToastSender((xml, appId) => shown.Add((xml, appId))).Show("t", "m", "My.App");
        Assert.Equal((ToastSender.BuildXml("t", "m"), "My.App"), Assert.Single(shown));
    }

    [Fact]
    public void AWinRtErrorPropagates()
    {
        var failure = new InvalidOperationException("no notifier");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => new ToastSender((_, _) => throw failure).Show("t", "m", "a")));
    }

    [Fact]
    public void ABlockedWinRtCallTimesOut()
    {
        var sender = new ToastSender((_, _) => _release.Wait()) { Timeout = TimeSpan.FromMilliseconds(200) };
        var error = Assert.Throws<TimeoutException>(() => sender.Show("t", "m", "a"));
        Assert.Equal("The toast notifier did not respond within 0.2 s.", error.Message);
    }

    /// <summary>Side effect: shows a toast under Windows PowerShell's AUMID.</summary>
    [Fact]
    [Trait("Category", "Desktop")]
    public void ShowsAToastUnderAnotherAppsAumid() =>
        Assert.Null(Record.Exception(() =>
            new ToastSender().Show("desktop-mcp test", "ToastSenderTests: you can dismiss this.", ToastSender.WindowsPowerShellAppId)));
}
