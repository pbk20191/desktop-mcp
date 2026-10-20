namespace DesktopMcp.Core.FileSystem;

/// <summary>shutil.rmtree for the FileSystem tool.</summary>
internal static class DirectoryTree
{
    /// <summary>
    /// Deletes <paramref name="root"/> and everything below it, walking with an explicit stack. A link or junction is
    /// removed as a link and never entered, so its target survives (a recursive Directory.Delete over a junction fails
    /// with access denied when not elevated). Files go as each directory is listed, directories bottom-up. Like rmtree,
    /// a read-only file is not made writable: the first failure is thrown and the rest stays.
    /// </summary>
    public static void Delete(string root)
    {
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            directories.Add(directory);
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList())
            {
                if (IsLink(entry))
                {
                    if (entry is DirectoryInfo) Directory.Delete(entry.FullName);
                    else File.Delete(entry.FullName);
                }
                else if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                }
                else
                {
                    File.Delete(entry.FullName);
                }
            }
        }
        // Every directory is listed after its parent, so the reverse order empties children first.
        for (var i = directories.Count - 1; i >= 0; i--) Directory.Delete(directories[i]);
    }

    /// <summary>
    /// True when <paramref name="entry"/> is a link: a name surrogate, meaning a symbolic link or a mount point
    /// (junction). The ReparsePoint attribute alone is not enough. Other reparse points, such as OneDrive
    /// Files-On-Demand placeholders (IO_REPARSE_TAG_CLOUD_*) or deduplicated files, are ordinary files and directories
    /// with a filter behind them, and must be walked and deleted like any other. This matches CPython, whose rmtree and
    /// os.walk stop only at is_symlink() or is_junction(). LinkTarget is non-null only for those two tags.
    /// </summary>
    public static bool IsLink(FileSystemInfo entry) =>
        entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null;
}
