using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Tests.Tree;

public class ElementBudgetTests
{
    [Fact]
    public void ReachingTheLimitTruncatesAndFurtherConsumesFail()
    {
        var budget = new ElementBudget(2);
        Assert.True(budget.TryConsume());
        Assert.False(budget.Truncated);
        Assert.True(budget.TryConsume());
        Assert.True(budget.Truncated && budget.Exhausted);
        Assert.False(budget.TryConsume());
        Assert.Equal(2, budget.Count);
    }

    [Fact]
    public void AnOversizedAmountIsCappedAtTheLimit()
    {
        var budget = new ElementBudget(5);
        Assert.False(budget.TryConsume(9));
        Assert.Equal((5, true), (budget.Count, budget.Truncated));
    }

    [Fact]
    public void TheLimitIsAtLeastOneAndVisitsAreCappedAt50PerElement()
    {
        var budget = new ElementBudget(0);
        Assert.Equal(1, budget.Limit);
        for (var i = 0; i < 49; i++) Assert.True(budget.Visit());
        Assert.False(budget.Exhausted);
        Assert.True(budget.Visit());
        Assert.True(budget.Exhausted && budget.Truncated);
        Assert.False(budget.Visit());
    }

    [Fact]
    public void AnUnlimitedElementLimitDoesNotOverflowTheVisitCap()
    {
        // DESKTOP_MCP_MAX_TREE_ELEMENTS may be int.MaxValue; int.MaxValue * 50 in int arithmetic would wrap negative and
        // stop every walk at its first element.
        var budget = new ElementBudget(int.MaxValue);
        Assert.True(budget.Visit());
        Assert.False(budget.Exhausted || budget.Truncated);
        Assert.Equal(int.MaxValue, budget.Remaining + budget.Count);
    }
}
