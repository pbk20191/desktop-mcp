using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

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

    private static string? FinalPath(string path)
    {
        // FILE_FLAG_BACKUP_SEMANTICS is required to open a directory.
        using var handle = PInvoke.CreateFile(path, 0, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024];
        while (true)
        {
            var length = PInvoke.GetFinalPathNameByHandle(handle, buffer, GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
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
