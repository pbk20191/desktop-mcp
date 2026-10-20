using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class UnicodeCleanupTests
{
    [Fact]
    public void RemovesPrivateUseCharactersInEveryPlane() =>
        Assert.Equal("ab c", UnicodeCleanup.RemovePrivateUse("ab \U000F0001c\U00100000"));

    [Fact]
    public void KeepsOrdinaryTextUnchanged()
    {
        const string text = "Explorer \U0001F437 창";
        Assert.Same(text, UnicodeCleanup.RemovePrivateUse(text));
        Assert.Same(text, UnicodeCleanup.RepairSurrogates(text));
    }

    [Fact]
    public void ReplacesUnpairedSurrogates() =>
        Assert.Equal("a�b�\U0001F437", UnicodeCleanup.RepairSurrogates("a\uD83Db\uDC37\U0001F437"));

    [Theory]
    [InlineData("a\rb", "a\\rb")]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("a\tb", "a\\tb")]
    [InlineData("a\r\nb", "a\\r\\nb")]
    [InlineData("a\u2028b", "a\\u2028b")]
    [InlineData("a\u2029b", "a\\u2029b")]
    [InlineData("a\0b", "ab")]
    [InlineData("a\u001Bb", "ab")]
    [InlineData("a\u007Fb", "ab")]
    [InlineData("a\u0085b", "ab")]
    [InlineData("a\u009Fb", "ab")]
    public void EscapeControlMapsEachControlCharacter(string text, string expected) =>
        Assert.Equal(expected, UnicodeCleanup.EscapeControl(text));

    [Fact]
    public void EscapeControlKeepsOrdinaryTextAndBackslashes()
    {
        const string text = "Explorer \U0001F437 창 C:\\dir \\n";
        Assert.Same(text, UnicodeCleanup.EscapeControl(text));
    }
}
