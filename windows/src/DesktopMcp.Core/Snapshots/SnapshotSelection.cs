using System.Text.Json;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Snapshots;

/// <summary>
/// The Snapshot/Screenshot <c>region</c> argument after its type check: a list of integers, or a string that upstream
/// parses as JSON (tools/_snapshot_helpers.py:169-172).
/// </summary>
public sealed record RegionArgument(IReadOnlyList<long>? Values, string? Json);

/// <summary>
/// Display and region selection (upstream desktop/service.py:1262-1364). The Read* methods are the argument type checks
/// that pydantic does upstream; the Parse* methods are upstream's own validation with its messages.
/// </summary>
public static class SnapshotSelection
{
    public const string DisplayIndicesMessage = "display must contain only zero-based active display indices";
    public const string RegionShapeMessage = "region must be a JSON array of 4 integers [left, top, right, bottom]";
    public const string RegionIntegersMessage = "region must contain only integers [left, top, right, bottom]";
    public const string RegionOrderMessage = "region must satisfy right > left and bottom > top";

    /// <summary><c>display: list[int] | None</c>: JSON null or absent, or a list of integers (Decisions 16).</summary>
    public static IReadOnlyList<long>? ReadDisplayArgument(JsonElement? value)
    {
        if (value is null) return null;
        if (value.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException("display must be a list of integers");
        return value.Value.EnumerateArray().Select(item => Integer(item, "display must be a list of integers")).ToArray();
    }

    /// <summary><c>region: list[int] | str | None</c> (Decisions 16).</summary>
    public static RegionArgument? ReadRegionArgument(JsonElement? value)
    {
        if (value is null) return null;
        return value.Value.ValueKind switch
        {
            JsonValueKind.String => new RegionArgument(null, value.Value.GetString()!),
            JsonValueKind.Array => new RegionArgument(
                value.Value.EnumerateArray().Select(item => Integer(item, "region must be a list of integers or a string")).ToArray(), null),
            _ => throw new ArgumentException("region must be a list of integers or a string"),
        };
    }

    /// <summary>upstream desktop/service.py:1262-1289 parse_display_selection: rejects negatives, drops duplicates, keeps order.</summary>
    public static IReadOnlyList<long>? ParseDisplays(IReadOnlyList<long>? values)
    {
        if (values is null) return null;
        var unique = new List<long>();
        foreach (var value in values)
        {
            if (value < 0) throw new ArgumentException(DisplayIndicesMessage);
            if (!unique.Contains(value)) unique.Add(value);
        }
        return unique.Count == 0 ? null : unique;
    }

    /// <summary>
    /// upstream desktop/service.py:1291-1318 parse_region_selection, after _as_region's json.loads. The result is the region
    /// clipped to the virtual screen (Decisions 14).
    /// </summary>
    public static ScreenRect? ParseRegion(RegionArgument? argument, ScreenRect virtualScreen)
    {
        if (argument is null) return null;
        var values = argument.Values ?? ParseJson(argument.Json!);
        if (values.Count != 4) throw new ArgumentException(RegionShapeMessage);
        var (left, top, right, bottom) = (values[0], values[1], values[2], values[3]);
        if (right <= left || bottom <= top) throw new ArgumentException(RegionOrderMessage);
        var overlapLeft = Math.Max(left, virtualScreen.Left);
        var overlapTop = Math.Max(top, virtualScreen.Top);
        var overlapRight = Math.Min(right, virtualScreen.Right);
        var overlapBottom = Math.Min(bottom, virtualScreen.Bottom);
        if (overlapRight <= overlapLeft || overlapBottom <= overlapTop)
            throw new ArgumentException(
                $"region [{left}, {top}, {right}, {bottom}] does not overlap the virtual screen bounds {virtualScreen.ToXyxy()}");
        return new ScreenRect((int)overlapLeft, (int)overlapTop, (int)overlapRight, (int)overlapBottom);
    }

    /// <summary>upstream desktop/service.py:1333-1364 get_display_union_rect.</summary>
    public static ScreenRect DisplayUnion(IReadOnlyList<long> indices, IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) throw new ArgumentException("No displays detected");
        var byIndex = displays.ToDictionary(d => (long)d.Index);
        var invalid = indices.Where(index => !byIndex.ContainsKey(index)).ToArray();
        if (invalid.Length > 0)
            throw new ArgumentException(
                $"Invalid display index {invalid[0]}. Available displays: {string.Join(",", displays.Select(d => d.Index))}");
        var selected = indices.Select(index => byIndex[index].Bounds).ToArray();
        return new ScreenRect(selected.Min(r => r.Left), selected.Min(r => r.Top), selected.Max(r => r.Right), selected.Max(r => r.Bottom));
    }

    // json.loads, then the list and integer checks (a bool or a float is not an integer, as isinstance(item, int) upstream).
    private static IReadOnlyList<long> ParseJson(string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new ArgumentException(e.Message);
        }
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 4) throw new ArgumentException(RegionShapeMessage);
        return root.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number)
                ? number
                : throw new ArgumentException(RegionIntegersMessage))
            .ToArray();
    }

    // pydantic's lax int: JSON integers, integral floats and integer strings (Coercion.Integer), but never booleans.
    private static long Integer(JsonElement item, string message)
    {
        try { return Coercion.Integer(item, "item"); }
        catch (ArgumentException) { throw new ArgumentException(message); }
    }
}
