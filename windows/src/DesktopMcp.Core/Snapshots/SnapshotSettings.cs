using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.Snapshots;

/// <summary>The environment settings that shape a snapshot; each takes the raw variable value (null when unset).</summary>
public static partial class SnapshotSettings
{
    public const string MaxTreeElementsVariable = "DESKTOP_MCP_MAX_TREE_ELEMENTS";
    public const string ExcludeProcessesVariable = "DESKTOP_MCP_EXCLUDE_PROCESSES";
    public const string ScreenshotScaleVariable = "DESKTOP_MCP_SCREENSHOT_SCALE";
    public const string ProfileSnapshotVariable = "DESKTOP_MCP_PROFILE_SNAPSHOT";

    /// <summary>upstream tree/budget.py:19.</summary>
    public const int DefaultMaxTreeElements = 500;

    /// <summary>upstream tools/_snapshot_helpers.py:21 MAX_IMAGE_WIDTH, MAX_IMAGE_HEIGHT.</summary>
    public const int MaxImageWidth = 1920;
    public const int MaxImageHeight = 1080;

    /// <summary>
    /// upstream tree/budget.py:22-52 resolve_max_tree_elements: Python's int() of the stripped value; blank, invalid or
    /// below 1 gives the default.
    /// </summary>
    public static int MaxTreeElements(string? raw)
    {
        var text = raw?.Trim() ?? "";
        if (!PythonInteger().IsMatch(text)) return DefaultMaxTreeElements;
        var digits = text.Replace("_", "");
        if (digits.StartsWith('-')) return DefaultMaxTreeElements;
        // Python's int is unbounded; anything past long.MaxValue is effectively no limit.
        if (!long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return int.MaxValue;
        return value < 1 ? DefaultMaxTreeElements : (int)Math.Min(value, int.MaxValue);
    }

    /// <summary>upstream tree/service.py:34-47: comma-separated process names, stripped and case-folded.</summary>
    public static IReadOnlySet<string> ExcludedProcesses(string? raw) =>
        (raw ?? "").Split(',').Select(part => part.Trim()).Where(part => part.Length > 0)
            .Select(part => part.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>upstream tools/_snapshot_helpers.py:37-39.</summary>
    public static bool ProfileEnabled(string? raw) => raw?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    /// <summary>
    /// upstream tools/_snapshot_helpers.py:24-34 _screenshot_scale: Python's float() of the value (default 1.0), clamped
    /// to [0.1, 1.0]; an invalid value or NaN gives 1.0.
    /// </summary>
    public static double ScreenshotScale(string? raw)
    {
        var text = (raw ?? "1.0").Trim().ToLowerInvariant();
        double value = text switch
        {
            "inf" or "+inf" or "infinity" or "+infinity" => double.PositiveInfinity,
            "-inf" or "-infinity" => double.NegativeInfinity,
            "nan" or "+nan" or "-nan" => double.NaN,
            _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1.0,
        };
        if (double.IsNaN(value)) return 1.0;
        return Math.Clamp(value, 0.1, 1.0);
    }

    /// <summary>upstream desktop/service.py:244-255: the configured scale, lowered further to fit 1920x1080.</summary>
    public static double ImageScale(string? raw, int width, int height)
    {
        var fitWidth = width > MaxImageWidth ? (double)MaxImageWidth / width : 1.0;
        var fitHeight = height > MaxImageHeight ? (double)MaxImageHeight / height : 1.0;
        return Math.Min(ScreenshotScale(raw), Math.Min(fitWidth, fitHeight));
    }

    [GeneratedRegex("^[+-]?[0-9]+(_[0-9]+)*$")]
    private static partial Regex PythonInteger();
}
