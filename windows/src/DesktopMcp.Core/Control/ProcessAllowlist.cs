namespace DesktopMcp.Core.Control;

/// <summary>[control] process_allowlist: image file names input may be sent to. Empty allows every process.</summary>
public sealed class ProcessAllowlist
{
    public const string InvalidEntry = "control.process_allowlist entries must be process names such as \"notepad.exe\"";

    private static readonly char[] Separators = [(char)92, '/', ':'];

    private readonly HashSet<string> _names;

    public ProcessAllowlist(IEnumerable<string> names)
    {
        _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0 || trimmed.AsSpan().IndexOfAny(Separators) >= 0) throw new ArgumentException(InvalidEntry);
            _names.Add(trimmed);
        }
    }

    public static ProcessAllowlist Everything { get; } = new([]);

    public bool AllowsAll => _names.Count == 0;

    /// <summary>A file-name match, ignoring case; an entry without an extension means "name.exe".</summary>
    public bool Allows(string? processName)
    {
        if (AllowsAll) return true;
        if (string.IsNullOrEmpty(processName)) return false;
        if (_names.Contains(processName)) return true;
        return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && _names.Contains(processName[..^4]);
    }

    public static string BlockedText(string? processName) =>
        $"Input blocked: the target window belongs to {(processName is null ? "an unknown process" : processName)}, which is not in control.process_allowlist.";
}
