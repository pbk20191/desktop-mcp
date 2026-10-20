using System.Globalization;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Desktop.Actions;

/// <summary>Reads the focused element's value after Type (P3 Decisions 4).</summary>
public interface ITypeReadBack
{
    /// <summary>
    /// The focused element's ValuePattern value, or null when the read is skipped (a password field, no ValuePattern) or
    /// fails (an error, the deadline, a window that does not respond or is blocked).
    /// </summary>
    Task<string?> ReadFocusedValueAsync(CancellationToken ct);
}

/// <summary>The read-back check and its warning text (P3 Decisions 4).</summary>
public static class TypeReadBack
{
    /// <summary>How many times the value is read before a mismatch is reported.</summary>
    public const int Reads = 3;

    /// <summary>A longer value is cut to this many code points (plus <c>...</c>) in the warning.</summary>
    public const int ShownCodePoints = 200;

    /// <summary>The pause between reads.</summary>
    public static readonly TimeSpan ReadInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Null when <paramref name="value"/> is null (skipped or unreadable) or contains <paramref name="typed"/> (CRLF reads as
    /// LF on both sides, a lone CR as LF in the value and not at all in the typed text); otherwise the warning line.
    /// <paramref name="at"/> names the item's point (MultiEdit).
    /// </summary>
    public static string? Warning(string typed, string? value, ScreenPoint? at = null)
    {
        ArgumentNullException.ThrowIfNull(typed);
        if (value is null || Newlines(value).Contains(TypedNewlines(typed), StringComparison.Ordinal)) return null;
        var shown = PyText.CodePointLength(value) > ShownCodePoints ? PyText.Prefix(value, ShownCodePoints) + "..." : value;
        var subject = at is { } point
            ? string.Create(CultureInfo.InvariantCulture, $"the focused element's value at ({point.X},{point.Y})")
            : "the focused element's value";
        return $"Warning: {subject} does not contain the typed text; it reads {PyText.Repr(shown)}.";
    }

    /// <summary>Reads up to <see cref="Reads"/> times, <see cref="ReadInterval"/> apart, until the value contains the text.</summary>
    public static Task<string?> CheckAsync(ITypeReadBack reader, string typed, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct) =>
        CheckAsync(reader, typed, null, delay, ct);

    /// <summary>
    /// Reads up to <see cref="Reads"/> times, <see cref="ReadInterval"/> apart, until the value contains the text; returns
    /// the last mismatch's warning, or null on a match or when a read is skipped or fails.
    /// </summary>
    public static async Task<string?> CheckAsync(ITypeReadBack reader, string typed, ScreenPoint? at,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(delay);
        for (var read = 1; ; read++)
        {
            ct.ThrowIfCancellationRequested();
            var warning = Warning(typed, await reader.ReadFocusedValueAsync(ct).ConfigureAwait(false), at);
            if (warning is null || read >= Reads) return warning;
            await delay(ReadInterval, ct).ConfigureAwait(false);
        }
    }

    private static string Newlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    // The per-key path skips a lone CR, so it is not in the value; CRLF still reads as LF.
    private static string TypedNewlines(string typed) =>
        typed.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
}

/// <summary>
/// The read-back on the UIA worker: one job with a 2 s deadline per read. The foreground window must not be on the hang
/// block list and must answer the WM_NULL check (P2 Decisions 36-38) before the job's
/// <c>GetFocusedElementBuildCache(ReadBackRequest)</c>; a window whose read passes the deadline is blocked like a label's
/// when it also fails the WM_NULL check.
/// Only ValuePattern's cached value is read, never TextPattern.
/// </summary>
public sealed class UiaTypeReadBack : ITypeReadBack
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    private readonly IWindowSystem _windows;
    private readonly HangBlockList _blocks;
    private readonly Func<long, CancellationToken, Task<string?>> _read;

    public UiaTypeReadBack(DesktopMachine machine)
        : this((machine ?? throw new ArgumentNullException(nameof(machine))).Windows, machine.Blocks, Reader(machine.Uia))
    {
    }

    /// <param name="read">Reads the focused element's value for the foreground window it is given (the UIA job).</param>
    internal UiaTypeReadBack(IWindowSystem windows, HangBlockList blocks, Func<long, CancellationToken, Task<string?>> read)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(read);
        _windows = windows;
        _blocks = blocks;
        _read = read;
    }

    public async Task<string?> ReadFocusedValueAsync(CancellationToken ct)
    {
        var window = _windows.GetForegroundWindow();
        if (window == 0) return null;
        var processId = _windows.GetProcessId(window);
        if (_blocks.IsBlocked(window, processId)) return null;
        try
        {
            return await _read(window, ct).ConfigureAwait(false);
        }
        catch (UiaTimeoutException)
        {
            // The job passed its deadline and its thread was abandoned. The application is skipped for a while (P2
            // Decisions 2) only when it also fails the WM_NULL check or Windows considers it hung; a slow but responsive
            // one is just not read this time.
            if (_windows.CheckResponding(window) != WindowResponse.Responding) _blocks.Block(window, processId);
            return null;
        }
        catch (DesktopException)
        {
            return null;
        }
    }

    private static Func<long, CancellationToken, Task<string?>> Reader(UiaWorker<UiaSession> worker) =>
        (window, ct) => worker.RunAsync((session, token) =>
        {
            try
            {
                return UiaJob.Run("UI Automation read-back", () => ReadFocused(session, window, token));
            }
            catch (Exception e) when (ComErrors.IsDeadElement(e) || ComErrors.IsTimeout(e))
            {
                return null;
            }
        }, Deadline, ct);

    private static string? ReadFocused(UiaSession session, long window, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Win32WindowSystem.CheckResponding(window) != WindowResponse.Responding) return null;
        ct.ThrowIfCancellationRequested();
        var element = session.Automation.GetFocusedElementBuildCache(session.ReadBackRequest);
        if (element is null || element.get_CachedIsPassword()) return null;
        if (UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_IsValuePatternAvailablePropertyId) != true) return null;
        return UiaValues.CachedString(element, UIA_PROPERTY_ID.UIA_ValueValuePropertyId);
    }
}
