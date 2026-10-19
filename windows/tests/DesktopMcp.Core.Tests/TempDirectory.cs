namespace DesktopMcp.Core.Tests;

/// <summary>A fresh folder under %TEMP%, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dmcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            // Tests leave read-only files behind, which rmtree (and so DirectoryTree) refuses to delete. Links and
            // junctions are not entered (DirectoryTree removes them as links); other reparse points are walked.
            var pending = new Stack<string>([Path]);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (DesktopMcp.Core.FileSystem.DirectoryTree.IsLink(entry)) continue;
                    if (entry is DirectoryInfo) pending.Push(entry.FullName);
                    else entry.Attributes = FileAttributes.Normal;
                }
            }
            DesktopMcp.Core.FileSystem.DirectoryTree.Delete(Path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
