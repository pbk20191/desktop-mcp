namespace DesktopMcp.Core.Config;

/// <summary>upstream _apply_tool_filter (__main__.py:447-493).</summary>
public static class ToolFilter
{
    public const string AlwaysKept = "ControlStatus";

    /// <summary>The registered names that stay, in registry order.</summary>
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> registered, IReadOnlyList<string> explicitTools, IReadOnlyList<string> excludeTools)
    {
        if (explicitTools.Count > 0)
        {
            var keep = new HashSet<string>(explicitTools, StringComparer.Ordinal) { AlwaysKept };
            return registered.Where(keep.Contains).ToArray();
        }
        if (excludeTools.Count > 0)
        {
            var drop = new HashSet<string>(excludeTools.Where(name => name != AlwaysKept), StringComparer.Ordinal);
            return registered.Where(name => !drop.Contains(name)).ToArray();
        }
        return registered;
    }
}
