using System.Globalization;
using System.Text.Json;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Input;

/// <summary>Argument shapes and messages of the act tools (P3 Decisions 38). Every method throws <see cref="ArgumentException"/>.</summary>
public static class ActArguments
{
    /// <summary>A [x, y] list (or a string holding one) of two integers. Any failure gives <paramref name="shapeMessage"/>.</summary>
    public static ScreenPoint? Point(JsonElement? value, string name, string shapeMessage)
    {
        if (value is not { } element || element.ValueKind == JsonValueKind.Null) return null;
        return TryPoint(element) ?? throw new ArgumentException(shapeMessage);
    }

    public static IReadOnlyList<ScreenPoint> PointList(JsonElement value, string name)
    {
        const string Shape = "Each loc must be a list of exactly 2 integers [x, y]";
        return List(value, name).Select(item => TryPoint(item) ?? throw new ArgumentException(Shape)).ToArray();
    }

    public static IReadOnlyList<long> LabelList(JsonElement value, string name)
    {
        const string Shape = "labels must be a list of integers";
        return List(value, name, Shape).Select(item => TryInteger(item) ?? throw new ArgumentException(Shape)).ToArray();
    }

    public static IReadOnlyList<(ScreenPoint Point, string Text)> EditLocs(JsonElement value)
    {
        var result = new List<(ScreenPoint, string)>();
        foreach (var item in List(value, "loc"))
        {
            var invalid = $"Each loc item must be [x, y, text]. Invalid: {item.GetRawText()}";
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3 || item[2].ValueKind != JsonValueKind.String)
                throw new ArgumentException(invalid);
            var x = TryInteger(item[0]);
            var y = TryInteger(item[1]);
            if (x is null || y is null || !FitsInt(x.Value) || !FitsInt(y.Value)) throw new ArgumentException(invalid);
            result.Add((new ScreenPoint((int)x.Value, (int)y.Value), item[2].GetString()!));
        }
        return result;
    }

    public static IReadOnlyList<(long Label, string Text)> EditLabels(JsonElement value)
    {
        var result = new List<(long, string)>();
        foreach (var item in List(value, "label"))
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2 || item[1].ValueKind != JsonValueKind.String)
                throw new ArgumentException($"Each label item must be [label, text]. Invalid: {item.GetRawText()}");
            var label = TryInteger(item[0]) ?? throw new ArgumentException($"Invalid label id in item: {item.GetRawText()}");
            result.Add((label, item[1].GetString()!));
        }
        return result;
    }

    /// <summary>desktop/service.py:906-919 _normalize_drag_duration.</summary>
    public static double? Duration(JsonElement? value)
    {
        if (value is not { } element || element.ValueKind == JsonValueKind.Null) return null;
        double seconds;
        try
        {
            seconds = FiniteNumber(element, "duration");
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("duration must be a finite number of seconds");
        }
        if (seconds < 0 || seconds > 10) throw new ArgumentException("duration must be between 0 and 10 seconds");
        return seconds;
    }

    public static (int A, int B)? Pair(JsonElement? value, string name)
    {
        if (value is not { } element || element.ValueKind == JsonValueKind.Null) return null;
        var shape = $"{name} must be a list of exactly 2 integers";
        var items = List(element, name, shape).ToArray();
        if (items.Length != 2) throw new ArgumentException(shape);
        var a = TryInteger(items[0]);
        var b = TryInteger(items[1]);
        if (a is null || b is null || !FitsInt(a.Value) || !FitsInt(b.Value)) throw new ArgumentException(shape);
        return ((int)a.Value, (int)b.Value);
    }

    /// <summary>A JSON number or a numeric string, never a boolean, and finite.</summary>
    public static double FiniteNumber(JsonElement value, string name)
    {
        var message = $"{name} must be a finite number";
        double number;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                number = value.GetDouble();
                break;
            case JsonValueKind.String:
                if (!double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) throw new ArgumentException(message);
                break;
            default:
                throw new ArgumentException(message);
        }
        return double.IsFinite(number) ? number : throw new ArgumentException(message);
    }

    /// <summary>Exactly one of <paramref name="allowed"/> (case-sensitive, like upstream's Literal), or <paramref name="defaultValue"/> when absent.</summary>
    public static string Choice(JsonElement? value, string name, string defaultValue, params string[] allowed)
    {
        if (value is not { } element || element.ValueKind == JsonValueKind.Null) return defaultValue;
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;
            if (allowed.Contains(text)) return text;
        }
        throw new ArgumentException($"{name} must be one of: {string.Join(", ", allowed)}");
    }

    private static bool FitsInt(long value) => value is >= int.MinValue and <= int.MaxValue;

    private static ScreenPoint? TryPoint(JsonElement value)
    {
        JsonElement[] items;
        try
        {
            var list = Coercion.JsonList(value, "value");
            if (list.ValueKind != JsonValueKind.Array) return null;
            items = list.EnumerateArray().ToArray();
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (items.Length != 2) return null;
        var x = TryInteger(items[0]);
        var y = TryInteger(items[1]);
        if (x is null || y is null || !FitsInt(x.Value) || !FitsInt(y.Value)) return null;
        return new ScreenPoint((int)x.Value, (int)y.Value);
    }

    private static long? TryInteger(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return null;
        try
        {
            return Coercion.Integer(value, "value");
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> List(JsonElement value, string name, string? shape = null)
    {
        var list = Coercion.JsonList(value, name);
        if (list.ValueKind != JsonValueKind.Array) throw new ArgumentException(shape ?? $"{name} must be a list");
        return list.EnumerateArray();
    }
}
