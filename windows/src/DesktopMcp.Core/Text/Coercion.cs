using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.Text;

/// <summary>
/// Upstream's argument coercion (spec §4). MCP clients that strip anyOf schemas send "true" for true and "[1, 2]" for a
/// list, so bool | str and list parameters accept those strings. Each method throws <see cref="ArgumentException"/>
/// whose message is the text the tool reports.
/// </summary>
public static partial class Coercion
{
    /// <summary>upstream desktop/utils.py:18-44 as_bool: a bool, or "true"/"false" after strip and casefold.</summary>
    public static bool StrictBool(JsonElement value, string name)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.String:
                var text = value.GetString()!.Trim().ToLowerInvariant();
                if (text == "true") return true;
                if (text == "false") return false;
                break;
        }
        throw new ArgumentException($"{name} must be true or false");
    }

    /// <summary>upstream tools/scrape.py:29 and tools/process.py:37: true, or a string whose lower case is "true".</summary>
    public static bool LooseBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => value.GetString()!.ToLowerInvariant() == "true",
        _ => false,
    };

    /// <summary>A JSON integer, an integral JSON number, or a string holding a signed decimal integer.</summary>
    public static long Integer(JsonElement value, string name)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var whole)) return whole;
                if (value.TryGetDouble(out var real) && real == Math.Floor(real) && Math.Abs(real) < 9.2e18) return (long)real;
                break;
            case JsonValueKind.String:
                var text = value.GetString()!.Trim();
                if (SignedInteger().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                break;
        }
        throw new ArgumentException($"{name} must be an integer");
    }

    public static string String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException($"{name} must be a string");

    /// <summary>upstream _as_loc / _as_region (tools/input.py:40-48): a list, or a string holding JSON.</summary>
    public static JsonElement JsonList(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Array) return value;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{name} must be a list");
        try
        {
            using var document = JsonDocument.Parse(value.GetString()!);
            return document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"{name} is not valid JSON: {e.Message}");
        }
    }

    /// <summary>upstream _as_args (tools/app.py:13-22).</summary>
    public static IReadOnlyList<string> StringList(JsonElement value, string name)
    {
        var list = JsonList(value, name);
        if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new ArgumentException($"{name} must be a list of strings");
        return list.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    [GeneratedRegex("^[+-]?[0-9]+$")]
    private static partial Regex SignedInteger();
}
