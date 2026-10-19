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
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
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
}
