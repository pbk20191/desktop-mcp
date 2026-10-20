using System.Text.Json;

namespace DesktopMcp.Core.Control;

/// <summary>Upstream's desktop-ownership table (<c>tools/control_policy.py</c>).</summary>
public static class ControlPolicy
{
    public static readonly IReadOnlyList<string> Policies = ["relaxed", "balanced", "strict"];

    private enum Category { Direct, Possible, Other }

    private static readonly HashSet<string> Possible = ["PowerShell", "Notification"];

    private static readonly HashSet<string> Other =
        ["FileSystem", "Registry", "Snapshot", "Screenshot", "Scrape", "Wait", "WaitFor", "DisplayInventory"];

    // Upstream's DIRECT set (Click, Type, Scroll, Move, Shortcut, MultiSelect, MultiEdit) needs no table:
    // direct is the default category for every tool not listed here.
    private static readonly Dictionary<string, Dictionary<string, Category>> Modes = new()
    {
        ["App"] = new()
        {
            ["launch"] = Category.Possible,
            ["launch_executable"] = Category.Possible,
            ["resize"] = Category.Direct,
            ["switch"] = Category.Direct,
        },
        ["Process"] = new() { ["list"] = Category.Other, ["kill"] = Category.Possible },
        ["Clipboard"] = new() { ["get"] = Category.Other, ["set"] = Category.Possible },
    };

    /// <summary>
    /// The call's <c>mode</c> as upstream reads it: a missing key is <c>launch</c> for App and none otherwise;
    /// a non-string value is none. Tools without modes return null.
    /// </summary>
    public static string? ModeOf(string toolName, IDictionary<string, JsonElement>? arguments)
    {
        if (!Modes.ContainsKey(toolName)) return null;
        if (arguments is not null && arguments.TryGetValue("mode", out var value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return toolName == "App" ? "launch" : null;
    }

    public static bool RequiresControl(string policy, string toolName, string? mode)
    {
        if (toolName == "ControlStatus") return false;
        if (policy == "strict") return true;
        if (policy is not ("relaxed" or "balanced")) throw new ArgumentException($"Unknown control policy: {policy}");
        var category = Category.Direct; // unknown tools and modes must not bypass ownership
        if (Modes.TryGetValue(toolName, out var modes))
        {
            if (mode is not null) category = modes.GetValueOrDefault(mode, Category.Direct);
        }
        else if (Possible.Contains(toolName)) category = Category.Possible;
        else if (Other.Contains(toolName)) category = Category.Other;
        return category == Category.Direct || (policy == "balanced" && category == Category.Possible);
    }
}
