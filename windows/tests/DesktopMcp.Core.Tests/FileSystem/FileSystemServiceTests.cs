using System.Globalization;
using DesktopMcp.Core.FileSystem;

namespace DesktopMcp.Core.Tests;

public sealed class FileSystemServiceTests : IDisposable
{
    private const string Hint = "\n\nHINT: This operation may require an elevated (Administrator) terminal.";
    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly FileSystemService _fs;

    public FileSystemServiceTests()
    {
        _root = PathResolver.Resolve(_temp.Path);
        _fs = new FileSystemService(_root, () => true);
    }

    public void Dispose() => _temp.Dispose();

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private string Write(string relative, string content)
    {
        var path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void RelativeArgumentsResolveFromTheDefaultDirectory()
    {
        Assert.Equal(P("a.txt"), _fs.ResolveArgument("a.txt"));
        Assert.Equal(@"D:\x.txt", _fs.ResolveArgument(@"D:\x.txt"));
    }

    [Fact]
    public void ReadWholeFile() =>
        Assert.Equal($"File: {P("a.txt")}\na\nb\nc", _fs.ReadFile(Write("a.txt", "a\r\nb\rc"), null, null, "utf-8"));

    [Theory]
    [InlineData(2L, 2L, "Lines 2-3 of 4:\n2\n3\n")]
    [InlineData(3L, 10L, "Lines 3-4 of 4:\n3\n4\n")]
    [InlineData(0L, 0L, "Lines 1-4 of 4:\n1\n2\n3\n4\n")]
    [InlineData(-5L, 1L, "Lines 1-1 of 4:\n1\n")]
    [InlineData(1L, -1L, "Lines 1--1 of 4:\n1\n2\n3\n")]
    public void ReadWithOffsetAndLimitFollowsPythonSlicing(long offset, long limit, string expected)
    {
        var path = Write("n.txt", "1\n2\n3\n4\n");
        Assert.Equal($"File: {path}\n{expected}", _fs.ReadFile(path, offset, limit, "utf-8"));
    }

    [Fact]
    public void ReadErrors()
    {
        Assert.Equal($"Error: File not found: {P("nope.txt")}", _fs.ReadFile(P("nope.txt"), null, null, "utf-8"));
        Assert.Equal($"Error: Path is not a file: {_root}", _fs.ReadFile(_root, null, null, "utf-8"));
        Assert.Equal("Error reading file: unknown encoding: nope", _fs.ReadFile(Write("a.txt", "x"), null, null, "nope"));
    }

    [Fact]
    public void ReadRefusesLargeFiles()
    {
        var path = P("big.bin");
        using (var stream = File.Create(path)) stream.SetLength(FileSystemService.MaxReadSize + 1);
        Assert.Equal("Error: File too large (10,485,761 bytes). Maximum is 10,485,760 bytes. Use offset/limit parameters or the Shell tool for large files.",
            _fs.ReadFile(path, null, null, "utf-8"));
    }

    [Fact]
    public void ALockedFileIsPermissionDeniedWithTheHintWhenNotElevated()
    {
        var path = Write("locked.txt", "x");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal($"Error: Permission denied: {path}{Hint}", new FileSystemService(_root, () => false).ReadFile(path, null, null, "utf-8"));
    }

    [Fact]
    public void WriteAndAppendReportTheSize()
    {
        Assert.Equal($"Written to {P("w.txt")} (7 bytes)", _fs.WriteFile(P("w.txt"), "a\nb\nc", false, "utf-8"));
        Assert.Equal($"Appended to {P("w.txt")} (9 bytes)", _fs.WriteFile(P("w.txt"), "\n", true, "utf-8"));
        Assert.Equal("a\r\nb\r\nc\r\n", File.ReadAllText(P("w.txt")));
    }

    [Fact]
    public void WriteCreatesParentDirectories()
    {
        _fs.WriteFile(P("sub", "dir", "f.txt"), "x", false, "utf-8");
        Assert.True(File.Exists(P("sub", "dir", "f.txt")));
    }

    [Fact]
    public void CopyFileDirectoryAndOverwrite()
    {
        var source = Write("a.txt", "x");
        Assert.Equal($"Copied file: {source} -> {P("b.txt")}", _fs.CopyPath(source, P("b.txt"), false, CancellationToken.None));
        Assert.Equal($"Error: Destination already exists: {P("b.txt")}. Set overwrite=True to replace.", _fs.CopyPath(source, P("b.txt"), false, CancellationToken.None));
        Directory.CreateDirectory(P("into"));
        Assert.Equal($"Copied file: {source} -> {P("into")}", _fs.CopyPath(source, P("into"), true, CancellationToken.None));
        Assert.True(File.Exists(P("into", "a.txt")));
        Write(@"tree\x\y.txt", "y");
        Assert.Equal($"Copied directory: {P("tree")} -> {P("tree2")}", _fs.CopyPath(P("tree"), P("tree2"), false, CancellationToken.None));
        Assert.Equal("y", File.ReadAllText(P("tree2", "x", "y.txt")));
        Assert.Equal($"Error: Source not found: {P("none")}", _fs.CopyPath(P("none"), P("z"), false, CancellationToken.None));
    }

    [Fact]
    public void MoveAndOverwrite()
    {
        var source = Write("a.txt", "x");
        Write("b.txt", "old");
        Assert.Equal($"Error: Destination already exists: {P("b.txt")}. Set overwrite=True to replace.", _fs.MovePath(source, P("b.txt"), false));
        Assert.Equal($"Moved: {source} -> {P("b.txt")}", _fs.MovePath(source, P("b.txt"), true));
        Assert.Equal("x", File.ReadAllText(P("b.txt")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void MoveOntoItselfWithOverwriteKeepsTheFile()
    {
        var path = Write("a.txt", "x");
        Assert.Equal($"Moved: {path} -> {path}", _fs.MovePath(path, P("A.TXT"), true));
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void CopyADirectoryIntoItsOwnSubdirectoryCopiesOneLevel()
    {
        Write(@"a\x.txt", "x");
        Write(@"a\s\y.txt", "y");
        Assert.Equal($"Copied directory: {P("a")} -> {P("a", "b")}", _fs.CopyPath(P("a"), P("a", "b"), false, CancellationToken.None));
        Assert.Equal("x", File.ReadAllText(P("a", "b", "x.txt")));
        Assert.Equal("y", File.ReadAllText(P("a", "b", "s", "y.txt")));
        Assert.False(Directory.Exists(P("a", "b", "b")));
    }

    [Fact]
    public void CopyADirectoryIntoANewDeepSubdirectoryStops()
    {
        Write(@"a\c\x.txt", "x");
        Assert.Equal($"Copied directory: {P("a")} -> {P("a", "c", "d")}", _fs.CopyPath(P("a"), P("a", "c", "d"), false, CancellationToken.None));
        Assert.Equal("x", File.ReadAllText(P("a", "c", "d", "c", "x.txt")));
        Assert.False(Directory.Exists(P("a", "c", "d", "c", "d")));
    }

    // A directory junction (mklink /J needs no privilege, unlike a symbolic link).
    private static void Junction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(Directory.Exists(link), process.StandardError.ReadToEnd());
    }

    [Fact]
    public void CopyADirectoryWithAJunctionLoopIsAnErrorAndCreatesNothing()
    {
        Write(@"a\x.txt", "x");
        Directory.CreateDirectory(P("a", "s"));
        Junction(P("a", "s", "loop"), P("a"));
        Assert.Equal($"Error copying: Directory loop: {P("a", "s", "loop")} leads back to {P("a")}",
            _fs.CopyPath(P("a"), P("b"), false, CancellationToken.None));
        Assert.False(Directory.Exists(P("b")));
    }

    [Fact]
    public void CrossVolumeMoveWithALoopKeepsTheDestination()
    {
        Write(@"a\x.txt", "x");
        Directory.CreateDirectory(P("a", "s"));
        Junction(P("a", "s", "loop"), P("a"));
        Write(@"b\keep.txt", "k");
        // Stands in for a second volume, where the rename fails and the move falls back to copy and delete.
        var fs = new FileSystemService(_root, () => true)
        {
            SameVolume = (_, _) => false,
            MoveDirectory = (_, _) => throw new IOException("not same device"),
        };
        Assert.Equal($"Error moving: Directory loop: {P("a", "s", "loop")} leads back to {P("a")}",
            fs.MovePath(P("a"), P("b"), true));
        Assert.Equal("k", File.ReadAllText(P("b", "keep.txt")));
        Assert.Equal("x", File.ReadAllText(P("a", "x.txt")));
    }

    [Fact]
    public void CrossVolumeMoveFallsBackToCopyAndDelete()
    {
        Write(@"a\s\x.txt", "x");
        Write(@"b\old.txt", "o");
        var fs = new FileSystemService(_root, () => true)
        {
            SameVolume = (_, _) => false,
            MoveDirectory = (_, _) => throw new IOException("not same device"),
        };
        Assert.Equal($"Moved: {P("a")} -> {P("b")}", fs.MovePath(P("a"), P("b"), true));
        Assert.False(Directory.Exists(P("a")));
        Assert.Equal(["s"], Directory.EnumerateFileSystemEntries(P("b")).Select(Path.GetFileName));
        Assert.Equal("x", File.ReadAllText(P("b", "s", "x.txt")));
    }

    [Fact]
    public void OnlySymlinksAndJunctionsAreLinks()
    {
        Write(@"plain\f.txt", "f");
        Write(@"target\t.txt", "t");
        Junction(P("junction"), P("target"));
        Assert.False(DirectoryTree.IsLink(new DirectoryInfo(P("plain"))));
        Assert.False(DirectoryTree.IsLink(new FileInfo(P("plain", "f.txt"))));
        Assert.True(DirectoryTree.IsLink(new DirectoryInfo(P("junction"))));
    }

    [SymlinkFact]
    public void ADirectorySymlinkIsALink()
    {
        Write(@"target\t.txt", "t");
        Write(@"tree\f.txt", "f");
        Directory.CreateSymbolicLink(P("tree", "symlink"), P("target"));
        Assert.True(DirectoryTree.IsLink(new DirectoryInfo(P("tree", "symlink"))));
        Assert.Equal($"Deleted directory: {P("tree")}", _fs.DeletePath(P("tree"), true));
        Assert.False(Directory.Exists(P("tree")));
        Assert.Equal("t", File.ReadAllText(P("target", "t.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteOfAJunctionRemovesOnlyTheLink(bool recursive)
    {
        Write(@"target\t.txt", "t");
        Junction(P("j"), P("target"));
        Assert.Equal($"Deleted directory: {P("j")}", _fs.DeletePath(P("j"), recursive));
        Assert.False(Directory.Exists(P("j")));
        Assert.Equal("t", File.ReadAllText(P("target", "t.txt")));
    }

    [SymlinkFact]
    public void DeleteOfAFileSymlinkRemovesOnlyTheLink()
    {
        Write("target.txt", "t");
        File.CreateSymbolicLink(P("link.txt"), P("target.txt"));
        Assert.Equal($"Deleted file: {P("link.txt")}", _fs.DeletePath(P("link.txt"), false));
        Assert.False(File.Exists(P("link.txt")));
        Assert.Equal("t", File.ReadAllText(P("target.txt")));
    }

    [SymlinkFact]
    public void DeleteOfADirectorySymlinkRemovesOnlyTheLink()
    {
        Write(@"target\t.txt", "t");
        Directory.CreateSymbolicLink(P("link"), P("target"));
        Assert.Equal($"Deleted directory: {P("link")}", _fs.DeletePath(P("link"), true));
        Assert.False(Directory.Exists(P("link")));
        Assert.Equal("t", File.ReadAllText(P("target", "t.txt")));
    }

    [Fact]
    public void MoveOfAJunctionMovesTheLink()
    {
        Write(@"target\t.txt", "t");
        Junction(P("j"), P("target"));
        Directory.CreateDirectory(P("elsewhere"));
        Assert.Equal($"Moved: {P("j")} -> {P("elsewhere", "j2")}", _fs.MovePath(P("j"), P("elsewhere", "j2"), false));
        Assert.False(Directory.Exists(P("j")));
        Assert.True(PathResolver.IsLink(P("elsewhere", "j2")));
        Assert.Equal("t", File.ReadAllText(P("elsewhere", "j2", "t.txt")));
        Assert.Equal(["t.txt"], Directory.EnumerateFileSystemEntries(P("target")).Select(Path.GetFileName));
    }

    [Fact]
    public void ACrossVolumeMoveOfAJunctionNeverCopiesOrDeletesTheTarget()
    {
        Write(@"target\t.txt", "t");
        Junction(P("j"), P("target"));
        var fs = new FileSystemService(_root, () => true)
        {
            SameVolume = (_, _) => false,
            MoveDirectory = (_, _) => throw new IOException("not same device"),
        };
        Assert.Equal("Error moving: not same device", fs.MovePath(P("j"), P("b"), false));
        Assert.True(PathResolver.IsLink(P("j")));
        Assert.False(Directory.Exists(P("b")));
        Assert.Equal("t", File.ReadAllText(P("target", "t.txt")));
    }

    [Fact]
    public void MoveOverwriteOntoAJunctionReplacesOnlyTheLink()
    {
        Write(@"target\t.txt", "t");
        Junction(P("j"), P("target"));
        Write(@"src\s.txt", "s");
        Assert.Equal($"Moved: {P("src")} -> {P("j")}", _fs.MovePath(P("src"), P("j"), true));
        Assert.False(PathResolver.IsLink(P("j")));
        Assert.Equal("s", File.ReadAllText(P("j", "s.txt")));
        Assert.Equal("t", File.ReadAllText(P("target", "t.txt")));
    }

    [Fact]
    public void MovingAJunctionOntoItsOwnTargetIsNotAnOverlap()
    {
        // The link and its target are different entries: the overwrite replaces the target, as the user asked.
        Write(@"target\t.txt", "t");
        Junction(P("j"), P("target"));
        Assert.Equal($"Moved: {P("j")} -> {P("target")}", _fs.MovePath(P("j"), P("target"), true));
        Assert.False(Directory.Exists(P("j")));
        Assert.True(PathResolver.IsLink(P("target")));
    }

    [Fact]
    public void CopyAndInfoStillFollowAJunction()
    {
        Write(@"target\t.txt", "t");
        Junction(P("j"), P("target"));
        Assert.StartsWith($"Path: {P("target")}\nType: Directory", _fs.GetFileInfo(P("j")));
        Assert.Equal($"Copied directory: {P("target")} -> {P("copy")}", _fs.CopyPath(P("j"), P("copy"), false, CancellationToken.None));
    }

    [Fact]
    public void AnOverlapBetweenRealPathsIsStillRefusedThroughALinkedParent()
    {
        // x\a reached through the junction xj: the parent resolves, so the overlap check sees the real paths.
        Write(@"x\a\y.txt", "y");
        Junction(P("xj"), P("x"));
        Assert.Equal($"Error moving: Destination {P("x")} contains the source {P("x", "a")}; overwriting it would delete the source.",
            _fs.MovePath(P("xj", "a"), P("x"), true));
        Assert.Equal("y", File.ReadAllText(P("x", "a", "y.txt")));
    }

    /// <summary>A fact that is skipped when this process cannot create a symbolic link (it needs Developer Mode or elevation).</summary>
    private sealed class SymlinkFactAttribute : FactAttribute
    {
        public SymlinkFactAttribute()
        {
            if (!CanCreateSymlinks()) Skip = "Creating a symbolic link needs Developer Mode or elevation.";
        }

        private static bool CanCreateSymlinks()
        {
            using var temp = new TempDirectory();
            try
            {
                Directory.CreateSymbolicLink(temp.Combine("link"), temp.Path);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    [Fact]
    public void CopyFollowsAJunctionThatIsNotALoop()
    {
        Write(@"c\y.txt", "y");
        Write(@"a\x.txt", "x");
        Junction(P("a", "toC"), P("c"));
        Junction(P("a", "toC2"), P("c"));
        Assert.Equal($"Copied directory: {P("a")} -> {P("b")}", _fs.CopyPath(P("a"), P("b"), false, CancellationToken.None));
        Assert.Equal("y", File.ReadAllText(P("b", "toC", "y.txt")));
        Assert.Equal("y", File.ReadAllText(P("b", "toC2", "y.txt")));
        Assert.Null(new DirectoryInfo(P("b", "toC")).LinkTarget);
    }

    [Fact]
    public void CopyADeepTreeKeepsEveryLevelAndTheDirectoryTimes()
    {
        var deep = Path.Combine(Enumerable.Repeat("d", 40).ToArray());
        Write(Path.Combine("a", deep, "z.txt"), "z");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(P("a", "d"), stamp);
        Assert.Equal($"Copied directory: {P("a")} -> {P("b")}", _fs.CopyPath(P("a"), P("b"), false, CancellationToken.None));
        Assert.Equal("z", File.ReadAllText(P(["b", deep, "z.txt"])));
        Assert.Equal(stamp, Directory.GetLastWriteTimeUtc(P("b", "d")));
    }

    // tree\sub\f.txt plus tree\sub\j, a junction to outside\keep.txt's directory.
    private void TreeWithAJunction()
    {
        Write(@"outside\keep.txt", "k");
        Write(@"tree\sub\f.txt", "f");
        Junction(P("tree", "sub", "j"), P("outside"));
    }

    [Fact]
    public void RecursiveDeleteRemovesAJunctionAsALinkOnly()
    {
        TreeWithAJunction();
        Assert.Equal($"Deleted directory: {P("tree")}", new FileSystemService(_root, () => false).DeletePath(P("tree"), true));
        Assert.False(Directory.Exists(P("tree")));
        Assert.Equal("k", File.ReadAllText(P("outside", "keep.txt")));
    }

    [Fact]
    public void CopyOverwriteOntoATreeWithAJunctionKeepsTheJunctionTarget()
    {
        TreeWithAJunction();
        Write(@"src\a.txt", "a");
        Assert.Equal($"Copied directory: {P("src")} -> {P("tree")}", new FileSystemService(_root, () => false).CopyPath(P("src"), P("tree"), true, CancellationToken.None));
        Assert.Equal(["a.txt"], Directory.EnumerateFileSystemEntries(P("tree")).Select(Path.GetFileName));
        Assert.Equal("k", File.ReadAllText(P("outside", "keep.txt")));
    }

    [Fact]
    public void MoveOverwriteOntoATreeWithAJunctionKeepsTheJunctionTarget()
    {
        TreeWithAJunction();
        Write(@"src\a.txt", "a");
        Assert.Equal($"Moved: {P("src")} -> {P("tree")}", new FileSystemService(_root, () => false).MovePath(P("src"), P("tree"), true));
        Assert.Equal(["a.txt"], Directory.EnumerateFileSystemEntries(P("tree")).Select(Path.GetFileName));
        Assert.Equal("k", File.ReadAllText(P("outside", "keep.txt")));
    }

    [Fact]
    public void RecursiveDeleteStopsAtAReadOnlyFileAsRmtreeDoes()
    {
        var file = Write(@"tree\r.txt", "r");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Assert.Equal($"Error: Permission denied: {P("tree")}{Hint}", new FileSystemService(_root, () => false).DeletePath(P("tree"), true));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void RecursiveDeleteOfADeepTree()
    {
        Write(Path.Combine("tree", Path.Combine(Enumerable.Repeat("d", 40).ToArray()), "z.txt"), "z");
        Assert.Equal($"Deleted directory: {P("tree")}", _fs.DeletePath(P("tree"), true));
        Assert.False(Directory.Exists(P("tree")));
    }

    [Fact]
    public void CopyADirectoryOntoItselfWithOverwriteKeepsTheSource()
    {
        Write(@"a\x.txt", "x");
        Assert.Equal($"Error copying: Destination {P("a")} is the source itself; overwriting it would delete the source.",
            _fs.CopyPath(P("a"), P("A"), true, CancellationToken.None));
        Assert.Equal("x", File.ReadAllText(P("a", "x.txt")));
    }

    [Fact]
    public void CopyADirectoryOntoAnAncestorOrADescendantWithOverwriteKeepsTheSource()
    {
        Write(@"x\a\s\y.txt", "y");
        Assert.Equal($"Error copying: Destination {P("x")} contains the source {P("x", "a")}; overwriting it would delete the source.",
            _fs.CopyPath(P("x", "a"), P("x"), true, CancellationToken.None));
        Assert.Equal($"Error copying: Destination {P("x", "a", "s")} is inside the source {P("x", "a")}; overwriting it would delete part of the source.",
            _fs.CopyPath(P("x", "a"), P("x", "a", "s"), true, CancellationToken.None));
        Assert.Equal("y", File.ReadAllText(P("x", "a", "s", "y.txt")));
    }

    [Fact]
    public void CopyAFileOntoItselfIsPythonsSameFileError()
    {
        var path = Write(@"d\a.txt", "x");
        var repr = "'" + path.Replace(@"\", @"\\") + "'";
        Assert.Equal($"Error copying: {repr} and {repr} are the same file", _fs.CopyPath(path, path, true, CancellationToken.None));
        Assert.Equal($"Error copying: {repr} and {repr} are the same file", _fs.CopyPath(path, P("d"), true, CancellationToken.None));
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void MoveADirectoryOntoItselfIsANoOp()
    {
        Write(@"a\x.txt", "x");
        Assert.Equal($"Moved: {P("a")} -> {P("a")}", _fs.MovePath(P("a"), P("A"), true));
        Assert.Equal("x", File.ReadAllText(P("a", "x.txt")));
    }

    [Fact]
    public void MoveOntoAnAncestorWithOverwriteKeepsTheSource()
    {
        Write(@"x\a\y.txt", "y");
        Assert.Equal($"Error moving: Destination {P("x")} contains the source {P("x", "a")}; overwriting it would delete the source.",
            _fs.MovePath(P("x", "a"), P("x"), true));
        Assert.Equal($"Error moving: Destination {P("x")} contains the source {P("x", "a", "y.txt")}; overwriting it would delete the source.",
            _fs.MovePath(P("x", "a", "y.txt"), P("x"), true));
        Assert.Equal("y", File.ReadAllText(P("x", "a", "y.txt")));
    }

    [Fact]
    public void MoveOntoADescendantWithOverwriteKeepsTheSource()
    {
        Write(@"a\s\y.txt", "y");
        Assert.Equal($"Error moving: Destination {P("a", "s")} is inside the source {P("a")}; overwriting it would delete part of the source.",
            _fs.MovePath(P("a"), P("a", "s"), true));
        Assert.Equal("y", File.ReadAllText(P("a", "s", "y.txt")));
    }

    [Fact]
    public void ASiblingWithASharedPrefixIsNotAnAncestor()
    {
        Write(@"ab\x.txt", "x");
        Write(@"a\y.txt", "y");
        Assert.Equal($"Moved: {P("ab")} -> {P("a")}", _fs.MovePath(P("ab"), P("a"), true));
        Assert.Equal("x", File.ReadAllText(P("a", "x.txt")));
    }

    [Fact]
    public void Delete()
    {
        var file = Write("a.txt", "x");
        Assert.Equal($"Deleted file: {file}", _fs.DeletePath(file, false));
        Write(@"d\x.txt", "x");
        Assert.Equal($"Error: Directory is not empty: {P("d")}. Set recursive=True to delete non-empty directories.", _fs.DeletePath(P("d"), false));
        Assert.Equal($"Deleted directory: {P("d")}", _fs.DeletePath(P("d"), true));
        Directory.CreateDirectory(P("e"));
        Assert.Equal($"Deleted directory: {P("e")}", _fs.DeletePath(P("e"), false));
        Assert.Equal($"Error: Path not found: {P("e")}", _fs.DeletePath(P("e"), false));
    }

    [Fact]
    public void ListPutsDirectoriesFirstAndHidesDotNames()
    {
        Write("b.txt", "12345");
        Write(".hidden", "x");
        Write("a.log", new string('x', 2048));
        Directory.CreateDirectory(P("Alpha"));
        Assert.Equal($"Directory: {_root}\n  [DIR ] Alpha  \n  [FILE] a.log  2.0 KB\n  [FILE] b.txt  5 B",
            _fs.ListDirectory(_root, null, false, false, CancellationToken.None));
        Assert.Equal($"Directory: {_root}\n  [DIR ] Alpha  \n  [FILE] .hidden  1 B\n  [FILE] a.log  2.0 KB\n  [FILE] b.txt  5 B",
            _fs.ListDirectory(_root, null, false, true, CancellationToken.None));
        Assert.Equal($"Directory: {_root} (filter: *.txt)\n  [FILE] b.txt  5 B",
            _fs.ListDirectory(_root, "*.txt", false, false, CancellationToken.None));
        Assert.Equal($"Directory {_root} is empty matching \"*.zz\".", _fs.ListDirectory(_root, "*.zz", false, false, CancellationToken.None));
    }

    [Fact]
    public void ListRecursiveShowsRelativePaths()
    {
        Write("z.txt", "1");
        Write(@"sub\a.txt", "1");
        Assert.Equal($"Directory: {_root}\n  [DIR ] sub  \n  [FILE] sub\\a.txt  1 B\n  [FILE] z.txt  1 B",
            _fs.ListDirectory(_root, null, true, false, CancellationToken.None));
    }

    [Fact]
    public void ListEdgeCases()
    {
        Directory.CreateDirectory(P("e"));
        Assert.Equal($"Directory {P("e")} is empty.", _fs.ListDirectory(P("e"), null, false, false, CancellationToken.None));
        Assert.Equal($"Error: Directory not found: {P("none")}", _fs.ListDirectory(P("none"), null, false, false, CancellationToken.None));
        var file = Write("f.txt", "x");
        Assert.Equal($"Error: Path is not a directory: {file}", _fs.ListDirectory(file, null, false, false, CancellationToken.None));
    }

    [Fact]
    public void ListStopsAfterFiveHundredItems()
    {
        for (var i = 0; i < 501; i++) Write($"f{i:D3}.txt", "");
        var lines = _fs.ListDirectory(_root, null, false, false, CancellationToken.None).Split('\n');
        Assert.Equal(502, lines.Length);
        Assert.Equal("... (truncated, 500+ items)", lines[^1]);
    }

    [Fact]
    public void Search()
    {
        Write("a.txt", "1");
        Write(@"sub\b.txt", "1");
        Write(@"sub\c.log", "1");
        Assert.Equal($"Search: \"*.txt\" in {_root} (2 matches)\n  [FILE] a.txt  1 B\n  [FILE] sub\\b.txt  1 B",
            _fs.SearchFiles(_root, "*.txt", true, CancellationToken.None));
        Assert.Equal($"Search: \"*.txt\" in {_root} (1 matches)\n  [FILE] a.txt  1 B", _fs.SearchFiles(_root, "*.txt", false, CancellationToken.None));
        Assert.Equal($"No matches found for \"*.zz\" in {_root}", _fs.SearchFiles(_root, "*.zz", true, CancellationToken.None));
        Assert.Equal("Error searching: Non-relative patterns are unsupported", _fs.SearchFiles(_root, @"C:\*.txt", true, CancellationToken.None));
        Assert.Equal($"Error: Search path not found: {P("none")}", _fs.SearchFiles(P("none"), "*", true, CancellationToken.None));
    }

    [Fact]
    public void InfoForAFile()
    {
        var path = Write("info.txt", "hello");
        var info = new FileInfo(path);
        string Stamp(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Equal(
            $"Path: {path}\nType: File\nSize: 5 B (5 bytes)\nCreated: {Stamp(info.CreationTime)}\nModified: {Stamp(info.LastWriteTime)}\n" +
            $"Accessed: {Stamp(info.LastAccessTime)}\nRead-only: False\nExtension: .txt",
            _fs.GetFileInfo(path));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert.Contains("\nRead-only: True\n", _fs.GetFileInfo(path));
    }

    [Fact]
    public void InfoForADirectory()
    {
        Write(@"d\a.txt", "x");
        Directory.CreateDirectory(P("d", "s"));
        var text = _fs.GetFileInfo(P("d"));
        Assert.StartsWith($"Path: {P("d")}\nType: Directory\nSize: 0 B (0 bytes)\n", text);
        Assert.EndsWith("\nRead-only: False\nContents: 1 files, 1 directories", text);
        Assert.Equal($"Error: Path not found: {P("none")}", _fs.GetFileInfo(P("none")));
    }

    [Theory]
    [InlineData("a.txt", ".txt")]
    [InlineData(".env", "(none)")]
    [InlineData("noext", "(none)")]
    [InlineData("a.tar.gz", ".gz")]
    [InlineData("a.", ".")]
    public void SuffixFollowsPathlib(string name, string expected) => Assert.Equal(expected, FileSystemService.Suffix(name));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(1610612736L, "1.5 GB")]
    public void FormatSizeMatchesUpstream(long bytes, string expected) => Assert.Equal(expected, FileSystemService.FormatSize(bytes));
}
