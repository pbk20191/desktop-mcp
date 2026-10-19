using DesktopMcp.Core.Interop;

namespace DesktopMcp.Core.FileSystem;

/// <summary>
/// Python's Path.resolve() on Windows (Decisions 9): the longest existing prefix becomes the OS final path (links
/// resolved, 8.3 names expanded, on-disk case) and the missing rest is appended unchanged.
/// </summary>
internal static class PathResolver
{
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path.Length == 0 ? "." : path);
        var existing = full;
        var tail = new Stack<string>();
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null) return full;
            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }
        var resolved = FinalPath(existing) ?? existing;
        foreach (var part in tail) resolved = Path.Combine(resolved, part);
        return resolved;
    }

    private static unsafe string? FinalPath(string path)
    {
        using var handle = CoreNative.CreateFile(path, 0, CoreNative.FileShareAll, 0, CoreNative.OpenExisting, CoreNative.FileFlagBackupSemantics, 0);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* pointer = buffer) length = CoreNative.GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, 0);
            if (length == 0) return null;
            if (length < buffer.Length) return StripPrefix(new string(buffer, 0, (int)length));
            buffer = new char[length];
        }
    }

    // GetFinalPathNameByHandle returns \\?\C:\... or \\?\UNC\server\share\...; realpath strips both prefixes.
    private static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}
