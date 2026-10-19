using DesktopMcp.Core.FileSystem;

namespace DesktopMcp.Core.Tests;

// EmptyMeansTheCurrentDirectory reads the process working directory.
[Collection(CwdSensitiveCollection.Name)]
public sealed class PathResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ExistingPathsGetTheirOnDiskSpelling()
    {
        Directory.CreateDirectory(_temp.Combine("MixedCase"));
        var resolved = PathResolver.Resolve(_temp.Combine("mixedcase"));
        Assert.Equal("MixedCase", Path.GetFileName(resolved));
        Assert.False(resolved.StartsWith(@"\\?\", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingTailIsAppendedToTheResolvedPrefix()
    {
        Directory.CreateDirectory(_temp.Combine("MixedCase"));
        var root = PathResolver.Resolve(_temp.Combine("MixedCase"));
        Assert.Equal(Path.Combine(root, "missing", "x.txt"), PathResolver.Resolve(_temp.Combine("mixedcase", "missing", "x.txt")));
    }

    [Fact]
    public void EmptyMeansTheCurrentDirectory() =>
        Assert.Equal(PathResolver.Resolve(Directory.GetCurrentDirectory()), PathResolver.Resolve(""));
}
