using System.Globalization;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Server.Cli;

/// <summary>click's parsing of option values that come from environment variables. An empty value means unset.</summary>
internal static class EnvValues
{
    private static readonly string[] TrueWords = ["1", "true", "t", "yes", "y", "on"];
    private static readonly string[] FalseWords = ["0", "false", "f", "no", "n", "off"];

    public static bool? Bool(string? raw, string option)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var value = raw.Trim().ToLowerInvariant();
        if (TrueWords.Contains(value)) return true;
        if (FalseWords.Contains(value)) return false;
        throw new CliUsageException($"Invalid value for '{option}': {PyText.Repr(raw)} is not a valid boolean.");
    }

    /// <summary>Python's float(): surrounding blanks, underscores, inf and nan are accepted.</summary>
    public static double? Float(string? raw, string option)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var text = raw.Trim().ToLowerInvariant().Replace("_", "");
        double? value = text switch
        {
            "inf" or "+inf" or "infinity" or "+infinity" => double.PositiveInfinity,
            "-inf" or "-infinity" => double.NegativeInfinity,
            "nan" or "+nan" or "-nan" => double.NaN,
            _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        };
        return value ?? throw new CliUsageException($"Invalid value for '{option}': {PyText.Repr(raw)} is not a valid float.");
    }

    public static string? Choice(string? raw, string option, IReadOnlyList<string> choices)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (choices.Contains(raw)) return raw;
        throw new CliUsageException(
            $"Invalid value for '{option}': {PyText.Repr(raw)} is not one of {string.Join(", ", choices.Select(PyText.Repr))}.");
    }

    /// <summary>upstream is_debug (config.py:4-6).</summary>
    public static bool IsDebug(string? raw) => raw is not null && raw.ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
