using System.Globalization;
using System.Text.RegularExpressions;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.FileSystem;

/// <summary>upstream filesystem/service.py and views.py. Every method returns upstream's text, errors included.</summary>
public sealed class FileSystemService(string defaultDirectory, Func<bool> isElevated)
{
    public const long MaxReadSize = 10 * 1024 * 1024;
    public const int MaxResults = 500;
    private const string ElevationHint = "\n\nHINT: This operation may require an elevated (Administrator) terminal.";
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    /// <summary>The user's Desktop folder (platformdirs user_desktop_dir) and the real elevation state.</summary>
    public FileSystemService() : this(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Elevation.IsElevated) { }

    /// <summary>tools/filesystem.py:41-46: relative paths are joined to the Desktop folder.</summary>
    public string ResolveArgument(string path) => Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path, defaultDirectory);

    public string ReadFile(string path, long? offset, long? limit, string encoding)
    {
        var file = PathResolver.Resolve(path);
        if (!Exists(file)) return $"Error: File not found: {file}";
        if (!File.Exists(file)) return $"Error: Path is not a file: {file}";
        var size = new FileInfo(file).Length;
        if (size > MaxReadSize)
            return $"Error: File too large ({PyText.Thousands(size)} bytes). Maximum is {PyText.Thousands(MaxReadSize)} bytes. Use offset/limit parameters or the Shell tool for large files.";
        try
        {
            var text = TextFiles.Read(file, encoding);
            if (offset is null && limit is null) return $"File: {file}\n{text}";
            var lines = TextFiles.SplitLines(text);
            var start = Math.Max(0, (offset is null or 0 ? 1 : offset.Value) - 1);
            var end = limit is null or 0 ? lines.Count : start + limit.Value;
            return $"File: {file}\nLines {start + 1}-{Math.Min(end, lines.Count)} of {lines.Count}:\n{string.Concat(Slice(lines, start, end))}";
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(file);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error reading file: {e.Message}";
        }
    }

    public string WriteFile(string path, string content, bool append, string encoding)
    {
        var file = PathResolver.Resolve(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            TextFiles.Write(file, content, append, encoding);
            var size = new FileInfo(file).Length;
            return $"{(append ? "Appended to" : "Written to")} {file} ({PyText.Thousands(size)} bytes)";
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(file);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error writing file: {e.Message}";
        }
    }

    public string CopyPath(string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        var src = PathResolver.Resolve(source);
        var dst = PathResolver.Resolve(destination);
        if (!Exists(src)) return $"Error: Source not found: {src}";
        if (Exists(dst) && !overwrite) return $"Error: Destination already exists: {dst}. Set overwrite=True to replace.";
        try
        {
            if (File.Exists(src))
            {
                // shutil.copy2 into an existing directory copies the file inside it.
                var target = Directory.Exists(dst) ? Path.Combine(dst, Path.GetFileName(src)) : dst;
                if (SamePath(src, target)) return $"Error copying: {PyText.Repr(src)} and {PyText.Repr(target)} are the same file";
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, target, overwrite: true);
                return $"Copied file: {src} -> {dst}";
            }
            if (Exists(dst) && OverlapReason(src, dst) is { } reason) return $"Error copying: {reason}";
            // The source is listed before the destination is touched, so a directory loop leaves both as they were.
            var tree = ListTree(src, cancellationToken);
            if (Exists(dst)) DirectoryTree.Delete(dst);
            CopyTree(src, tree, dst, cancellationToken);
            return $"Copied directory: {src} -> {dst}";
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return "Error: Permission denied." + Hint();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error copying: {e.Message}";
        }
    }

    public string MovePath(string source, string destination, bool overwrite)
    {
        // Decisions 11: a link in the final component is moved, or replaced, as a link; its target is never touched.
        var src = PathResolver.ResolveKeepingLink(source);
        var dst = PathResolver.ResolveKeepingLink(destination);
        if (!Exists(src)) return $"Error: Source not found: {src}";
        if (Exists(dst) && !overwrite) return $"Error: Destination already exists: {dst}. Set overwrite=True to replace.";
        // Decisions 11: moving a path onto itself changes nothing.
        if (SamePath(src, dst)) return $"Moved: {src} -> {dst}";
        try
        {
            if (Exists(dst) && OverlapReason(src, dst) is { } reason) return $"Error moving: {reason}";
            // A move between volumes may fall back to copying, which walks the source and fails on a directory loop.
            // The source is listed here, before anything is created or deleted, so a loop leaves the destination as
            // it was. A rename on one volume does not walk the tree and needs no listing.
            // A link is renamed, never walked: a failed rename across volumes is an error, not a copy of its target.
            var isDirectory = Directory.Exists(src);
            var tree = isDirectory && !PathResolver.IsLink(src) && !SameVolume(src, dst) ? ListTree(src, CancellationToken.None) : null;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (Exists(dst)) DeleteEntry(dst);
            if (isDirectory) MoveTree(src, tree, dst);
            else File.Move(src, dst);
            return $"Moved: {src} -> {dst}";
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return "Error: Permission denied." + Hint();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error moving: {e.Message}";
        }
    }

    public string DeletePath(string path, bool recursive)
    {
        // Decisions 11: a link in the final component is deleted as a link, recursive or not; its target survives.
        var target = PathResolver.ResolveKeepingLink(path);
        if (!Exists(target)) return $"Error: Path not found: {target}";
        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
                return $"Deleted file: {target}";
            }
            if (PathResolver.IsLink(target))
            {
                Directory.Delete(target);
            }
            else if (!recursive)
            {
                if (Directory.EnumerateFileSystemEntries(target).Any())
                    return $"Error: Directory is not empty: {target}. Set recursive=True to delete non-empty directories.";
                Directory.Delete(target);
            }
            else
            {
                DirectoryTree.Delete(target);
            }
            return $"Deleted directory: {target}";
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(target);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error deleting: {e.Message}";
        }
    }

    public string ListDirectory(string path, string? pattern, bool recursive, bool showHidden, CancellationToken cancellationToken)
    {
        var directory = PathResolver.Resolve(path);
        if (!Exists(directory)) return $"Error: Directory not found: {directory}";
        if (!Directory.Exists(directory)) return $"Error: Path is not a directory: {directory}";
        var hasPattern = !string.IsNullOrEmpty(pattern);
        try
        {
            var candidates = recursive
                ? GlobMatcher.Glob(directory, hasPattern ? pattern! : "*", recursive: true, cancellationToken)
                : Directory.EnumerateFileSystemEntries(directory);
            var sorted = candidates.Select(Entry.Of)
                .OrderBy(e => e.IsDirectory ? 0 : 1)
                .ThenBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();
            Regex? filter = !recursive && hasPattern ? Fnmatch.ToRegex(pattern!) : null;
            var lines = new List<string>();
            var count = 0;
            foreach (var entry in sorted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!showHidden && entry.Name.StartsWith('.')) continue;
                if (filter is not null && !filter.IsMatch(entry.Name)) continue;
                if (++count > MaxResults)
                {
                    lines.Add($"... (truncated, {MaxResults}+ items)");
                    break;
                }
                lines.Add(FormatEntry(recursive ? Path.GetRelativePath(directory, entry.FullPath) : entry.Name, entry));
            }
            if (lines.Count == 0) return $"Directory {directory} is empty{(hasPattern ? $" matching \"{pattern}\"" : "")}.";
            var header = $"Directory: {directory}" + (hasPattern ? $" (filter: {pattern})" : "");
            return header + "\n" + string.Join("\n", lines);
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(directory);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error listing directory: {e.Message}";
        }
    }

    public string SearchFiles(string path, string pattern, bool recursive, CancellationToken cancellationToken)
    {
        var root = PathResolver.Resolve(path);
        if (!Exists(root)) return $"Error: Search path not found: {root}";
        if (!Directory.Exists(root)) return $"Error: Search path is not a directory: {root}";
        try
        {
            var matches = GlobMatcher.Glob(root, pattern, recursive, cancellationToken)
                .Select(Entry.Of)
                .OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();
            var lines = new List<string>();
            var count = 0;
            foreach (var match in matches)
            {
                if (++count > MaxResults)
                {
                    lines.Add($"... (truncated, {MaxResults}+ matches)");
                    break;
                }
                lines.Add(FormatEntry(Path.GetRelativePath(root, match.FullPath), match));
            }
            if (lines.Count == 0) return $"No matches found for \"{pattern}\" in {root}";
            return $"Search: \"{pattern}\" in {root} ({Math.Min(count, MaxResults)} matches)\n" + string.Join("\n", lines);
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(root);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error searching: {e.Message}";
        }
    }

    public string GetFileInfo(string path)
    {
        var target = PathResolver.Resolve(path);
        if (!Exists(target)) return $"Error: Path not found: {target}";
        try
        {
            var isDirectory = Directory.Exists(target);
            FileSystemInfo info = isDirectory ? new DirectoryInfo(target) : new FileInfo(target);
            var size = info is FileInfo file ? file.Length : 0;
            var lines = new List<string>
            {
                $"Path: {target}",
                $"Type: {(isDirectory ? "Directory" : "File")}",
                $"Size: {FormatSize(size)} ({PyText.Thousands(size)} bytes)",
                $"Created: {Stamp(info.CreationTime)}",
                $"Modified: {Stamp(info.LastWriteTime)}",
                $"Accessed: {Stamp(info.LastAccessTime)}",
                // os.access(W_OK) on Windows: only a file's read-only attribute counts.
                $"Read-only: {(!isDirectory && info.Attributes.HasFlag(FileAttributes.ReadOnly) ? "True" : "False")}",
            };
            if (isDirectory)
            {
                try
                {
                    var entries = Directory.EnumerateFileSystemEntries(target).ToList();
                    lines.Add($"Contents: {entries.Count(File.Exists)} files, {entries.Count(Directory.Exists)} directories");
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            else
            {
                lines.Add($"Extension: {Suffix(Path.GetFileName(target))}");
            }
            return string.Join("\n", lines);
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(target);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error getting file info: {e.Message}";
        }
    }

    /// <summary>upstream format_size (filesystem/views.py:72-81).</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{PyText.Fixed(bytes / 1024.0, 1)} KB",
        < 1024L * 1024 * 1024 => $"{PyText.Fixed(bytes / (1024.0 * 1024), 1)} MB",
        _ => $"{PyText.Fixed(bytes / (1024.0 * 1024 * 1024), 1)} GB",
    };

    /// <summary>Path.suffix (Python 3.14): the last dot part, none for a leading-dot name, "." for a trailing dot.</summary>
    internal static string Suffix(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "(none)" : name[dot..];
    }

    private sealed record Entry(string FullPath, string Name, bool IsDirectory, long Size)
    {
        public static Entry Of(string path)
        {
            var isDirectory = Directory.Exists(path);
            long size = 0;
            if (!isDirectory)
            {
                try { size = new FileInfo(path).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return new Entry(path, Path.GetFileName(path), isDirectory, size);
        }
    }

    // upstream Directory.to_string (filesystem/views.py:63-69): directories keep the two trailing spaces.
    private static string FormatEntry(string display, Entry entry) =>
        $"  [{(entry.IsDirectory ? "DIR " : "FILE")}] {display}  {(entry.IsDirectory ? "" : FormatSize(entry.Size))}";

    private static string Stamp(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    // Removes an existing entry for an overwrite: a link as a link, a directory as a tree, a file as a file.
    private static void DeleteEntry(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (PathResolver.IsLink(path)) Directory.Delete(path);
        else DirectoryTree.Delete(path);
    }

    // Python maps sharing and lock violations to PermissionError as well.
    private static bool IsPermissionError(Exception e) =>
        e is UnauthorizedAccessException || e is IOException { HResult: ErrorSharingViolation or ErrorLockViolation };

    private string Hint() => isElevated() ? "" : ElevationHint;

    private string PermissionDenied(string path) => $"Error: Permission denied: {path}{Hint()}";

    // Python's list[start:end] with start >= 0.
    private static IEnumerable<string> Slice(List<string> items, long start, long end)
    {
        long count = items.Count;
        var from = Math.Min(start, count);
        var to = end < 0 ? Math.Max(0, count + end) : Math.Min(end, count);
        return to <= from ? [] : items.Skip((int)from).Take((int)(to - from));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    // True when `path` is strictly below `ancestor`, compared on segment boundaries (C:\ab is not below C:\a).
    private static bool IsBelow(string path, string ancestor)
    {
        var parent = Path.TrimEndingDirectorySeparator(ancestor);
        return path.Length > parent.Length
            && path.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            && (Path.EndsInDirectorySeparator(parent) || path[parent.Length] == Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Decisions 11: why deleting the existing destination would delete some of the source, or null when it would not.
    /// Upstream deletes the destination first, so these cases lost the source.
    /// </summary>
    private static string? OverlapReason(string src, string dst)
    {
        if (SamePath(src, dst)) return $"Destination {dst} is the source itself; overwriting it would delete the source.";
        if (IsBelow(src, dst)) return $"Destination {dst} contains the source {src}; overwriting it would delete the source.";
        if (IsBelow(dst, src)) return $"Destination {dst} is inside the source {src}; overwriting it would delete part of the source.";
        return null;
    }

    // One entry of a source tree, listed before anything is created. Relative is the path below the tree's root.
    private sealed record TreeEntry(string Source, string Relative, bool IsDirectory);

    // The final paths of a directory and its ancestors in the walk, innermost first.
    private sealed record Ancestors(string FinalPath, Ancestors? Parent)
    {
        public bool Contains(string finalPath)
        {
            for (var chain = this; chain is not null; chain = chain.Parent)
                if (SamePath(chain.FinalPath, finalPath)) return true;
            return false;
        }
    }

    /// <summary>
    /// Every entry below <paramref name="root"/>, parents before children, walked with an explicit stack. Links to
    /// directories are followed as copytree follows them. A directory whose final path is one of its own ancestors
    /// is a loop and an error (upstream ends in an OSError or RecursionError); two links to the same directory are not.
    /// </summary>
    private static List<TreeEntry> ListTree(string root, CancellationToken cancellationToken)
    {
        var entries = new List<TreeEntry>();
        var pending = new Stack<(string Directory, string Relative, Ancestors Ancestors)>();
        pending.Push((root, "", new Ancestors(PathResolver.Resolve(root), null)));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subdirectories = new List<(string, string, Ancestors)>();
            foreach (var child in Directory.EnumerateFileSystemEntries(item.Directory).ToList())
            {
                var relative = Path.Join(item.Relative, Path.GetFileName(child));
                var isDirectory = Directory.Exists(child);
                entries.Add(new TreeEntry(child, relative, isDirectory));
                if (!isDirectory) continue;
                var finalPath = PathResolver.Resolve(child);
                if (item.Ancestors.Contains(finalPath)) throw new IOException($"Directory loop: {child} leads back to {finalPath}");
                subdirectories.Add((child, relative, new Ancestors(finalPath, item.Ancestors)));
            }
            for (var i = subdirectories.Count - 1; i >= 0; i--) pending.Push(subdirectories[i]);
        }
        return entries;
    }

    /// <summary>
    /// shutil.copytree. copytree lists each directory before it creates the copy; listing the whole source tree first
    /// goes further, so a destination inside the source is copied once and never into itself, at any depth.
    /// <paramref name="entries"/> is the source as ListTree listed it.
    /// </summary>
    private static void CopyTree(string source, List<TreeEntry> entries, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, entry.Relative);
            if (entry.IsDirectory) Directory.CreateDirectory(target);
            else File.Copy(entry.Source, target);
        }
        // copystat on every directory once its contents are in place.
        foreach (var entry in entries.Where(e => e.IsDirectory))
            Directory.SetLastWriteTimeUtc(Path.Combine(destination, entry.Relative), Directory.GetLastWriteTimeUtc(entry.Source));
        Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
    }

    /// <summary>Whether a directory move can be a rename. Different roots mean the copy fallback may be needed. A test seam.</summary>
    internal Func<string, string, bool> SameVolume { get; init; } =
        (a, b) => string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The rename step of a directory move. A test seam.</summary>
    internal Action<string, string> MoveDirectory { get; init; } = Directory.Move;

    // tree is the source listed by MovePath, present whenever the volumes differ.
    private void MoveTree(string source, List<TreeEntry>? tree, string destination)
    {
        try
        {
            MoveDirectory(source, destination);
        }
        catch (IOException) when (tree is not null)
        {
            // shutil.move across volumes: copy, then delete.
            CopyTree(source, tree, destination, CancellationToken.None);
            DirectoryTree.Delete(source);
        }
    }
}
