using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.FileSystem;

/// <summary>Python 3.14 Path(root).glob(pattern), or rglob(pattern) when recursive (Decisions 12).</summary>
internal static class GlobMatcher
{
    public static IEnumerable<string> Glob(string root, string pattern, bool recursive, CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(pattern)) throw new NotSupportedException("Non-relative patterns are unsupported");
        if (pattern.Length == 0) throw new ArgumentException($"Unacceptable pattern: {PyText.Repr(pattern)}");
        var segments = pattern.Split('/', '\\').Where(s => s.Length > 0 && s != ".").ToList();
        if (recursive) segments.Insert(0, "**");
        if (segments.Count == 0) return [root];
        return Walk(root, segments, 0, cancellationToken).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Walk(string directory, IReadOnlyList<string> segments, int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (index == segments.Count)
        {
            yield return directory;
            yield break;
        }
        var segment = segments[index];
        var last = index == segments.Count - 1;
        if (segment == "**")
        {
            if (last)
            {
                // A trailing ** matches the directory itself and everything below it.
                yield return directory;
                foreach (var entry in Descendants(directory, cancellationToken)) yield return entry;
                yield break;
            }
            foreach (var match in Walk(directory, segments, index + 1, cancellationToken)) yield return match;
            foreach (var subdirectory in Entries(directory, directoriesOnly: true))
            {
                if (IsLink(subdirectory)) continue;
                foreach (var match in Walk(subdirectory, segments, index, cancellationToken)) yield return match;
            }
            yield break;
        }
        var regex = Fnmatch.ToRegex(segment);
        foreach (var entry in Entries(directory, directoriesOnly: !last))
        {
            if (!regex.IsMatch(Path.GetFileName(entry))) continue;
            foreach (var match in Walk(entry, segments, index + 1, cancellationToken)) yield return match;
        }
    }

    private static IEnumerable<string> Descendants(string directory, CancellationToken cancellationToken)
    {
        foreach (var entry in Entries(directory, directoriesOnly: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
            if (!Directory.Exists(entry) || IsLink(entry)) continue;
            foreach (var child in Descendants(entry, cancellationToken)) yield return child;
        }
    }

    // Unreadable directories are skipped, as Python's glob does.
    private static List<string> Entries(string directory, bool directoriesOnly)
    {
        try
        {
            return (directoriesOnly ? Directory.EnumerateDirectories(directory) : Directory.EnumerateFileSystemEntries(directory)).ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    // Only symlinks and junctions stop the walk; other reparse points are plain directories (see DirectoryTree.IsLink).
    private static bool IsLink(string path) => DirectoryTree.IsLink(new DirectoryInfo(path));
}
