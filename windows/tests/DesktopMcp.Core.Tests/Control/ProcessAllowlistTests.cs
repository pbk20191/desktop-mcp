using DesktopMcp.Core.Control;

namespace DesktopMcp.Core.Tests;

public class ProcessAllowlistTests
{
    [Theory]
    [InlineData("notepad.exe", "Notepad.EXE", true)]
    [InlineData("notepad", "notepad.exe", true)]
    [InlineData("notepad", "notepad.com", false)]
    [InlineData("code.exe", "notepad.exe", false)]
    [InlineData("code.exe", null, false)]
    public void MatchesFileNamesCaseInsensitively(string entry, string? process, bool allowed) =>
        Assert.Equal(allowed, new ProcessAllowlist([entry]).Allows(process));

    [Fact]
    public void EmptyAllowsEverything() => Assert.True(new ProcessAllowlist([]).AllowsAll);

    [Theory]
    [InlineData("")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("tools/x.exe")]
    public void PathsAndBlanksAreRejected(string entry) =>
        Assert.Equal(ProcessAllowlist.InvalidEntry, Assert.Throws<ArgumentException>(() => new ProcessAllowlist([entry])).Message);

    [Fact]
    public void BlockedTexts()
    {
        Assert.Equal("Input blocked: the target window belongs to code.exe, which is not in control.process_allowlist.", ProcessAllowlist.BlockedText("code.exe"));
        Assert.Equal("Input blocked: the target window belongs to an unknown process, which is not in control.process_allowlist.", ProcessAllowlist.BlockedText(null));
    }
}
