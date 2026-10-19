using DesktopMcp.Desktop.ClipboardAccess;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// Side effect: changes the user's clipboard text and restores the previous text in <c>finally</c> (non-text content
/// is lost). Category=Desktop, so the default run skips it.
/// </summary>
[Trait("Category", "Desktop")]
public sealed class ClipboardTests
{
    private readonly SystemClipboard _clipboard = new();

    [Fact]
    public Task SystemClipboardRoundTripsThroughWinRt() => PreservingClipboardAsync(async () =>
    {
        var text = "desktop-mcp " + Guid.NewGuid();
        await _clipboard.SetTextAsync(text, CancellationToken.None);
        Assert.Equal(text, await _clipboard.GetTextAsync(CancellationToken.None));
        Assert.True(_clipboard.LastPath == ClipboardPath.WinRt, $"fell back to {_clipboard.LastPath}: {_clipboard.LastWinRtError}");
    });

    [Fact]
    public Task Win32FallbackRoundTripsUnicode() => PreservingClipboardAsync(async () =>
    {
        var text = "Win32 ✓ \U0001F437 " + Guid.NewGuid();
        await Task.Run(() => Win32Clipboard.SetText(text));
        Assert.Equal(text, await Task.Run(Win32Clipboard.GetText));
    });

    [Fact]
    public Task WinRtSeesWhatWin32Wrote() => PreservingClipboardAsync(async () =>
    {
        var text = "cross " + Guid.NewGuid();
        await Task.Run(() => Win32Clipboard.SetText(text));
        Assert.Equal(text, await _clipboard.GetTextAsync(CancellationToken.None));
    });

    private static async Task PreservingClipboardAsync(Func<Task> test)
    {
        var clipboard = new SystemClipboard();
        var saved = await clipboard.GetTextAsync(CancellationToken.None);
        try
        {
            await test();
        }
        finally
        {
            if (saved is not null) await clipboard.SetTextAsync(saved, CancellationToken.None);
        }
    }
}
