using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public class ClipboardToolTests
{
    private sealed class FakeClipboard(string? text) : IClipboard
    {
        public string? Text { get; private set; } = text;
        public Task<string?> GetTextAsync(CancellationToken cancellationToken) => Task.FromResult(Text);
        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private static Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(FakeClipboard clipboard, string json) =>
        ToolCalls.CallAsync(ToolCalls.Create("Clipboard", ToolServices.CreateDefault() with { Clipboard = clipboard }), json);

    [Fact]
    public async Task GetReturnsTheText() =>
        Assert.Equal("Clipboard content:\nhello", ToolCalls.OkText(await CallAsync(new FakeClipboard("hello"), """{"mode": "get"}""")));

    [Fact]
    public async Task GetWithoutText() =>
        Assert.Equal("Clipboard is empty or contains non-text data.", ToolCalls.OkText(await CallAsync(new FakeClipboard(null), """{"mode": "get"}""")));

    [Fact]
    public async Task SetEchoesUpToAHundredCodePoints()
    {
        var clipboard = new FakeClipboard(null);
        var text = new string('a', 99) + "\U0001F437" + "tail";
        Assert.Equal($"Clipboard set to: {new string('a', 99)}\U0001F437...", ToolCalls.OkText(await CallAsync(clipboard, $$"""{"mode": "set", "text": "{{text}}"}""")));
        Assert.Equal(text, clipboard.Text);
        Assert.Equal("Clipboard set to: short", ToolCalls.OkText(await CallAsync(clipboard, """{"mode": "set", "text": "short"}""")));
    }

    [Theory]
    [InlineData("""{"mode": "set"}""", "Error: text parameter required for set mode.")]
    [InlineData("""{"mode": "cut"}""", "Error: mode must be either \"get\" or \"set\".")]
    public async Task UpstreamMessages(string json, string expected) =>
        Assert.Equal(expected, ToolCalls.OkText(await CallAsync(new FakeClipboard("x"), json)));
}
