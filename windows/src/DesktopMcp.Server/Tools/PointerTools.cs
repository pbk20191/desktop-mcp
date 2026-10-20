using System.Globalization;
using System.Text.Json;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Server.Tools;

/// <summary>The texts and argument helpers the pointer tools (Click, Move, Scroll) share.</summary>
internal static class PointerTools
{
    public const string LocationShape = "Location must be a list of exactly 2 integers [x, y]";
    public const string EitherLocOrLabel = "Either loc or label must be provided.";
    public const string EitherLocsOrLabels = "Either locs or labels must be provided.";

    /// <summary>
    /// A MultiSelect or MultiEdit target that failed after <paramref name="done"/> items (Decisions 23): the same error
    /// with <c> Stopped after k of n items.</c> appended.
    /// </summary>
    public static Exception Stopped(Exception error, int done, int total)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = Text($"{error.Message} Stopped after {done} of {total} items.");
        return error is ArgumentException ? new ArgumentException(message, error) : new DesktopException(message, error);
    }

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
