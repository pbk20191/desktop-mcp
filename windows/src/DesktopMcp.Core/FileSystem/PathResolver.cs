using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace DesktopMcp.Core.FileSystem;

/// <summary>
/// Python's Path.resolve() on Windows (Decisions 9): the longest existing prefix becomes the OS final path (links
/// resolved, 8.3 names expanded, on-disk case) and the missing rest is appended unchanged. Public for App
/// launch_executable (P3 Decisions 34), which resolves its executable and working directory the same way.
/// </summary>
public static class PathResolver
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

    /// <summary>
    /// Like <see cref="Resolve"/>, but a symbolic link or junction in the final component is not followed (Decisions
    /// 11: delete and move act on a link itself). The parent is resolved, and the link keeps its own name.
    /// </summary>
    internal static string ResolveKeepingLink(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Length == 0 ? "." : path));
        if (!IsLink(full)) return Resolve(full);
        return FinalPath(full, openReparsePoint: true) ?? Path.Combine(Resolve(Path.GetDirectoryName(full)!), Path.GetFileName(full));
    }

    /// <summary>Whether <paramref name="path"/> itself is a symbolic link or junction (see DirectoryTree.IsLink).</summary>
    internal static bool IsLink(string path)
    {
        // FileInfo reads the attributes of the entry itself, directory or not. A missing path reports -1 (every bit).
        var info = new FileInfo(path);
        return (int)info.Attributes != -1 && DirectoryTree.IsLink(info);
    }

    private static string? FinalPath(string path, bool openReparsePoint = false)
    {
        // FILE_FLAG_BACKUP_SEMANTICS is required to open a directory; FILE_FLAG_OPEN_REPARSE_POINT opens a link itself.
        var flags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS
            | (openReparsePoint ? FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT : 0);
        using var handle = PInvoke.CreateFile(path, 0, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, flags, null);
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
