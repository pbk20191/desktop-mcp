using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

public class FuzzyTests
{
    [Theory]
    [InlineData("chrome", "chrome.exe", 100)]
    [InlineData("note", "notepad.exe", 100)]
    [InlineData("chorme", "chrome.exe", 83)]
    [InlineData("abc", "xyz", 0)]
    [InlineData("this is a test", "this is a test!", 100)]
    [InlineData("abcd", "dcba", 40)]
    [InlineData("ab", "", 0)]
    [InlineData("", "", 100)]
    public void PartialRatioMatchesRapidfuzz(string s1, string s2, int expected) => Assert.Equal(expected, Fuzzy.PartialRatio(s1, s2));

    [Fact]
    public void PartialRatioIsSymmetricInTheShorterString() =>
        Assert.Equal(Fuzzy.PartialRatio("chrome.exe", "chorme"), Fuzzy.PartialRatio("chorme", "chrome.exe"));
}
