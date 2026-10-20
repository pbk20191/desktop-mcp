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

    [Fact]
    public void TheVisitCapIsAtLeastOne()
    {
        var budget = new ElementBudget(limit: 5, maxVisits: 0);
        Assert.False(budget.Exhausted);
        Assert.True(budget.Visit());
        Assert.True(budget.Exhausted && budget.Truncated);
    }

    [Fact]
    public void ASliceGetsWhatIsLeftAndOnlyAnAbsorbedSliceCounts()
    {
        var budget = new ElementBudget(3, 10);
        budget.TryConsume();
        budget.Visit();
        var slice = budget.Slice();
        Assert.Equal((2, 9), (slice.Limit, slice.RemainingVisits));
        slice.TryConsume();
        slice.Visit();
        Assert.Equal((1, 9), (budget.Count, budget.RemainingVisits));
        budget.Absorb(slice);
        Assert.Equal((2, 8, false), (budget.Count, budget.RemainingVisits, budget.Truncated));
    }

    [Fact]
    public void ATruncatedSliceTruncatesTheBudget()
    {
        var budget = new ElementBudget(5, 3);
        var slice = budget.Slice();
        while (slice.Visit()) { }
        budget.Absorb(slice);
        Assert.True(budget.Truncated && budget.Exhausted);
    }
}
