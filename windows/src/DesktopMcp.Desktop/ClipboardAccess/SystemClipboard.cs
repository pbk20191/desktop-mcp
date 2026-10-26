using DesktopMcp.Desktop.Threading;

namespace DesktopMcp.Desktop.ClipboardAccess;

/// <summary>
/// spec §2 WinRT first: the WinRT Clipboard on a short-lived STA thread; when it throws or does not answer within
/// <see cref="WinRtTimeout"/>, the Win32 clipboard on the thread pool, bounded by <see cref="Win32Timeout"/>.
/// Touches neither the Windows App SDK nor the dispatcher thread. A call that times out is abandoned, not stopped:
/// its thread stays blocked until the call returns.
/// </summary>
public sealed class SystemClipboard : IClipboard
{
    private readonly Func<string?> _winRtGet;
    private readonly Action<string> _winRtSet;
    private readonly Func<string?> _win32Get;
    private readonly Action<string> _win32Set;

    public SystemClipboard()
        : this(WinRtClipboard.GetText, WinRtClipboard.SetText, Win32Clipboard.GetText, Win32Clipboard.SetText)
    {
    }

    /// <summary>Tests replace the two clipboard paths with fakes.</summary>
    internal SystemClipboard(Func<string?> winRtGet, Action<string> winRtSet, Func<string?> win32Get, Action<string> win32Set)
    {
        _winRtGet = winRtGet;
        _winRtSet = winRtSet;
        _win32Get = win32Get;
        _win32Set = win32Set;
    }

    /// <summary>How long a WinRT call may take before the Win32 fallback runs (NativeAOT can block it, see the clipboard probe).</summary>
    internal TimeSpan WinRtTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the Win32 fallback may take before the call fails.</summary>
    internal TimeSpan Win32Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The path the last call used, or None when both failed (diagnostics, probe and tests).</summary>
    public ClipboardPath LastPath { get; private set; }

    /// <summary>Why WinRT failed on the last fallback, if it did.</summary>
    public string? LastWinRtError { get; private set; }

    public Task<string?> GetTextAsync(CancellationToken cancellationToken) =>
        RunAsync(_winRtGet, _win32Get, cancellationToken);

    public Task SetTextAsync(string text, CancellationToken cancellationToken) =>
        RunAsync(() => { _winRtSet(text); return true; }, () => { _win32Set(text); return true; }, cancellationToken);

    private async Task<T> RunAsync<T>(Func<T> winRt, Func<T> win32, CancellationToken cancellationToken)
    {
        using (var winRtDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            winRtDeadline.CancelAfter(WinRtTimeout);
            try
            {
                var result = await StaThread.RunAsync(winRt, winRtDeadline.Token).ConfigureAwait(false);
                LastPath = ClipboardPath.WinRt;
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                RecordWinRtFailure(new TimeoutException($"The WinRT clipboard did not respond within {WinRtTimeout.TotalSeconds:0.###} s."));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                RecordWinRtFailure(e);
            }
        }
        try
        {
            var fallback = await Task.Run(win32, cancellationToken).WaitAsync(Win32Timeout, cancellationToken).ConfigureAwait(false);
            LastPath = ClipboardPath.Win32;
            return fallback;
        }
        catch (TimeoutException)
        {
            LastPath = ClipboardPath.None;
            throw new TimeoutException(
                $"The clipboard did not respond within {Win32Timeout.TotalSeconds:0.###} s (WinRT: {LastWinRtError}).");
        }
        catch
        {
            LastPath = ClipboardPath.None;
            throw;
        }
    }

    private void RecordWinRtFailure(Exception e) => LastWinRtError = $"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message}";
}
