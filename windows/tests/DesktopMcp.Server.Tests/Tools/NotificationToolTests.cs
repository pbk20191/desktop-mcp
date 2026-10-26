using DesktopMcp.Desktop.Toasts;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public class NotificationToolTests
{
    private sealed class FakeToasts(Exception? failure = null, ManualResetEventSlim? block = null) : IToastSender
    {
        public List<(string Title, string Message, string AppId)> Shown { get; } = [];

        public void Show(string title, string message, string appId)
        {
            block?.Wait();
            if (failure is not null) throw failure;
            Shown.Add((title, message, appId));
        }
    }

    private static Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(FakeToasts toasts, string json, CancellationToken cancellationToken = default) =>
        ToolCalls.CallAsync(ToolCalls.Create("Notification", ToolServices.CreateDefault() with { Toasts = toasts }), json, cancellationToken);

    [Fact]
    public async Task SendsAndReportsTheToast()
    {
        var toasts = new FakeToasts();
        Assert.Equal("Notification sent: \"Hi\" - Body", ToolCalls.OkText(await CallAsync(toasts, """{"title": "Hi", "message": "Body", "app_id": "My.App"}""")));
        Assert.Equal(("Hi", "Body", "My.App"), Assert.Single(toasts.Shown));
    }

    [Fact]
    public async Task AFailureKeepsTheFirstTwoHundredCharacters() =>
        Assert.Equal("Notification may have been sent. Error: " + new string('x', 200),
            ToolCalls.OkText(await CallAsync(new FakeToasts(new InvalidOperationException(new string('x', 300))), """{"title": "t", "message": "m", "app_id": "a"}""")));

    [Fact]
    public async Task ATimeoutIsAFailureToo() =>
        Assert.Equal("Notification may have been sent. Error: The toast notifier did not respond within 5 s.",
            ToolCalls.OkText(await CallAsync(new FakeToasts(new TimeoutException("The toast notifier did not respond within 5 s.")), """{"title": "t", "message": "m", "app_id": "a"}""")));

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var block = new ManualResetEventSlim();
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CallAsync(new FakeToasts(block: block), """{"title": "t", "message": "m", "app_id": "a"}""", cancellation.Token));
        }
        finally { block.Set(); }
    }

    [Fact]
    public async Task AppIdIsRequired()
    {
        var result = await CallAsync(new FakeToasts(), """{"title": "t", "message": "m"}""");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Notification': Missing required argument: app_id", ToolCalls.Text(result));
    }
}
