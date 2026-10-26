using DesktopMcp.Core.FileSystem;

namespace DesktopMcp.Core.Tests;

public sealed class GlobMatcherTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;

    public GlobMatcherTests()
    {
        _root = PathResolver.Resolve(_temp.Path);
        foreach (var file in new[] { "a.txt", "B.TXT", ".hidden.txt", "c.log", @"sub\d.txt", @"sub\deep\e.txt" })
            _temp.WriteFile(file, "x");
    }

    public void Dispose() => _temp.Dispose();

    private string[] Relative(string pattern, bool recursive) =>
        GlobMatcher.Glob(_root, pattern, recursive, CancellationToken.None).Select(p => Path.GetRelativePath(_root, p)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    [Fact]
    public void GlobMatchesOneLevel() => Assert.Equal([".hidden.txt", "a.txt", "B.TXT"], Relative("*.txt", recursive: false));

    [Fact]
    public void RecursiveGlobIsDoubleStarSlashPattern() =>
        Assert.Equal([".hidden.txt", "a.txt", "B.TXT", @"sub\d.txt", @"sub\deep\e.txt"], Relative("*.txt", recursive: true));

    [Fact]
    public void SegmentsAndDoubleStarCombine() => Assert.Equal([@"sub\deep\e.txt"], Relative("sub/**/e.txt", recursive: false));

    [Fact]
    public void TrailingDoubleStarMatchesTheDirectoryAndEverythingBelow() =>
        Assert.Equal(["sub", @"sub\d.txt", @"sub\deep", @"sub\deep\e.txt"], Relative(@"sub\**", recursive: false));

    [Fact]
    public void AbsolutePatternsAreRejected() =>
        Assert.Equal("Non-relative patterns are unsupported",
            Assert.Throws<NotSupportedException>(() => GlobMatcher.Glob(_root, @"C:\*.txt", false, CancellationToken.None).ToList()).Message);
}
