using DesktopMcp.Desktop.Control;

namespace DesktopMcp.Desktop.Tests.Control;

/// <summary>The interactive-desktop rule over its two checks; the real check is in <see cref="InputMonitorDesktopTests"/>.</summary>
public class InteractiveDesktopTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void BothTheActiveSessionAndTheDefaultDesktopAreNeeded(bool active, bool defaultDesktop, bool available) =>
        Assert.Equal(available, InteractiveDesktop.IsAvailable(() => active, () => defaultDesktop));

    [Fact]
    public void AFailingCheckCountsAsUnavailable()
    {
        Assert.False(InteractiveDesktop.IsAvailable(() => throw new InvalidOperationException(), () => true));
        Assert.False(InteractiveDesktop.IsAvailable(() => true, () => throw new InvalidOperationException()));
    }
}
