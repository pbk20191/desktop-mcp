namespace DesktopMcp.Core.Snapshots;

/// <summary>upstream tools/input.py:16-22 WaitForCondition.</summary>
public enum WaitCondition
{
    TextExists,
    ActiveWindow,
    ElementExists,
    ElementEnabled,
    FocusedElement,
}

/// <summary>WaitFor's argument rules and text matching (upstream tools/input.py:78-212; P3 Decisions 25).</summary>
public static class WaitConditions
{
    /// <summary>The most a WaitFor call may wait, in seconds (upstream input.py:200).</summary>
    public const double MaxTimeout = 120;

    /// <summary>The longest pause between polls, in seconds (upstream input.py:202).</summary>
    public const double MaxInterval = 5;

    private static readonly Dictionary<string, WaitCondition> Conditions = new(StringComparer.Ordinal)
    {
        ["text_exists"] = WaitCondition.TextExists,
        ["active_window"] = WaitCondition.ActiveWindow,
        ["element_exists"] = WaitCondition.ElementExists,
        ["element_enabled"] = WaitCondition.ElementEnabled,
        ["focused_element"] = WaitCondition.FocusedElement,
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["text"] = "text_exists",
        ["window"] = "active_window",
        ["element"] = "element_exists",
        ["enabled"] = "element_enabled",
        ["focused"] = "focused_element",
    };

    /// <summary>
    /// upstream input.py:173-212 _validate_wait_for_args, in its order: finite numbers, the condition (trimmed, lower-cased,
    /// <c>-</c> read as <c>_</c>, then the aliases), the ranges, then the text each condition needs (an empty text is
    /// missing, as Python's <c>not text</c>). Throws <see cref="ArgumentException"/> with upstream's messages.
    /// </summary>
    public static WaitCondition Validate(string condition, string? text, string? windowName, double timeout, double interval)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!double.IsFinite(timeout)) throw new ArgumentException("timeout must be a finite number");
        if (!double.IsFinite(interval)) throw new ArgumentException("interval must be a finite number");

        var normalized = condition.Trim().ToLowerInvariant().Replace('-', '_');
        normalized = Aliases.GetValueOrDefault(normalized, normalized);
        if (!Conditions.TryGetValue(normalized, out var result))
            throw new ArgumentException("condition must be one of: text_exists, active_window, element_exists, element_enabled, focused_element");

        if (timeout <= 0 || timeout > MaxTimeout) throw new ArgumentException("timeout must be greater than 0 and at most 120 seconds");
        if (interval <= 0 || interval > MaxInterval) throw new ArgumentException("interval must be greater than 0 and at most 5 seconds");

        var hasText = !string.IsNullOrEmpty(text);
        var hasWindow = !string.IsNullOrEmpty(windowName);
        if (result == WaitCondition.TextExists && !hasText) throw new ArgumentException("text is required when condition is text_exists");
        if (result == WaitCondition.ActiveWindow && !(hasText || hasWindow))
            throw new ArgumentException("text or window_name is required when condition is active_window");
        if (result is WaitCondition.ElementExists or WaitCondition.ElementEnabled && !(hasText || hasWindow))
            throw new ArgumentException("text or window_name is required when condition is element_exists or element_enabled");
        return result;
    }

    /// <summary>upstream's condition name (<c>text_exists</c>, ...).</summary>
    public static string Name(WaitCondition condition) => condition switch
    {
        WaitCondition.TextExists => "text_exists",
        WaitCondition.ActiveWindow => "active_window",
        WaitCondition.ElementExists => "element_exists",
        WaitCondition.ElementEnabled => "element_enabled",
        WaitCondition.FocusedElement => "focused_element",
        _ => throw new ArgumentOutOfRangeException(nameof(condition)),
    };

    /// <summary>
    /// upstream input.py:78-83 _text_matches: no expectation matches anything, a missing value matches nothing, otherwise a
    /// case-insensitive substring test (<see cref="string.ToLowerInvariant"/> for Python's casefold, Decisions 25).
    /// </summary>
    public static bool TextMatches(string? value, string? expected)
    {
        if (expected is null) return true;
        if (value is null) return false;
        return value.ToLowerInvariant().Contains(expected.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>
    /// upstream input.py:118-124 _node_matches: the text in the name, the control type or a text-valued metadata entry,
    /// and the window name in the element's window name.
    /// </summary>
    public static bool ElementMatches(UiElement element, string? text, string? windowName)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (TextMatches(element.Name, text) || TextMatches(element.ControlType, text) || MetadataTexts(element.Metadata).Any(value => TextMatches(value, text)))
            && TextMatches(element.WindowName, windowName);
    }

    /// <summary>
    /// The text-valued metadata (Decisions 25): value, minimum, maximum, toggle state, expand/collapse state and shortcut,
    /// those that are set. Booleans and scroll percentages are left out, so <c>text="true"</c> matches no element by its flags.
    /// </summary>
    public static IEnumerable<string> MetadataTexts(UiMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        string?[] values = [metadata.Value, metadata.Minimum, metadata.Maximum, metadata.ToggleState, metadata.ExpandCollapseState, metadata.Shortcut];
        return values.OfType<string>();
    }
}
