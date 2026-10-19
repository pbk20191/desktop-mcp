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
}
