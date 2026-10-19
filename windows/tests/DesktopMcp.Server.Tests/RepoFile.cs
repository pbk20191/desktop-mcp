namespace DesktopMcp.Server.Tests;

internal static class RepoFile
{
    /// <summary>Walks up from the test binaries to the repository root and returns the file's full path.</summary>
    public static string Find(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relativePath} not found above {AppContext.BaseDirectory}");
    }
}
