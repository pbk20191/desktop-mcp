using DesktopMcp.Desktop.Threading;

namespace DesktopMcp.Desktop.ClipboardAccess;

/// <summary>
/// spec §2 WinRT first: the WinRT Clipboard on a short-lived STA thread; when it throws, the Win32 clipboard on the
/// thread pool. Touches neither the Windows App SDK nor the dispatcher thread.
/// </summary>
public sealed class SystemClipboard : IClipboard
{
    /// <summary>The path the last call used (diagnostics, probe and tests).</summary>
    public ClipboardPath LastPath { get; private set; }

    /// <summary>Why WinRT failed on the last fallback, if it did.</summary>
    public string? LastWinRtError { get; private set; }

    public async Task<string?> GetTextAsync(CancellationToken cancellationToken)
    {
        try
        {
            var text = await StaThread.RunAsync(WinRtClipboard.GetText, cancellationToken).ConfigureAwait(false);
            LastPath = ClipboardPath.WinRt;
            return text;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            RecordWinRtFailure(e);
        }
        var fallback = await Task.Run(Win32Clipboard.GetText, cancellationToken).ConfigureAwait(false);
        LastPath = ClipboardPath.Win32;
        return fallback;
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await StaThread.RunAsync(() => WinRtClipboard.SetText(text), cancellationToken).ConfigureAwait(false);
            LastPath = ClipboardPath.WinRt;
            return;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            RecordWinRtFailure(e);
        }
        await Task.Run(() => Win32Clipboard.SetText(text), cancellationToken).ConfigureAwait(false);
        LastPath = ClipboardPath.Win32;
    }

    private void RecordWinRtFailure(Exception e) => LastWinRtError = $"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message}";
}
