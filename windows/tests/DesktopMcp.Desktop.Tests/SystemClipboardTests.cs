using DesktopMcp.Desktop.ClipboardAccess;

namespace DesktopMcp.Desktop.Tests;

/// <summary>SystemClipboard's path selection with fake WinRT and Win32 delegates: no real clipboard, so it always runs.</summary>
public sealed class SystemClipboardTests : IDisposable
{
    // Releases the fakes that block, so no test leaves a thread waiting.
    private readonly ManualResetEventSlim _release = new();
    private int _win32Calls;

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }

    private string? Block()
    {
        _release.Wait();
        return "late";
    }

    private SystemClipboard Create(Func<string?> winRtGet, Func<string?>? win32Get = null) =>
        new(winRtGet, _ => winRtGet(), () =>
        {
            Interlocked.Increment(ref _win32Calls);
            return (win32Get ?? (() => "from win32"))();
        }, _ => Interlocked.Increment(ref _win32Calls))
        {
            WinRtTimeout = TimeSpan.FromMilliseconds(200),
            Win32Timeout = TimeSpan.FromMilliseconds(200),
        };

    [Fact]
    public async Task UsesWinRtWhenItAnswers()
    {
        var clipboard = Create(() => "from winrt");
        Assert.Equal("from winrt", await clipboard.GetTextAsync(CancellationToken.None));
        Assert.Equal(ClipboardPath.WinRt, clipboard.LastPath);
        Assert.Null(clipboard.LastWinRtError);
        Assert.Equal(0, _win32Calls);
    }

    [Fact]
    public async Task FallsBackToWin32WhenWinRtThrows()
    {
        var clipboard = Create(() => throw new InvalidOperationException("no clipboard"));
        Assert.Equal("from win32", await clipboard.GetTextAsync(CancellationToken.None));
        Assert.Equal(ClipboardPath.Win32, clipboard.LastPath);
        Assert.Equal($"InvalidOperationException 0x{new InvalidOperationException().HResult:X8}: no clipboard", clipboard.LastWinRtError);
    }

    [Fact]
    public async Task FallsBackToWin32WhenWinRtBlocks()
    {
        var clipboard = Create(Block);
        await clipboard.SetTextAsync("text", CancellationToken.None);
        Assert.Equal(ClipboardPath.Win32, clipboard.LastPath);
        Assert.Equal(1, _win32Calls);
        Assert.StartsWith("TimeoutException", clipboard.LastWinRtError);
    }

    [Fact]
    public async Task CallerCancellationDoesNotFallBack()
    {
        var clipboard = Create(Block);
        clipboard.WinRtTimeout = TimeSpan.FromSeconds(30);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clipboard.GetTextAsync(cancellation.Token));
        Assert.Equal(0, _win32Calls);
        Assert.Null(clipboard.LastWinRtError);
    }

    [Fact]
    public async Task Win32BlockingIsABoundedError()
    {
        var clipboard = Create(() => throw new InvalidOperationException("no clipboard"), Block);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => clipboard.GetTextAsync(CancellationToken.None));
        Assert.Contains("did not respond", error.Message);
        Assert.Equal(ClipboardPath.None, clipboard.LastPath);
    }
}
