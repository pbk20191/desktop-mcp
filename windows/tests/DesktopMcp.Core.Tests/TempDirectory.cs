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
            Clear(Path);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Removes links and junctions without entering them (a junction loop would never end, and a recursive
    // Directory.Delete over a junction throws access denied when not elevated) and clears read-only files.
    private static void Clear(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) entry.Delete();
            else if (entry is DirectoryInfo subdirectory) Clear(subdirectory.FullName);
            else entry.Attributes = FileAttributes.Normal;
        }
    }
}
