using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

/// <summary>Python's <c>subprocess.list2cmdline</c> (P3 Decisions 34).</summary>
public class CommandLineTests
{
    [Fact]
    public void FollowsList2Cmdline() =>
        Assert.Equal("\"C:\\a b\\x.exe\" a\\\"b c\\ \"\" \"d e\\\\\"", CommandLine.Join([@"C:\a b\x.exe", "a\"b", @"c\", "", @"d e\"]));

    // list2cmdline([r'a\\"b', 't\tab', r'x\y']) == r'a\\\\\"b' + ' "t\tab" ' + r'x\y'
    [Fact]
    public void BackslashesBeforeAQuoteAreDoubledAndTabsQuote() =>
        Assert.Equal(@"a\\\\\""b ""t" + "\t" + @"ab"" x\y", CommandLine.Join([@"a\\""b", "t\tab", @"x\y"]));

    [Fact]
    public void NoArgumentsIsEmpty() => Assert.Equal("", CommandLine.Join([]));
}
