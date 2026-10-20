using System.Globalization;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Desktop.WindowControl;

namespace DesktopMcp.Desktop.Actions;

/// <summary>
/// One Type at a resolved target, shared by Type and each MultiEdit item (preflight F14): the click, the keyboard-target
/// check of the target's window after it (P3 Decisions 7; it throws and nothing is typed when the window cannot take the
/// keys), the keys (upstream desktop/service.py:767-814), then the read-back (Decisions 4) unless Enter was pressed or the
/// text is empty. The caller holds the input lock.
/// </summary>
public sealed class TypeSequence
{
    private readonly IDesktopInput _input;
    private readonly IForeground _foreground;
    private readonly ITypeReadBack _readBack;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public TypeSequence(IDesktopInput input, IForeground foreground, ITypeReadBack readBack, Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(foreground);
        ArgumentNullException.ThrowIfNull(readBack);
        ArgumentNullException.ThrowIfNull(delay);
        _input = input;
        _foreground = foreground;
        _readBack = readBack;
        _delay = delay;
    }

    /// <summary>Types <paramref name="text"/> at the target; returns the read-back warning line, or null.</summary>
    /// <param name="warningAt">The point the warning names (MultiEdit's items), or null for Type's warning.</param>
    public async Task<string?> RunAsync(ActionTarget target, string text, CaretPosition caret, bool clear, bool pressEnter,
        ScreenPoint? warningAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(text);
        // No top-level window under the point: nothing can take the keys, so nothing is clicked or typed.
        if (target.Window == 0)
            throw new DesktopException(string.Create(CultureInfo.InvariantCulture,
                $"No window is at ({target.Point.X},{target.Point.Y}); nothing was typed."));
        await _input.TypeAsync(target.Point, text, caret, clear, pressEnter,
            token => _foreground.EnsureKeyboardTargetAsync(target.Window, target.WindowName, token), ct).ConfigureAwait(false);
        // A submit clears the field or navigates, and an empty text has nothing to find (Decisions 4).
        if (pressEnter || text.Length == 0) return null;
        return await TypeReadBack.CheckAsync(_readBack, text, warningAt, _delay, ct).ConfigureAwait(false);
    }
}
