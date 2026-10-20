using System.Globalization;
using System.Text.Json;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Actions;

namespace DesktopMcp.Server.Tools;

/// <summary>The texts and argument helpers the pointer tools (Click, Move, Scroll) share.</summary>
internal static class PointerTools
{
    public const string LocationShape = "Location must be a list of exactly 2 integers [x, y]";
    public const string EitherLocOrLabel = "Either loc or label must be provided.";

    /// <summary>A result text formatted with the invariant culture.</summary>
    public static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>An empty list, or a string holding one: upstream's falsy <c>loc</c> (tools/input.py:328).</summary>
    public static bool IsEmptyList(JsonElement? value)
    {
        if (value is not { } element) return false;
        try
        {
            var list = Coercion.JsonList(element, "loc");
            return list.ValueKind == JsonValueKind.Array && list.GetArrayLength() == 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The label's checked point (Decisions 1, 22), else the checked <paramref name="loc"/> (Decisions 8, 15).</summary>
    public static async Task<ScreenPoint> ResolveAsync(this IActionTargets targets, long? label, ScreenPoint? loc, CancellationToken ct) =>
        (await targets.ResolveTargetAsync(label, loc, ct).ConfigureAwait(false)).Point;

    /// <summary><see cref="ResolveAsync"/> with the target's window, for the keyboard-target check.</summary>
    public static async Task<ActionTarget> ResolveTargetAsync(this IActionTargets targets, long? label, ScreenPoint? loc, CancellationToken ct)
    {
        if (label is { } value) return await targets.LabelAsync(value, ct).ConfigureAwait(false);
        return targets.Point(loc ?? throw new ArgumentException(EitherLocOrLabel));
    }
}
